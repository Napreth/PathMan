using Microsoft.Win32;
using Pathman.Core;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;
using Pathman.Core.Shims;

internal static class HealthAliasRegressionTests
{
    public static void Run(Action<bool,string> assert)
    {
        EnvironmentVariableInfo U(string name,string value)=>new(name,value,VariableScope.User,RegistryValueKind.ExpandString);
        var direct=EnvironmentHealth.Analyze([U("Path",@"%A_PATH%;C:\plain;%MISSING%;%B_PATH%;%MISSING%"),U("A_PATH",@"C:\a"),U("B_PATH",@"C:\b")]);
        var undefined=direct.Issues.Single(issue=>issue.Kind==EnvironmentIssueKind.UndefinedReference && issue.Name=="Path");
        var detail=EnvironmentHealthDetails.For(undefined);
        assert(detail.Count==1 && detail[0].Snapshot.Name=="Path" && detail[0].ProblemLines.SetEquals([3,5]),"Undefined references highlight their complete rows without displaying unrelated referenced variables");
        assert(undefined.Sources.Count==3,"Display filtering retains all snapshots needed for stale-repair checks");
        var nested=EnvironmentHealth.Analyze([U("Path","%A_PATH%;%UNRELATED_PATH%"),U("A_PATH",@"%B_PATH%;C:\a"),U("B_PATH",@"C:\one;;C:\two"),U("UNRELATED_PATH",@"C:\unrelated")]);
        var empty=nested.Issues.Single(issue=>issue.Kind==EnvironmentIssueKind.EmptyPathEntry && issue.Name=="Path");
        detail=EnvironmentHealthDetails.For(empty);
        assert(detail.Select(item=>item.Snapshot.Name).SequenceEqual(["Path","A_PATH","B_PATH"]) && detail[0].ProblemLines.SetEquals([1]) && detail[1].ProblemLines.SetEquals([1]) && detail[2].ProblemLines.SetEquals([2]),"Nested findings retain only the recursive problem chain and its exact affected rows");
        var cycle=EnvironmentHealth.Analyze([U("Path","%A_PATH%"),U("A_PATH","%B_PATH%"),U("B_PATH","%A_PATH%;%UNRELATED_PATH%"),U("UNRELATED_PATH",@"C:\healthy")]);
        detail=EnvironmentHealthDetails.For(cycle.Issues.First(issue=>issue.Kind==EnvironmentIssueKind.ReferenceCycle));
        assert(detail.All(item=>item.Snapshot.Name!="UNRELATED_PATH") && detail.Any(item=>item.Snapshot.Name=="B_PATH" && item.ProblemLines.SetEquals([1])),"Cycle details exclude healthy sibling references");
        var wrongType=EnvironmentHealth.Analyze([new("Path",@"C:\plain;%A_PATH%;C:\tail",VariableScope.User),U("A_PATH",@"C:\a")]).Issues.Single(issue=>issue.Kind==EnvironmentIssueKind.RegistryType);
        detail=EnvironmentHealthDetails.For(wrongType);
        assert(detail.Count==1 && detail[0].ProblemLines.SetEquals([2]),"Registry-type details mark reference rows without including the referenced value");

        var root=Path.Combine(Path.GetTempPath(),"PathMan-alias-literal-"+Guid.NewGuid().ToString("N"));
        try
        {
            var layout=new PathmanLayout(Path.Combine(root,"app"),Path.Combine(root,"data"));Directory.CreateDirectory(layout.InternalDirectory);File.WriteAllBytes(layout.ShimExecutable,[1,2,3]);
            var manager=new ShimManager(layout,ensureUserPath:false);
            var targets=new[]{"",@"C:\missing\app.exe","?:<>|","relative","  C:\bad  ","C:\\line\r\nbreak","C:\\nul\0value","\"C:\\quoted.exe\""};
            for(var index=0;index<targets.Length;index++) manager.Add("literal-"+index,targets[index]);
            var listed=manager.List();
            assert(Enumerable.Range(0,targets.Length).All(index=>listed.Single(alias=>alias.Name=="literal-"+index).Target==targets[index]),"Empty, nonexistent and invalid alias targets round-trip literally without path validation");
            var orphan=Path.Combine(layout.ShimsDirectory,"orphan.exe");File.Copy(layout.ShimExecutable,orphan);
            assert(manager.List().Single(alias=>alias.Name=="orphan").Target=="","An executable without a manifest remains visible as an alias");
            manager.Rename("orphan","renamed-orphan","");manager.Update("renamed-orphan","invalid?target","");
            assert(manager.List().Single(alias=>alias.Name=="renamed-orphan").Target=="invalid?target","Orphan aliases can be renamed and repaired without a valid target");
            File.WriteAllText(Path.Combine(layout.ShimsDirectory,"legacy.shim"),"[PathMan.Shim]\r\nFormat=1\r\nTarget=C:\\missing.exe\r\n",System.Text.Encoding.Unicode);
            assert(manager.List().Single(alias=>alias.Name=="legacy").Target==@"C:\missing.exe","Legacy manifests remain readable even when their targets do not exist");
            manager.Remove("legacy",@"C:\missing.exe");manager.Remove("renamed-orphan","invalid?target");
        }
        finally {if(Directory.Exists(root)) Directory.Delete(root,true);}
        Console.WriteLine("PASS: precise health detail chains and rows; literal invalid/empty alias targets, legacy manifests and orphan repair.");
    }
}
