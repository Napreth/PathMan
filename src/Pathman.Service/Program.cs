using Pathman.Core;
using Pathman.Core.EnvironmentVariables;

// A normal user-session background process, with no SCM registration or elevation.
if (args.Length != 0 && (args.Length != 2 || args[0] != "--data-root")) return 2;
var layout = PathmanLayout.Discover(args.Length == 2 ? args[1] : null);
var background = new BackgroundProcess(layout);
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
    ready.Set();
    var monitor = new EnvironmentMonitor(new EnvironmentWorkspaceStore(layout.DataDirectory));
    using var signal = new EnvironmentRegistrySignal();
    while (true)
    {
        try { monitor.Poll(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.Text.Json.JsonException)
        { /* Keep the background process available; the UI reports configuration/read errors. */ }
        var reason = WaitHandle.WaitAny([stop, signal.UserChanged, signal.MachineChanged], 1000);
        if (reason == 0) break;
        if (reason is 1 or 2) signal.Arm(reason-1);
    }
    ready.Reset();
}
finally { instance.ReleaseMutex(); }
return 0;
