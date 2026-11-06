using Pathman.Core;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Shims;
using Pathman.Service;

// A normal user-session background process, with no SCM registration or elevation.
if(args is ["--environment-changed"]) {WindowsEnvironmentVariableEditor.NotifyEnvironmentChanged();return 0;}
if(args is ["--system-monitor"]) return SystemMonitorHost.Run();
if(args.Length is 4 or 8 or 9 && args[0]=="--configure-global-monitor")
{
    var flags=new bool[args.Length==4 ? 3 : 7];
    for(var index=0;index<flags.Length;index++) if(!bool.TryParse(args[index+1],out flags[index])) return 2;
    try {new GlobalMonitoring(PathmanLayout.Discover()).ConfigureElevated(flags[0],flags[1],flags[2],flags.Length==7 ? flags[3] : null,flags.Length==7 ? flags[4] : null,flags.Length==7 ? flags[5] : null,flags.Length==7 ? flags[6] : null,args.Length==9 ? args[8] : null);return 0;}
    catch(Exception error) when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or ArgumentException) {Console.Error.WriteLine(error);return error is System.ComponentModel.Win32Exception windows ? windows.NativeErrorCode : 1;}
}
if(args.FirstOrDefault()=="--registry-origin-worker") return RegistryOriginTracker.RunWorker(args);
if(args.FirstOrDefault()=="--scheduled-origin-worker") return RegistryOriginTracker.RunScheduledWorker(args);
if(args is ["--register-origin-task",var sid])
{
    try {new OriginTrackingTask(PathmanLayout.Discover()).Register(sid);return 0;}
    catch(Exception error) when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or ArgumentException) {Console.Error.WriteLine(error);return error is System.ComponentModel.Win32Exception windows ? windows.NativeErrorCode : 1;}
}
if(args is ["--remove-origin-tasks"])
{
    try {var installation=PathmanLayout.Discover();new GlobalMonitoring(installation).Remove();new OriginTrackingTask(installation).RemoveInstallation();return 0;}
    catch(Exception error) when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException) {return error is System.ComponentModel.Win32Exception windows ? windows.NativeErrorCode : 1;}
}
if(args is ["--maintain-origin-task",var originalSid,var originalData])
{
    try
    {
        var config=Path.Combine(Path.GetFullPath(originalData),"environment.json");
        var installation=PathmanLayout.Discover();var global=new GlobalMonitoring(installation);global.Maintain();
        var task=new OriginTrackingTask(installation);
        var enabled=File.Exists(config) && System.Text.Json.JsonSerializer.Deserialize<EnvironmentWorkspace>(File.ReadAllText(config),EnvironmentWorkspaceStore.JsonOptions)?.TrackChangeOrigins==true;
        if(!global.IsEnabled && (enabled || task.HasUserTask(originalSid))) task.Register(originalSid);
        return 0;
    }
    catch(Exception error) when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or System.Text.Json.JsonException or ArgumentException) {Console.Error.WriteLine(error);return error is System.ComponentModel.Win32Exception windows ? windows.NativeErrorCode : 1;}
}
if(args.FirstOrDefault()=="--installer-origin-broker") return InstallerOriginBroker.Run(args);
// Installer maintenance is independent of the user's background monitoring switches.
if(args is ["--installer-refresh-shims",var dataDirectory])
{
    try {var installation=PathmanLayout.Discover(dataDirectory);var workspace=new EnvironmentWorkspaceStore(installation.DataDirectory);L.SetLanguage(workspace.Read().Preferences.Language);new ShimManager(installation,ensureUserPath:false).Refresh();InstallerEnvironmentSync.Synchronize(installation);return 0;}
    catch(Exception error) when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) {return 1;}
}
if (args.Length != 0 && (args.Length != 2 || args[0] != "--data-root")) return 2;
var layout = PathmanLayout.Discover(args.Length == 2 ? args[1] : null);
if(new GlobalMonitoring(layout).IsEnabled) return 0;
using var userIdentity=System.Security.Principal.WindowsIdentity.GetCurrent();
using var owner=BackgroundProcess.OwnerMutex(userIdentity.User!.Value,layout.InstallationDirectory);
try {if(!owner.WaitOne(0)) return 0;}catch(AbandonedMutexException) { }
var background = new BackgroundProcess(layout);
var store=new EnvironmentWorkspaceStore(layout.DataDirectory);
L.SetLanguage(store.Read().Preferences.Language);
var startup=new StartupManager(layout);
startup.Reconcile();
if(!StartupManager.CanRun(store.Read(),startup.IsEnabled)) return 5;
using var instance = new Mutex(false, background.InstanceName);
try
{
    if (!instance.WaitOne(0)) return 0;
}
catch (AbandonedMutexException) { /* A previous instance crashed; this thread now owns the mutex. */ }

try
{
    using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, background.StopEventName);
    using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, background.ReadyEventName);
    stop.Reset();
    var monitor = new EnvironmentMonitor(store);
    using var writer=BackgroundProcess.WriterMutex(userIdentity.User!.Value);
    try {writer.WaitOne();}catch(AbandonedMutexException) { }
    try {monitor.SetEnabled(true);}finally {writer.ReleaseMutex();}
    using var signal = new EnvironmentRegistrySignal();
    ready.Set();
    RegistryOriginTracker? origins=null;var requestedOrigin=false;string? originError=null;string? originRequest=null;
    try {while (true)
    {
        if(new GlobalMonitoring(layout).IsEnabled) break;
        if(!StartupManager.CanRun(store.Read(),startup.IsEnabled)) {startup.Reconcile();break;}
        try
        {
            var settings=store.Read();
            L.SetLanguage(settings.Preferences.Language);
            if(originRequest!=settings.OriginTrackingRequest) {originRequest=settings.OriginTrackingRequest;origins?.Dispose();origins=null;requestedOrigin=false;originError=null;}
            if(!settings.TrackChangeOrigins) {origins?.Dispose();origins=null;requestedOrigin=false;originError=null;}
            else if(!requestedOrigin)
            {
                requestedOrigin=true;
                try {origins=RegistryOriginTracker.Start();}
                catch(Exception error) when(error is System.ComponentModel.Win32Exception or IOException or OperationCanceledException)
                {originError=L.Get("来源跟踪未启动，请关闭并重新开启该开关以重试。")+error.Message;}
            }
            var tracking=origins?.IsReady==true;
            var message=settings.TrackChangeOrigins && !tracking ? originError ?? origins?.Error ?? L.Get("修改来源跟踪尚未就绪。") : null;
            if(settings.OriginTrackingReady!=tracking || settings.OriginTrackingMessage!=message)
                store.Update(state=> {state.OriginTrackingReady=tracking;state.OriginTrackingMessage=message;});
            if(tracking) Thread.Sleep(1000); // Allow buffered ETW writes and process events to reach the batch before comparing.
            try {writer.WaitOne();}catch(AbandonedMutexException) { }
            try {monitor.Poll(origins?.Batch());}finally {writer.ReleaseMutex();}
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.Text.Json.JsonException)
        {try {monitor.ReportError(L.Get("监控暂时无法读取或保存环境变量：")+error.Message);}catch(IOException) {}}
        var reason = WaitHandle.WaitAny([stop, signal.UserChanged, signal.MachineChanged], 1000);
        if (reason == 0) break;
        if (reason is 1 or 2) signal.Arm(reason-1);
    }}
    finally {try {origins?.Dispose();store.Update(state=> {state.OriginTrackingReady=false;state.OriginTrackingMessage=null;});} finally {ready.Reset();}}
}
finally { instance.ReleaseMutex();owner.ReleaseMutex(); }
return 0;
