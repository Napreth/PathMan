using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using Pathman.Core;
using Pathman.Core.Shims;
using Pathman.Core.EnvironmentVariables;
using Pathman.Gui.Controls;

namespace Pathman.Gui.Views;
public partial class AliasesView : UserControl
{
    private readonly PathmanLayout layout;
    private readonly EnvironmentWorkspaceStore store;
    private readonly ShimManager manager;
    private readonly ObservableCollection<AliasRow> rows=[];
    private bool updating,busy,enabled;
    private EditableRow? activeEditor;
    private readonly DispatcherTimer statePoll=new() {Interval=TimeSpan.FromSeconds(1)};
    public event EventHandler? SnapshotChanged;
    public bool IsBusy=>busy;
    public bool HasInlineEditor=>activeEditor?.IsEditing==true;
    public AliasesView(PathmanLayout layout,EnvironmentWorkspaceStore store)
    {
        this.layout=layout;this.store=store;manager=new(layout,ensureUserPath:false);InitializeComponent();Aliases.ItemsSource=rows;
        Loaded+=(_,_)=> {statePoll.Start();Refresh(this,new());};Unloaded+=(_,_)=>statePoll.Stop();
        statePoll.Tick+=(_,_)=> {
            if(busy) return;
            try
            {
                var actual=new AliasPathSetting(layout).IsEnabled(EnvironmentMonitor.ReadAll());
                if(actual==enabled) return;
                if(!actual) activeEditor?.CancelEdit();
                CheckEnabled();SnapshotChanged?.Invoke(this,EventArgs.Empty);
            }
            catch(Exception error) when(Expected(error)) {AliasStatus.Text=error.Message;}
        };
    }
    private static bool Expected(Exception error)=>error is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception or InvalidOperationException;
    private void UpdateActions()
    {
        var editable=enabled && !busy;var selected=Aliases.SelectedItem is AliasRow;
        NewAliasButton.IsEnabled=editable;RenameAliasButton.IsEnabled=EditAliasButton.IsEnabled=BrowseAliasButton.IsEnabled=CopyAliasButton.IsEnabled=DeleteAliasButton.IsEnabled=editable && selected;
        RefreshAliasesButton.IsEnabled=EnableAliases.IsEnabled=!busy;
        foreach(var row in rows) row.CanEdit=editable;
    }
    private bool CheckEnabled()
    {
        enabled=new AliasPathSetting(layout).IsEnabled(EnvironmentMonitor.ReadAll());
        updating=true;EnableAliases.IsChecked=enabled;updating=false;UpdateActions();
        if(!enabled) AliasStatus.Text="程序别名未启用。";
        return enabled;
    }
    private async Task<bool> CommitActive()
    {return activeEditor?.IsEditing!=true || await activeEditor.CommitAsync();}
    public async Task<bool> ConfirmLeaveAsync()
    {
        if(!await CommitActive()) return false;
        foreach(var draft in rows.Where(row=>row.IsDraft).ToArray()) rows.Remove(draft);
        return !busy;
    }
    private async void Refresh(object sender,RoutedEventArgs e)
    {
        if(busy || !await ConfirmLeaveAsync()) return;
        try
        {
            busy=true;UpdateActions();var selected=(Aliases.SelectedItem as AliasRow)?.Name;
            var aliases=await Task.Run(manager.List);rows.Clear();foreach(var alias in aliases) rows.Add(new(alias.Name,alias.Target));
            Aliases.SelectedItem=rows.FirstOrDefault(row=>row.Name.Equals(selected,StringComparison.OrdinalIgnoreCase));CheckEnabled();
        }
        catch(Exception error) when(Expected(error)) {AliasStatus.Text=error.Message;}
        finally {busy=false;UpdateActions();}
    }
    private async void ChangeEnabled(object sender,RoutedEventArgs e)
    {
        if(updating || busy) return;
        var requested=EnableAliases.IsChecked==true;
        if(!await CommitActive()) {updating=true;EnableAliases.IsChecked=enabled;updating=false;return;}
        busy=true;UpdateActions();
        try
        {
            var change=new AliasPathSetting(layout).Plan(requested,EnvironmentMonitor.ReadAll());
            if(change is not null) await EnvironmentEditHost.ApplyAsync(change,store,layout.DataDirectory);
            using var key=Registry.CurrentUser.CreateSubKey(@"Software\PathMan",true);
            if(requested) key.SetValue("ShimsPathOwned",1,RegistryValueKind.DWord);else key.DeleteValue("ShimsPathOwned",false);
            enabled=requested;updating=true;EnableAliases.IsChecked=requested;updating=false;AliasStatus.Text=requested ? "垫片目录已放到用户 Path 末尾，新终端生效。" : "已从用户 Path 移除垫片目录，新终端生效。";
            SnapshotChanged?.Invoke(this,EventArgs.Empty);
        }
        catch(Exception error) when(Expected(error)) {CheckEnabled();AliasStatus.Text=error.Message;}
        finally {busy=false;UpdateActions();}
    }
    private void SelectedAliasChanged(object sender,SelectionChangedEventArgs e)=>UpdateActions();
    private EditableRow? RowControl(AliasRow row)
    {
        Aliases.UpdateLayout();return Aliases.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem container ? FindVisual<EditableRow>(container) : null;
    }
    private static T? FindVisual<T>(DependencyObject root) where T:DependencyObject
    {
        if(root is T match) return match;
        for(var index=0;index<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);index++) if(FindVisual<T>(System.Windows.Media.VisualTreeHelper.GetChild(root,index)) is {} found) return found;
        return null;
    }
    private async void NewAlias(object sender,RoutedEventArgs e)
    {
        if(busy || !CheckEnabled() || !await ConfirmLeaveAsync()) return;
        var row=new AliasRow("","",true) {CanEdit=true};rows.Add(row);Aliases.SelectedItem=row;Aliases.ScrollIntoView(row);
        await Dispatcher.InvokeAsync(()=>RowControl(row)?.BeginEdit(),DispatcherPriority.Loaded);
    }
    private async void RenameAlias(object sender,RoutedEventArgs e)
    {if(!busy && CheckEnabled() && await CommitActive() && Aliases.SelectedItem is AliasRow row) RowControl(row)?.BeginEdit();}
    private async void EditAlias(object sender,RoutedEventArgs e)
    {if(!busy && CheckEnabled() && await CommitActive() && Aliases.SelectedItem is AliasRow row) RowControl(row)?.BeginEdit(detail:true);}
    private void EditAliasRow(object? sender,EventArgs e)
    {
        if(sender is EditableRow {DataContext:AliasRow row} && !busy && CheckEnabled()) {Aliases.SelectedItem=row;RowControl(row)?.BeginEdit(detail:true);}
    }
    private void AliasEditingChanged(object? sender,EventArgs e)
    {
        if(sender is not EditableRow control) return;
        if(control.IsEditing) activeEditor=control;
        else
        {
            if(activeEditor==control) activeEditor=null;
            if(control.DataContext is AliasRow {IsDraft:true,Name.Length:>0,Target.Length:0} row)
                Dispatcher.BeginInvoke(()=> {if(IsVisible && rows.Contains(row) && Aliases.SelectedItem==row && activeEditor is null && enabled) RowControl(row)?.BeginEdit(detail:true);},DispatcherPriority.Loaded);
        }
    }
    private void AliasEditCanceled(object? sender,EventArgs e)
    {if(sender is EditableRow {DataContext:AliasRow {IsDraft:true} row}) {rows.Remove(row);UpdateActions();}}
    private void AliasEditRequested(object? sender,InlineEditEventArgs e)
    {if(sender is EditableRow {DataContext:AliasRow row}) e.Completion=CommitRow(row,e.Value,e.IsDetail);}
    private async Task<string?> CommitRow(AliasRow row,string value,bool detail)
    {
        if(busy) return "别名正在保存，请稍后重试。";
        try
        {
            if(!CheckEnabled()) return "程序别名未启用，请先打开开关。";
            if(row.IsDraft && !detail && value.Length==0) {rows.Remove(row);return null;}
            if(!detail)
            {
                var name=ShimManager.ValidateName(value);
                if(rows.Any(other=>other!=row && other.Name.Equals(name,StringComparison.OrdinalIgnoreCase))) return "别名已经存在。";
                if(row.IsDraft) {row.Name=name;return null;}
                busy=true;UpdateActions();var alias=await Task.Run(()=>manager.Rename(row.Name,name,row.Target));row.Name=alias.Name;
            }
            else
            {
                var target=value.Trim().Trim('"');busy=true;UpdateActions();
                var alias=await Task.Run(()=>row.IsDraft ? manager.Add(row.Name,target) : manager.Update(row.Name,target,row.Target));
                row.Name=alias.Name;row.Target=alias.Target;row.IsDraft=false;
            }
            AliasStatus.Text="已保存。";SnapshotChanged?.Invoke(this,EventArgs.Empty);return null;
        }
        catch(Exception error) when(Expected(error)) {AliasStatus.Text=error.Message;return error.Message;}
        finally {busy=false;UpdateActions();}
    }
    private async void BrowseAlias(object sender,RoutedEventArgs e)
    {
        if(busy || !CheckEnabled() || !await CommitActive() || Aliases.SelectedItem is not AliasRow row) return;
        var dialog=new OpenFileDialog {Title="选择目标程序",Filter="可执行程序 (*.exe)|*.exe",CheckFileExists=true};
        if(dialog.ShowDialog(Window.GetWindow(this))!=true) return;
        var error=await CommitRow(row,dialog.FileName,true);if(error is not null) AliasStatus.Text=error;
    }
    private void CopyAlias(object sender,RoutedEventArgs e)=>Copy(false);
    private void CopyTarget(object sender,RoutedEventArgs e)=>Copy(true);
    private void Copy(bool target)
    {
        if(busy || !CheckEnabled() || Aliases.SelectedItem is not AliasRow row) return;
        try {Clipboard.SetDataObject(target ? row.Target : row.Name,true);AliasStatus.Text="已复制。";}
        catch(System.Runtime.InteropServices.ExternalException) {AliasStatus.Text="剪贴板暂时不可用。";}
    }
    private async void DeleteAlias(object sender,RoutedEventArgs e)
    {
        if(busy || !CheckEnabled() || !await CommitActive() || Aliases.SelectedItem is not AliasRow row) return;
        try {busy=true;UpdateActions();if(!row.IsDraft) await Task.Run(()=>manager.Remove(row.Name,row.Target));rows.Remove(row);AliasStatus.Text="已删除。";SnapshotChanged?.Invoke(this,EventArgs.Empty);}
        catch(Exception error) when(Expected(error)) {AliasStatus.Text=error.Message;}
        finally {busy=false;UpdateActions();}
    }
}
