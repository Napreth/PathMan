using Microsoft.Win32;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;
using Pathman.Core.Localization;

internal static class InstallerSyncTests
{
    public static void Run(Action<bool,string> assert)
    {
        var now=DateTime.Now;
        VariableChange External(VariableScope scope,string before,string after)=>new(now,scope,"Path","set",before,after,"外部程序");
        var state=new EnvironmentWorkspace {
            Snapshot=[new(VariableScope.Machine,"Path",@"C:\Windows",2)],
            Changes=[new(now.AddMinutes(2),VariableScope.Machine,"Path","set",@"C:\Windows;C:\Program Files\dotnet\",@"C:\Windows","PathMan"),External(VariableScope.Machine,@"C:\Windows",@"C:\Windows;C:\Program Files\dotnet\")],
            MonitorMessage=EnvironmentMonitorNotices.UnknownOriginMessage,MonitorMessageTime=now
        };
        assert(EnvironmentMonitorNotices.Reconcile(state) && state.MonitorMessage is null,"An old unknown-origin warning is retired after the corresponding PATH was edited by PathMan");
        assert(state.Changes[1].Source=="外部程序" && state.Changes[1].After==@"C:\Windows;C:\Program Files\dotnet\","Resolving a warning preserves the original writer attribution and exact before/after evidence");
        state.Changes.RemoveAt(0);state.Snapshot[0]=state.Snapshot[0] with {Value=@"C:\Windows;C:\Program Files\dotnet\"};
        state.MonitorMessage=EnvironmentMonitorNotices.UnknownOriginMessage;state.MonitorMessageTime=now;
        EnvironmentMonitorNotices.Reconcile(state);
        assert(state.MonitorMessage is not null && state.UnattributedPathChanges.Count==1,"An unresolved observed PATH change retains its warning");
        assert(!EnvironmentMonitorNotices.Reconcile(state),"Repeated reconciliation is a no-op for an unresolved event");
        state.Changes.Insert(0,new(now.AddSeconds(1),VariableScope.User,"OTHER","set","a","b","PathMan"));
        assert(!EnvironmentMonitorNotices.Reconcile(state) && state.MonitorMessage is not null,"An unrelated edit must not dismiss an unresolved PATH warning");
        state.Snapshot[0]=state.Snapshot[0] with {Value=@"C:\Windows"};
        assert(EnvironmentMonitorNotices.Reconcile(state) && state.MonitorMessage is null,"Reverting the observed change resolves the warning");
        state.Snapshot=[new(VariableScope.User,"Path","a;b",2),new(VariableScope.Machine,"Path","x;y",2)];
        EnvironmentMonitorNotices.ReportUnknown(state,VariableScope.User,"a","a;b");
        EnvironmentMonitorNotices.ReportUnknown(state,VariableScope.Machine,"x","x;y");
        state.Changes.Insert(0,new(DateTime.Now.AddSeconds(1),VariableScope.User,"Path","set","a;b","a","PathMan"));
        EnvironmentMonitorNotices.Reconcile(state);
        assert(state.UnattributedPathChanges.Count==1 && state.UnattributedPathChanges[0].Scope==VariableScope.Machine && state.MonitorMessage is not null,"Resolving user PATH does not dismiss an unresolved machine PATH warning");
        Text.SetLanguage("en-US");
        assert(External(VariableScope.User,"a","b").IsExternal && new VariableChange(now,VariableScope.User,"Path","set","a","b","External program").IsExternal,"External journal entries remain visible across language changes");
        Text.SetLanguage("zh-CN");
        Console.WriteLine("PASS: monitor warning lifecycle, legacy migration, scope isolation and unchanged change evidence.");
    }
}
