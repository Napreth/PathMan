using Microsoft.Win32;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;

internal static class MonitoringTests
{
    public static void Run(Action<bool,string> assert)
    {
        EnvironmentVariableInfo V(string name,string value,VariableScope scope=VariableScope.User)=>new(name,value,scope,value.Contains('%') ? RegistryValueKind.ExpandString : RegistryValueKind.String);
        VariableSnapshot S(string value,VariableScope scope=VariableScope.User)=>new(scope,"Path",value,(int)RegistryValueKind.String);
        var runtime=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) {["SystemRoot"]=@"C:\Windows"};
        var settings=new EnvironmentWorkspace {AutoArrangeUserPath=true,AutoArrangeMachinePath=true};
        EnvironmentVariableInfo[] Apply(IReadOnlyList<EnvironmentVariableInfo> items,ExternalPathOrganizationPlan plan)=>items
            .Where(item=>!plan.Mutations.Any(mutation=>mutation.Scope==item.Scope && mutation.Name.Equals(item.Name,StringComparison.OrdinalIgnoreCase)))
            .Concat(plan.Mutations.Select(mutation=>V(mutation.Name,mutation.Value!,mutation.Scope))).ToArray();
        ExternalPathOrganizationPlan Plan(string before,params EnvironmentVariableInfo[] items)=>ExternalPathOrganizer.Plan(VariableScope.User,S(before),items,settings,runtime);

        var basic=Plan(@"C:\old;C:\old",V("Path",@"C:\old;C:\old;C:\NEW;c:\old\;D:\new"));
        assert(basic.RemovedDuplicates==1 && basic.MovedEntries==2,"Only newly added duplicates and software entries may be arranged");
        assert(basic.Mutations.Single(item=>item.Name=="Path").Value==@"C:\old;C:\old;%EXTERNEL_USER_PATH%" && basic.Mutations.Single(item=>item.Name==ExternalPathOrganizer.GroupName(VariableScope.User)).Value==@"C:\NEW;D:\new","Existing duplicates must remain untouched and new paths retain their text");
        assert(!basic.ChangesPathOrder,"Consecutive new paths can move behind a reference without changing expanded order");
        var current=new[] {V("Path",@"C:\old;C:\old;C:\NEW;c:\old\;D:\new")};
        var applied=Apply(current,basic);
        var repeated=ExternalPathOrganizer.Plan(VariableScope.User,S(applied.Single(item=>item.Name=="Path").RawValue),applied,settings,runtime);
        assert(repeated.Mutations.Count==0,"The organizer must not react to its own output");

        var interleaved=Plan(@"C:\old;D:\old",V("Path",@"C:\old;C:\new;D:\old;D:\new"));
        assert(interleaved.ChangesPathOrder,"Merging nonconsecutive additions must report changed command precedence");
        var existing=Plan(@"%EXTERNEL_USER_PATH%;C:\old",V("Path",@"%EXTERNEL_USER_PATH%;C:\old;D:\new"),V("EXTERNEL_USER_PATH",@"C:\external"));
        assert(existing.Mutations.Single(item=>item.Name=="EXTERNEL_USER_PATH").Value==@"C:\external;D:\new" && existing.Mutations.Single(item=>item.Name=="Path").Value==@"%EXTERNEL_USER_PATH%;C:\old" && existing.ChangesPathOrder,"Append to an existing external group without adding a second reference; report its order impact");
        var emptyGroup=Plan(@"%EXTERNEL_USER_PATH%;C:\old",V("Path",@"%EXTERNEL_USER_PATH%;C:\old;D:\new"),V("EXTERNEL_USER_PATH",""));
        assert(emptyGroup.Mutations.Single(item=>item.Name=="EXTERNEL_USER_PATH").Value==@";D:\new","Migration must preserve pre-existing empty entries in an existing external variable");
        var scopes=Plan(@"C:\user",V("Path",@"C:\user;c:\windows\;D:\tool"),V("Path",@"C:\Windows",VariableScope.Machine));
        assert(scopes.RemovedDuplicates==1 && scopes.Preconditions.Any(item=>item.Scope==VariableScope.Machine),"User duplicates must account for the preceding machine PATH with guarded inputs");

        var references=Plan(@"%SDK_PATH%",V("Path",@"%SDK_PATH%;C:\sdk;%MORE_PATH%"),V("SDK_PATH",@"C:\sdk"),V("MORE_PATH",@"C:\sdk;D:\unique"));
        assert(references.RemovedDuplicates==1 && references.Mutations.Single(item=>item.Name=="EXTERNEL_USER_PATH").Value=="%MORE_PATH%","Fully overlapping new entries can be removed; partially overlapping references must retain their unique paths");
        assert(references.Preconditions.Any(item=>item.Name=="MORE_PATH"),"Referenced input values must be guarded against external races");
        var missing=Plan(@"C:\old",V("Path",@"C:\old;%MISSING%;D:\new"));
        assert(missing.Mutations.Count==0 && missing.Warning is not null,"Unresolved additions must stay in place rather than silently change their binding");
        var cycle=Plan(@"C:\old",V("Path",@"C:\old;%A_PATH%"),V("A_PATH","%EXTERNEL_USER_PATH%"),V("EXTERNEL_USER_PATH",@"D:\old"));
        assert(cycle.Mutations.Count==0 && cycle.Warning is not null,"Indirect external-group references must never create a cycle");
        var pathCycle=Plan(@"C:\old",V("Path",@"C:\old;%Path%"));
        assert(pathCycle.Mutations.Count==0,"Self-referencing PATH entries must not be moved");
        var machine=ExternalPathOrganizer.Plan(VariableScope.Machine,S(@"C:\Windows",VariableScope.Machine),[V("Path",@"C:\Windows;%USER_ONLY%",VariableScope.Machine),V("USER_ONLY",@"C:\private")],settings,runtime);
        assert(machine.Mutations.Count==0,"Machine organization must not expand user-only definitions");

        settings.MoveExternalUserPathEntries=false;
        var dedupOnly=Plan(@"C:\old",V("Path",@"C:\old;C:\new;c:\OLD\"));
        assert(dedupOnly.Mutations.Count==1 && dedupOnly.Mutations[0].Value==@"C:\old;C:\new","Duplicate and migration suboptions must work independently");
        settings.RemoveUserPathDuplicates=false;settings.MoveExternalUserPathEntries=true;
        var moveOnly=Plan(@"C:\old",V("Path",@"C:\old;C:\old"));
        assert(moveOnly.RemovedDuplicates==0 && moveOnly.MovedEntries==1,"Disabling deduplication must preserve duplicate new entries during migration");
        settings.AutoArrangeUserPath=false;
        assert(Plan(@"C:\old",V("Path",@"C:\old;D:\new")).Mutations.Count==0,"Automatic PATH rewriting must default to opt-in");
        settings.AutoArrangeUserPath=true;settings.RemoveUserPathDuplicates=true;

        var before=new[] {S(@"C:\old")};
        var state=new EnvironmentWorkspace {MonitoringEnabled=true,AutoArrangeUserPath=true,AutoArrangeMachinePath=true,Snapshot=before.ToList()};
        var changed=EnvironmentMonitor.Compare(state,[V("Path",@"C:\old;D:\new"),V("NEW_VAR","added")]);
        assert(changed && !state.ExternalVariables.Contains("User:Path") && state.ExternalVariables.Contains("User:NEW_VAR"),"External grouping applies to newly created variables, not edits to an existing PATH");
        assert(EnvironmentMonitor.PreparePlans(state,before,[V("Path",@"C:\old;D:\new")],changed,runtime) && state.PendingPathOrganizations.Count==1,"Monitoring must durably queue a plan before applying it");
        var id=state.PendingPathOrganizations[0].Id;
        assert(!EnvironmentMonitor.PreparePlans(state,state.Snapshot,[V("Path",@"C:\old;D:\new")],false,runtime) && state.PendingPathOrganizations[0].Id==id,"Unchanged pending work must keep its identity without repeated writes");
        EnvironmentMonitor.PreparePlans(state,state.Snapshot,[V("Path",@"C:\old;D:\new;E:\new")],true,runtime);
        assert(state.PendingPathOrganizations[0].MovedEntries==2,"Additional external writes while waiting for authorization must retain the first baseline");
        var createdPath=new EnvironmentWorkspace {MonitoringEnabled=true,AutoArrangeUserPath=true,AutoArrangeMachinePath=true};
        EnvironmentMonitor.PreparePlans(createdPath,[],[V("Path",@"C:\new")],true,runtime);
        EnvironmentMonitor.PreparePlans(createdPath,[S(@"C:\new")],[V("Path",@"C:\new;D:\new")],true,runtime);
        assert(createdPath.PendingPathOrganizations.Single().MovedEntries==2 && createdPath.PendingPathOrganizations.Single().BeforePath is null,"A newly created PATH retains its absent baseline while waiting across multiple external writes");
        state.AutoArrangeUserPath=state.AutoArrangeMachinePath=false;
        assert(EnvironmentMonitor.PreparePlans(state,state.Snapshot,[],false,runtime) && state.PendingPathOrganizations.Count==0,"Disabling automation must discard pending work");

        settings.AutoArrangeMachinePath=false;
        var machineInput=new[] {V("Path",@"C:\machine;D:\new",VariableScope.Machine),V("Path",@"C:\user;E:\new")};
        assert(ExternalPathOrganizer.Plan(VariableScope.Machine,S(@"C:\machine",VariableScope.Machine),machineInput,settings,runtime).Mutations.Count==0 &&
            ExternalPathOrganizer.Plan(VariableScope.User,S(@"C:\user"),machineInput,settings,runtime).MovedEntries==1,"User-only automation must leave the system PATH untouched");
        settings.AutoArrangeUserPath=false;settings.AutoArrangeMachinePath=true;
        assert(ExternalPathOrganizer.Plan(VariableScope.User,S(@"C:\user"),machineInput,settings,runtime).Mutations.Count==0 &&
            ExternalPathOrganizer.Plan(VariableScope.Machine,S(@"C:\machine",VariableScope.Machine),machineInput,settings,runtime).MovedEntries==1,"System-only automation must leave the user PATH untouched");
        assert(ExternalPathOrganizer.GroupName(VariableScope.User)=="EXTERNEL_USER_PATH" && ExternalPathOrganizer.GroupName(VariableScope.Machine)=="EXTERNEL_PATH","User and system migrations must use their own explicit target names");
        settings.AutoArrangeUserPath=true;settings.RemoveUserPathDuplicates=false;settings.MoveExternalUserPathEntries=false;
        assert(ExternalPathOrganizer.Plan(VariableScope.User,S(@"C:\user"),machineInput,settings,runtime).Mutations.Count==0 &&
            ExternalPathOrganizer.Plan(VariableScope.Machine,S(@"C:\machine",VariableScope.Machine),machineInput,settings,runtime).MovedEntries==1,"Each scope's duplicate and migration suboptions must remain independent");
        var separate=new EnvironmentWorkspace {MonitoringEnabled=true,AutoArrangeUserPath=true,AutoArrangeMachinePath=true};
        VariableSnapshot[] original=[S(@"C:\user"),S(@"C:\machine",VariableScope.Machine)];
        EnvironmentMonitor.PreparePlans(separate,original,machineInput,true,runtime);
        assert(separate.PendingPathOrganizations.Count==2,"Both scopes can be enabled at the same time");
        var machineId=separate.PendingPathOrganizations.Single(item=>item.Scope==VariableScope.Machine).Id;
        separate.AutoArrangeUserPath=false;
        separate.RemoveUserPathDuplicates=false;separate.MoveExternalUserPathEntries=false;
        EnvironmentMonitor.PreparePlans(separate,original,machineInput,false,runtime);
        assert(separate.PendingPathOrganizations.Count==1 && separate.PendingPathOrganizations[0].Id==machineId,"Disabling user automation cancels only user work without invalidating an unchanged system plan");
        separate.AutoArrangeUserPath=true;separate.AutoArrangeMachinePath=false;
        EnvironmentMonitor.PreparePlans(separate,original,machineInput,false,runtime);
        assert(separate.PendingPathOrganizations.Count==0,"Enabling a scope establishes no retroactive work; disabling system automation cancels its pending authorization");
        var migrationRoot=Path.Combine(Path.GetTempPath(),"PathMan.MonitorMigration",Guid.NewGuid().ToString("N"));
        try
        {
            var migration=new EnvironmentWorkspaceStore(migrationRoot);
            migration.Update(item=> {item.LegacyAutoArrangePath=true;item.LegacyGroupExternalVariables=false;item.LegacyRemovePathDuplicates=false;item.LegacyMovePathEntries=false;});
            var migrated=migration.Read();
            assert(migrated.AutoArrangeUserPath && migrated.AutoArrangeMachinePath && migrated.LegacyAutoArrangePath is null,"The previous shared switch migrates to both new scope switches");
            assert(!migrated.GroupExternalUserVariables && !migrated.GroupExternalMachineVariables && !migrated.RemoveUserPathDuplicates && !migrated.RemoveMachinePathDuplicates && !migrated.MoveExternalUserPathEntries && !migrated.MoveExternalMachinePathEntries,"Previous shared grouping and child options migrate into each independent scope card");
            migration.Update(item=>item.AutoArrangeMachinePath=false);
            var reloaded=migration.Read();
            assert(reloaded.AutoArrangeUserPath && !reloaded.AutoArrangeMachinePath && !File.ReadAllText(migration.FilePath).Contains("\"autoArrangePath\""),"Scope choices persist independently and the legacy switch is retired after saving");
        }
        finally {if(Directory.Exists(migrationRoot)) Directory.Delete(migrationRoot,true);}

        var defaults=new EnvironmentWorkspaceStore(Path.Combine(Path.GetTempPath(),"PathMan.Nonexistent",Guid.NewGuid().ToString("N"))).Read();
        defaults.MonitoringEnabled=true;defaults.GroupExternalUserVariables=false;defaults.ExternalVariables.Add("User:NEW_VAR");
        assert(defaults.VisibleGroups(VariableScope.User).All(item=>item.Rule!="external") && EnvironmentVariableClassifier.Classify(V("NEW_VAR","added"),[V("NEW_VAR","added")],defaults,runtime)=="other","The grouping setting is independent from background recording");
        defaults.ExternalVariables.Add("Machine:NEW_VAR");
        assert(defaults.VisibleGroups(VariableScope.Machine).Any(item=>item.Rule=="external") && EnvironmentVariableClassifier.Classify(V("NEW_VAR","added",VariableScope.Machine),[V("NEW_VAR","added",VariableScope.Machine)],defaults,runtime)=="external","Disabling user classification must leave system external classification enabled");

        var unchanged=PathOrderComparison.Compare(@"C:\a;D:\b;C:\a",@"C:\a;D:\b",VariableScope.User,[],[],runtime);
        assert(!unchanged.Changed,"Removing redundant later copies must not claim that command precedence changed");
        assert(PathOrderComparison.Compare(@"C:\a;D:\b;C:\a",@"D:\b;C:\a",VariableScope.User,[],[],runtime).Changed,"Removing an earlier copy can change precedence and must be detected");
        assert(!PathOrderComparison.Compare("%UNKNOWN%","%UNKNOWN%",VariableScope.User,[],[],runtime).IsResolved,"Unknown references must not claim a verified order");
        var cleanup=EnvironmentPathCleanup.Analyze([V("Path",@"C:\tool;C:\Windows;D:\tool",VariableScope.Machine)],runtime);
        assert(cleanup.Scopes.Single().ChangesPathOrder,"Manual software migration must expose its search-order warning in the preview model");
        Console.WriteLine("PASS: external-only PATH organization, independent options, scope/recursive deduplication, pending work, cycle safety, classification, and effective-order warnings.");
    }
}
