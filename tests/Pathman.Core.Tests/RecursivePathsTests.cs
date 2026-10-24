using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;
using Pathman.Core.Notifications;

internal static class RecursivePathsTests
{
    public static void Run(Action<bool,string> assert)
    {
        EnvironmentVariableInfo U(string name,string raw)=>new(name,raw,VariableScope.User);
        EnvironmentVariableInfo[] variables=[U("Path",@"%A_PATH%;C:\last"),U("A_PATH",@"%B_PATH%;C:\a"),U("B_PATH",@"C:\b1;C:\b2")];
        var session=new PathEditingSession(variables[0],variables);
        var expanded=new HashSet<string>();
        var rows=session.VisibleRows(expanded);
        assert(rows.Count==2 && rows[0].IsGroup,"Pure path references start collapsed without flattening the saved value");
        expanded.Add(rows[0].ViewKey); rows=session.VisibleRows(expanded);
        expanded.Add(rows[1].ViewKey); rows=session.VisibleRows(expanded);
        assert(rows.Count==6 && rows[2].Depth==2 && rows[2].Item.Value==@"C:\b1","Path references recursively expand with stable ownership and indentation");
        var firstLeaf=rows[2].Item.Id;
        session.Move(firstLeaf,VariableScope.User,"Path",session.Items(VariableScope.User,"Path")[1].Id);
        assert(session.Raw(VariableScope.User,"B_PATH")==@"C:\b2" && session.Raw(VariableScope.User,"Path")==@"%A_PATH%;C:\b1;C:\last","A nested leaf can move to the root without copying the rest of its group");
        var group=session.Items(VariableScope.User,"Path")[0];
        assert(session.ValidateMove(group.Id,VariableScope.User,"B_PATH",null) is not null,"A reference group cannot be dragged into its own descendants");
        session.Move(session.Items(VariableScope.User,"Path")[2].Id,VariableScope.User,"Path",group.Id);
        assert(session.Raw(VariableScope.User,"Path")==@"C:\last;%A_PATH%;C:\b1","Literal and reference rows can freely reorder among one another");
        var nestedGroup=session.Items(VariableScope.User,"A_PATH")[0];
        session.Move(nestedGroup.Id,VariableScope.User,"Path");
        assert(session.Raw(VariableScope.User,"A_PATH")==@"C:\a" && session.Raw(VariableScope.User,"Path").EndsWith("%B_PATH%"),"Reference groups can move between variables while preserving their raw references");
        var draft=session.Append(VariableScope.User,"B_PATH",draft:true);
        assert(session.Raw(VariableScope.User,"B_PATH")==@"C:\b2","An empty draft is excluded from the staged value");
        session.Update(draft.Id,"new"); session.Update(draft.Id,"");
        assert(session.Raw(VariableScope.User,"B_PATH")==@"C:\b2","Clearing an edited row removes only that occurrence");
        assert(session.Mutations().Count==3 && session.Preconditions(VariableScope.User).Count==3,"A recursive save tracks every changed variable and all materialized guards");
        session.Accept(VariableScope.User); assert(!session.HasChanges,"Accepted saves become the new lossless baseline");
        var cyclic=new PathEditingSession(U("A_PATH","%B_PATH%"),[U("A_PATH","%B_PATH%"),U("B_PATH","%A_PATH%")]);
        var cycleOpen=new HashSet<string>(); var cycleRows=cyclic.VisibleRows(cycleOpen); cycleOpen.Add(cycleRows[0].ViewKey); cycleRows=cyclic.VisibleRows(cycleOpen);
        assert(cycleRows.Count==2 && cycleRows[1].IsCycle && !cycleRows[1].IsExpanded,"Recursive expansion stops at cycles");
        var scoped=new PathEditingSession(U("Path",@"C:\user;%SHARED_PATH%"),[U("Path",@"C:\user;%SHARED_PATH%"),new("SHARED_PATH",@"C:\shared",VariableScope.Machine)]);
        assert(scoped.ValidateMove(scoped.Items(VariableScope.User,"Path")[0].Id,VariableScope.Machine,"SHARED_PATH",null) is not null && !scoped.HasChanges,"Cross-scope drops are rejected without mutating the staged data");

        var health=EnvironmentHealth.Analyze([U("A_PATH","%B_PATH%"),U("B_PATH","%A_PATH%"),U("BROKEN","%MISSING%"),U("PATHEXT",".EXE;.exe"),U("Path",@"C:\one;c:\ONE\;")]);
        assert(health.Issues.Any(item=>item.Kind==EnvironmentIssueKind.ReferenceCycle) && health.Issues.Any(item=>item.Kind==EnvironmentIssueKind.UndefinedReference),"Health detection covers cycles and undefined references outside PATH");
        assert(health.Issues.Count(item=>item.Kind==EnvironmentIssueKind.Duplicate)==2 && health.Issues.Any(item=>item.Kind==EnvironmentIssueKind.EmptyPathEntry),"Health detection covers expanded path duplicates, other list duplicates and empty path entries");
        assert(EnvironmentHealth.Analyze([U("Path","%EMPTY_PATH%"),U("EMPTY_PATH","")]).Issues.Any(item=>item.Kind==EnvironmentIssueKind.EmptyPathEntry),"Empty entries produced by references are detected too");
        var text="hello "+Osc8Text.Link("变量","apppage:variables?scope=User&name=A_PATH")+"!";
        var spans=Osc8Text.Parse(text);
        assert(spans.Count==3 && spans[1].Link!.StartsWith("apppage:") && spans[1].Text=="变量","OSC8 ST links preserve plain text before and after the hyperlink");
        assert(Osc8Text.Parse("\u001b]8;;apppage:health\a健康\u001b]8;;\a").Single().Text=="健康","OSC8 BEL terminators are supported");
        var link=new AppPageLink("variables",VariableScope.Machine,"中文_%NAME%");
        assert(AppPageLink.Parse(link.ToString())==link && AppPageLink.Parse("apppage:health")?.Page=="health","Internal page routes round-trip Unicode variable names");
        assert(AppPageLink.Parse("apppage:unknown")==null && AppPageLink.Parse("javascript:alert(1)")==null && AppPageLink.Parse("apppage:variables?scope=9&name=X")==null,"Internal routes reject unknown pages and invalid scopes");
        var diff=EnvironmentValueDiff.Create("a;b;c","a;x;c;d");
        assert(diff.Where(item=>item.Kind!=ValueDiffKind.Added).Select(item=>item.Text).SequenceEqual(["a","b","c"]) &&
            diff.Where(item=>item.Kind!=ValueDiffKind.Removed).Select(item=>item.Text).SequenceEqual(["a","x","c","d"]),"Unified diffs reconstruct both sides without losing order");
        Console.WriteLine("PASS: recursive path projections, ordered moves, cycle guards, staged multi-variable saves, health analysis, OSC8, internal routes and unified diffs.");
    }
}
