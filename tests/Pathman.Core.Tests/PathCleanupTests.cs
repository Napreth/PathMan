using Microsoft.Win32;
using Pathman.Core;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;
using Pathman.Core.Shims;

internal static class PathCleanupTests
{
    public static void Run(Action<bool,string> assert)
    {
        EnvironmentVariableInfo M(string name,string raw)=>new(name,raw,VariableScope.Machine,raw.Contains('%') ? RegistryValueKind.ExpandString : RegistryValueKind.String);
        EnvironmentVariableInfo U(string name,string raw)=>new(name,raw,VariableScope.User,raw.Contains('%') ? RegistryValueKind.ExpandString : RegistryValueKind.String);
        var runtime=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) { ["SystemRoot"]=@"C:\Windows" };
        EnvironmentVariableInfo[] input=[
            M("Path",@"%SystemRoot%\system32;C:\WINDOWS;C:\Windows\System32\Wbem;C:\Windows\System32\WindowsPowerShell\v1.0\;C:\Windows\System32\OpenSSH;C:\apps;C:\APPS\;%JAVA_HOME%\bin;%DEVELOP_PATH%\;C:\dev;C:\shared;C:\Windows\Custom"),
            M("JAVA_HOME",@"C:\Java"),M("DEVELOP_PATH",@"C:\dev;C:\shared"),M("SOFTWARE_PATH",@"C:\occupied"),U("SOFTWARE1_PATH",@"D:\occupied"),
            U("Path",@"C:\one;%DEVELOP_PATH%;c:\one\;C:\dev;%JAVA_HOME%\bin;C:\Java\bin")];
        var plan=EnvironmentPathCleanup.Analyze(input,runtime);
        var machine=plan.Scopes.Single(scope=>scope.Scope==VariableScope.Machine);
        var user=plan.Scopes.Single(scope=>scope.Scope==VariableScope.User);
        assert(machine.SoftwareVariable=="SOFTWARE2_PATH" && machine.RemovedDuplicates==3,"Cleanup must deduplicate after expansion and reserve names in both scopes");
        assert(machine.Mutations.Single(change=>change.Action=="create").Value==@"C:\apps;%JAVA_HOME%\bin;C:\Windows\Custom","Only software entries and non-pure references must be migrated, preserving their text");
        assert(machine.Mutations.Single(change=>change.Action=="set").Value==@"%SystemRoot%\system32;C:\WINDOWS;C:\Windows\System32\Wbem;C:\Windows\System32\WindowsPowerShell\v1.0\;C:\Windows\System32\OpenSSH;%DEVELOP_PATH%\;%SOFTWARE2_PATH%","Windows component paths and pure references must stay in system PATH");
        assert(user.MovedEntries==0 && user.Mutations.Count==1 && user.Mutations[0].Value==@"C:\one;%DEVELOP_PATH%;%JAVA_HOME%\bin","User cleanup must deduplicate without creating a software variable");
        assert(plan.Warnings.Count==0,"Nonoverlapping references must not create warnings");
        var updated=input.ToList();
        foreach(var scope in plan.Scopes) foreach(var change in scope.Mutations)
        {
            updated.RemoveAll(item=>item.Scope==change.Scope && item.Name.Equals(change.Name,StringComparison.OrdinalIgnoreCase));
            updated.Add(new(change.Name,change.Value!,change.Scope,change.Value!.Contains('%') ? RegistryValueKind.ExpandString : RegistryValueKind.String));
        }
        assert(!EnvironmentPathCleanup.Analyze(updated,runtime).HasChanges,"Repeated cleanup must be idempotent");

        EnvironmentVariableInfo[] overlaps=[U("Path",@"C:\overlap;%A_PATH%;%B_PATH%;%a_path%\"),U("A_PATH",@"%NESTED_PATH%;C:\same;c:\SAME\"),U("NESTED_PATH",@"C:\overlap"),U("B_PATH",@"C:\overlap;D:\unique")];
        var overlapPlan=EnvironmentPathCleanup.Analyze(overlaps,runtime);
        assert(overlapPlan.Scopes[0].RemovedDuplicates==2 && overlapPlan.Scopes[0].Mutations.Single().Value=="%A_PATH%;%B_PATH%","Direct duplicates must yield to referenced paths; repeated pure references must be removed");
        assert(overlapPlan.Warnings.Any(item=>item.Name=="A_PATH" && item.Message.Contains("重复")) && overlapPlan.Warnings.Any(item=>item.Name=="Path" && item.Message.Contains("重叠")),"Internal and cross-variable duplicates must produce navigable warnings");
        assert(overlapPlan.Scopes.SelectMany(scope=>scope.Mutations).All(change=>change.Name=="Path"),"Cleanup must not rewrite referenced path variables");
        var nested=EnvironmentPathCleanup.Analyze([U("Path","%A_PATH%"),U("A_PATH","%B_PATH%"),U("B_PATH",@"C:\one;D:\two")],runtime);
        assert(nested.Warnings.Count==0 && !nested.HasChanges,"A nested reference is not by itself a duplicate");
        var resolver=new EnvironmentPathResolver([M("A_PATH",@"C:\machine"),U("A_PATH",@"D:\user"),U("ONLY_USER",@"D:\private")],runtime);
        assert(resolver.Expand("%a_path%",VariableScope.Machine).Paths.Single()==@"C:\machine" && resolver.Expand("%A_PATH%",VariableScope.User).Paths.Single()==@"D:\user","Expansion must respect the saved scope and user overrides");
        assert(!resolver.Expand("%ONLY_USER%",VariableScope.Machine).IsResolved,"A machine value cannot inherit a user-only variable from the process");
        assert(!new EnvironmentPathResolver([],new Dictionary<string,string> { ["STALE_CUSTOM_PATH"]=@"C:\stale" }).Expand("%STALE_CUSTOM_PATH%",VariableScope.User).IsResolved,"A stale process-only custom value must not be used to delete persisted PATH entries");
        var unresolved=EnvironmentPathCleanup.Analyze([U("Path",@"%A_PATH%;%MISSING%"),U("A_PATH","%B_PATH%"),U("B_PATH","%A_PATH%")],runtime);
        assert(!unresolved.HasChanges && unresolved.Warnings.Count>=2,"Cyclic and missing references must be retained and reported");
        var collision=EnvironmentPathCleanup.Analyze([M("Path",@"C:\tool;%SOFTWARE_PATH%")],runtime);
        assert(collision.Scopes[0].SoftwareVariable=="SOFTWARE1_PATH","Creating a software variable must not accidentally bind an existing unresolved reference");
        assert(EnvironmentPathResolver.PureReference(@"%SDK_PATH%\")=="SDK_PATH" && EnvironmentPathResolver.PureReference(@"%SDK_PATH%\bin")==null && EnvironmentPathResolver.PureReference("\"%SDK_PATH%\"")==null,"Pure reference parsing must allow trailing separators but no suffix or quotes");
        var purePreferred=EnvironmentPathCleanup.Analyze([U("Path",@"C:\Windows;%SystemRoot%")],runtime);
        assert(purePreferred.Scopes.Single().Mutations.Single().Value=="%SystemRoot%","Pure references must win over equivalent literal paths even if the literal occurs first");

        var layout=new PathmanLayout(@"C:\PathMan",@"C:\Users\fixture\AppData\Local\PathMan");
        var aliases=new AliasPathSetting(layout);
        var shim=layout.ShimsDirectory;
        var aliasInput=new[] { M("Path",@"C:\Windows"),U("Path",$@"{shim};C:\tools;{shim.ToUpperInvariant()}\;") };
        var enable=aliases.Plan(true,aliasInput)!;
        assert(enable.Scope==VariableScope.User && enable.Value==$@"C:\tools;;{shim}","Enabling aliases must place one shim entry last without changing other entries or system PATH");
        assert(aliases.Plan(false,aliasInput)!.Value==@"C:\tools;","Disabling aliases must remove only the shim directory");
        assert(aliases.IsEnabled(aliasInput) && !aliases.IsEnabled([M("Path",shim)]),"The alias switch must reflect the user PATH, independently of machine PATH");
        assert(aliases.Plan(true,[]) is { Action:"create",Scope:VariableScope.User } && aliases.Plan(false,[]) is null,"The alias switch must handle a missing user PATH");
        Console.WriteLine("PASS: PATH cleanup, Windows locations, expanded deduplication, nested/cyclic/overlapping references, collision-safe names, idempotence, scope isolation, and alias PATH ordering.");
    }
}
