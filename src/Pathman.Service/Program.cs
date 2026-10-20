using Pathman.Core;

// A normal user-session background process, with no SCM registration or elevation.
if (args.Length != 0) return 2;
var background = new BackgroundProcess(PathmanLayout.Discover());
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
    // Monitoring/synchronization will live here. Startup currently never writes environment variables.
    stop.WaitOne();
    ready.Reset();
}
finally { instance.ReleaseMutex(); }
return 0;
