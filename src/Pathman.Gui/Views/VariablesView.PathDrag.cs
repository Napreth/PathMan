using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using Pathman.Core.Models;
using Pathman.Gui.Controls;

namespace Pathman.Gui.Views;

public partial class VariablesView
{
    private bool pathDragging,pathCompleting,pathWasExpanded;
    private EnvironmentEntry? pathDragSource,pathHover;
    private Point pathStart,pathOffset;
    private RowDragAdorner? pathAdorner;
    private FrameworkElement? pathDragVisual;
    private AdornerLayer? pathAdornerLayer;
    private DispatcherTimer? pathScrollTimer;
    private DateTime pathHoverSince;
    private PathDrop? pathDrop;
    private sealed record PathDrop(VariableScope Scope,string Name,Guid? BeforeId,string Prefix,string? ExpandKey);
    private void PathMouseDown(object sender,MouseButtonEventArgs e)
    {
        pathDragSource=null;
        if(busy || dragging || HasInlineEditor || e.ClickCount>1 || EditableRow.FindParent<ButtonBase>(e.OriginalSource as DependencyObject) is not null ||
            EditableRow.FindParent<TextBox>(e.OriginalSource as DependencyObject) is not null) return;
        if(EditableRow.FindParent<EditableRow>(e.OriginalSource as DependencyObject) is {DataContext:EnvironmentEntry entry} && !entry.IsDraft)
        {pathDragSource=entry; pathStart=e.GetPosition(this);}
    }
    private void PathMouseMove(object sender,MouseEventArgs e)
    {
        if(pathDragSource is null || pathDragging || e.LeftButton!=MouseButtonState.Pressed) return;
        var point=e.GetPosition(this);
        if(Math.Abs(point.X-pathStart.X)<SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y-pathStart.Y)<SystemParameters.MinimumVerticalDragDistance) return;
        e.Handled=true; StartPathDrag();
    }
    private void PathMouseUp(object sender,MouseButtonEventArgs e) {if(!pathDragging) pathDragSource=null;}
    private void StartPathDrag()
    {
        if(pathDragSource is not {} source || editingSession is null) return;
        pathAdornerLayer=AdornerLayer.GetAdornerLayer(this); if(pathAdornerLayer is null) return;
        pathWasExpanded=source.IsExpanded;
        if(pathWasExpanded) {expandedPathRows.Remove(source.ViewKey); RefreshPathEntries(source.ItemId,source.ViewKey);}
        ValuesList.UpdateLayout();
        var current=entries.FirstOrDefault(item=>item.ViewKey==source.ViewKey);
        if(current is null || ValuesList.ItemContainerGenerator.ContainerFromItem(current) is not ListBoxItem visual) return;
        var origin=visual.TranslatePoint(new(),this);
        pathOffset=new(pathStart.X-origin.X,pathStart.Y-origin.Y);
        pathAdorner=new(this,visual); pathAdornerLayer.Add(pathAdorner);
        pathDragSource=current; pathDragging=true; pathDragVisual=visual; visual.Opacity=0;
        PreviewMouseMove+=PathCapturedMove; PreviewMouseLeftButtonUp+=PathCapturedUp; PreviewKeyDown+=PathCapturedKey;
        LostMouseCapture+=PathCaptureLost; Unloaded+=PathUnloaded;
        if(!CaptureMouse()) {FinishPathDrag(false);return;}
        if(Mouse.LeftButton!=MouseButtonState.Pressed) {FinishPathDrag(true);return;}
        pathScrollTimer=new DispatcherTimer(DispatcherPriority.Input) {Interval=TimeSpan.FromMilliseconds(16)};
        pathScrollTimer.Tick+=PathDragTick; pathScrollTimer.Start(); UpdatePathDrag(false);
    }
    private void PathCapturedMove(object sender,MouseEventArgs e) {if(pathDragging){UpdatePathDrag(true);e.Handled=true;}}
    private void PathCapturedUp(object sender,MouseButtonEventArgs e) {if(pathDragging){e.Handled=true;FinishPathDrag(true);}}
    private void PathCapturedKey(object sender,KeyEventArgs e) {if(e.Key==Key.Escape){e.Handled=true;FinishPathDrag(false);}}
    private void PathCaptureLost(object sender,MouseEventArgs e) {if(pathDragging && !pathCompleting && !IsMouseCaptured) FinishPathDrag(false);}
    private void PathUnloaded(object sender,RoutedEventArgs e)=>FinishPathDrag(false);
    private void PathDragTick(object? sender,EventArgs e)
    {
        if(!pathDragging) return;
        if(Mouse.LeftButton!=MouseButtonState.Pressed) {FinishPathDrag(true);return;}
        var point=Mouse.GetPosition(ValuesList); var scroll=FindVisual<ScrollViewer>(ValuesList);
        if(scroll is not null && point.X>=0 && point.X<=ValuesList.ActualWidth)
        {
            const double edge=24;
            var distance=point.Y<edge ? point.Y-edge : point.Y>ValuesList.ActualHeight-edge ? point.Y-(ValuesList.ActualHeight-edge) : 0;
            if(distance!=0) scroll.ScrollToVerticalOffset(scroll.VerticalOffset+Math.Sign(distance)*Math.Min(4000,30+Math.Abs(distance)*7)*.016);
        }
        if(pathHover is {CanExpand:true} hover && hover.ViewKey!=pathDragSource?.ViewKey && !expandedPathRows.Contains(hover.ViewKey) && DateTime.UtcNow-pathHoverSince>TimeSpan.FromMilliseconds(600))
        {expandedPathRows.Add(hover.ViewKey); RefreshPathEntries(pathDragSource?.ItemId,pathDragSource?.ViewKey); ValuesList.UpdateLayout();}
        UpdatePathDrag(false);
    }
    private void UpdatePathDrag(bool animate)
    {
        if(pathDragSource is not {} source || editingSession is not {} session || pathAdorner is null) return;
        var point=Mouse.GetPosition(this); var local=Mouse.GetPosition(ValuesList);
        var bounds=new Rect(ValuesList.TranslatePoint(new(),this),ValuesList.RenderSize);
        pathAdorner.Move(new(point.X-pathOffset.X,point.Y-pathOffset.Y),animate);
        pathDrop=null; Rect? highlight=null; double? line=null; var indent=0d; EnvironmentEntry? hovered=null;
        if(bounds.Contains(point))
        {
            var container=EditableRow.FindParent<ListBoxItem>(ValuesList.InputHitTest(local) as DependencyObject);
            if(container?.DataContext is EnvironmentEntry {Row:{} row} target)
            {
                var rectangle=new Rect(container.TranslatePoint(new(),this),container.RenderSize);
                var within=point.Y-rectangle.Top;
                indent=row.Depth*16;
                if(target.CanExpand && within>=5 && within<=19)
                {
                    hovered=target;
                    pathDrop=new(row.TargetScope!.Value,row.TargetName!,null,target.ViewKey,target.ViewKey);
                    highlight=rectangle;
                }
                else
                {
                    var siblings=session.Items(row.OwnerScope,row.OwnerName); var index=siblings.FindIndex(item=>item.Id==target.ItemId);
                    var before=within<12 ? target.ItemId : index+1<siblings.Count ? siblings[index+1].Id : (Guid?)null;
                    var slash=target.ViewKey.LastIndexOf('/');
                    pathDrop=new(row.OwnerScope,row.OwnerName,before,target.ViewKey[..slash],null);
                    line=within<12 ? rectangle.Top : rectangle.Bottom;
                }
            }
            else
            {
                var root=session.Root; var items=session.Items(root.Scope,root.Name);
                var top=local.Y<8;
                pathDrop=new(root.Scope,root.Name,top && items.Count>0 ? items[0].Id : null,$"{root.Scope}:{root.Name}",null);
                line=top ? bounds.Top+4 : entries.Count>0 && ValuesList.ItemContainerGenerator.ContainerFromItem(entries[^1]) is ListBoxItem last
                    ? Math.Min(bounds.Bottom-4,last.TranslatePoint(new(),this).Y+last.ActualHeight) : bounds.Top+4;
            }
            if(pathDrop is {} drop && session.ValidateMove(source.ItemId,drop.Scope,drop.Name,drop.BeforeId) is {} reason)
            {pathDrop=null; highlight=null; line=null; hovered=null; StatusText.Text=reason;}
        }
        if(pathHover?.ViewKey!=hovered?.ViewKey) {pathHover=hovered; pathHoverSince=DateTime.UtcNow;}
        if(line is not null) line=Math.Clamp(line.Value,bounds.Top+2,bounds.Bottom-2);
        pathAdorner.ShowTarget(highlight,line,bounds.Left+12+indent,Math.Max(0,bounds.Width-30-indent));
        Mouse.OverrideCursor=pathDrop is null ? Cursors.No : Cursors.SizeAll;
    }
    private void FinishPathDrag(bool drop)
    {
        if(!pathDragging || pathCompleting) return;
        pathCompleting=true;
        if(drop) UpdatePathDrag(false);
        var target=drop ? pathDrop : null; var source=pathDragSource;
        pathScrollTimer?.Stop(); pathScrollTimer=null;
        if(pathDragVisual is not null) pathDragVisual.Opacity=1;
        if(pathAdorner is not null) pathAdornerLayer?.Remove(pathAdorner);
        PreviewMouseMove-=PathCapturedMove; PreviewMouseLeftButtonUp-=PathCapturedUp; PreviewKeyDown-=PathCapturedKey;
        LostMouseCapture-=PathCaptureLost; Unloaded-=PathUnloaded;
        pathDragging=false; Mouse.OverrideCursor=null;
        if(IsMouseCaptured) ReleaseMouseCapture();
        try
        {
            if(target is not null && source is not null && editingSession is {} session)
            {
                session.Move(source.ItemId,target.Scope,target.Name,target.BeforeId);
                if(target.ExpandKey is {} expand) expandedPathRows.Add(expand);
                RefreshPathEntries(source.ItemId,target.Prefix+"/"+source.ItemId.ToString("N")); SetDirty();
            }
            else
            {
                if(pathWasExpanded && source is not null) expandedPathRows.Add(source.ViewKey);
                RefreshPathEntries(source?.ItemId,source?.ViewKey);
            }
        }
        catch(ArgumentException error) {StatusText.Text=error.Message; RefreshPathEntries();}
        finally {pathDragSource=null;pathHover=null;pathDrop=null;pathAdorner=null;pathDragVisual=null;pathAdornerLayer=null;pathCompleting=false;}
    }
}
