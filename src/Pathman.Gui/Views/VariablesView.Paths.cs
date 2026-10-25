using System.Windows;
using System.Windows.Controls;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;

namespace Pathman.Gui.Views;

public partial class VariablesView
{
    private (VariableScope Scope,string Name,int Index,int Count,Guid Id)? SelectedPathItem()
    {
        if(editingSession is not {} session || ValuesList.SelectedItem is not EnvironmentEntry entry) return null;
        try {var source=session.Locate(entry.ItemId); return(source.Scope,source.Name,source.Items.IndexOf(source.Item),source.Items.Count,entry.ItemId);}
        catch(ArgumentException) {return null;}
    }
    private (VariableScope Scope,string Name) NewPathOwner()
    {
        if(ValuesList.SelectedItem is EnvironmentEntry {Row:{} row} entry)
        {
            if(entry.CanExpand && row.TargetScope is {} scope && row.TargetName is {} name)
            {expandedPathRows.Add(entry.ViewKey); return(scope,name);}
            return(row.OwnerScope,row.OwnerName);
        }
        return(editingSession!.Root.Scope,editingSession.Root.Name);
    }
    private void RefreshPathEntries(Guid? selectedId=null,string? selectedKey=null)
    {
        if(editingSession is not {} session) {entries.Clear(); return;}
        var old=ValuesList.SelectedItem as EnvironmentEntry;
        selectedKey ??= old?.ViewKey;
        selectedId ??= old?.ItemId;
        var resolver=new EnvironmentPathResolver(session.CurrentVariables(),RuntimeValues());
        var previousUpdating=updating; updating=true;
        entries.Clear();
        foreach(var row in session.VisibleRows(expandedPathRows))
        {
            var links=EnvironmentPathResolver.ReferenceNames(row.Item.Value).Select(name=>new EnvironmentReferenceLink(name,resolver.Find(name,session.Root.Scope)?.Scope)).ToArray();
            entries.Add(new(row.Item.Value,row.Item.IsDraft,links,row,pathDragging && row.ViewKey==pathDragSource?.ViewKey));
        }
        ValuesList.SelectedItem=entries.FirstOrDefault(item=>item.ViewKey==selectedKey) ?? entries.FirstOrDefault(item=>item.ItemId==selectedId);
        updating=previousUpdating;
        dirty=session.HasChanges;
        UpdateActions();
    }
    private void TogglePathReference(object sender,RoutedEventArgs e)
    {
        e.Handled=true;
        if(sender is not FrameworkElement {DataContext:EnvironmentEntry entry} || !entry.CanExpand || busy || pathDragging) return;
        if(!expandedPathRows.Remove(entry.ViewKey)) expandedPathRows.Add(entry.ViewKey);
        RefreshPathEntries(entry.ItemId,entry.ViewKey);
    }
    private void MovePathSibling(int offset)
    {
        if(editingSession is not {} session || SelectedPathItem() is not {} source) return;
        var items=session.Items(source.Scope,source.Name);
        var next=source.Index+offset;
        if(next<0 || next>=items.Count) return;
        var before=offset<0 ? items[next].Id : next+1<items.Count ? items[next+1].Id : (Guid?)null;
        try {session.Move(source.Id,source.Scope,source.Name,before); RefreshPathEntries(source.Id); SetDirty();}
        catch(ArgumentException error) {StatusText.Text=error.Message;}
    }
    private async Task SavePathSession()
    {
        if(busy || editingSession is not {} session) return;
        var mutations=session.Mutations();
        if(mutations.Count==0) {session.FinishReview();RefreshPathEntries();SetDirty();return;}
        busy=true; IsEnabled=false;UpdateActions();
        var applied=new List<string>();
        var succeeded=false;
        try
        {
            foreach(var scope in mutations.GroupBy(item=>item.Scope).OrderBy(group=>group.Key==VariableScope.Machine ? 0 : 1))
            {
                await EnvironmentEditHost.ApplyBatchAsync(scope.ToArray(),session.Preconditions(scope.Key),store,layout.DataDirectory);
                session.Accept(scope.Key); applied.Add(scope.Key==VariableScope.Machine ? "系统变量" : "用户变量");
            }
            succeeded=true; StatusText.Text="已保存。";
        }
        catch(Exception error) when(Expected(error) || error is OperationCanceledException)
        {
            allVariables=session.Originals;
            StatusText.Text=(applied.Count>0 ? $"已保存{string.Join("、",applied)}；其余更改仍在编辑区。" : "未保存。")+error.Message;
            SnapshotChanged?.Invoke(this,EventArgs.Empty);
        }
        finally {busy=false; IsEnabled=true; SetDirty();}
        if(succeeded) {dirty=false; await ReloadAsync();}
    }
}
