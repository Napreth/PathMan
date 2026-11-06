using Microsoft.Win32;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;

internal static class MonitoringDestinationTests
{
    public static void Run(Action<bool,string> assert)
    {
        EnvironmentVariableInfo V(string name,string value,VariableScope scope=VariableScope.User)=>new(name,value,scope,RegistryValueKind.String);
        var state=EnvironmentWorkspaceStore.ReadDefault<EnvironmentWorkspace>("groups");
        state.Groups.Add(new(){Id="machine-only",Name="Machine",Scope=VariableScope.Machine});
        state.Assignments["User:MANUAL_TOOLS"]="paths";state.Assignments["User:OUT_PATH"]="other";
        EnvironmentVariableInfo[] variables=[V("Path",@"C:\base"),V("A_PATH",@"C:\a;D:\a"),V("MANUAL_TOOLS",""),V("OUT_PATH",@"C:\out;D:\out"),
            V("JAVA_HOME",@"C:\Java"),V("EXTERNAL_USER_PATH",@"C:\external;D:\external"),V("TEAM_PATH",@"C:\team;D:\team",VariableScope.Machine)];
        var user=MonitoringDestinations.PathVariables(state,variables,VariableScope.User);
        assert(user.SequenceEqual(new[]{"EXTERNAL_USER_PATH","A_PATH","MANUAL_TOOLS"}),"Only actual path-category members appear after the pinned default, including manual assignments and excluding moved-out variables");
        assert(MonitoringDestinations.PathVariables(state,variables,VariableScope.Machine).SequenceEqual(new[]{"EXTERNAL_PATH","TEAM_PATH"}),"Path destinations remain scope-isolated");
        var count=state.Groups.Count;
        var categories=MonitoringDestinations.Categories(state,VariableScope.User);
        assert(categories[0].Id=="external" && categories.All(group=>group.Id!="machine-only") && state.Groups.Count==count && state.Groups.All(group=>group.Id!="external"),"The default category is pinned and virtual without creating it in saved settings");
        state.Groups.Add(MonitoringDestinations.DefaultExternalGroup());
        assert(MonitoringDestinations.Categories(state,VariableScope.User).Count(group=>group.Id=="external")==1,"An existing default category appears only once and stays first");
        var root=Path.Combine(Path.GetTempPath(),"PathMan.DestinationTests",Guid.NewGuid().ToString("N"));
        try
        {
            var store=new EnvironmentWorkspaceStore(root);
            store.Update(saved=> {saved.MonitoringEnabled=true;saved.ExternalUserGroupId="external";});
            assert(store.Read().Groups.All(group=>group.Id!="external"),"Selecting the default category or enabling monitoring does not create it before it is used");
            new EnvironmentMonitor(store).RefreshPlans(captured:[V("NEW_EXTERNAL","value")]);
            var classified=store.Read();
            assert(classified.Groups.Any(group=>group.Id=="external") && EnvironmentVariableClassifier.Classify(V("NEW_EXTERNAL","value"),[V("NEW_EXTERNAL","value")],classified)=="external","An observed external variable materializes the default category on its first use");
            store.Update(saved=> {saved.Assignments["User:NEW_EXTERNAL"]="other";saved.Groups.RemoveAll(group=>group.Id=="external");});
            assert(store.Read().Groups.All(group=>group.Id!="external"),"Deleting an external category and moving its variables to another category does not immediately recreate it");
            store.Update(saved=> {saved.Groups.Add(new(){Id="chosen",Name="Chosen",Scope=VariableScope.User});saved.ExternalUserGroupId="chosen";saved.Assignments.Remove("User:NEW_EXTERNAL");});
            assert(store.Read().Groups.All(group=>group.Id!="external"),"Choosing another category keeps the unused default deferred");
            var plan=ExternalPathOrganizer.Plan(VariableScope.User,null,[V("Path",@"C:\new")],new(){MonitoringEnabled=true,AutoArrangeUserPath=true});
            assert(plan.Mutations.Any(change=>change.Name=="EXTERNAL_USER_PATH" && change.Action=="create"),"The default path variable is created by actual migration work");
        }
        finally {if(Directory.Exists(root) && Path.GetFullPath(root).StartsWith(Path.Combine(Path.GetTempPath(),"PathMan.DestinationTests")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) Directory.Delete(root,true);}
        Console.WriteLine("PASS: pinned defaults, category-based scoped path choices, manual classification, virtual categories and first-use materialization.");
    }
}
