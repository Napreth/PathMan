using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;
using Pathman.Gui.Controls;
using Pathman.Gui.Dialogs;

namespace Pathman.Gui.Views;

public partial class VariablesView
{
    private EnvironmentTreeNode? selectionAnchor;
    private IEnumerable<EnvironmentTreeNode> AllNodes=>userGroups.Concat(systemGroups).SelectMany(group=>new[]{group}.Concat(group.Children));
    private void MarkSingleNode(EnvironmentTreeNode? node)
    {foreach(var item in AllNodes) item.IsMarked=item==node;selectionAnchor=node;}
    private bool SelectTreeRange(TreeView tree,EnvironmentTreeNode node)
    {
        if(!EditingKeys.SelectionModifier) return false;
        var groups=tree==UserVariablesTree ? userGroups : systemGroups;
        var visible=groups.SelectMany(group=>new[]{group}.Concat(group.IsExpanded ? group.Children : [])).ToArray();
        foreach(var item in AllNodes.Where(item=>item.Scope!=node.Scope)) {item.IsMarked=false;item.IsSelected=false;}
        if(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            var anchor=Array.IndexOf(visible,selectionAnchor);if(anchor<0) anchor=Array.IndexOf(visible,node);
            var end=Array.IndexOf(visible,node);
            for(var index=0;index<visible.Length;index++)
            {
                var mark=index>=Math.Min(anchor,end) && index<=Math.Max(anchor,end);
                visible[index].IsMarked=EditingKeys.Control ? visible[index].IsMarked || mark : mark;
                if(!visible[index].IsMarked) visible[index].IsSelected=false;
            }
        }
        else {node.IsMarked=!node.IsMarked;if(!node.IsMarked) node.IsSelected=false;selectionAnchor=node;}
        return true;
    }
    private async void EditorKeyDown(object sender,KeyEventArgs e)
    {
        if(busy || dragging || pathDragging || CleanupPanel.Visibility==Visibility.Visible) return;
        if(EditingKeys.Control && e.Key==Key.S) {e.Handled=true;SaveValue(this,new());return;}
        if(EditingKeys.InTextInput(e)) return;
        var tree=UserVariablesTree.IsKeyboardFocusWithin ? UserVariablesTree : SystemVariablesTree.IsKeyboardFocusWithin ? SystemVariablesTree : null;
        if(EditingKeys.Undo(e) || EditingKeys.Redo(e))
        {
            if(editingSession is not {} session) return;
            e.Handled=true;
            if(EditingKeys.Redo(e) ? session.Redo() : session.Undo())
            {RefreshPathEntries();updating=true;ScalarValueBox.Text=CurrentValue;updating=false;SetDirty();}
            return;
        }
        if(tree is not null)
        {
            if(e.Key==Key.F2)
            {
                e.Handled=true;var marked=AllNodes.Where(node=>node.IsMarked).ToArray();
                if(marked.Length==1 && Container(tree,marked[0]) is {} container) FindVisual<EditableRow>((DependencyObject)container.Template.FindName("PART_Header",container))?.BeginEdit();
                return;
            }
            if(EditingKeys.Control && e.Key==Key.A)
            {e.Handled=true;var groups=tree==UserVariablesTree ? userGroups : systemGroups;var scope=tree==UserVariablesTree ? VariableScope.User : VariableScope.Machine;foreach(var node in AllNodes.Where(node=>node.Scope!=scope)) {node.IsMarked=false;node.IsSelected=false;}foreach(var node in groups.SelectMany(group=>new[]{group}.Concat(group.IsExpanded ? group.Children : []))) node.IsMarked=true;return;}
            if(e.Key==Key.Delete) {e.Handled=true;await DeleteMarkedNodes();return;}
            if(EditingKeys.New(e)) {e.Handled=true;var scope=tree==UserVariablesTree ? VariableScope.User : VariableScope.Machine;if(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) await NewGroup(scope);else await NewVariable(scope);return;}
            if(EditingKeys.Control && e.Key==Key.C)
            {e.Handled=true;CopySelectionText(string.Join(Environment.NewLine,AllNodes.Where(node=>node.IsMarked).Select(node=>node.Variable is {} value ? $"{value.Name}={value.RawValue}" : node.Title)));return;}
            return;
        }
        if(ValuesList.IsKeyboardFocusWithin || ValueActions.IsKeyboardFocusWithin)
        {
            if(e.Key==Key.Delete) {e.Handled=true;DeleteEntry(this,new());}
            else if(e.Key is Key.F2 or Key.Enter) {e.Handled=true;if(EditEntryButton.IsEnabled) EditEntry(this,new());}
            else if(EditingKeys.New(e)) {e.Handled=true;AddEntry(this,new());}
            else if(EditingKeys.Control && e.Key==Key.C) {e.Handled=true;CopyValue(this,new());}
            else if(EditingKeys.Control && e.Key==Key.A) {e.Handled=true;ValuesList.SelectAll();}
            else if(Keyboard.Modifiers==ModifierKeys.Alt && (e.Key is Key.Up or Key.Down || e.Key==Key.System && e.SystemKey is Key.Up or Key.Down)) {e.Handled=true;MovePathSibling((e.Key==Key.System ? e.SystemKey : e.Key)==Key.Up ? -1 : 1);}
        }
        else if(GroupVariablesList.IsKeyboardFocusWithin)
        {
            if(EditingKeys.Control && e.Key==Key.C) {e.Handled=true;CopySelectionText(string.Join(Environment.NewLine,GroupVariablesList.SelectedItems.OfType<EnvironmentTreeNode>().Select(node=>$"{node.Title}={node.Variable!.RawValue}")));}
            else if(e.Key==Key.Delete) {e.Handled=true;await DeleteMarkedNodes(GroupVariablesList.SelectedItems.OfType<EnvironmentTreeNode>().ToArray());}
            else if(e.Key==Key.Enter && GroupVariablesList.SelectedItems.Count==1 && GroupVariablesList.SelectedItem is EnvironmentTreeNode node && await ConfirmLeaveAsync()) {e.Handled=true;ShowSelection(node);}
            else if(EditingKeys.New(e) && selected is {} group) {e.Handled=true;await NewVariable(group.Scope);}
        }
    }
    private void CopySelectionText(string text)
    {try {Clipboard.SetDataObject(text,true);}catch(System.Runtime.InteropServices.ExternalException) {StatusText.Text=L.Get("剪贴板暂时不可用。");}}
    private async Task DeleteMarkedNodes(EnvironmentTreeNode[]? selection=null)
    {
        var nodes=(selection ?? AllNodes.Where(node=>node.IsMarked).ToArray()).Where(node=>node.CanDelete).ToArray();
        if(nodes.Length==0 || !await ConfirmLeaveAsync()) return;
        if(nodes.Length==1) {await DeleteNode(nodes[0]);return;}
        if(new ConfirmDialog(OwnerWindow,L.Get("删除"),L.Format("删除选中的 {0} 个环境变量和 {1} 个分组？分组内未选中的变量会移到默认分组。",nodes.Count(node=>!node.IsGroup),nodes.Count(node=>node.IsGroup)),L.Get("删除")).ShowDialog()!=true) return;
        busy=true;IsEnabled=false;
        try
        {
            foreach(var scope in nodes.Where(node=>!node.IsGroup).GroupBy(node=>node.Scope).OrderBy(group=>group.Key==VariableScope.Machine ? 0 : 1))
            {
                var mutations=scope.Select(node=>new EnvironmentMutation("delete",node.Scope,node.Title,ExpectedValue:node.Variable!.RawValue,ExpectedKind:node.Variable.Kind)).ToArray();
                await EnvironmentEditHost.ApplyBatchAsync(mutations,[],store,layout.DataDirectory);
            }
            store.Update(state=>
            {
                foreach(var node in nodes.Where(node=>node.IsGroup))
                {
                    var fallback=state.VisibleGroups(node.Scope).First(group=>group.Rule=="fallback").Id;
                    foreach(var child in node.Children) state.Assignments[EnvironmentWorkspace.Key(child.Scope,child.Title)]=fallback;
                    foreach(var key in state.Assignments.Where(pair=>pair.Value==node.Group.Id).Select(pair=>pair.Key).ToArray()) state.Assignments[key]=fallback;
                    state.Groups.RemoveAll(group=>group.Id==node.Group.Id);
                    foreach(var order in state.GroupOrder.Values.Concat(state.PinnedGroups.Values)) order.RemoveAll(id=>id==node.Group.Id);
                }
            });
            dirty=false;
        }
        catch(Exception error) when(Expected(error) || error is OperationCanceledException) {StatusText.Text=error.Message;}
        finally {busy=false;IsEnabled=true;await ReloadAsync();}
    }
}
