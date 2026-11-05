using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Pathman.Core;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;

namespace Pathman.Service;

internal static class SystemMonitorHost
{
    private delegate void ServiceMain(int count,IntPtr arguments);
    private delegate uint Handler(uint control,uint type,IntPtr data,IntPtr context);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct Entry {public string? Name;public ServiceMain? Main;}
    [StructLayout(LayoutKind.Sequential)] private struct Status {public uint Type,State,Accepted,Exit,Specific,Checkpoint,Wait;}
    private static readonly CancellationTokenSource stop=new();
    private static readonly ServiceMain main=ServiceEntry;
    private static readonly Handler handler=Handle;
    private static IntPtr statusHandle;
    private static bool running;
    public static int Run()
    {
        return StartServiceCtrlDispatcher([new Entry {Name=GlobalMonitoring.ServiceName,Main=main},new Entry()]) ? 0 : Marshal.GetLastWin32Error();
    }
    private static uint Handle(uint control,uint type,IntPtr data,IntPtr context)
    {if(control is 1 or 5) {Report(3);stop.Cancel();}return 0;}
    private static void Report(uint state,uint exit=0)
    {running=state==4;var status=new Status {Type=16,State=state,Accepted=state==4 ? 5u : 0,Exit=exit,Wait=state is 2 or 3 ? 30000u : 0};SetServiceStatus(statusHandle,ref status);}
    private static void ServiceEntry(int count,IntPtr arguments)
    {
        statusHandle=RegisterServiceCtrlHandlerEx(GlobalMonitoring.ServiceName,handler,IntPtr.Zero);Report(2);
        try {Monitor();Report(1);}
        catch(Exception error)
        {
            running=false;
            try {WriteStatus(false,error.Message);}catch { /* SCM must see the failure even if its status file is unavailable. */ }
            finally {Report(1,1);}
        }
    }
    private static void WriteStatus(bool ready,string? error=null)
    {
        var file=GlobalMonitoring.StatusPath;
        var temporary=file+"."+Guid.NewGuid().ToString("N")+".tmp";
        File.WriteAllText(temporary,JsonSerializer.Serialize(new {Running=running && !stop.IsCancellationRequested,ProcessId=Environment.ProcessId,OriginsReady=ready,Error=error,Time=DateTime.UtcNow}));
        File.Move(temporary,file,true);
    }
    private static void Monitor()
    {
        var queued=new ConcurrentQueue<OriginMessage>();var ready=false;string? error=null;
        using var collectorStop=new CancellationTokenSource();
        var collector=Task.Run(()=>
        {
            try {RegistryOriginCollector.Run(null,message=> {if(message.Kind=="ready") ready=true;else {queued.Enqueue(message);while(queued.Count>8192) queued.TryDequeue(out _);}},collectorStop.Token);}
            catch(Exception failure) {error=failure.Message;}
            finally {ready=false;}
        });
        var machineStore=new EnvironmentWorkspaceStore(GlobalMonitoring.MachineDirectory);
        var machineMonitor=new EnvironmentMonitor(machineStore,VariableScope.Machine);
        machineMonitor.SetEnabled(true);
        var users=new Dictionary<string,EnvironmentMonitor>();
        var pendingOrigins=new List<OriginMessage>();
        try
        {
            while(!stop.IsCancellationRequested)
            {
                var settings=GlobalMonitoring.ReadSettings();if(!settings.Enabled) break;
                var machineSnapshot=new WindowsEnvironmentVariableReader().Read(VariableScope.Machine);
                var loggedOn=LoggedOnUsers.Enumerate();
                try
                {
                var snapshots=new Dictionary<string,IReadOnlyList<EnvironmentVariableInfo>>();
                foreach(var user in loggedOn.DistinctBy(user=>user.Sid))
                {
                    try {snapshots[user.Sid]=WindowsIdentity.RunImpersonated(user.Token,EnvironmentMonitor.ReadAll);}
                    catch(Exception failure) when(Expected(failure)) {error=failure.Message;}
                }
                // Compare the captured registry values only after buffered ETW
                // events for those writes have arrived. Newer writes are guarded
                // by the organizer's existing stale-value preconditions.
                stop.Token.WaitHandle.WaitOne(300);
                while(queued.TryDequeue(out var message)) pendingOrigins.Add(message);
                pendingOrigins.RemoveAll(item=>item.Origin is null || item.Origin.Time<DateTime.UtcNow.AddSeconds(-5));
                if(pendingOrigins.Count>8192) pendingOrigins.RemoveRange(0,pendingOrigins.Count-8192);
                machineStore.InspectForService(settings.ApplyMachineOptions);
                L.SetLanguage("system");
                var machineApplied=false;
                var handoffPending=false;
                try {machineApplied=machineMonitor.Poll(new Origins(ready,pendingOrigins,null),machineSnapshot);}
                catch(Exception failure) when(Expected(failure)) {machineMonitor.ReportError(failure.Message);}
                SessionRouting.Poll(loggedOn,user=>user.Sid,desktops=>
                {
                    var user=desktops[0];
                    var userApplied=false;
                    try
                    {
                        WindowsIdentity.RunImpersonated(user.Token,()=>
                        {
                            var store=new EnvironmentWorkspaceStore(user.DataDirectory);var state=store.Read();
                            L.SetLanguage(state.Preferences.Language);
                            if(!state.MonitoringEnabled || !snapshots.ContainsKey(user.Sid)) {users.Remove(user.Sid);return;}
                            foreach(var desktop in desktops)
                            {
                                using var owner=BackgroundProcess.OwnerMutex(user.Sid,settings.InstallationDirectory,(int)desktop.SessionId);
                                bool acquired;try {acquired=owner.WaitOne(0);}catch(AbandonedMutexException) {acquired=true;}
                                if(!acquired) {handoffPending=true;return;}
                                owner.ReleaseMutex();
                            }
                                if(!users.TryGetValue(user.Sid,out var monitor)) {monitor=new(store,VariableScope.User,user.Runtime);monitor.SetEnabled(true,snapshots[user.Sid]);users.Add(user.Sid,monitor);}
                                var enabled=state.TrackChangeOrigins && (user.Administrator || settings.ProvideNonAdministratorOrigins);
                                var tracking=enabled && ready;
                                userApplied=monitor.Poll(enabled ? new Origins(ready,pendingOrigins,user.Sid) : null,snapshots[user.Sid]);
                                store.InspectForService(saved=>
                                {
                                    var notice=enabled && !ready ? error ?? L.Get("修改来源跟踪尚未就绪。") : null;
                                    if(saved.OriginTrackingReady==tracking && saved.OriginTrackingMessage==notice) return false;
                                    saved.OriginTrackingReady=tracking;saved.OriginTrackingMessage=notice;return true;
                                });
                        });
                    }
                    catch(Exception failure) when(Expected(failure)) {error=failure.Message;}
                    return userApplied;
                },user=> {try {user.NotifyEnvironmentChanged();}catch(Exception failure) when(Expected(failure)) {error=failure.Message;}},machineApplied);
                if(!running && !handoffPending) Report(4);
                WriteStatus(ready,error);stop.Token.WaitHandle.WaitOne(1000);
                }
                finally {foreach(var user in loggedOn) user.Dispose();}
            }
        }
        finally
        {
            collectorStop.Cancel();try {collector.Wait(5000);}catch(AggregateException) { }
            foreach(var user in LoggedOnUsers.Enumerate()) using(user)
            {
                try
                {
                    WindowsIdentity.RunImpersonated(user.Token,()=>new EnvironmentWorkspaceStore(user.DataDirectory).InspectForService(state=> {var changed=state.OriginTrackingReady;state.OriginTrackingReady=false;state.OriginTrackingMessage=null;return changed;}));
                    if(!GlobalMonitoring.ReadSettings().Enabled) user.ResumeBackground(GlobalMonitoring.ReadSettings().InstallationDirectory);
                }
                catch(Exception failure) when(Expected(failure)) { }
            }
            running=false;WriteStatus(false);
        }
    }
    private static bool Expected(Exception error)=>error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Security.SecurityException or JsonException or InvalidOperationException;
    private sealed class Origins(bool ready,List<OriginMessage> values,string? sid) : IEnvironmentChangeOrigins
    {
        private readonly Dictionary<string,IReadOnlyList<EnvironmentChangeOrigin>> captured=new(StringComparer.OrdinalIgnoreCase);
        public bool IsReady=>ready;
        public IReadOnlyList<EnvironmentChangeOrigin> Take(VariableScope scope,string name)
        {
            var key=EnvironmentWorkspace.Key(scope,name);
            // Recording the change and preparing its PATH plan need the same evidence.
            if(captured.TryGetValue(key,out var existing)) return existing;
            var selected=values.Where(item=>(scope==VariableScope.Machine ? item.UserSid is null : item.UserSid==sid) && item.Origin is {} origin && origin.Scope==scope && origin.Name.Equals(name,StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach(var item in selected) values.Remove(item);
            return captured[key]=selected.Select(item=>item.Origin!).ToArray();
        }
    }
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="StartServiceCtrlDispatcherW")] private static extern bool StartServiceCtrlDispatcher([In] Entry[] entries);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr RegisterServiceCtrlHandlerEx(string name,Handler handler,IntPtr context);
    [DllImport("advapi32.dll")] private static extern bool SetServiceStatus(IntPtr handle,ref Status status);
}
