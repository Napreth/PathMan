using Pathman.Core;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;
using Pathman.Core.Shims;

internal static class AliasAndHealthTests
{
    public static void Run(Action<bool,string> assert)
    {
        void Reject(Action action,string message) {var failed=false;try {action();}catch(Exception error) when(error is ArgumentException or IOException){failed=true;}assert(failed,message);}
        foreach(var invalid in new[]{"", ".", "..", "CON", "con.exe", "LPT¹.txt", "COM9.data", "NUL .txt", "bad.", "bad ", "a/b", "a\\b", "a:b", "a|b", "a?b", "a*b", "a\"b", "a<b", "a>b", "a\0b", "a\nb",new string('a',251)})
            Reject(()=>ShimManager.ValidateName(invalid),"Alias names reject Windows-invalid characters, reserved devices, trailing punctuation and excessive length");
        assert(ShimManager.ValidateName("p'w'sh-中文.exe")=="p'w'sh-中文" && ShimManager.ValidateName("with spaces")=="with spaces","Unicode, apostrophes and spaces remain valid alias names");
        var root=Path.Combine(Path.GetTempPath(),"PathMan-alias-core-"+Guid.NewGuid().ToString("N"));
        try
        {
            var layout=new PathmanLayout(Path.Combine(root,"app"),Path.Combine(root,"data"));Directory.CreateDirectory(layout.InternalDirectory);
            File.WriteAllBytes(layout.ShimExecutable,[1,2,3]);var a=Path.Combine(root,"a.exe");var b=Path.Combine(root,"b.exe");File.WriteAllBytes(a,[4]);File.WriteAllBytes(b,[5]);
            var manager=new ShimManager(layout,ensureUserPath:false);manager.Add("first",a);manager.Add("second",b);
            Reject(()=>manager.Rename("first","SECOND",a),"Rename cannot overwrite a case-insensitive alias collision");
            Reject(()=>manager.Update("first",b,"stale"),"Target edits check the saved target before overwriting");
            manager.Update("first",b,a);manager.Rename("first","FIRST",b);
            assert(manager.List().Any(alias=>alias.Name=="FIRST" && alias.Target==b) && File.Exists(Path.Combine(layout.ShimsDirectory,"FIRST.exe")),"Target edits and case-only renames keep executable/manifest pairs usable");
            var manifest=Path.Combine(layout.ShimsDirectory,"FIRST.shim");
            using(var locked=new FileStream(manifest,FileMode.Open,FileAccess.Read,FileShare.Read))
                Reject(()=>manager.Rename("FIRST","third",b),"A locked manifest rejects rename before publishing a new alias");
            assert(File.Exists(Path.Combine(layout.ShimsDirectory,"FIRST.exe")) && manager.List().Any(alias=>alias.Name=="FIRST"),"A failed rename restores the original executable and manifest");
            manager.Remove("FIRST",b);assert(!manager.List().Any(alias=>alias.Name=="FIRST"),"Deletion removes the selected alias without deleting its target");
        }
        finally {if(Directory.Exists(root)) Directory.Delete(root,true);}
        EnvironmentVariableInfo U(string name,string value)=>new(name,value,VariableScope.User);
        const string shims=@"C:\PathMan-data\shims";
        EnvironmentHealthReport Check(params EnvironmentVariableInfo[] vars)=>EnvironmentHealth.Analyze(vars,shimsDirectory:shims);
        assert(!Check(U("Path",@"C:\tools;"+shims)).Issues.Any(issue=>issue.Kind==EnvironmentIssueKind.AliasPathOrder),"A unique shim directory at the bottom of user PATH is healthy");
        var misplaced=Check(U("Path",shims+@";C:\tools")).Issues.Single(issue=>issue.Kind==EnvironmentIssueKind.AliasPathOrder);
        assert(!misplaced.IsError && misplaced.Fixes.Single().Mutations.Single().Value==@"C:\tools;"+shims,"A misplaced user shim directory has a warning and a guarded move-to-end action");
        assert(Check(new("Path",shims,VariableScope.Machine),U("Path",@"C:\tools")).Issues.Any(issue=>issue.Kind==EnvironmentIssueKind.AliasPathOrder && issue.Fixes.Count==0 && issue.Sources.Count==2),"A machine shim directory warns and includes both PATH scopes in details");
        assert(Check(U("Path",shims+";"+shims)).Issues.Any(issue=>issue.Kind==EnvironmentIssueKind.AliasPathOrder),"An earlier duplicate shim directory still warns when the last entry is correct");
        assert(EnvironmentHealth.DefaultChecks.Select(check=>check.Id).Distinct().Count()==EnvironmentHealth.DefaultChecks.Count && EnvironmentHealth.DefaultChecks.Any(check=>check is AliasPathOrderHealthCheck),"Independent health check classes are discovered automatically with stable IDs");
        var custom=new DelegateHealthCheck("test-check","新检查",context=>[new(EnvironmentIssueKind.Custom,VariableScope.User,"X","custom") {IsError=true}]);
        var report=EnvironmentHealth.Analyze([],checks:[custom]);
        assert(report.Errors==1 && report.Issues[0].Category=="新检查" && report.Issues[0].CheckId=="test-check","Function checks supply their category and severity without modifying the analyzer or GUI");
        Console.WriteLine("PASS: alias name rules, guarded CRUD and rename rollback; automatic/function health checks and shim PATH order.");
    }
}
