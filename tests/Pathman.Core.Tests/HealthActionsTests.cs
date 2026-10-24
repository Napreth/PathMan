using Microsoft.Win32;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;

internal static class HealthActionsTests
{
    public static void Run(Action<bool,string> assert)
    {
        EnvironmentVariableInfo U(string name,string value)=>new(name,value,VariableScope.User,value.Contains('%') ? RegistryValueKind.ExpandString : RegistryValueKind.String);
        var original=U("Path",@"C:\a;;C:\b;");
        var session=new PathEditingSession(original,[original]);
        var rows=session.Items(VariableScope.User,"Path");
        assert(rows.Count==4 && rows[1].Value=="" && rows[3].Value=="","Consecutive and trailing semicolons have separate visible empty rows");
        session.Update(rows[1].Id,"",false);
        assert(!session.HasChanges && rows.Count==4,"Opening and leaving an existing empty row preserves it unchanged");
        session.Update(rows[1].Id,"temporary"); session.Update(rows[1].Id,"");
        assert(session.Raw(VariableScope.User,"Path")==@"C:\a;C:\b;","Editing an empty row and clearing it removes exactly its separator position");
        session.Update(rows[1].Id,"");
        assert(session.Raw(VariableScope.User,"Path")==@"C:\a;","Clearing a populated row removes the item while preserving pre-existing empty rows");
        var empties=EnvironmentHealth.Analyze([original]).Issues.Where(issue=>issue.Kind==EnvironmentIssueKind.EmptyPathEntry).ToArray();
        assert(empties.Length==2 && empties.Select(issue=>issue.Id).Distinct().Count()==2,"Each empty occurrence has a separate finding and ignore identity");
        assert(empties[0].Locations[0] is {Line:2,Previous:@"C:\a",Next:@"C:\b"} && empties[1].Locations[0].Next is null,"Empty findings show exact owner, line and neighboring entries");
        assert(empties[0].Fixes.Single().Mutations.Single().Value==@"C:\a;C:\b;","An empty-row fix removes one entry and preserves all remaining raw values");
        var stale=false;try {empties[0].Fixes[0].Validate([U("Path",@"C:\new")]);}catch(IOException){stale=true;}
        assert(stale,"A changed source invalidates an old health repair");
        var changed=EnvironmentHealth.Analyze([U("Path",@"C:\different;;C:\b;")]).Issues.First(issue=>issue.Kind==EnvironmentIssueKind.EmptyPathEntry);
        assert(changed.Id!=empties[0].Id,"Changed source content is not hidden by a previous ignore decision");
        EnvironmentVariableInfo[] duplicate=[U("Path",@"C:\same;%TOOLS_PATH%"),U("TOOLS_PATH",@"C:\same;C:\unique")];
        var finding=EnvironmentHealth.Analyze(duplicate).Issues.Single(issue=>issue.Kind==EnvironmentIssueKind.Duplicate && issue.Name=="Path");
        assert(finding.Fixes.Count==2 && finding.Fixes.Select(fix=>fix.Mutations[0].Name).Distinct().Count()==2,"Expanded duplicate findings offer deletion from either actual source");
        void PreservesPaths(EnvironmentVariableInfo[] variables,EnvironmentHealthFix fix)
        {
            fix.Validate(variables);
            var updated=variables.Select(item=>fix.Mutations.FirstOrDefault(change=>change.Scope==item.Scope && change.Name==item.Name) is {} mutation ? U(item.Name,mutation.Value!) : item).ToArray();
            HashSet<string> Paths(EnvironmentVariableInfo[] values)=>new EnvironmentPathResolver(values).Expand(values[0].RawValue,VariableScope.User).Paths.Select(path=>EnvironmentPathResolver.Normalize(path)!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            assert(Paths(variables).SetEquals(Paths(updated)),"A duplicate repair preserves every distinct expanded path");
        }
        foreach(var fix in finding.Fixes) PreservesPaths(duplicate,fix);
        EnvironmentVariableInfo[] reused=[U("Path","%TOOLS_PATH%;%TOOLS_PATH%"),U("TOOLS_PATH",@"C:\one;C:\two")];
        foreach(var issue in EnvironmentHealth.Analyze(reused).Issues.Where(issue=>issue.Kind==EnvironmentIssueKind.Duplicate && issue.Name=="Path"))
        {
            assert(issue.Fixes.Count==2 && issue.Fixes.All(fix=>fix.Mutations[0].Name=="Path"),"Repeated reference repairs remove one reference rather than their shared definition");
            foreach(var fix in issue.Fixes) PreservesPaths(reused,fix);
        }
        var errors=EnvironmentHealth.Analyze([U("ONLY_USER",@"C:\user"),new("MACHINE_REF","%ONLY_USER%",VariableScope.Machine,RegistryValueKind.ExpandString),new("PROFILE_REF","%USERPROFILE%",VariableScope.Machine,RegistryValueKind.ExpandString)],new Dictionary<string,string>{{"USERPROFILE",@"C:\user"}});
        assert(errors.Issues.Count(issue=>issue.Kind==EnvironmentIssueKind.UserScopeReference && issue.IsError)==2 && !errors.Issues.Any(issue=>issue.Kind==EnvironmentIssueKind.UndefinedReference),"Machine-to-user and user-only built-in references are explicit errors");
        assert(!new EnvironmentPathResolver([],new Dictionary<string,string>{{"USERPROFILE",@"C:\user"}}).Expand("%USERPROFILE%",VariableScope.Machine).IsResolved,"Machine expansion cannot leak a user-only built-in from the running process");
        var shadow=EnvironmentHealth.Analyze([U("BOTH",@"C:\user"),new("BOTH",@"C:\machine",VariableScope.Machine),new("REF","%BOTH%",VariableScope.Machine,RegistryValueKind.ExpandString)]);
        assert(!shadow.Issues.Any(issue=>issue.Kind==EnvironmentIssueKind.UserScopeReference),"A machine definition remains usable even when a user override exists");
        var type=EnvironmentHealth.Analyze([new("REF","%SystemRoot%",VariableScope.User,RegistryValueKind.String)],new Dictionary<string,string>{{"SystemRoot",@"C:\Windows"}}).Issues.Single(issue=>issue.Kind==EnvironmentIssueKind.RegistryType);
        assert(type.IsError && type.Fixes.Single().Mutations.Single().Value=="%SystemRoot%","The registry type fix preserves raw references and reports an error");
        var pairs=EnvironmentValueDiff.Pair(EnvironmentValueDiff.Create("a;b;c;d","a;x;y;z;d"));
        assert(pairs.Where(pair=>pair.Before is not null).Select(pair=>pair.Before!.Text).SequenceEqual(["a","b","c","d"]) &&
            pairs.Where(pair=>pair.After is not null).Select(pair=>pair.After!.Text).SequenceEqual(["a","x","y","z","d"]),"Side-by-side diff rows reconstruct both values without duplicating context");
        assert(pairs.Any(pair=>pair.Before is null && pair.After?.Text=="z") && pairs[^1].After?.NewLine==5,"Side-by-side diffs align unequal edit blocks and preserve each side's line numbers");
        Console.WriteLine("PASS: unchanged empty rows, separator deletion, contextual health repairs, stale guards, shared-reference safety, scope/type errors and side-by-side diffs.");
    }
}
