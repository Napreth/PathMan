using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Pathman.Gui.Controls;

namespace Pathman.Gui.Views;

public partial class VariablesView
{
    private bool dragging, completingDrag, dragExpanded;
    private Point dragStart, dragOffset;
    private EnvironmentTreeNode? dragNode;
    private TreeView? dragTree;
    private EditableRow? dragRow;
    private FrameworkElement? dragVisual;
    private RowDragAdorner? dragAdorner;
    private AdornerLayer? dragLayer;
    private DispatcherTimer? scrollTimer;
    private DropDestination? destination;
    private EnvironmentTreeNode? hoverGroup;
    private DateTime hoverSince;
    private sealed record DropDestination(string GroupId, string? BeforeId, bool Group);

    internal static T? FindVisual<T>(DependencyObject root) where T:DependencyObject
    {
        if(root is T found) return found;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) if(FindVisual<T>(VisualTreeHelper.GetChild(root,i)) is {} child) return child;
        return null;
    }
    private void TreeMouseDown(object sender, MouseButtonEventArgs e)
    {
        dragNode=null;
        if(busy || activeEditor?.IsEditing==true || sender is not TreeView tree ||
            EditableRow.FindParent<ButtonBase>(e.OriginalSource as DependencyObject) is not null ||
            EditableRow.FindParent<TextBox>(e.OriginalSource as DependencyObject) is not null) return;
        var row=EditableRow.FindParent<EditableRow>(e.OriginalSource as DependencyObject);
        if(row?.DataContext is not EnvironmentTreeNode node) return;
        if(e.ClickCount>1) { e.Handled=true; row.BeginEdit(); return; }
        dragNode=node; dragTree=tree; dragRow=row; dragStart=e.GetPosition(this); dragOffset=e.GetPosition(row);
    }
    private void TreeMouseMove(object sender, MouseEventArgs e)
    {
        if(dragNode is null || e.LeftButton!=MouseButtonState.Pressed) return;
        var point=e.GetPosition(this);
        if(!dragging && (Math.Abs(point.X-dragStart.X)>=SystemParameters.MinimumHorizontalDragDistance || Math.Abs(point.Y-dragStart.Y)>=SystemParameters.MinimumVerticalDragDistance)) StartDrag();
    }
    private void StartDrag()
    {
        if(dragNode is null || dragRow is null || dragTree is null || !ConfirmLeave()) { dragNode=null; return; }
        dragLayer=AdornerLayer.GetAdornerLayer(this);
        if(dragLayer is null) { dragNode=null; return; }
        dragExpanded=dragNode.IsExpanded;
        if(dragNode.IsGroup) dragNode.IsExpanded=false;
        dragTree.UpdateLayout();
        dragVisual=Container(dragTree,dragNode) is {} item ? (FrameworkElement)item.Template.FindName("Row",item) : dragRow;
        dragOffset=Mouse.GetPosition(dragVisual);
        dragAdorner=new(this,dragVisual); dragLayer.Add(dragAdorner);
        dragVisual.Opacity=0;
        dragging=true;
        PreviewMouseMove+=DragMouseMove; PreviewMouseLeftButtonUp+=DragMouseUp;
        PreviewKeyDown+=DragKeyDown; LostMouseCapture+=DragCaptureLost; Unloaded+=DragUnloaded;
        if(!CaptureMouse()) { _=FinishDrag(false); return; }
        scrollTimer=new DispatcherTimer(DispatcherPriority.Input) {Interval=TimeSpan.FromMilliseconds(16)};
        scrollTimer.Tick+=DragTick; scrollTimer.Start();
        UpdateDrag(false);
    }
    private void DragMouseMove(object sender, MouseEventArgs e) { if(dragging) { UpdateDrag(true); e.Handled=true; } }
    private async void DragMouseUp(object sender, MouseButtonEventArgs e)
    { if(dragging) { e.Handled=true; await FinishDrag(true); } }
    private void TreeMouseUp(object sender, MouseButtonEventArgs e) { if(!dragging) dragNode=null; }
    private async void DragKeyDown(object sender, KeyEventArgs e) { if(e.Key==Key.Escape) { e.Handled=true; await FinishDrag(false); } }
    private async void DragCaptureLost(object sender, MouseEventArgs e) { if(dragging && !completingDrag) await FinishDrag(false); }
    private async void DragUnloaded(object sender, RoutedEventArgs e) { if(dragging) await FinishDrag(false); }
    private void TreeKeyDown(object sender, KeyEventArgs e)
    {
        if(e.Key!=Key.F2 || sender is not TreeView tree || tree.SelectedItem is not EnvironmentTreeNode node) return;
        if(Container(tree,node) is {} item && FindVisual<EditableRow>((DependencyObject)item.Template.FindName("PART_Header",item)) is {} row)
        { e.Handled=true; row.BeginEdit(); }
    }
    private TreeViewItem? Container(TreeView tree, EnvironmentTreeNode node)
    {
        if(node.IsGroup) return tree.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;
        var groups=tree==UserVariablesTree ? userGroups : systemGroups;
        var parent=groups.FirstOrDefault(group=>group.Children.Contains(node));
        return parent is null ? null : (tree.ItemContainerGenerator.ContainerFromItem(parent) as TreeViewItem)?.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;
    }
    private Rect HeaderBounds(TreeViewItem item)
    {
        var header=(FrameworkElement)item.Template.FindName("Row",item);
        return new(header.TranslatePoint(new(),this),header.RenderSize);
    }
    private void DragTick(object? sender, EventArgs e)
    {
        if(!dragging || dragTree is null) return;
        var scroll=FindVisual<ScrollViewer>(dragTree);
        var point=Mouse.GetPosition(dragTree);
        if(scroll is not null && point.X>=0 && point.X<=dragTree.ActualWidth)
        {
            const double edge=24;
            var distance=point.Y<edge ? point.Y-edge : point.Y>dragTree.ActualHeight-edge ? point.Y-(dragTree.ActualHeight-edge) : 0;
            // Pixels/second grows continuously with the distance outside the list.
            if(distance!=0) scroll.ScrollToVerticalOffset(scroll.VerticalOffset+Math.Sign(distance)*Math.Min(4000,30+Math.Abs(distance)*7)*.016);
        }
        if(hoverGroup is not null && !hoverGroup.IsExpanded && DateTime.UtcNow-hoverSince>TimeSpan.FromMilliseconds(600)) hoverGroup.IsExpanded=true;
        UpdateDrag(false);
    }
    private void UpdateDrag(bool animate)
    {
        if(dragTree is null || dragNode is null || dragAdorner is null) return;
        var point=Mouse.GetPosition(this);
        dragAdorner.Move(new(point.X-dragOffset.X,point.Y-dragOffset.Y),animate);
        destination=null;
        var bounds=new Rect(dragTree.TranslatePoint(new(),this),dragTree.RenderSize);
        Rect? highlight=null; double? line=null;
        EnvironmentTreeNode? hovered=null;
        if(bounds.Contains(point))
        {
            var groups=(dragTree==UserVariablesTree ? userGroups : systemGroups).ToList();
            if(dragNode.IsGroup)
            {
                var remaining=groups.Where(group=>group!=dragNode).ToList();
                var index=remaining.Count;
                for(var i=0;i<remaining.Count;i++) if(Container(dragTree,remaining[i]) is {} item)
                {
                    var top=item.TranslatePoint(new(),this).Y;
                    if(point.Y<top+item.ActualHeight/2) { index=i; break; }
                }
                var pinnedCount=remaining.Count(group=>group.IsPinned);
                if(dragNode.IsPinned ? index<=pinnedCount : index>=pinnedCount)
                {
                    var before=index<remaining.Count ? remaining[index] : null;
                    destination=new(dragNode.Group.Id,before?.Group.Id,true);
                    if(before is not null && Container(dragTree,before) is {} next) line=next.TranslatePoint(new(),this).Y;
                    else if(remaining.LastOrDefault() is {} last && Container(dragTree,last) is {} bottom) line=bottom.TranslatePoint(new(),this).Y+bottom.ActualHeight;
                    else line=bounds.Top+4;
                }
            }
            else
            {
                foreach(var group in groups)
                {
                    if(Container(dragTree,group) is not {} groupItem) continue;
                    var groupBounds=new Rect(groupItem.TranslatePoint(new(),this),groupItem.RenderSize);
                    if(!groupBounds.Contains(point)) continue;
                    hovered=group;
                    var header=HeaderBounds(groupItem);
                    var pins=group.Children.Where(child=>child.IsPinned && child!=dragNode).ToArray();
                    if(dragNode.IsPinned && group.IsExpanded && !header.Contains(point))
                    {
                        var index=pins.Length;
                        for(var i=0;i<pins.Length;i++) if(Container(dragTree,pins[i]) is {} item && point.Y<HeaderBounds(item).Top+12) { index=i; break; }
                        var pinnedBottom=pins.LastOrDefault() is {} last && Container(dragTree,last) is {} lastItem ? HeaderBounds(lastItem).Bottom : header.Bottom;
                        if(point.Y<=pinnedBottom+12)
                        {
                            var before=index<pins.Length ? pins[index] : null;
                            destination=new(group.Group.Id,before?.Title,false);
                            line=before is not null && Container(dragTree,before) is {} next ? HeaderBounds(next).Top : pinnedBottom;
                        }
                    }
                    if(destination is null && (group.Group.Id!=dragNode.Group.Id || dragNode.IsPinned))
                    { destination=new(group.Group.Id,null,false); highlight=header; }
                    break;
                }
            }
        }
        if(hoverGroup!=hovered) { hoverGroup=hovered; hoverSince=DateTime.UtcNow; }
        if(line is not null) line=Math.Clamp(line.Value,bounds.Top+2,bounds.Bottom-2);
        dragAdorner.ShowTarget(highlight,line,bounds.Left+5,Math.Max(0,bounds.Width-22));
        Mouse.OverrideCursor=destination is null ? Cursors.No : Cursors.SizeAll;
    }
    private async Task FinishDrag(bool drop)
    {
        if(!dragging || completingDrag) return;
        completingDrag=true;
        if(drop) UpdateDrag(false);
        var plan=drop ? destination : null;
        var node=dragNode;
        scrollTimer?.Stop(); scrollTimer=null;
        if(dragVisual is not null) dragVisual.Opacity=1;
        if(dragAdorner is not null) dragLayer?.Remove(dragAdorner);
        if(node?.IsGroup==true) node.IsExpanded=dragExpanded;
        dragging=false; Mouse.OverrideCursor=null;
        PreviewMouseMove-=DragMouseMove; PreviewMouseLeftButtonUp-=DragMouseUp;
        PreviewKeyDown-=DragKeyDown; LostMouseCapture-=DragCaptureLost; Unloaded-=DragUnloaded;
        if(IsMouseCaptured) ReleaseMouseCapture();
        dragNode=null; dragTree=null; dragRow=null; dragVisual=null; dragAdorner=null; dragLayer=null; destination=null; hoverGroup=null;
        try
        {
            if(plan is null || node is null) return;
            store.Update(state=>
            {
                if(plan.Group) state.MoveGroup(node.Scope,node.Group.Id,plan.BeforeId);
                else state.MoveVariable(node.Scope,node.Title,node.Group.Id,plan.GroupId,plan.BeforeId);
            });
            await ReloadAsync();
        }
        catch(Exception error) when(Expected(error)) { Error(error); }
        finally { completingDrag=false; }
    }
}
