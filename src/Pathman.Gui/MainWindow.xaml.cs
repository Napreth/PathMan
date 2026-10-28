using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Pathman.Core;
using Pathman.Core.EnvironmentVariables;
using Pathman.Gui.Views;
using Pathman.Core.Notifications;
using Pathman.Gui.Controls;
using Pathman.Gui.Notifications;
namespace Pathman.Gui;
public partial class MainWindow : Window
{
    private readonly PathmanLayout layout=AppInfo.Layout();
    private readonly EnvironmentWorkspaceStore store;
    private readonly VariablesView variables;
    private readonly AliasesView aliases;
    private readonly MonitorView monitor;
    private readonly AboutView about;
    private readonly SettingsView settings;
    private readonly ApplicationUpdateService updates;
    private readonly HealthView health=new();
    private readonly VersionsView versions;
    private readonly NotificationCenter notifications=new();
    private bool healthBusy,scanAgain;
    private string healthSignature="";
    private string monitorSignature="";
    private bool monitorApplying;
    private readonly HashSet<string> attemptedMonitorPlans=[];
    private EnvironmentSnapshotWatcher? foregroundMonitor;
    private IReadOnlyList<EnvironmentVariableInfo>? pendingRegistrySnapshot;
    private int previousIssueCount;
    private readonly DispatcherTimer changes=new() {Interval=TimeSpan.FromSeconds(1)};
    private DateTime lastWrite;
    private bool navigating,checking,closingApproved;
    private ListBoxItem? previousNavigation;
    public MainWindow()
    {
        InitializeComponent();Language=System.Windows.Markup.XmlLanguage.GetLanguage(L.Current.CultureName);RefreshWindowTitle(null,EventArgs.Empty);store=new(layout.DataDirectory);
        updates=new(layout.DataDirectory,release=>UpdateToast.Show(release,layout),store.Read().Preferences.ReceivePrereleaseUpdates);
        variables=new(layout,store); aliases=new(layout,store); monitor=new(layout,store); about=new(updates,InstallUpdateAsync);settings=new(store);
        versions=new(layout,store,InstalledScope()=="/AllUsers" ? Pathman.Core.Models.VariableScope.Machine : Pathman.Core.Models.VariableScope.User);AppVersion.Text=AppInfo.DisplayVersion;
        settings.PreferencesChanged+=async(_,_)=>await ApplyPreferencesAsync();
        PageHost.Content=variables; previousNavigation=Navigation.SelectedItem as ListBoxItem;
        NotificationsList.ItemsSource=notifications.Items;
        AddHandler(Osc8TextBlock.LinkRequestedEvent,new EventHandler<AppLinkEventArgs>(HandleAppLink));
        health.ScanRequested+=async(_,_)=>await ScanHealthAsync();
        health.FixRequested+=async(_,args)=>await ApplyHealthFixAsync(args.Fix);
        health.IgnoreRequested+=async(_,args)=>await IgnoreHealthIssueAsync(args);
        variables.SnapshotChanged+=async(_,_)=>await ScanHealthAsync();
        aliases.SnapshotChanged+=async(_,_)=>await ScanHealthAsync();
        versions.SnapshotChanged+=async(_,_)=>await ScanHealthAsync();
        monitor.PendingApplyRequested+=async(_,_)=>await ApplyPendingMonitoringAsync();
        monitor.OptionsChanged+=async(_,_)=> {await SynchronizeCommandLinePathAsync();await ScanHealthAsync();};
        Loaded+=async(_,_)=>
        {
            await SynchronizeCommandLinePathAsync();
            foregroundMonitor=new EnvironmentSnapshotWatcher(current=>
            {
                if(Dispatcher.HasShutdownStarted) return;
                Dispatcher.BeginInvoke(()=> {pendingRegistrySnapshot=current;variables.NotifyIncoming(current);CheckWorkspace(null,EventArgs.Empty);});
            });
            await ScanHealthAsync();
            await CheckAutomaticUpdateAsync();
        };
        EnvironmentEditHost.AuthorizationChanged+=RefreshWindowTitle;
        changes.Tick+=RefreshWindowTitle;changes.Tick+=CheckWorkspace; changes.Start(); Closing+=OnClosing;
        Closed+=(_,_)=> {foregroundMonitor?.Dispose();changes.Stop();updates.Dispose();EnvironmentEditHost.AuthorizationChanged-=RefreshWindowTitle;};
    }
    private void RefreshWindowTitle(object? sender,EventArgs e)
    {
        var title="PathMan · "+(EnvironmentEditHost.IsAdministrator ? L.Get("以管理员身份运行") : EnvironmentEditHost.IsAuthorized ? L.Get("管理员操作已授权") : L.Get("以非管理员身份运行"));
        if(Title!=title) {Title=title;WindowTitle.Text=title;}
    }
    private async Task ApplyPreferencesAsync()
    {
        var preferences=store.Read().Preferences;
        updates.SetReceivePrerelease(preferences.ReceivePrereleaseUpdates);
        if(Appearance.Apply(preferences))
        {
            Language=System.Windows.Markup.XmlLanguage.GetLanguage(Pathman.Core.Localization.Text.Current.CultureName);
            healthSignature="";monitorSignature="";RefreshWindowTitle(null,EventArgs.Empty);
            await variables.ReloadAsync();await versions.ReloadAsync();await ScanHealthAsync();
        }
        if(!preferences.AutoCheckUpdates) updates.CancelAutomaticCheck();
    }
    private async Task CheckAutomaticUpdateAsync()
    {
        if(store.Read().Preferences.AutoCheckUpdates) await updates.CheckAsync(automatic:true);
    }
    private async Task<bool> InstallUpdateAsync(string installer)
    {
        if(healthWorking || monitorApplying || aliases.IsBusy || variables.IsBusy || versions.IsBusy) return false;
        if(!await variables.ConfirmLeaveAsync() || !await aliases.ConfirmLeaveAsync() || !await versions.ConfirmLeaveAsync()) return false;
        var scope=InstalledScope();
        var start=new System.Diagnostics.ProcessStartInfo(installer) {UseShellExecute=true};
        if(scope is not null) start.Arguments=$"/S /Update {scope} /D={layout.InstallationDirectory}";
        System.Diagnostics.Process.Start(start);
        closingApproved=true;Application.Current.Shutdown();return true;
    }
    public async Task OpenUpdateDetailsAsync()
    {
        if(WindowState==WindowState.Minimized) WindowState=WindowState.Normal;
        if(about.IsUpdateOpen) {about.ShowReleaseDetails(fromNotification:true);return;}
        for(var attempt=0;attempt<200 && (healthWorking || monitorApplying || aliases.IsBusy || variables.IsBusy || versions.IsBusy);attempt++)
        {await Task.Delay(100);if(Dispatcher.HasShutdownStarted) return;}
        if(healthWorking || monitorApplying || aliases.IsBusy || variables.IsBusy || versions.IsBusy) return;
        if(!await variables.ConfirmLeaveAsync() || !await aliases.ConfirmLeaveAsync() || !await versions.ConfirmLeaveAsync()) return;
        if(WindowState==WindowState.Minimized) WindowState=WindowState.Normal;Show();Activate();
        navigating=true;
        Navigation.SelectedItem=Navigation.Items.Cast<ListBoxItem>().First(item=>item.Tag as string=="about");
        previousNavigation=(ListBoxItem)Navigation.SelectedItem;PageHost.Content=about;navigating=false;
        if(updates.Latest is null) await updates.CheckAsync();
        about.ShowReleaseDetails(fromNotification:true);
    }
    private string? InstalledScope()
    {
        foreach(var hive in new[] {Microsoft.Win32.RegistryHive.CurrentUser,Microsoft.Win32.RegistryHive.LocalMachine})
        {
            using var registry=Microsoft.Win32.RegistryKey.OpenBaseKey(hive,Microsoft.Win32.RegistryView.Registry32);
            using var key=registry.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\PathMan");
            if(key?.GetValue("InstallLocation") is string directory &&
               string.Equals(System.IO.Path.TrimEndingDirectorySeparator(directory),System.IO.Path.TrimEndingDirectorySeparator(layout.InstallationDirectory),StringComparison.OrdinalIgnoreCase))
                return hive==Microsoft.Win32.RegistryHive.CurrentUser ? "/CurrentUser" : "/AllUsers";
        }
        return null;
    }
    private async void Navigate(object sender,SelectionChangedEventArgs e)
    {
        if(PageHost is null || variables is null || navigating || Navigation.SelectedItem is not ListBoxItem item) return;
        navigating=true;
        if(versions.IsBusy) {Navigation.SelectedItem=previousNavigation;navigating=false;return;}
        var canLeave=PageHost.Content==aliases ? await aliases.ConfirmLeaveAsync() : PageHost.Content==versions ? await versions.ConfirmLeaveAsync() : PageHost.Content!=variables || await variables.ConfirmLeaveAsync();
        if(!canLeave) { Navigation.SelectedItem=previousNavigation; navigating=false; return; }
        Navigation.SelectedItem=item;
        previousNavigation=item;
        PageHost.Content=(item.Tag as string) switch { "health"=>health,"aliases"=>aliases,"versions"=>versions,"monitor"=>monitor,"settings"=>settings,"about"=>about,_=>variables };
        navigating=false;
    }
    private void DismissNotification(object sender,RoutedEventArgs e)
    {if(sender is Button {DataContext:AppNotification notification}) notifications.Dismiss(notification.Id);}
    private async void HandleAppLink(object? sender,AppLinkEventArgs e)
    {
        e.Handled=true;
        if(healthWorking || versions.IsBusy) return;
        if(AppPageLink.Parse(e.Target) is {} route)
        {
            if(PageHost.Content==aliases && !await aliases.ConfirmLeaveAsync()) return;
            if(PageHost.Content==versions && !await versions.ConfirmLeaveAsync()) return;
            if(route.Page=="variables" && route.Scope is {} scope && route.Name is {} name)
            {
                if(!await variables.NavigateToVariableAsync(scope,name)) return;
                navigating=true; Navigation.SelectedItem=Navigation.Items.Cast<ListBoxItem>().First(item=>item.Tag as string=="variables");
                previousNavigation=(ListBoxItem)Navigation.SelectedItem; PageHost.Content=variables; navigating=false;
            }
            else Navigation.SelectedItem=Navigation.Items.Cast<ListBoxItem>().First(item=>string.Equals(item.Tag as string,route.Page,StringComparison.Ordinal));
            return;
        }
        if(Uri.TryCreate(e.Target,UriKind.Absolute,out var uri) && uri.Scheme is "https" or "http")
        {
            try {System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) {UseShellExecute=true});}
            catch(System.ComponentModel.Win32Exception error) {notifications.Show(new("link",L.Get("链接未打开"),error.Message,NotificationColor.Red));}
        }
    }
    private async Task ScanHealthAsync()
    {
        if(healthBusy) {scanAgain=true; return;}
        healthBusy=true; health.SetScanning(true);
        try
        {
            do
            {
                scanAgain=false;
                var result=await Task.Run(()=>
                {
                    var state=store.Read();
                    return (Report:EnvironmentHealth.Analyze(EnvironmentMonitor.ReadAll(),shimsDirectory:layout.ShimsDirectory,workspace:state),Ignored:state.IgnoredHealthIssues);
                });
                health.Update(result.Report,result.Ignored);
                var report=new EnvironmentHealthReport(result.Report.Issues.Where(issue=>!result.Ignored.Contains(issue.Id)).ToArray());
                var signature=string.Join('\n',report.Issues.Select(issue=>issue.Id));
                if(signature!=healthSignature)
                {
                    healthSignature=signature;
                    if(report.Issues.Count>0) notifications.Show(new("health",L.Get("环境变量存在问题"),L.Format("发现 {0} 项错误、{1} 项提醒。\n",report.Errors,report.Warnings)+Osc8Text.Link(L.Get("查看健康检测"),"apppage:health"),report.Errors>0 ? NotificationColor.Red : NotificationColor.Yellow));
                    else if(result.Report.Issues.Count>0) notifications.Show(new("health",L.Get("健康问题已忽略"),Osc8Text.Link(L.Get("查看已忽略的问题"),"apppage:health"),NotificationColor.Blue));
                    else if(previousIssueCount>0) notifications.Show(new("health",L.Get("健康检测通过"),L.Get("之前发现的问题已解决。"),NotificationColor.Green));
                    previousIssueCount=report.Issues.Count;
                }
            } while(scanAgain);
        }
        catch(Exception error) when(error is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException or System.Text.Json.JsonException)
        {health.ShowError(error.Message); notifications.Show(new("health",L.Get("检测未完成"),error.Message,NotificationColor.Red));}
        finally {healthBusy=false; health.SetScanning(false);}
    }
    private async void CheckWorkspace(object? sender,EventArgs e)
    {
        if(checking || monitorApplying || healthWorking || aliases.IsBusy || variables.IsBusy || versions.IsBusy) return;
        checking=true;
        try
        {
            var registryChanged=pendingRegistrySnapshot is not null;
            if(pendingRegistrySnapshot is {} pending && await variables.ObserveRegistryAsync(pending) && ReferenceEquals(pendingRegistrySnapshot,pending)) pendingRegistrySnapshot=null;
            var stamp=System.IO.File.Exists(store.FilePath) ? System.IO.File.GetLastWriteTimeUtc(store.FilePath) : DateTime.MinValue;
            if(versions.IsLoaded && (registryChanged || stamp!=lastWrite)) await versions.ReloadAsync();
            if(stamp==lastWrite) return;
            var state=await Task.Run(store.Read); monitor.Update(state);
            if(settings.IsLoaded) settings.Update(state);
            ShowMonitorNotification(state);
            if(state.MonitoringEnabled && state.AutoArrangeMachinePath && (EnvironmentEditHost.IsAdministrator || EnvironmentEditHost.IsAuthorized) && state.PendingPathOrganizations.Any(item=>item.Scope==Pathman.Core.Models.VariableScope.Machine && !attemptedMonitorPlans.Contains(item.Id)))
            {await ApplyPendingMonitoringAsync(automatic:true);return;}
            if(!variables.HasUnsavedChanges && !variables.IsBusy)
            {
                if(variables.LoadedRevision!=state.Revision) await variables.ReloadAsync(external:true);
                if(variables.LoadedRevision==state.Revision) lastWrite=stamp;
            }
        }
        catch(Exception error) when(error is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        finally { checking=false; }
    }
    private void ShowMonitorNotification(EnvironmentWorkspace state)
    {
        var pending=state.MonitoringEnabled && state.AutoArrangeMachinePath && state.PendingPathOrganizations.Any(item=>item.Scope==Pathman.Core.Models.VariableScope.Machine);
        var signature=$"{pending}:{state.MonitorMessageTime:O}:{state.MonitorMessage}:{state.OriginTrackingMessage}";
        if(signature==monitorSignature) return;monitorSignature=signature;
        if(state.OriginTrackingMessage is {} tracking) notifications.Show(new("monitor",L.Get("修改来源识别"),tracking+"\n"+Osc8Text.Link(L.Get("前往变量监控"),"apppage:monitor"),NotificationColor.Yellow));
        else if(pending) notifications.Show(new("monitor",L.Get("系统 PATH 等待整理"),L.Get("外部新增项需要管理员授权。\n")+Osc8Text.Link(L.Get("前往变量监控"),"apppage:monitor"),NotificationColor.Yellow));
        else if(state.MonitorMessage is {} message) notifications.Show(new("monitor",L.Get("变量监控"),message+"\n"+Osc8Text.Link(L.Get("查看变量监控"),"apppage:monitor"),NotificationColor.Yellow));
        else notifications.Dismiss("monitor");
    }
    private async Task ApplyPendingMonitoringAsync(bool automatic=false)
    {
        if(monitorApplying) return;
        monitorApplying=true;monitor.SetApplying(true);
        try
        {
            var state=await Task.Run(store.Read);
            foreach(var plan in state.PendingPathOrganizations.Where(item=>item.Scope==Pathman.Core.Models.VariableScope.Machine))
            {
                if(automatic && attemptedMonitorPlans.Contains(plan.Id)) continue;
                attemptedMonitorPlans.Add(plan.Id);
                state=await Task.Run(store.Read);
                if(!state.MonitoringEnabled || !state.IsPathOrganizationEnabled(plan.Scope) || plan.Policy!=ExternalPathOrganizer.Policy(state,plan.Scope) || !state.PendingPathOrganizations.Any(item=>item.Id==plan.Id)) continue;
                if(EnvironmentMonitor.RequiresOrigin(state,plan.Scope) && !state.OriginTrackingReady || plan.Origins.Any(origin=>origin.Excluded(state))) continue;
                await Task.Run(()=>EnvironmentMonitor.Backup(store,plan));
                await EnvironmentEditHost.ApplyBatchAsync(plan.Mutations,plan.Preconditions,store,layout.DataDirectory);
                await Task.Run(()=>EnvironmentMonitor.Complete(store,plan));
            }
            await ScanHealthAsync();
        }
        catch(Exception error) when(error is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or OperationCanceledException)
        {
            if(error is not OperationCanceledException)
            {
                notifications.Show(new("monitor",L.Get("系统 PATH 未整理"),error.Message,NotificationColor.Yellow));
                try {new EnvironmentMonitor(store).ReportError(L.Get("系统 PATH 未整理：")+error.Message);}
                catch(Exception recordError) when(recordError is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException or System.Text.Json.JsonException) { }
            }
        }
        finally
        {
            monitorApplying=false;monitor.SetApplying(false);
            try {var state=store.Read();monitor.Update(state);ShowMonitorNotification(state);}
            catch(Exception error) when(error is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException or System.Text.Json.JsonException)
            {notifications.Show(new("monitor",L.Get("变量监控"),error.Message,NotificationColor.Yellow));}
        }
    }
    private async void OnClosing(object? sender,CancelEventArgs e)
    {
        if(healthWorking || monitorApplying || versions.IsBusy) {e.Cancel=true;return;}
        if(closingApproved) return;
        if(versions.HasInlineEditor)
        {e.Cancel=true;if(await versions.ConfirmLeaveAsync()) {closingApproved=true;Close();}return;}
        if(aliases.HasInlineEditor)
        {e.Cancel=true;if(await aliases.ConfirmLeaveAsync()) {closingApproved=true;Close();}return;}
        if(aliases.IsBusy) {e.Cancel=true;return;}
        if(variables.HasInlineEditor)
        {
            e.Cancel=true;
            if(await variables.ConfirmLeaveAsync()) { closingApproved=true; Close(); }
        }
        else if(!variables.ConfirmLeave()) e.Cancel=true;
    }
}
