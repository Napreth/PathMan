using System.Collections;
using System.Collections.ObjectModel;
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

namespace Pathman.Gui.Views;

public partial class VariablesView : UserControl
{
    private readonly PathmanLayout layout;
    private readonly EnvironmentWorkspaceStore store;
    private readonly ObservableCollection<EnvironmentEntry> entries = [];
    private IReadOnlyList<EnvironmentTreeNode> userGroups = [], systemGroups = [];
    private EnvironmentTreeNode? selected;
    private bool updating, dirty, busy, listMode;
    private string? preferredKey;
    public bool HasUnsavedChanges => dirty;
    public bool IsBusy => busy;
    public string LoadedRevision { get; private set; } = "";
    public VariablesView(PathmanLayout layout, EnvironmentWorkspaceStore store)
    {
        this.layout=layout; this.store=store; InitializeComponent(); ValuesList.ItemsSource=entries;
        entries.CollectionChanged += (_,_) => { if (!updating) SetDirty(); };
        Loaded += async (_,_) => { if (userGroups.Count == 0) await ReloadAsync(); };
    }
    private Window OwnerWindow => Window.GetWindow(this);
    private static bool Expected(Exception error) => error is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or System.Text.Json.JsonException or InvalidOperationException;
    private void Error(Exception error) { StatusText.Text=error.Message; MessageBox.Show(OwnerWindow,error.Message,"操作未完成",MessageBoxButton.OK,MessageBoxImage.Error); }
    private static Dictionary<string,string> RuntimeValues() => Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
        .Where(item => item.Key is string && item.Value is string).ToDictionary(item => (string)item.Key,item => (string)item.Value!,StringComparer.OrdinalIgnoreCase);
    public async Task ReloadAsync(bool external = false)
    {
        if (busy || external && dirty) return;
        busy=true; RefreshButton.IsEnabled=false; UserVariablesTree.IsEnabled=false; SystemVariablesTree.IsEnabled=false;
        var key=preferredKey ?? selected?.Key; preferredKey=null;
        var oldGroups=userGroups.Concat(systemGroups).ToArray();
        var expanded=oldGroups.Where(group => group.IsExpanded).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entryIndex=ValuesList.SelectedIndex;
        try
        {
            var result=await Task.Run(() => (Variables:EnvironmentMonitor.ReadAll(), State:store.Read()));
            var runtime=RuntimeValues();
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
        }
        catch(Exception error) when(Expected(error)) { Error(error); }
        finally { updating=false; busy=false; RefreshButton.IsEnabled=true; UserVariablesTree.IsEnabled=true; SystemVariablesTree.IsEnabled=true; UpdateActions(); }
    }
    public bool ConfirmLeave()
    {
        if (!dirty) return true;
        if(new ConfirmDialog(OwnerWindow,"未保存的更改","有未保存的更改，是否放弃？","放弃").ShowDialog()!=true) return false;
        ShowSelection(selected); return true;
    }
    private async void Refresh(object sender,RoutedEventArgs e) { if (ConfirmLeave()) await ReloadAsync(); }
    private void SelectVariable(object sender,RoutedPropertyChangedEventArgs<object> e)
    {
        if (updating || e.NewValue is not EnvironmentTreeNode node || node==selected) return;
        if (!ConfirmLeave())
        {
            updating=true; node.IsSelected=false; if (selected is not null) selected.IsSelected=true; updating=false; return;
        }
        ShowSelection(node);
    }
    private void ShowSelection(EnvironmentTreeNode? node)
    {
        updating=true;
        if (selected!=node && selected is not null) selected.IsSelected=false;
        selected=node; if(node is not null) node.IsSelected=true;
        VariableTitle.Text=node?.Title ?? "环境变量";
        SelectionActions.Visibility=node is null ? Visibility.Hidden : Visibility.Visible;
        var variable=node?.Variable;
        entries.Clear(); ScalarValueBox.Clear();
        listMode=variable?.IsList==true;
        EmptySelection.Visibility=variable is null ? Visibility.Visible : Visibility.Collapsed;
        ValueActions.Visibility=variable is null ? Visibility.Collapsed : Visibility.Visible;
        ValuesList.Visibility=variable is not null && listMode ? Visibility.Visible : Visibility.Collapsed;
        ScalarValueBox.Visibility=variable is not null && !listMode ? Visibility.Visible : Visibility.Collapsed;
        ListButtons.Visibility=listMode ? Visibility.Visible : Visibility.Collapsed;
        if(variable is not null) { if(listMode) foreach(var entry in variable.Entries) entries.Add(new(entry)); else ScalarValueBox.Text=variable.RawValue; }
        dirty=false; updating=false; UpdateActions();
    }
    private void SetDirty() { dirty=true; UpdateActions(); }
    private void ValueTextChanged(object sender,TextChangedEventArgs e) { if(!updating && selected?.Variable is not null) SetDirty(); }
    private void EntrySelected(object sender,SelectionChangedEventArgs e) => UpdateActions();
    private void UpdateActions()
    {
        if (SaveButton is null) return;
        SaveButton.IsEnabled=dirty && !busy; RevertButton.IsEnabled=dirty && !busy;
        EditEntryButton.IsEnabled=DeleteEntryButton.IsEnabled=ValuesList.SelectedIndex>=0 && !busy;
        MoveUpButton.IsEnabled=ValuesList.SelectedIndex>0 && !busy;
        MoveDownButton.IsEnabled=ValuesList.SelectedIndex>=0 && ValuesList.SelectedIndex<entries.Count-1 && !busy;
        RenameVariableButton.IsEnabled=selected is not null && !busy;
        DeleteVariableButton.IsEnabled=selected is not null && !busy && !(selected.IsGroup && selected.Group.Rule=="fallback");
        DeleteVariableButton.ToolTip=selected is {IsGroup:true} && selected.Group.Rule=="fallback" ? "保留默认分组以容纳未分类变量" : "删除";
    }
    private string CurrentValue => listMode ? string.Join(';',entries.Select(entry=>entry.Value)) : ScalarValueBox.Text;
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
        if(selected?.Variable is not {} variable) return;
        if(await ApplyAsync(new("set",variable.Scope,variable.Name,CurrentValue,ExpectedValue:variable.RawValue,ExpectedKind:variable.Kind)))
        { dirty=false; await ReloadAsync(); }
    }
    private void RevertValue(object sender,RoutedEventArgs e) => ShowSelection(selected);
    private string? Prompt(string title,string initial="",bool multiline=false,Func<string,string?>? validator=null)
    {
        var dialog=new TextDialog(OwnerWindow,title,multiline ? "文本" : "名称",initial,multiline,validator);
        return dialog.ShowDialog()==true ? dialog.Value : null;
    }
    private static string? GroupNameError(string value) => string.IsNullOrWhiteSpace(value) || value.Length>80 ? "分组名不能为空，最长 80 个字符。" : null;
    private void AddEntry(object sender,RoutedEventArgs e)
    {
        var value=Prompt("新建条目"); if(value is null) return; var index=ValuesList.SelectedIndex<0 ? entries.Count : ValuesList.SelectedIndex+1;
        entries.Insert(index,new(value)); ValuesList.SelectedIndex=index;
    }
    private void EditEntry(object sender,RoutedEventArgs e)
    {
        var index=ValuesList.SelectedIndex; if(index<0 || selected?.Variable is null) return;
        var value=Prompt("编辑条目",entries[index].Value); if(value is not null) { entries[index]=new(value); ValuesList.SelectedIndex=index; }
    }
    private void BrowseEntry(object sender,RoutedEventArgs e)
    {
        var dialog=new Microsoft.Win32.OpenFolderDialog { Title="选择目录" };
        if(dialog.ShowDialog(OwnerWindow)!=true) return;
        var index=ValuesList.SelectedIndex;
        if(index>=0) entries[index]=new(dialog.FolderName); else { entries.Add(new(dialog.FolderName)); ValuesList.SelectedIndex=entries.Count-1; }
    }
    private void RemoveEntry(object sender,RoutedEventArgs e)
    { var index=ValuesList.SelectedIndex; if(index<0) return; entries.RemoveAt(index); ValuesList.SelectedIndex=Math.Min(index,entries.Count-1); }
    private void MoveUp(object sender,RoutedEventArgs e) { var index=ValuesList.SelectedIndex; if(index>0) { entries.Move(index,index-1); ValuesList.SelectedIndex=index-1; } }
    private void MoveDown(object sender,RoutedEventArgs e) { var index=ValuesList.SelectedIndex; if(index>=0 && index<entries.Count-1) { entries.Move(index,index+1); ValuesList.SelectedIndex=index+1; } }
    private void EditText(object sender,RoutedEventArgs e)
    {
        if(selected?.Variable is null) return;
        var value=Prompt("编辑文本",CurrentValue,true); if(value is null) return;
        updating=true;
        listMode=selected.Variable.IsList || value.Contains(';');
        entries.Clear(); if(listMode) foreach(var item in value.Split(';',StringSplitOptions.None)) entries.Add(new(item)); else ScalarValueBox.Text=value;
        ValuesList.Visibility=ListButtons.Visibility=listMode ? Visibility.Visible : Visibility.Collapsed;
        ScalarValueBox.Visibility=listMode ? Visibility.Collapsed : Visibility.Visible;
        updating=false; SetDirty();
    }
    private void CopyValue(object sender,RoutedEventArgs e)
    {
        var value=listMode && ValuesList.SelectedIndex>=0 ? entries[ValuesList.SelectedIndex].Value : CurrentValue;
        try { Clipboard.SetDataObject(value,true); StatusText.Text="已复制。"; } catch(ExternalException) { StatusText.Text="剪贴板暂时不可用。"; }
    }
    private void OpenNewMenu(object sender,RoutedEventArgs e) => NewMenuPopup.IsOpen=true;
    private void ShowNewMenu(object sender,MouseEventArgs e) { if (IsEnabled) NewMenuPopup.IsOpen=true; }
    private async void CloseNewMenu(object sender,MouseEventArgs e) { await Task.Delay(180); if(!NewButton.IsMouseOver && !(NewMenuPopup.Child is FrameworkElement child && child.IsMouseOver)) NewMenuPopup.IsOpen=false; }
    private async void NewGroup(object sender,RoutedEventArgs e)
    {
        NewMenuPopup.IsOpen=false; if(!ConfirmLeave()) return;
        var name=Prompt("新建分组",validator:GroupNameError); if(name is null) return;
        var scope=selected?.Scope ?? VariableScope.User;
        var group=new VariableGroupDefinition { Name=name,Scope=scope };
        try { store.Update(state=>state.Groups.Add(group)); preferredKey=$"group:{scope}:{group.Id}"; dirty=false; await ReloadAsync(); }
        catch(Exception error) when(Expected(error)) { Error(error); }
    }
    private async void NewVariable(object sender,RoutedEventArgs e)
    {
        NewMenuPopup.IsOpen=false; if(!ConfirmLeave()) return;
        var group=selected?.Group; var dialog=new VariableDialog(OwnerWindow,selected?.Scope ?? VariableScope.User);
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
    private async void RenameSelection(object sender,RoutedEventArgs e)
    {
        if(selected is null || !ConfirmLeave()) return;
        var node=selected;
        var name=Prompt(node.IsGroup ? "重命名分组" : "重命名环境变量",node.Title,validator:node.IsGroup ? GroupNameError : value=>
        { try { WindowsEnvironmentVariableEditor.ValidateName(value); return null; } catch(ArgumentException error) { return error.Message; } });
        if(name is null || name==node.Title) return;
        if(!node.IsGroup && name.Equals(node.Title,StringComparison.OrdinalIgnoreCase)) { StatusText.Text="变量名不区分大小写，名称未变更。"; return; }
        try
        {
            if(node.IsGroup) store.Update(state=>state.Groups.First(group=>group.Id==node.Group.Id).Name=name);
            else
            {
                var variable=node.Variable!;
                if(!await ApplyAsync(new("rename",variable.Scope,variable.Name,NewName:name,ExpectedValue:variable.RawValue,ExpectedKind:variable.Kind))) return;
                preferredKey=$"variable:{variable.Scope}:{name}";
            }
            dirty=false; await ReloadAsync();
        }
        catch(Exception error) when(Expected(error)) { Error(error); }
    }
    private async void DeleteSelection(object sender,RoutedEventArgs e)
    {
        if(selected is null || !ConfirmLeave()) return;
        var node=selected;
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
