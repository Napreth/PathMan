using Pathman.Core;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;
using Pathman.Core.Versions;

namespace Pathman.Cli;

internal static class VersionCommands
{
    private static EnvironmentWorkspaceStore Store(PathmanLayout layout)=>new(layout.DataDirectory);
    private static string Display(string value)=>value.Replace("\r","\\r").Replace("\n","\\n");
    public static void List(PathmanLayout layout,string[] args)
    {
        if(args.Length>1) throw new ArgumentException("用法：pathman list [项目]");
        var state=Store(layout).Read();
        if(args.Length==1) {var selected=VersionCatalog.Find(state,args[0]);state.VersionProjects=[selected];}
        var projects=VersionCatalog.Describe(state,EnvironmentMonitor.ReadAll());
        if(projects.Count==0) {Console.WriteLine("尚未登记版本项目。使用 pathman add 登记。");return;}
        foreach(var item in projects)
        {
            Console.WriteLine($"{item.Name}:");
            Console.WriteLine($"    VariableName: {item.VariableName}");
            Console.WriteLine($"    RefsByPATH: {item.RefsByPATH}");
            Console.WriteLine($"    Type: {item.Type}");
            Console.WriteLine("    Versions:");
            foreach(var version in item.Project.Versions) Console.WriteLine($"        {version.Key}: {Display(version.Value)}");
            var labels=item.CurrentVersions.Count>0 ? $" ({string.Join(", ",item.CurrentVersions)})" : "";
            Console.WriteLine($"    Current: {(item.Current is null ? "(undefined)" : Display(item.Current))}{labels}");
        }
    }
    public static void Add(PathmanLayout layout,string[] args)
    {
        if(args.Length<3) throw new ArgumentException("用法：pathman add <项目> <版本> <值> [--variable <变量名>] [--type User|Machine] [--replace]");
        string? variable=null;VariableScope? scope=null;var replace=false;var seen=new HashSet<string>();
        for(var index=3;index<args.Length;index++)
        {
            var option=args[index];
            if(!seen.Add(option)) throw new ArgumentException($"重复的参数：{option}");
            if(option=="--replace") {replace=true;continue;}
            if(option is not ("--variable" or "--type") || ++index>=args.Length) throw new ArgumentException($"未知或缺少值的参数：{option}");
            if(option=="--variable") variable=args[index];
            else scope=args[index].ToLowerInvariant() switch {"user"=>VariableScope.User,"machine"=>VariableScope.Machine,_=>throw new ArgumentException("--type 仅支持 User 或 Machine。")};
        }
        Store(layout).Update(state=>VersionCatalog.Add(state,args[0],args[1],args[2],variable,scope,replace));
        Console.WriteLine($"已登记 {args[0]} / {args[1]}；当前环境变量未改动。");
    }
    public static void Delete(PathmanLayout layout,string project,string? version)
    {
        Store(layout).Update(state=>VersionCatalog.Delete(state,project,version));
        Console.WriteLine($"已删除版本配置 {project}{(version is null ? "" : "/"+version)}；环境变量及当前值已保留。");
    }
    public static void Switch(PathmanLayout layout,string project,string version)
    {
        var store=Store(layout);
        var mutation=VersionCatalog.PrepareSwitch(store.Read(),project,version,EnvironmentMonitor.ReadAll());
        WindowsEnvironmentVariableEditor.Apply(mutation,store);
        Console.WriteLine($"已切换 {project} → {version}：{mutation.Name}={Display(mutation.Value!)}");
    }
}
