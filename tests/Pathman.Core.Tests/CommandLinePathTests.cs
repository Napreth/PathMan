using Microsoft.Win32;
using Pathman.Core;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;
using Pathman.Core.Versions;

internal static class CommandLinePathTests
{
    public static void Run(Action<bool,string> assert)
    {
        var layout=new PathmanLayout(@"C:\Apps\PathMan",@"C:\Users\fixture\PathMan");
        var setting=new CommandLinePathSetting(layout,VariableScope.User);
        var state=new EnvironmentWorkspace();
        EnvironmentVariableInfo V(string name,string value,VariableScope scope=VariableScope.User)=>new(name,value,scope,RegistryValueKind.ExpandString);
        EnvironmentVariableInfo[] Apply(EnvironmentVariableInfo[] current,CommandLinePathPlan plan)=>current.Where(item=>!plan.Mutations.Any(change=>change.Scope==item.Scope && change.Name.Equals(item.Name,StringComparison.OrdinalIgnoreCase)))
            .Concat(plan.Mutations.Select(change=>V(change.Name,change.Value!,change.Scope))).ToArray();
        assert(!state.CommandLineVersionSwitchingEnabled,"Command-line version switching is disabled by default");
        EnvironmentVariableInfo[] current=[V("Path",@"C:\first;;C:\tail;")];
        var plan=setting.Plan(true,state,current);
        assert(plan.Mutations.Single().Value==@"C:\first;;C:\tail;;"+layout.BinDirectory,"Direct CLI registration preserves existing empty items and trailing separators");
        var enabled=Apply(current,plan);
        assert(setting.Plan(true,state,enabled).Mutations.Count==0,"Repeated CLI enable is a no-op");
        assert(setting.Plan(false,state,enabled).Mutations.Single().Value==current[0].RawValue,"Disabling removes the CLI without consuming original empty entries");
        plan=setting.Plan(true,state,[V("Path",@"C:\first;"+layout.ShimsDirectory)]);
        assert(plan.Mutations.Single().Value==@"C:\first;"+layout.BinDirectory+";"+layout.ShimsDirectory,"Direct CLI registration keeps shims last in the user PATH");
        current=[V("Path",@"%TOOLS_PATH%"),V("TOOLS_PATH",@"C:\first;"+layout.BinDirectory)];
        assert(setting.Plan(true,state,current).Mutations.Count==0,"Existing recursive PATH coverage never gains a duplicate CLI entry");
        plan=setting.Plan(false,state,current);
        assert(plan.Mutations.Count==1 && plan.Mutations[0].Name=="TOOLS_PATH" && plan.Mutations[0].Value==@"C:\first","Disabling removes a nested CLI leaf while retaining its group reference");
        state.MonitoringEnabled=true;state.AutoArrangeUserPath=true;
        current=[V("Path",@"C:\Windows;%SOFTWARE12_PATH%\"),V("SOFTWARE_PATH",@"C:\unused"),V("SOFTWARE12_PATH",@"C:\tools;")];
        plan=setting.Plan(true,state,current);
        assert(plan.Mutations.Count==2 && plan.Mutations.Single(item=>item.Name=="EXTERNAL_USER_PATH").Value==layout.BinDirectory && plan.Mutations.All(item=>item.Name is "Path" or "EXTERNAL_USER_PATH"),"Automatic organization writes only PATH and the selected destination");
        assert(setting.Plan(true,state,Apply(current,plan)).Mutations.Count==0,"A CLI already exposed through the selected destination causes no writes");
        assert(!EnvironmentHealth.Analyze(current,workspace:state).Issues.Any(item=>item.CheckId=="path-organization-reference"),"An absent destination creates no missing-reference error");
        var unreferencedSoftware=setting.Plan(false,state,[V("Path",@"C:\first"),V("SOFTWARE_PATH",layout.BinDirectory),V("SOFTWARE123_PATH",layout.BinDirectory)]);
        assert(unreferencedSoftware.Mutations.Count==0,"SOFTWARE_PATH and numbered names have no special behavior when they are not referenced or selected");
        current=[V("Path",@"C:\Windows")];
        plan=setting.Plan(true,state,current);
        assert(plan.Mutations.Count==2 && plan.Mutations.Any(item=>item.Name=="EXTERNAL_USER_PATH" && item.Action=="create") && plan.Mutations.Any(item=>item.Name=="Path" && item.Value!.Contains("%EXTERNAL_USER_PATH%")),"An explicit CLI enable creates the external group and its usable PATH reference together");
        var issue=EnvironmentHealth.Analyze([V("Path",@"C:\Windows"),V("EXTERNAL_USER_PATH",@"C:\tools")],workspace:state).Issues.Single(item=>item.CheckId=="path-organization-reference");
        assert(issue.IsError && issue.Scope==VariableScope.User,"An existing nonempty configured target without a PATH reference is reported in its own scope");
        assert(!EnvironmentHealth.Analyze(Apply(current,plan),workspace:state).Issues.Any(item=>item.CheckId=="path-organization-reference"),"CLI registration leaves a usable target instead of immediately producing a health error");
        current=[V("Path",@"%SOFTWARE3_PATH%"),V("UNRELATED","unchanged")];
        assert(setting.Plan(true,state,current).Mutations.Any(item=>item.Name=="EXTERNAL_USER_PATH") && setting.Plan(true,state,current).Mutations.All(item=>item.Name!="SOFTWARE3_PATH"),"An unrelated dangling reference is not repurposed as the selected destination");
        current=[V("Path",@"C:\first;"+layout.BinDirectory+";%SOFTWARE_PATH%"),V("SOFTWARE_PATH",@"C:\tools")];
        plan=setting.Plan(true,state,current);
        assert(plan.Mutations.Count==2 && plan.Mutations.Single(item=>item.Name=="Path").Value==@"C:\first;%SOFTWARE_PATH%;%EXTERNAL_USER_PATH%","Enabling automatic placement migrates a direct CLI entry into the selected target while preserving other references");
        var machine=new CommandLinePathSetting(layout,VariableScope.Machine);
        state.AutoArrangeUserPath=false;state.AutoArrangeMachinePath=true;
        state.ExternalMachinePathVariable="TEAM_PATH";
        plan=machine.Plan(true,state,[V("Path",@"C:\Windows;%TEAM_PATH%",VariableScope.Machine),V("TEAM_PATH",@"D:\tools",VariableScope.Machine),V("TEAM_PATH",@"D:\user")]);
        assert(plan.Mutations.Single().Scope==VariableScope.Machine && plan.Mutations.Single().Value==@"D:\tools;"+layout.BinDirectory && plan.Preconditions.All(item=>item.Scope==VariableScope.Machine),"Installation scope isolates system and user software variables");
        state.AutoArrangeMachinePath=false;
        assert(!EnvironmentHealth.Analyze([V("Path",@"C:\Windows")],workspace:state).Issues.Any(item=>item.CheckId=="path-organization-reference"),"Disabling automatic organization removes the software-reference requirement");
        Console.WriteLine("PASS: opt-in CLI PATH registration, selected destinations, ordinary variable names, nested removal, empty entries, scope isolation and missing-reference errors.");
    }
}
