using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Pathman.Core;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;
namespace Pathman.Gui.Views;
public partial class MonitorView : UserControl
{
    private readonly PathmanLayout layout;
    private readonly EnvironmentWorkspaceStore store;
    private readonly DispatcherTimer statusTimer=new() {Interval=TimeSpan.FromSeconds(1)};
    private bool updating,busy;
    public event EventHandler? PendingApplyRequested;
    public event EventHandler? OptionsChanged;
    public MonitorView(PathmanLayout layout,EnvironmentWorkspaceStore store)
    {
        this.layout=layout;this.store=store;InitializeComponent();Update(store.Read());
        statusTimer.Tick+=(_,_)=> {if(!busy) RefreshProcessState();};
        Loaded+=(_,_)=> {RefreshProcessState();statusTimer.Start();};Unloaded+=(_,_)=>statusTimer.Stop();
    }
    private void RefreshProcessState()
    {
        updating=true;
        try
        {
            var state=store.Read();var startup=new StartupManager(layout);startup.Reconcile();
            EnableMonitoring.IsChecked=state.MonitoringEnabled;StartupEnabled.IsChecked=startup.IsEnabled;
            StartupEnabled.IsEnabled=TrackOrigins.IsEnabled=MonitoringRules.IsEnabled=state.MonitoringEnabled;
            UserEditorExclusions.IsEnabled=MachineEditorExclusions.IsEnabled=state.TrackChangeOrigins;
        }
        finally {updating=false;}
    }
    public void Update(EnvironmentWorkspace state)
    {
        if(busy) return;
        updating=true;
        GroupExternalUser.IsChecked=state.GroupExternalUserVariables;GroupExternalMachine.IsChecked=state.GroupExternalMachineVariables;
        AutoArrangeUser.IsChecked=state.AutoArrangeUserPath;AutoArrangeMachine.IsChecked=state.AutoArrangeMachinePath;
        RemoveUserDuplicates.IsChecked=state.RemoveUserPathDuplicates;RemoveMachineDuplicates.IsChecked=state.RemoveMachinePathDuplicates;
        MoveUserEntries.IsChecked=state.MoveExternalUserPathEntries;MoveMachineEntries.IsChecked=state.MoveExternalMachinePathEntries;
        TrackOrigins.IsChecked=state.TrackChangeOrigins;
        IgnoreSystemEditorUser.IsChecked=state.IgnoreSystemEditorUserChanges;IgnoreSystemEditorMachine.IsChecked=state.IgnoreSystemEditorMachineChanges;
        IgnorePowerToysUser.IsChecked=state.IgnorePowerToysUserChanges;IgnorePowerToysMachine.IsChecked=state.IgnorePowerToysMachineChanges;
        UserArrangeOptions.IsEnabled=state.AutoArrangeUserPath;MachineArrangeOptions.IsEnabled=state.AutoArrangeMachinePath;
        updating=false;RefreshProcessState();
        var pending=state.MonitoringEnabled && state.AutoArrangeMachinePath && state.PendingPathOrganizations.Any(item=>item.Scope==VariableScope.Machine);
        var message=state.OriginTrackingMessage ?? (pending ? L.Get("系统 PATH 有待整理的外部新增项，需要管理员授权。") : state.MonitorMessage);
        MonitorStatus.Text=message ?? "";MonitorNotice.Visibility=string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        ApplyPending.Visibility=pending ? Visibility.Visible : Visibility.Collapsed;
        ApplyPending.IsEnabled=!EnvironmentMonitor.RequiresOrigin(state,VariableScope.Machine) || state.OriginTrackingReady;
        var changes=state.Changes.Where(change=>change.IsExternal).Select(change=>new {
            Heading=$"{change.Time:yyyy-MM-dd HH:mm:ss}  ·  {(change.Scope==VariableScope.User ? L.Get("用户") : L.Get("系统"))} · {change.Name}  ·  {L.Get(change.Action switch {"create"=>"新增","delete"=>"删除",_=>"修改"})}",
            ProcessText=L.Get("修改进程")+"："+(change.Origins.Count==0 ? L.Get("未识别") : string.Join("、",change.Origins.Select(origin=>$"{origin.ProcessName} ({origin.ProcessId})").Distinct())),
            change.Before,change.After,
            Diff=EnvironmentValueDiff.Create(change.Before,change.After ?? "").Where(line=>line.Kind!=ValueDiffKind.Context)
                .Select(line=>new {IsAdded=line.Kind==ValueDiffKind.Added,Text=(line.Kind==ValueDiffKind.Added ? "+ " : "− ")+line.Text}).ToArray()
        }).ToArray();
        Changes.ItemsSource=changes;NoChanges.Visibility=changes.Length==0 ? Visibility.Visible : Visibility.Collapsed;
        Changes.Visibility=changes.Length==0 ? Visibility.Collapsed : Visibility.Visible;
    }
    private void OpenHistory(object sender,RoutedEventArgs e) {SettingsPage.Visibility=Visibility.Collapsed;HistoryPage.Visibility=Visibility.Visible;}
    private void CloseHistory(object sender,RoutedEventArgs e) {HistoryPage.Visibility=Visibility.Collapsed;SettingsPage.Visibility=Visibility.Visible;}
    private void ShowError(Exception error) {MonitorStatus.Text=error.Message;MonitorNotice.Visibility=Visibility.Visible;}
    private async void ChangeStartup(object sender,RoutedEventArgs e)
    {
        if(updating || busy) return;
        var enabled=StartupEnabled.IsChecked==true;busy=true;SettingsPanel.IsEnabled=false;
        try
        {
            await Task.Run(()=> {new StartupManager(layout).SetEnabled(enabled);var process=new BackgroundProcess(layout);if(enabled) process.Start();else process.Stop();});
            busy=false;Update(store.Read());
        }
        catch(Exception error) when(Expected(error)) {RefreshProcessState();ShowError(error);}
        finally {busy=false;SettingsPanel.IsEnabled=true;}
    }
    private async void ChangeMonitoring(object sender,RoutedEventArgs e)
    {
        if(updating || busy) return;
        var enabled=EnableMonitoring.IsChecked==true;busy=true;SettingsPanel.IsEnabled=false;
        try
        {
            await Task.Run(()=>
            {
                new EnvironmentMonitor(store).SetEnabled(enabled);
                var process=new BackgroundProcess(layout);var startup=new StartupManager(layout);
                if(enabled) {if(startup.IsEnabled) process.Start();}
                else {startup.SetEnabled(false);process.Stop();}
            });
            busy=false;Update(store.Read());OptionsChanged?.Invoke(this,EventArgs.Empty);
        }
        catch(Exception error) when(Expected(error)) {RefreshProcessState();ShowError(error);}
        finally {busy=false;SettingsPanel.IsEnabled=true;}
    }
    private void ChangeOptions(object sender,RoutedEventArgs e)
    {
        if(updating || busy) return;
        try
        {
            store.Update(state=>
            {
                state.GroupExternalUserVariables=GroupExternalUser.IsChecked==true;state.GroupExternalMachineVariables=GroupExternalMachine.IsChecked==true;
                state.AutoArrangeUserPath=AutoArrangeUser.IsChecked==true;state.AutoArrangeMachinePath=AutoArrangeMachine.IsChecked==true;
                state.RemoveUserPathDuplicates=RemoveUserDuplicates.IsChecked==true;state.RemoveMachinePathDuplicates=RemoveMachineDuplicates.IsChecked==true;
                state.MoveExternalUserPathEntries=MoveUserEntries.IsChecked==true;state.MoveExternalMachinePathEntries=MoveMachineEntries.IsChecked==true;
                if(state.TrackChangeOrigins!=(TrackOrigins.IsChecked==true)) state.OriginTrackingRequest=Guid.NewGuid().ToString("N");
                state.TrackChangeOrigins=TrackOrigins.IsChecked==true;
                state.IgnoreSystemEditorUserChanges=IgnoreSystemEditorUser.IsChecked==true;state.IgnoreSystemEditorMachineChanges=IgnoreSystemEditorMachine.IsChecked==true;
                state.IgnorePowerToysUserChanges=IgnorePowerToysUser.IsChecked==true;state.IgnorePowerToysMachineChanges=IgnorePowerToysMachine.IsChecked==true;
                state.PendingPathOrganizations.RemoveAll(plan=>!state.IsPathOrganizationEnabled(plan.Scope));
            });
            Update(store.Read());OptionsChanged?.Invoke(this,EventArgs.Empty);
        }
        catch(Exception error) when(Expected(error)) {Update(store.Read());ShowError(error);}
    }
    private void ApplyPendingChanges(object sender,RoutedEventArgs e)=>PendingApplyRequested?.Invoke(this,EventArgs.Empty);
    public void SetApplying(bool value) {busy=value;SettingsPanel.IsEnabled=!value;}
    private static bool Expected(Exception error)=>error is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException;
}
