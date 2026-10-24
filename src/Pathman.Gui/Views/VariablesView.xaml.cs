using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Pathman.Core;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;
using Pathman.Gui.Dialogs;
using Pathman.Gui.Controls;

namespace Pathman.Gui.Views;

public partial class VariablesView : UserControl
{
    private readonly PathmanLayout layout;
    private readonly EnvironmentWorkspaceStore store;
    private readonly ObservableCollection<EnvironmentEntry> entries = [];
    private PathEditingSession? editingSession;
    private readonly HashSet<string> expandedPathRows=[];
    public event EventHandler? SnapshotChanged;
    private IReadOnlyList<EnvironmentTreeNode> userGroups = [], systemGroups = [];
    private IReadOnlyList<EnvironmentVariableInfo> allVariables = [];
    private EnvironmentTreeNode? selected;
    private bool updating, dirty, busy, listMode;
    private string? preferredKey;
    private EditableRow? activeEditor;
    public bool HasUnsavedChanges => dirty || activeEditor?.IsEditing == true;
    public bool HasInlineEditor => activeEditor?.IsEditing == true;
    public bool IsBusy => busy || dragging || pathDragging || activeEditor?.IsEditing == true;
    public string LoadedRevision { get; private set; } = "";
    public VariablesView(PathmanLayout layout, EnvironmentWorkspaceStore store)
    {
        this.layout=layout; this.store=store; InitializeComponent(); ValuesList.ItemsSource=entries;
        Loaded += async (_,_) => { if (userGroups.Count == 0) await ReloadAsync(); };
    }
    private Window OwnerWindow => Window.GetWindow(this);
    private static bool Expected(Exception error) => error is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or System.Text.Json.JsonException or InvalidOperationException;
    private void Error(Exception error) { StatusText.Text=error.Message; MessageBox.Show(OwnerWindow,error.Message,"操作未完成",MessageBoxButton.OK,MessageBoxImage.Error); }
    private static Dictionary<string,string> RuntimeValues() => Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
        .Where(item => item.Key is string && item.Value is string).ToDictionary(item => (string)item.Key,item => (string)item.Value!,StringComparer.OrdinalIgnoreCase);
    public async Task ReloadAsync(bool external = false)
    {
        if (busy || external && (HasUnsavedChanges || dragging || pathDragging)) return;
        busy=true; RefreshButton.IsEnabled=CleanupButton.IsEnabled=false; UserVariablesTree.IsEnabled=false; SystemVariablesTree.IsEnabled=false;
        var key=preferredKey ?? selected?.Key; preferredKey=null;
        var oldGroups=userGroups.Concat(systemGroups).ToArray();
        var expanded=oldGroups.Where(group => group.IsExpanded).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entryIndex=ValuesList.SelectedIndex;
        try
        {
            var result=await Task.Run(() => (Variables:EnvironmentMonitor.ReadAll(), State:store.Read()));
            var runtime=RuntimeValues();
            allVariables=result.Variables;
            updating=true;
            if (selected is not null) selected.IsSelected=false;
            selected=null;
            userGroups=EnvironmentTreeNode.Build(VariableScope.User,result.Variables,result.State,runtime);
            systemGroups=EnvironmentTreeNode.Build(VariableScope.Machine,result.Variables,result.State,runtime);
            foreach(var group in userGroups.Concat(systemGroups)) group.IsExpanded=oldGroups.Length==0 ? !group.Group.Collapsed : expanded.Contains(group.Key);
            UserVariablesTree.ItemsSource=userGroups; SystemVariablesTree.ItemsSource=systemGroups;
            updating=false;
            var all=userGroups.Concat(systemGroups).SelectMany(group => new[] {group}.Concat(group.Children)).ToArray();
            var next=all.FirstOrDefault(node => string.Equals(node.Key,key,StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(node => node.Variable?.Name.Equals("PATH",StringComparison.OrdinalIgnoreCase)==true)
                ?? all.FirstOrDefault();
            if (next?.Variable is not null)
                foreach(var group in userGroups.Concat(systemGroups).Where(group => group.Children.Contains(next))) group.IsExpanded=true;
            ShowSelection(next);
            if (next?.Key.Equals(key,StringComparison.OrdinalIgnoreCase)==true && entryIndex>=0 && entryIndex<entries.Count) ValuesList.SelectedIndex=entryIndex;
            LoadedRevision=result.State.Revision;
            SnapshotChanged?.Invoke(this,EventArgs.Empty);
        }
        catch(Exception error) when(Expected(error)) { Error(error); }
        finally { updating=false; busy=false; RefreshButton.IsEnabled=CleanupButton.IsEnabled=true; UserVariablesTree.IsEnabled=true; SystemVariablesTree.IsEnabled=true; UpdateActions(); }
    }
    public bool ConfirmLeave()
    {
        if (activeEditor?.IsEditing == true) { _ = activeEditor.CommitAsync(); return false; }
        if (!dirty) return true;
        if(new ConfirmDialog(OwnerWindow,"未保存的更改","有未保存的更改，是否放弃？","放弃").ShowDialog()!=true) return false;
        ShowSelection(selected); return true;
    }
    public async Task<bool> ConfirmLeaveAsync()
    {
        if(activeEditor?.IsEditing==true && !await activeEditor.CommitAsync()) return false;
        return ConfirmLeave();
    }
    private async void Refresh(object sender,RoutedEventArgs e)
    { if (CleanupPanel.Visibility==Visibility.Visible) PreviewCleanup(sender,e); else if (await ConfirmLeaveAsync()) await ReloadAsync(); }
    private async void SelectVariable(object sender,RoutedPropertyChangedEventArgs<object> e)
    {
        if (updating || e.NewValue is not EnvironmentTreeNode node || node==selected) return;
        var key=node.Key;
        if (!await ConfirmLeaveAsync())
        {
            updating=true; node.IsSelected=false; if (selected is not null) selected.IsSelected=true; updating=false; return;
        }
        var current=userGroups.Concat(systemGroups).SelectMany(group=>new[]{group}.Concat(group.Children)).FirstOrDefault(item=>item.Key==key);
        ShowSelection(current ?? node);
    }
    private void ShowSelection(EnvironmentTreeNode? node)
    {
        updating=true;
        if (selected!=node && selected is not null) selected.IsSelected=false;
        selected=node; if(node is not null) node.IsSelected=true;
        VariableTitle.Text=node?.Title ?? "环境变量";
        var variable=node?.Variable is {} original ? allVariables.FirstOrDefault(item=>item.Scope==original.Scope && item.Name==original.Name) ?? original : null;
        var restoreTargets=editingSession is {} previous && variable is not null && previous.Root.Scope==variable.Scope && previous.Root.Name.Equals(variable.Name,StringComparison.OrdinalIgnoreCase)
            ? entries.Where(entry=>entry.IsExpanded && entry.Row?.TargetScope is not null).Select(entry=>$"{entry.Row!.TargetScope}:{entry.Row.TargetName}").ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
        editingSession=variable is null ? null : new PathEditingSession(variable,allVariables,RuntimeValues());
        expandedPathRows.Clear();
        if(editingSession is {} fresh && restoreTargets.Count>0)
            for(var depth=0;depth<33;depth++)
            {
                var added=false;
                foreach(var row in fresh.VisibleRows(expandedPathRows).Where(row=>row.IsGroup && !row.IsCycle && restoreTargets.Contains($"{row.TargetScope}:{row.TargetName}")))
                    added|=expandedPathRows.Add(row.ViewKey);
                if(!added) break;
            }
        entries.Clear(); ScalarValueBox.Clear();
        listMode=variable is not null && (variable.IsList || EnvironmentVariableClassifier.IsPathVariable(variable,allVariables,RuntimeValues()));
        EmptySelection.Visibility=node is null ? Visibility.Visible : Visibility.Collapsed;
        GroupVariablesList.ItemsSource=node?.IsGroup == true ? node.Children : null;
        GroupVariablesList.Visibility=node?.IsGroup == true ? Visibility.Visible : Visibility.Collapsed;
        ValueActions.Visibility=variable is null ? Visibility.Collapsed : Visibility.Visible;
        if(variable is not null) { if(listMode) RefreshPathEntries(); else ScalarValueBox.Text=variable.RawValue; }
        UpdateValuePresentation();
        dirty=false; updating=false; UpdateActions();
    }
    private void SetDirty() { dirty=editingSession?.HasChanges==true; UpdateActions(); }
    private void ValueTextChanged(object sender,TextChangedEventArgs e) { if(!updating && editingSession is {} session) {session.SetRaw(session.Root.Scope,session.Root.Name,ScalarValueBox.Text); UpdateValuePresentation(); SetDirty();} }
    private void EntrySelected(object sender,SelectionChangedEventArgs e) => UpdateActions();
    private async void OpenGroupVariable(object? sender, EventArgs e)
    {
        if (busy || sender is not EditableRow { DataContext: EnvironmentTreeNode node } || node.IsGroup || !await ConfirmLeaveAsync()) return;
        var groups=node.Scope==VariableScope.User ? userGroups : systemGroups;
        foreach(var group in groups.Where(group=>group.Children.Contains(node))) group.IsExpanded=true;
        ShowSelection(node);
        var tree=node.Scope==VariableScope.User ? UserVariablesTree : SystemVariablesTree;
        await Dispatcher.InvokeAsync(()=> { tree.UpdateLayout(); Container(tree,node)?.BringIntoView(); },DispatcherPriority.Loaded);
    }
    private void UpdateActions()
    {
        if (SaveButton is null) return;
        SaveButton.IsEnabled=(dirty || HasInlineEditor) && !busy; RevertButton.IsEnabled=dirty && !busy;
        EditEntryButton.IsEnabled=ValuesList.SelectedIndex>=0 && !busy;
        DeleteEntryButton.IsEnabled=ValuesList.SelectedIndex>=0 && !busy;
        var current=SelectedPathItem();
        MoveUpButton.IsEnabled=current is {} up && up.Index>0 && !busy;
        MoveDownButton.IsEnabled=current is {} down && down.Index<down.Count-1 && !busy;
        foreach (var toolbar in new[] { UserActions, SystemActions })
        {
            toolbar.IsEnabled = !busy;
        }
    }
    private string CurrentValue => editingSession is {} session ? session.Raw(session.Root.Scope,session.Root.Name) : "";
    private void UpdateValuePresentation()
    {
        var variable = selected?.Variable;
        ValuesList.Visibility=variable is not null && listMode ? Visibility.Visible : Visibility.Collapsed;
        ScalarValueBox.Visibility=variable is not null && !listMode ? Visibility.Visible : Visibility.Collapsed;
        ListButtons.Visibility=DeleteEntryButton.Visibility=listMode ? Visibility.Visible : Visibility.Collapsed;
        ToggleModeButton.Visibility=variable is not null ? Visibility.Visible : Visibility.Collapsed;
        ToggleModeButton.Content=listMode ? "文本模式" : "多行模式";
        ToggleModeButton.ToolTip=listMode ? "直接编辑以分号分隔的原始文本" : "按分号拆分为多行编辑";
    }
    private async Task<bool> ApplyAsync(EnvironmentMutation change)
    {
        if (busy) return false;
        busy=true; IsEnabled=false;
        try { await EnvironmentEditHost.ApplyAsync(change,store,layout.DataDirectory); StatusText.Text="已保存。"; return true; }
        catch(OperationCanceledException error) { StatusText.Text=error.Message; return false; }
        catch(Exception error) when(Expected(error)) { Error(error); return false; }
        finally { busy=false; IsEnabled=true; UpdateActions(); }
    }
    private async void SaveValue(object sender,RoutedEventArgs e)
    {
        if (activeEditor?.IsEditing == true && !await activeEditor.CommitAsync()) return;
        await SavePathSession();
    }
    private async void RevertValue(object sender,RoutedEventArgs e) {dirty=false; await ReloadAsync();}
    private string? Prompt(string title,string initial="",bool multiline=false,Func<string,string?>? validator=null)
    {
        var dialog=new TextDialog(OwnerWindow,title,multiline ? "文本" : "名称",initial,multiline,validator);
        return dialog.ShowDialog()==true ? dialog.Value : null;
    }
    private static string? GroupNameError(string value) => string.IsNullOrWhiteSpace(value) || value.Length>80 ? "分组名不能为空，最长 80 个字符。" : null;
    private async void AddEntry(object sender,RoutedEventArgs e)
    {
        if (activeEditor?.IsEditing == true && !await activeEditor.CommitAsync()) return;
        if(editingSession is not {} session) return;
        var owner=NewPathOwner();
        var item=session.Append(owner.Scope,owner.Name,draft:true); RefreshPathEntries(item.Id);
        var entry=entries.First(itemEntry=>itemEntry.ItemId==item.Id);
        ValuesList.ScrollIntoView(entry);
        await Dispatcher.InvokeAsync(() =>
        {
            ValuesList.UpdateLayout();
            if (ValuesList.ItemContainerGenerator.ContainerFromItem(entry) is ListBoxItem item) FindVisual<EditableRow>(item)?.BeginEdit();
        }, DispatcherPriority.Loaded);
    }
    private void EditEntry(object sender,RoutedEventArgs e)
    {
        if(ValuesList.SelectedItem is not EnvironmentEntry entry) return;
        ValuesList.ScrollIntoView(entry); ValuesList.UpdateLayout();
        if (ValuesList.ItemContainerGenerator.ContainerFromItem(entry) is ListBoxItem item) FindVisual<EditableRow>(item)?.BeginEdit();
    }
    private void BrowseEntry(object sender,RoutedEventArgs e)
    {
        var dialog=new Microsoft.Win32.OpenFolderDialog { Title="选择目录" };
        if(dialog.ShowDialog(OwnerWindow)!=true) return;
        if(editingSession is not {} session) return;
        if(ValuesList.SelectedItem is EnvironmentEntry entry) {session.Update(entry.ItemId,dialog.FolderName); RefreshPathEntries(entry.ItemId);}
        else {var root=session.Root; var item=session.Append(root.Scope,root.Name,dialog.FolderName); RefreshPathEntries(item.Id);}
        SetDirty();
    }
    private void MoveUp(object sender,RoutedEventArgs e)=>MovePathSibling(-1);
    private void MoveDown(object sender,RoutedEventArgs e)=>MovePathSibling(1);
    private async void ToggleValueMode(object sender,RoutedEventArgs e)
    {
        if (activeEditor?.IsEditing == true && !await activeEditor.CommitAsync()) return;
        if(selected?.Variable is null) return;
        var value=CurrentValue;
        updating=true;
        listMode=!listMode;
        if(listMode) RefreshPathEntries(); else ScalarValueBox.Text=value;
        UpdateValuePresentation(); updating=false; UpdateActions();
    }
    private void CopyValue(object sender,RoutedEventArgs e)
    {
        var value=listMode && ValuesList.SelectedIndex>=0 ? entries[ValuesList.SelectedIndex].Value : CurrentValue;
        try { Clipboard.SetDataObject(value,true); StatusText.Text="已复制。"; } catch(ExternalException) { StatusText.Text="剪贴板暂时不可用。"; }
    }
    private async void ScopeActionRequested(object? sender, ScopeActionEventArgs e)
    {
        if (busy) return;
        switch (e.Action)
        {
            case ScopeAction.NewGroup: await NewGroup(e.Scope); break;
            case ScopeAction.NewVariable: await NewVariable(e.Scope); break;
        }
    }
    private async Task NewGroup(VariableScope scope)
    {
        if(!await ConfirmLeaveAsync()) return;
        var name=Prompt("新建分组",validator:GroupNameError); if(name is null) return;
        var group=new VariableGroupDefinition { Name=name,Scope=scope };
        try { store.Update(state=>state.Groups.Add(group)); preferredKey=$"group:{scope}:{group.Id}"; dirty=false; await ReloadAsync(); }
        catch(Exception error) when(Expected(error)) { Error(error); }
    }
    private async Task NewVariable(VariableScope scope)
    {
        if(!await ConfirmLeaveAsync()) return;
        var group=selected?.Scope == scope ? selected.Group : null;
        var dialog=new VariableDialog(OwnerWindow,scope);
        if(dialog.ShowDialog()!=true) return;
        if(await ApplyAsync(new("create",dialog.Scope,dialog.VariableName,dialog.RawValue)))
        {
            try {
            if(group is not null && (group.Scope is null || group.Scope==dialog.Scope))
                store.Update(state=>state.Assignments[EnvironmentWorkspace.Key(dialog.Scope,dialog.VariableName)]=group.Id);
            preferredKey=$"variable:{dialog.Scope}:{dialog.VariableName}"; dirty=false; await ReloadAsync();
            } catch(Exception error) when(Expected(error)) { Error(error); }
        }
    }
    private void RowEditingChanged(object? sender, EventArgs e)
    {
        if (sender is not EditableRow row) return;
        if (row.IsEditing) activeEditor = row;
        else if (activeEditor == row) activeEditor = null;
        UpdateActions();
    }
    private void RowEditRequested(object? sender, InlineEditEventArgs e)
    { if (sender is EditableRow { DataContext: EnvironmentTreeNode node }) e.Completion=RenameNode(node,e.Value); }
    private void EntryEditRequested(object? sender, InlineEditEventArgs e)
    {
        if (sender is not EditableRow { DataContext: EnvironmentEntry entry } || selected?.Variable is null) return;
        if (e.Value.Contains(';') || e.Value.Contains('\0') || e.Value.Contains('\r') || e.Value.Contains('\n'))
        { e.Completion=Task.FromResult<string?>("单个条目不能包含分号、空字符或换行；多个条目请切换到文本模式。"); return; }
        if(editingSession is not {} session) return;
        session.Update(entry.ItemId,e.Value); RefreshPathEntries(entry.ItemId); SetDirty();
    }
    private void EntryEditCanceled(object? sender, EventArgs e)
    {
        if (sender is EditableRow { DataContext: EnvironmentEntry { IsDraft: true } entry } && editingSession is {} session) {session.Remove(entry.ItemId); RefreshPathEntries();}
    }
    private void DeleteEntry(object sender, RoutedEventArgs e)
    {
        if (ValuesList.SelectedItem is not EnvironmentEntry entry) return;
        if(editingSession is not {} session) return;
        var index=ValuesList.SelectedIndex; session.Remove(entry.ItemId); RefreshPathEntries(); ValuesList.SelectedIndex=Math.Min(index,entries.Count-1); SetDirty();
    }
    private async void RowDeleteRequested(object? sender, EventArgs e)
    { if(sender is EditableRow { DataContext: EnvironmentTreeNode node } && node.CanDelete) await DeleteNode(node); }
    private async void RowPinRequested(object? sender, EventArgs e)
    {
        if (busy || sender is not EditableRow { DataContext: EnvironmentTreeNode node } || !await ConfirmLeaveAsync()) return;
        try
        {
            store.Update(state => { if(node.IsGroup) state.PinGroup(node.Scope,node.Group.Id,!node.IsPinned); else state.PinVariable(node.Scope,node.Title,!node.IsPinned); });
            await ReloadAsync();
        }
        catch(Exception error) when(Expected(error)) { Error(error); }
    }
    private async Task<string?> RenameNode(EnvironmentTreeNode node, string name)
    {
        if(busy) return "请稍后重试。";
        if(name==node.Title) return null;
        if(node.IsGroup && GroupNameError(name) is {} groupError) return groupError;
        try
        {
            if(!node.IsGroup) WindowsEnvironmentVariableEditor.ValidateName(name);
            if(!node.IsGroup && name.Equals(node.Title,StringComparison.OrdinalIgnoreCase)) return null;
            if(node.IsGroup) store.Update(state=>state.Groups.First(group=>group.Id==node.Group.Id).Name=name);
            else
            {
                var variable=node.Variable!;
                // Keep a staged value edit with the renamed variable rather than losing it on reload.
                busy=true; IsEnabled=false;
                try { await EnvironmentEditHost.ApplyAsync(new("rename",variable.Scope,variable.Name,NewName:name,ExpectedValue:variable.RawValue,ExpectedKind:variable.Kind),store,layout.DataDirectory); }
                finally { busy=false; IsEnabled=true; }
                if(selected==node) preferredKey=$"variable:{variable.Scope}:{name}";
            }
            var staged=editingSession?.Mutations() ?? [];
            await ReloadAsync();
            if(staged.Count>0 && editingSession is {} session)
            {
                updating=true;
                foreach(var change in staged)
                {
                    var stagedName=node.Variable is {} renamed && change.Scope==renamed.Scope && change.Name==renamed.Name ? name : change.Name;
                    if(session.Originals.Any(item=>item.Scope==change.Scope && item.Name==stagedName)) session.SetRaw(change.Scope,stagedName,change.Value!);
                }
                if(listMode) RefreshPathEntries(); else ScalarValueBox.Text=CurrentValue;
                updating=false; SetDirty();
            }
            return null;
        }
        catch(Exception error) when(Expected(error) || error is OperationCanceledException) { StatusText.Text=error.Message; return error.Message; }
    }
    private async Task DeleteNode(EnvironmentTreeNode node)
    {
        if(busy || !node.CanDelete || !await ConfirmLeaveAsync()) return;
        var message=node.IsGroup ? $"删除分组“{node.Title}”？组内环境变量将移到默认分组。" : $"删除环境变量“{node.Title}”？";
        if(new ConfirmDialog(OwnerWindow,"删除",message,"删除").ShowDialog()!=true) return;
        try
        {
            if(node.IsGroup) store.Update(state=>
            {
                var fallback=state.VisibleGroups(node.Scope).First(group=>group.Rule=="fallback").Id;
                foreach(var child in node.Children) state.Assignments[EnvironmentWorkspace.Key(node.Scope,child.Title)]=fallback;
                foreach(var key in state.Assignments.Where(item=>item.Value==node.Group.Id).Select(item=>item.Key).ToArray()) state.Assignments[key]=fallback;
                state.Groups.RemoveAll(group=>group.Id==node.Group.Id);
                foreach(var order in state.GroupOrder.Values.Concat(state.PinnedGroups.Values)) order.RemoveAll(id=>id==node.Group.Id);
            });
            else
            {
                var variable=node.Variable!;
                if(!await ApplyAsync(new("delete",variable.Scope,variable.Name,ExpectedValue:variable.RawValue,ExpectedKind:variable.Kind))) return;
            }
            dirty=false; await ReloadAsync();
        }
        catch(Exception error) when(Expected(error)) { Error(error); }
    }
}
