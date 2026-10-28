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
        assert(CommandLinePathSetting.IsSoftwareVariable("SOFTWARE_PATH") && CommandLinePathSetting.IsSoftwareVariable("software123_path") &&
            !CommandLinePathSetting.IsSoftwareVariable("SOFTWARE_PATH_EXTRA") && !CommandLinePathSetting.IsSoftwareVariable("SOFTWAREX_PATH") && !CommandLinePathSetting.IsSoftwareVariable("PATH"),"Software path names allow only an optional numeric suffix");
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
        assert(plan.Mutations.Count==1 && plan.Mutations[0].Name=="SOFTWARE12_PATH" && plan.Mutations[0].Value==@"C:\tools;;"+layout.BinDirectory,"Automatic organization writes directly to the numbered group actually referenced by PATH");
        assert(setting.Plan(true,state,Apply(current,plan)).Mutations.Count==0,"A CLI already in SOFTWARE*_PATH causes no writes");
        assert(!EnvironmentHealth.Analyze(current,workspace:state).Issues.Any(item=>item.CheckId=="software-path-reference"),"A numbered pure reference with a trailing slash satisfies the health check");
        current=[V("Path",@"C:\Windows")];
        plan=setting.Plan(true,state,current);
        assert(plan.Mutations.Count==1 && plan.Mutations[0].Name=="SOFTWARE_PATH" && plan.Mutations[0].Action=="create","An absent software group is created without silently writing PATH");
        var issue=EnvironmentHealth.Analyze(Apply(current,plan),workspace:state).Issues.Single(item=>item.CheckId=="software-path-reference");
        assert(issue.IsError && issue.Scope==VariableScope.User,"Automatic organization without a software reference is an error in its own scope");
        current=[V("Path",@"%SOFTWARE3_PATH%"),V("UNRELATED","unchanged")];
        assert(setting.Plan(true,state,current).Mutations.Single().Name=="SOFTWARE3_PATH","A dangling numbered reference receives its matching variable rather than a new unrelated group");
        current=[V("Path",@"C:\first;"+layout.BinDirectory+";%SOFTWARE_PATH%"),V("SOFTWARE_PATH",@"C:\tools")];
        plan=setting.Plan(true,state,current);
        assert(plan.Mutations.Count==2 && plan.Mutations.Single(item=>item.Name=="Path").Value==@"C:\first;%SOFTWARE_PATH%","Enabling automatic placement migrates an existing direct CLI entry without duplicating it");
        var machine=new CommandLinePathSetting(layout,VariableScope.Machine);
        state.AutoArrangeUserPath=false;state.AutoArrangeMachinePath=true;
        plan=machine.Plan(true,state,[V("Path",@"C:\Windows;%SOFTWARE1_PATH%",VariableScope.Machine),V("SOFTWARE1_PATH",@"D:\tools",VariableScope.Machine),V("SOFTWARE1_PATH",@"D:\user")]);
        assert(plan.Mutations.Single().Scope==VariableScope.Machine && plan.Mutations.Single().Value==@"D:\tools;"+layout.BinDirectory && plan.Preconditions.All(item=>item.Scope==VariableScope.Machine),"Installation scope isolates system and user software variables");
        state.AutoArrangeMachinePath=false;
        assert(!EnvironmentHealth.Analyze([V("Path",@"C:\Windows")],workspace:state).Issues.Any(item=>item.CheckId=="software-path-reference"),"Disabling automatic organization removes the software-reference requirement");
        Console.WriteLine("PASS: opt-in CLI PATH registration, numbered software groups, nested removal, empty entries, scope isolation and missing-reference errors.");
    }
}
