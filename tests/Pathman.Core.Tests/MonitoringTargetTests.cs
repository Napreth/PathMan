using Microsoft.Win32;
using System.Text.Json;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;

internal static class MonitoringTargetTests
{
    public static void Run(Action<bool,string> assert)
    {
        EnvironmentVariableInfo V(string name,string value,VariableScope scope=VariableScope.Machine)=>new(name,value,scope,value.Contains('%') ? RegistryValueKind.ExpandString : RegistryValueKind.String);
        var settings=new EnvironmentWorkspace {MonitoringEnabled=true,AutoArrangeMachinePath=true,ExternalMachinePathVariable="TEAM_PATH"};
        var before=new VariableSnapshot(VariableScope.Machine,"Path",@"C:\base;%SOFTWARE_PATH%",2);
        var current=new[]{V("Path",@"C:\base;%SOFTWARE_PATH%;D:\VSCode;E:\new"),V("SOFTWARE_PATH",@"D:\VSCode")};
        var plan=ExternalPathOrganizer.Plan(VariableScope.Machine,before,current,settings);
        assert(plan.RemovedDuplicates==1 && plan.MovedEntries==1 && plan.Mutations.Single(item=>item.Name=="TEAM_PATH").Value==@"E:\new","An updater's duplicate already covered by an ordinary referenced variable is removed while new paths use the selected target");
        assert(plan.Mutations.All(item=>item.Name is not ("EXTERNAL_PATH" or "SOFTWARE_PATH")),"Selecting a target does not overwrite other variables");
        var changedTarget=settings.ExternalMachinePathVariable="OTHER_PATH";
        assert(plan.Policy!=ExternalPathOrganizer.Policy(settings,VariableScope.Machine),"A destination change invalidates plans prepared for the previous target");
        var cleanup=EnvironmentPathCleanup.Analyze([V("Path",@"C:\Windows;E:\new;%OTHER_PATH%"),V("OTHER_PATH",@"D:\kept")],new Dictionary<string,string>{{"SystemRoot",@"C:\Windows"}},settings);
        assert(cleanup.Scopes.Single().Mutations.Single(item=>item.Name=="OTHER_PATH").Value==@"D:\kept;E:\new","Manual cleanup shares the selected target and appends without replacing its contents");
        assert(cleanup.Scopes.Single().Preconditions.Any(item=>item.Name=="OTHER_PATH" && item.Value==@"D:\kept"),"Manual target append guards its preexisting contents against concurrent changes");
        var groups=EnvironmentWorkspaceStore.ReadDefault<EnvironmentWorkspace>("groups");
        groups.MonitoringEnabled=true;groups.ExternalUserGroupId="custom";groups.Groups.Add(new(){Id="custom",Name="My tools",Scope=VariableScope.User});groups.ExternalVariables.Add("User:NEW_TOOL");
        var variable=V("NEW_TOOL",@"D:\tools",VariableScope.User);
        assert(EnvironmentVariableClassifier.Classify(variable,[variable],groups)=="custom","External variables use the user-selected category");
        groups.Assignments["User:NEW_TOOL"]="other";
        assert(EnvironmentVariableClassifier.Classify(variable,[variable],groups)=="other","Manual classification still takes precedence over the automatic destination");
        var persisted=JsonSerializer.Deserialize<EnvironmentWorkspace>(JsonSerializer.Serialize(settings,EnvironmentWorkspaceStore.JsonOptions),EnvironmentWorkspaceStore.JsonOptions)!;
        assert(persisted.ExternalMachinePathVariable==changedTarget && persisted.ExternalUserPathVariable=="EXTERNAL_USER_PATH","User and machine targets persist independently with safe legacy defaults");
        settings.ExternalMachinePathVariable="Path";
        assert(ExternalPathOrganizer.GroupName(VariableScope.Machine,settings)=="EXTERNAL_PATH" && !ExternalPathOrganizer.IsValidTarget("SystemRoot"),"An invalid persisted target cannot create a self reference or rewrite a built-in variable");
        settings.ExternalMachinePathVariable="TEAM_PATH";
        settings.PendingPathOrganizations.Add(plan);
        var referenceChanged=current.Select(item=>item.Name=="SOFTWARE_PATH" ? V("SOFTWARE_PATH",@"D:\different") : item).ToArray();
        EnvironmentMonitor.PreparePlans(settings,referenceChanged.Select(item=>new VariableSnapshot(item.Scope,item.Name,item.RawValue,(int)item.Kind)).ToArray(),referenceChanged,false);
        assert(settings.PendingPathOrganizations.Single().Id!=plan.Id && settings.PendingPathOrganizations.Single().RemovedDuplicates==0,"A stale referenced value rebuilds the plan even when another observer already updated the snapshot");
        var root=Path.Combine(Path.GetTempPath(),"PathMan.TargetRegression",Guid.NewGuid().ToString("N"));
        try
        {
            var store=new EnvironmentWorkspaceStore(root);
            store.Update(state=> {state.MonitoringEnabled=true;state.AutoArrangeMachinePath=true;state.PendingPathOrganizations.Add(plan);state.MonitorMessage="old stale PATH error";state.MonitorMessageTime=DateTime.Now.AddSeconds(-1);});
            WindowsEnvironmentVariableEditor.RecordOwnChanges(store,[new("delete",VariableScope.Machine,"Path",ExpectedValue:current[0].RawValue)]);
            var state=store.Read();
            assert(state.PendingPathOrganizations.Count==0 && state.MonitorMessage is null,"A user health repair supersedes the old PATH plan and its persistent error without resurrecting a warning");
        }
        finally {if(Directory.Exists(root) && Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)) Directory.Delete(root,true);}
        Console.WriteLine("PASS: configurable PATH and category targets, legacy-reference deduplication, shared manual cleanup and stale-warning retirement.");
    }
}
