using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Pathman.Core;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;
using Pathman.Core.Versions;
using Pathman.Gui.Controls;
using Pathman.Gui.Dialogs;

namespace Pathman.Gui.Views;

public partial class VersionsView : UserControl
{
    private sealed class ProjectRow : INotifyPropertyChanged
    {
        public string Name {get;set;}="";
        public string VariableName {get;set;}="";
        public VariableScope Scope {get;set;}
        public bool IsDraft {get;set;}
        public bool CanEdit {get;set;}=true;
        public VersionProjectStatus? Status {get;set;}
        public string ScopeText=>Scope==VariableScope.User ? "用户" : "系统";
        public string BindingText=>VariableName.Length==0 ? "选择变量" : $"{VariableName}（{ScopeText}）";
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Refresh()=>PropertyChanged?.Invoke(this,new(""));
    }
    private sealed class VersionRow : INotifyPropertyChanged
    {
        public required ProjectRow Project {get;init;}
        public string Name {get;set;}="";
        public string Value {get;set;}="";
        public bool IsDraft {get;set;}
        public bool Current {get;set;}
        public bool CanEdit {get;set;}=true;
        public string CurrentLabel=>Current ? "当前" : "";
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Refresh()=>PropertyChanged?.Invoke(this,new(""));
    }
    private readonly PathmanLayout layout;
    private readonly EnvironmentWorkspaceStore store;
    private readonly ObservableCollection<ProjectRow> projects=[];
    private readonly ObservableCollection<VersionRow> versions=[];
    private bool busy,updating,browsing,refreshPending;
    private int autoSaveEpoch;
    private long reloadRequest;
    private EditableRow? activeEditor;
    private Window? owner;
    public bool IsBusy=>busy || browsing;
    public bool HasInlineEditor=>activeEditor?.IsEditing==true;
    public event EventHandler? SnapshotChanged;
    private ProjectRow? Selected=>ProjectsList.SelectedItem as ProjectRow;
    private VersionRow? SelectedVersion=>VersionsList.SelectedItem as VersionRow;
    public VersionsView(PathmanLayout layout,EnvironmentWorkspaceStore store)
    {
        this.layout=layout;this.store=store;InitializeComponent();ProjectsList.ItemsSource=projects;VersionsList.ItemsSource=versions;
        Loaded+=async(_,_)=>
        {
            owner=Window.GetWindow(this);owner.PreviewMouseUp+=SaveAfterClick;owner.LostKeyboardFocus+=SaveAfterFocus;owner.Deactivated+=SaveAfterDeactivation;
            await ReloadAsync();
        };
        Unloaded+=(_,_)=> {if(owner is not null) {owner.PreviewMouseUp-=SaveAfterClick;owner.LostKeyboardFocus-=SaveAfterFocus;owner.Deactivated-=SaveAfterDeactivation;owner=null;}};
        UpdateActions();
    }
    private static bool Expected(Exception error)=>error is IOException or ArgumentException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or System.Security.SecurityException;
    public async Task ReloadAsync()
    {
        if(IsBusy || HasInlineEditor) {refreshPending=true;return;}
        var request=++reloadRequest;
        try
        {
            var snapshot=await Task.Run(()=>VersionCatalog.Describe(store.Read(),EnvironmentMonitor.ReadAll()));
            if(IsBusy || HasInlineEditor || request!=reloadRequest) {refreshPending=true;return;}
            var name=Selected?.Name;var version=(VersionsList.SelectedItem as VersionRow)?.Name;
            updating=true;projects.Clear();
            foreach(var item in snapshot) projects.Add(new() {Name=item.Name,VariableName=item.VariableName,Scope=item.Type,Status=item});
            ProjectsList.SelectedItem=projects.FirstOrDefault(row=>row.Name.Equals(name,StringComparison.OrdinalIgnoreCase)) ?? projects.FirstOrDefault();
            ShowProject(version);refreshPending=false;
        }
        catch(Exception error) when(Expected(error)) {VersionStatus.Text=error.Message;}
        finally {updating=false;UpdateActions();}
    }
    private void ShowProject(string? selectVersion=null)
    {
        versions.Clear();var row=Selected;ProjectTitle.Text=string.IsNullOrEmpty(row?.Name) ? "版本" : row.Name;
        if(row?.Status is {} status)
        {
            foreach(var pair in status.Project.Versions) versions.Add(new() {Project=row!,Name=pair.Key,Value=pair.Value,Current=status.CurrentVersions.Contains(pair.Key,StringComparer.OrdinalIgnoreCase)});
            CurrentValue.Text="当前："+(status.Current is null ? "未定义" : status.Current.Replace("\r\n"," ↵ ").Replace("\n"," ↵ "))+(status.CurrentVersions.Count>0 ? $" ({string.Join(", ",status.CurrentVersions)})" : "");
            CurrentValue.ToolTip=status.Current;
        }
        else CurrentValue.Text="";
        VersionsList.SelectedItem=versions.FirstOrDefault(item=>item.Name.Equals(selectVersion,StringComparison.OrdinalIgnoreCase)) ?? versions.FirstOrDefault(item=>item.Current);
        UpdateActions();
    }
    private async void ProjectSelected(object sender,SelectionChangedEventArgs e)
    {
        if(updating) return;
        if(!await CommitActive())
        {
            if(activeEditor?.DataContext is ProjectRow previous) {updating=true;ProjectsList.SelectedItem=previous;updating=false;}
            return;
        }
        ShowProject();
    }
    private void UpdateActions()
    {
        if(NewProjectButton is null) return;
        NewProjectButton.IsEnabled=!IsBusy;AddVersionButton.IsEnabled=Selected is {IsDraft:false} && !IsBusy;
        ProjectsList.IsEnabled=VersionsList.IsEnabled=!busy;
        var allow=Selected is {VariableName.Length:>0} selected && VersionTargetPolicy.Restriction(selected.VariableName,store.Read().AllowBuiltInVersionTargets) is null;
        foreach(var row in projects) {row.CanEdit=!IsBusy;row.Refresh();}
        foreach(var row in versions) {row.CanEdit=!IsBusy;row.Refresh();}
        var selectedVersion=SelectedVersion;
        RenameVersionButton.IsEnabled=EditVersionButton.IsEnabled=BrowseVersionButton.IsEnabled=CopyVersionButton.IsEnabled=DeleteVersionButton.IsEnabled=selectedVersion is not null && !IsBusy;
        SwitchVersionButton.IsEnabled=allow && selectedVersion is {IsDraft:false} && !IsBusy;
    }
    private async void VersionSelected(object sender,SelectionChangedEventArgs e)
    {
        if(updating) return;
        if(activeEditor is {DataContext:VersionRow previous,IsEditing:true} && !await CommitActive())
        {updating=true;VersionsList.SelectedItem=previous;updating=false;}
        UpdateActions();
    }
    private Task<bool> CommitActive()=>activeEditor?.IsEditing==true ? activeEditor.CommitAsync() : Task.FromResult(true);
    public async Task<bool> ConfirmLeaveAsync()=>!IsBusy && await CommitActive() && !IsBusy;
    private static bool Defers(DependencyObject? source)=>EditableRow.FindParent<Button>(source)?.Tag as string=="defer";
    private void SaveAfterClick(object sender,MouseButtonEventArgs e)
    {if(e.ClickCount==1 && !Defers(e.OriginalSource as DependencyObject) && activeEditor is {IsEditing:true} editor && !editor.IsInputTarget(e.OriginalSource as DependencyObject)) QueueSave(editor);}
    private void SaveAfterFocus(object sender,KeyboardFocusChangedEventArgs e)
    {if(!Defers(e.NewFocus as DependencyObject) && Mouse.LeftButton==MouseButtonState.Released && activeEditor is {IsEditing:true} editor && editor.IsInputTarget(e.OldFocus as DependencyObject) && !editor.IsInputTarget(e.NewFocus as DependencyObject)) QueueSave(editor);}
    private void SaveAfterDeactivation(object? sender,EventArgs e) {if(activeEditor is {IsEditing:true} editor) QueueSave(editor);}
    private void QueueSave(EditableRow editor)
    {
        if(browsing) return;var version=editor.EditVersion;var epoch=autoSaveEpoch;
        Dispatcher.BeginInvoke(async()=> {if(!browsing && epoch==autoSaveEpoch && activeEditor==editor && editor.IsEditing && editor.EditVersion==version) await editor.CommitAsync();},DispatcherPriority.Background);
    }
    private async void EditRegion(object? sender,InlineEditRegionEventArgs e)
    {
        if(sender is not EditableRow row || IsBusy) return;
        if(activeEditor==row && row.IsEditing && row.IsEditingDetail==e.IsDetail) {row.FocusEditor();return;}
        if(!await CommitActive()) return;
        if(row.DataContext is ProjectRow project) ProjectsList.SelectedItem=project;
        else if(row.DataContext is VersionRow version) VersionsList.SelectedItem=version;
        row.BeginEdit(e.IsDetail);
    }
    private void EditingChanged(object? sender,EventArgs e)
    {
        if(sender is not EditableRow row) return;
        if(row.IsEditing) activeEditor=row;
        else if(activeEditor==row) {activeEditor=null;Dispatcher.BeginInvoke(async()=> {if(refreshPending && !HasInlineEditor && !IsBusy) await ReloadAsync();},DispatcherPriority.Background);}
        UpdateActions();
    }
    private void EditingCanceled(object? sender,EventArgs e)
    {
        if(sender is EditableRow {DataContext:ProjectRow {IsDraft:true} project}) projects.Remove(project);
        else if(sender is EditableRow {DataContext:VersionRow {IsDraft:true} version}) versions.Remove(version);
    }
    private void EditCell(object? sender,InlineEditEventArgs e)
    {
        if(sender is not EditableRow editor) return;
        if(!e.WasChanged && (editor.DataContext is ProjectRow {IsDraft:false} || editor.DataContext is VersionRow {IsDraft:false})) return;
        try
        {
            e.Completion=editor.DataContext switch
            {
                ProjectRow row=>SaveProjectName(row,e.Value),
                VersionRow row=>SaveVersion(row,e.IsDetail ? row.Name : e.Value,e.IsDetail ? e.Value : row.Value),
                _=>Task.FromResult<string?>(null)
            };
        }
        catch(Exception error) when(Expected(error)) {e.Completion=Task.FromResult<string?>(error.Message);}
    }
    private async Task<string?> Metadata(Action<EnvironmentWorkspace> change,Action applied)
    {
        if(busy) return "正在保存，请稍候。";busy=true;UpdateActions();
        try {await Task.Run(()=>store.Update(change));applied();refreshPending=true;VersionStatus.Text="";return null;}
        catch(Exception error) when(Expected(error)) {VersionStatus.Text=error.Message;return error.Message;}
        finally {busy=false;UpdateActions();}
    }
    private Task<string?> SaveProjectName(ProjectRow row,string value)
    {
        if(value.Length==0)
        {
            if(row.IsDraft) {projects.Remove(row);return Task.FromResult<string?>(null);}
            return Metadata(state=>VersionCatalog.Delete(state,row.Name),()=>projects.Remove(row));
        }
        var old=row.Name;var draft=row.IsDraft;
        return Metadata(state=> {if(draft) VersionCatalog.CreateUnbound(state,value);else VersionCatalog.Rename(state,old,value);},()=> {row.Name=value;row.IsDraft=false;row.Refresh();ProjectTitle.Text=value;});
    }
    private async Task<string?> SaveBinding(ProjectRow row,string variable,VariableScope scope)
    {
        try
        {
            if(row.IsDraft) return "请先填写项目名称。";
            if(variable.Length>0)
            {
                VersionTargetPolicy.EnsureAllowed(variable,store.Read());
                if(!EnvironmentMonitor.ReadAll().Any(item=>item.Scope==scope && item.Name.Equals(variable,StringComparison.OrdinalIgnoreCase))) return "该范围内不存在此变量，请浏览选择列出的变量。";
                if(VersionTargetPolicy.IsBuiltIn(variable) && (row.VariableName!=variable || row.Scope!=scope))
                {
                    browsing=true;autoSaveEpoch++;
                    try {if(new DangerConfirmDialog(Window.GetWindow(this),"选择内置系统变量",$"{variable} 是内置系统变量。切换其值可能导致 Windows 或应用无法正常运行。","仍然选择").ShowDialog()!=true) return "已取消选择。";}
                    finally {browsing=false;}
                }
            }
            var old=row.VariableName;var oldScope=row.Scope;
            return await Metadata(state=>VersionCatalog.Bind(state,row.Name,variable,scope,old,oldScope),()=> {row.VariableName=variable;row.Scope=scope;row.Refresh();});
        }
        catch(Exception error) when(Expected(error)) {return error.Message;}
    }
    private Task<string?> SaveVersion(VersionRow row,string name,string value)
    {
        var project=row.Project;
        if(name.Length==0)
        {
            if(row.IsDraft) {versions.Remove(row);return Task.FromResult<string?>(null);}
            return Metadata(state=>VersionCatalog.Delete(state,project.Name,row.Name),()=>versions.Remove(row));
        }
        var old=row.Name;var previous=row.Value;var draft=row.IsDraft;
        return Metadata(state=> {if(draft) VersionCatalog.Add(state,project.Name,name,value);else VersionCatalog.Edit(state,project.Name,old,name,value,previous);},()=> {row.Name=name;row.Value=value;row.IsDraft=false;row.Refresh();});
    }
    private async void NewProject(object sender,RoutedEventArgs e)
    {
        if(IsBusy || !await CommitActive()) return;
        var row=new ProjectRow {IsDraft=true};projects.Add(row);ProjectsList.SelectedItem=row;ProjectsList.ScrollIntoView(row);
        await Dispatcher.InvokeAsync(()=> {ProjectsList.UpdateLayout();FindCell(ProjectsList,row,"project")?.BeginEdit();},DispatcherPriority.Loaded);
    }
    private async void AddVersion(object sender,RoutedEventArgs e)
    {
        if(IsBusy || !await CommitActive() || Selected is not {IsDraft:false} project) return;
        var row=new VersionRow {Project=project,IsDraft=true};versions.Add(row);VersionsList.SelectedItem=row;VersionsList.ScrollIntoView(row);
        await Dispatcher.InvokeAsync(()=> {VersionsList.UpdateLayout();FindCell(VersionsList,row,"version")?.BeginEdit();},DispatcherPriority.Loaded);
    }
    private static EditableRow? FindCell(ListBox list,object row,string tag)
    {
        if(list.ItemContainerGenerator.ContainerFromItem(row) is not DependencyObject container) return null;
        EditableRow? Visit(DependencyObject item)
        {
            if(item is EditableRow cell && cell.Tag as string==tag) return cell;
            for(var index=0;index<System.Windows.Media.VisualTreeHelper.GetChildrenCount(item);index++) if(Visit(System.Windows.Media.VisualTreeHelper.GetChild(item,index)) is {} found) return found;
            return null;
        }
        return Visit(container);
    }
    private async void BindingMouseDown(object sender,MouseButtonEventArgs e)
    {
        if(e.ClickCount!=2 || IsBusy || sender is not FrameworkElement {DataContext:ProjectRow row}) return;
        e.Handled=true;
        autoSaveEpoch++;
        if(!await CommitActive()) return;
        if(!projects.Contains(row) || row.IsDraft) return;
        browsing=true;
        try
        {
            var dialog=new VersionTargetDialog(Window.GetWindow(this),store.Read());
            if(dialog.ShowDialog()!=true || dialog.Target is not {} target) {activeEditor?.FocusEditor();return;}
            activeEditor?.CancelEdit();
            var issue=await SaveBinding(row,target.Name,target.Scope);if(issue is not null) VersionStatus.Text=issue;
        }
        catch(Exception error) when(Expected(error)) {VersionStatus.Text=error.Message;}
        finally {browsing=false;UpdateActions();if(refreshPending && !HasInlineEditor) await ReloadAsync();}
    }
    private async void DeleteProject(object sender,RoutedEventArgs e)
    {
        if(IsBusy || sender is not FrameworkElement {DataContext:ProjectRow row}) return;autoSaveEpoch++;
        if(activeEditor?.DataContext==row) activeEditor.CancelEdit();else if(!await CommitActive()) return;
        if(row.IsDraft) {projects.Remove(row);return;}
        await Metadata(state=>VersionCatalog.Delete(state,row.Name),()=>projects.Remove(row));await ReloadAsync();
    }
    private async void DeleteVersion(object sender,RoutedEventArgs e)
    {
        if(IsBusy || SelectedVersion is not {} row) return;autoSaveEpoch++;
        if(activeEditor?.DataContext==row) activeEditor.CancelEdit();else if(!await CommitActive()) return;
        if(row.IsDraft) {versions.Remove(row);return;}
        await Metadata(state=>VersionCatalog.Delete(state,row.Project.Name,row.Name),()=>versions.Remove(row));await ReloadAsync();
    }
    private async void RenameVersion(object sender,RoutedEventArgs e)=>await EditSelectedVersion(false);
    private async void EditVersion(object sender,RoutedEventArgs e)=>await EditSelectedVersion(true);
    private async Task EditSelectedVersion(bool detail)
    {
        if(IsBusy || SelectedVersion is not {} row) return;
        if(activeEditor?.DataContext==row && activeEditor.IsEditing && activeEditor.IsEditingDetail==detail) {activeEditor.FocusEditor();return;}
        if(!await CommitActive() || !versions.Contains(row)) return;
        VersionsList.ScrollIntoView(row);VersionsList.UpdateLayout();FindCell(VersionsList,row,"version")?.BeginEdit(detail);
    }
    private async void BrowseVersion(object sender,RoutedEventArgs e)
    {
        if(IsBusy || SelectedVersion is not {} row) return;
        var editor=activeEditor;browsing=true;autoSaveEpoch++;UpdateActions();
        try
        {
            var dialog=new OpenFolderDialog {Title="选择版本目录",Multiselect=false};
            if(dialog.ShowDialog(Window.GetWindow(this))!=true) {editor?.FocusEditor();return;}
            if(editor is {IsEditingDetail:true} && editor.DataContext==row)
            {editor.EditingValue=dialog.FolderName;await editor.CommitAsync();}
            else if(await CommitActive() && versions.Contains(row))
            {var issue=await SaveVersion(row,row.Name,dialog.FolderName);if(issue is not null) VersionStatus.Text=issue;}
        }
        finally {autoSaveEpoch++;browsing=false;UpdateActions();if(refreshPending && !HasInlineEditor) await ReloadAsync();}
    }
    private async void CopyVersion(object sender,RoutedEventArgs e)
    {
        if(IsBusy || !await CommitActive() || SelectedVersion is not {} row) return;
        try {Clipboard.SetDataObject(row.Value,true);VersionStatus.Text="已复制变量值。";}
        catch(System.Runtime.InteropServices.ExternalException) {VersionStatus.Text="剪贴板暂时不可用。";}
    }
    private async void SwitchVersion(object sender,RoutedEventArgs e)
    {
        if(IsBusy || !await CommitActive() || SelectedVersion is not {IsDraft:false} row) return;
        var project=row.Project;
        busy=true;UpdateActions();
        try
        {
            var mutation=await Task.Run(()=>VersionCatalog.PrepareSwitch(store.Read(),project.Name,row.Name,EnvironmentMonitor.ReadAll()));
            await EnvironmentEditHost.ApplyAsync(mutation,store,layout.DataDirectory);VersionStatus.Text=$"已切换 {project.Name} → {row.Name}。";SnapshotChanged?.Invoke(this,EventArgs.Empty);
        }
        catch(OperationCanceledException) {VersionStatus.Text="已取消切换，原值保留。";}
        catch(Exception error) when(Expected(error)) {VersionStatus.Text=error.Message;}
        finally {busy=false;await ReloadAsync();}
    }
}
