using Microsoft.Win32;
using Pathman.Core;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;

internal static class LiveMonitoringTests
{
    private sealed class Origins(bool ready,params EnvironmentChangeOrigin[] values) : IEnvironmentChangeOrigins
    {
        public bool IsReady=>ready;
        public IReadOnlyList<EnvironmentChangeOrigin> Take(VariableScope scope,string name)=>values.Where(item=>item.Scope==scope && item.Name.Equals(name,StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    public static void Run(Action<bool,string> assert)
    {
        EnvironmentVariableInfo V(string name,string raw,VariableScope scope=VariableScope.User,RegistryValueKind kind=RegistryValueKind.String)=>new(name,raw,scope,kind);
        var merge=EnvironmentValueMerge.Merge(@"C:\a;C:\b;C:\c",@"C:\a;D:\mine;C:\c",@"C:\a;C:\b;C:\c;E:\external");
        assert(merge.Value==@"C:\a;D:\mine;C:\c;E:\external" && !merge.HasConflicts,"Independent external and local edits must merge without losing either side");
        assert(EnvironmentValueMerge.Merge("a","a;local","a;external").Value=="a;external;local","Concurrent appends must keep both additions in a deterministic order");
        var conflict=new EnvironmentLiveValue(@"C:\old");conflict.Edit(@"C:\mine");conflict.Observe(@"C:\external");
        assert(conflict.Working==@"C:\mine" && conflict.HasConflicts,"Overlapping changes retain the user's draft and report a conflict");
        var review=conflict.Review();
        assert(review.Any(line=>line.Origin==EnvironmentEditOrigin.ExternalRemoved && line.Text==@"C:\old") && review.Any(line=>line.Origin==EnvironmentEditOrigin.ExternalAdded && line.Text==@"C:\external") && review.Any(line=>line.Origin==EnvironmentEditOrigin.UserAdded && line.Text==@"C:\mine"),"Diff rows must distinguish external removal, external addition and user changes");
        conflict.Observe(@"C:\external-two");conflict.Edit(@"C:\mine-two");conflict.Observe(@"C:\external-three");conflict.UndoExternal();
        assert(conflict.Working==@"C:\mine-two","Repeated incoming writes and reverting external changes must preserve the current local branch");
        var coincident=new EnvironmentLiveValue("one");coincident.Edit("");coincident.Observe("");coincident.UndoExternal();
        assert(coincident.Working=="","Coincident local/external deletions must not resurrect the user's removed row on external undo");
        var rejected=new EnvironmentLiveValue("base");rejected.Observe("base;external");rejected.Edit("base");rejected.Observe("base;external;later");
        assert(rejected.Working=="base;later","Deleting an incoming row must keep it deleted on the next observation without blocking later additions");
        var duplicate=new EnvironmentLiveValue("same;middle");duplicate.Observe("same;middle;same");duplicate.Edit("same;middle");duplicate.Observe("same;middle;same;last");
        assert(duplicate.Working=="same;middle;last","Rejecting a duplicate incoming occurrence must retain the original occurrence and its position");
        var before=V("Path",@"C:\a;C:\b");var session=new PathEditingSession(before,[before]);
        session.Update(session.Items(VariableScope.User,"Path")[1].Id,@"D:\mine");
        session.Observe([V("Path",@"C:\a;C:\b;E:\external")]);
        assert(session.Raw(VariableScope.User,"Path")==@"C:\a;D:\mine;E:\external" && session.Mutations().Single().ExpectedValue==@"C:\a;C:\b;E:\external","Saving a merged draft must guard the latest registry value, not a stale opening snapshot");
        var rows=session.VisibleRows(new HashSet<string>());
        assert(rows.Any(row=>row.Origin==EnvironmentEditOrigin.ExternalAdded && !row.IsGhost) && rows.Any(row=>row.Origin==EnvironmentEditOrigin.UserRemoved && row.IsGhost) && rows.Any(row=>row.Origin==EnvironmentEditOrigin.UserAdded && !row.IsGhost),"Review projections retain editable working rows and noneditable removed-row markers");
        session.UndoExternal();
        assert(session.Raw(VariableScope.User,"Path")==@"C:\a;D:\mine","External undo must remove external additions and retain local edits");
        session.Accept(VariableScope.User);
        assert(!session.HasChanges && !session.IsReviewMode,"Accepting a saved external undo must clear the local dirty state");
        session.SetRaw(VariableScope.User,"Path",@"C:\local");
        session.Observe([]);
        assert(session.Mutations().Single().Action=="create","An externally deleted variable can be explicitly restored on save without an unconditional overwrite");
        var root=V("Path","%A_PATH%;root");var child=V("A_PATH","old");var nested=new PathEditingSession(root,[root,child]);
        nested.Update(nested.Items(VariableScope.User,"A_PATH")[0].Id,"mine");
        nested.Observe([root,V("A_PATH","external")]);
        assert(nested.Raw(VariableScope.User,"A_PATH")=="mine" && nested.Mutations().Single().ExpectedValue=="external","Recursive referenced-variable edits must participate in the same guarded live merge");
        assert(EnvironmentSnapshotWatcher.Equal([V("Path","a"),V("B","b")],[V("B","b"),V("pAtH","a")]) && !EnvironmentSnapshotWatcher.Equal([V("Path","a")],[V("Path","a",kind:RegistryValueKind.ExpandString)]),"Read-only snapshots ignore enumeration order and name casing but detect registry-type changes");
        var settings=new EnvironmentWorkspace();
        assert(!StartupManager.CanRun(settings,false) && !StartupManager.CanRun(settings,true),"A disabled background preference must reject manual and login launches");
        settings.MonitoringEnabled=true;
        assert(!StartupManager.CanRun(settings,false) && StartupManager.CanRun(settings,true),"The background requires both its preference and actual login registration");
        settings.AutoArrangeUserPath=true;settings.TrackChangeOrigins=true;settings.IgnoreSystemEditorUserChanges=true;settings.IgnorePowerToysUserChanges=true;
        EnvironmentChangeOrigin Origin(string image,string command="")=>new(VariableScope.User,"Path",123,image,DateTime.UtcNow,command);
        var system=Origin(@"C:\Windows\System32\SystemPropertiesAdvanced.exe");
        var rundll=Origin(@"C:\Windows\System32\rundll32.exe",@"rundll32.exe sysdm.cpl,EditEnvironmentVariables");
        var quoted=Origin(@"C:\Windows\System32\rundll32.exe","rundll32.exe \"C:\\Windows\\System32\\sysdm.cpl\",EditEnvironmentVariables");
        assert(system.Excluded(settings) && rundll.Excluded(settings) && quoted.Excluded(settings) && !Origin("rundll32.exe","rundll32.exe shell32.dll,Control_RunDLL").Excluded(settings),"Only the requested system editors and the sysdm environment entry point must be excluded");
        var toys=Origin("PowerToys.EnvironmentVariables.exe");
        assert(toys.Excluded(settings) && !Origin("powershell.exe").Excluded(settings),"PowerToys exclusion must follow the actual writer, not whether an editor happens to be running");
        VariableSnapshot[] baseline=[new(VariableScope.User,"Path",@"C:\old",(int)RegistryValueKind.String)];
        EnvironmentVariableInfo[] incoming=[V("Path",@"C:\old;D:\new")];
        EnvironmentMonitor.PreparePlans(settings,baseline,incoming,true,origins:new Origins(true,toys));
        assert(settings.PendingPathOrganizations.Count==0,"An excluded actual writer must never produce automatic pending work");
        EnvironmentMonitor.PreparePlans(settings,baseline,incoming,true,origins:new Origins(true,Origin("powershell.exe")));
        assert(settings.PendingPathOrganizations.Count==1,"A nonexcluded writer remains eligible while the exclusion switches are enabled");
        EnvironmentMonitor.PreparePlans(settings,baseline,incoming,true,origins:new Origins(false));
        assert(settings.PendingPathOrganizations.Count==0 && settings.MonitorMessage is not null,"Missing origin evidence must leave the change untouched and cancel obsolete pending work");
        settings.TrackChangeOrigins=false;
        EnvironmentMonitor.PreparePlans(settings,baseline,incoming,true);
        assert(settings.PendingPathOrganizations.Count==1,"Saved exclusion preferences remain inactive when precise tracking is off");
        Console.WriteLine("PASS: guarded live three-way drafts, colored review rows, external undo, deletion/duplicate identity, read-only snapshots, strict background startup, and process-accurate exclusion policies.");
    }
}
