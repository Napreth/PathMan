using Pathman.Core;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Shims;
using Pathman.Service;

// A normal user-session background process, with no SCM registration or elevation.
if(args.FirstOrDefault()=="--registry-origin-worker") return RegistryOriginTracker.RunWorker(args);
// Installer maintenance is independent of the user's background monitoring switches.
if(args is ["--installer-refresh-shims",var dataDirectory])
{
    try {new ShimManager(PathmanLayout.Discover(dataDirectory),ensureUserPath:false).Refresh();return 0;}
    catch(Exception error) when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) {return 1;}
}
if (args.Length != 0 && (args.Length != 2 || args[0] != "--data-root")) return 2;
var layout = PathmanLayout.Discover(args.Length == 2 ? args[1] : null);
var background = new BackgroundProcess(layout);
var store=new EnvironmentWorkspaceStore(layout.DataDirectory);
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
    monitor.SetEnabled(true);
    using var signal = new EnvironmentRegistrySignal();
    ready.Set();
    RegistryOriginTracker? origins=null;var requestedOrigin=false;string? originError=null;string? originRequest=null;
    try {while (true)
    {
        if(!StartupManager.CanRun(store.Read(),startup.IsEnabled)) {startup.Reconcile();break;}
        try
        {
            var settings=store.Read();
            if(originRequest!=settings.OriginTrackingRequest) {originRequest=settings.OriginTrackingRequest;origins?.Dispose();origins=null;requestedOrigin=false;originError=null;}
            if(!settings.TrackChangeOrigins) {origins?.Dispose();origins=null;requestedOrigin=false;originError=null;}
            else if(!requestedOrigin)
            {
                requestedOrigin=true;
                try {origins=RegistryOriginTracker.Start();}
                catch(Exception error) when(error is System.ComponentModel.Win32Exception or IOException or OperationCanceledException)
                {originError="来源跟踪未启动，请关闭并重新开启该开关以重试。"+error.Message;}
            }
            var tracking=origins?.IsReady==true;
            var message=settings.TrackChangeOrigins && !tracking ? originError ?? origins?.Error ?? "修改来源跟踪尚未就绪。" : null;
            if(settings.OriginTrackingReady!=tracking || settings.OriginTrackingMessage!=message)
                store.Update(state=> {state.OriginTrackingReady=tracking;state.OriginTrackingMessage=message;});
            if(tracking) Thread.Sleep(1000); // Allow buffered ETW writes and process events to reach the batch before comparing.
            monitor.Poll(origins?.Batch());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.Text.Json.JsonException)
        {try {monitor.ReportError("监控暂时无法读取或保存环境变量："+error.Message);}catch(IOException) {}}
        var reason = WaitHandle.WaitAny([stop, signal.UserChanged, signal.MachineChanged], 1000);
        if (reason == 0) break;
        if (reason is 1 or 2) signal.Arm(reason-1);
    }}
    finally {try {origins?.Dispose();store.Update(state=> {state.OriginTrackingReady=false;state.OriginTrackingMessage=null;});} finally {ready.Reset();}}
}
finally { instance.ReleaseMutex(); }
return 0;
