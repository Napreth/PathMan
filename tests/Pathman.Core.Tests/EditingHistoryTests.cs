using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;

internal static class EditingHistoryTests
{
    public static void Run(Action<bool,string> assert)
    {
        EnvironmentVariableInfo V(string name,string value)=>new(name,value,VariableScope.User);
        var root=V("Path",@"C:\one;;C:\two");
        var session=new PathEditingSession(root,[root]);
        var ids=session.Items(root.Scope,root.Name).Select(item=>item.Id).ToArray();
        session.RemoveMany([ids[0],ids[2]]);
        assert(session.Raw(root.Scope,root.Name)=="" && session.Items(root.Scope,root.Name).Count==1,"Bulk deletion preserves an unselected empty entry");
        assert(session.Undo() && session.Raw(root.Scope,root.Name)==root.RawValue && session.Items(root.Scope,root.Name).Select(item=>item.Id).SequenceEqual(ids),"Undo restores separators, order and stable row identities as one operation");
        assert(session.Redo() && session.Raw(root.Scope,root.Name)=="","Redo repeats the whole bulk operation");
        session.Undo();session.Update(ids[1],@"C:\middle");
        assert(!session.CanRedo,"A new edit discards the redo branch");
        var before=session.Raw(root.Scope,root.Name);var draft=session.Append(root.Scope,root.Name,draft:true);
        session.Update(draft.Id,@"D:\new");session.Undo();
        assert(session.Raw(root.Scope,root.Name)==before && session.Items(root.Scope,root.Name).All(item=>!item.IsDraft),"Undoing a committed new row removes its draft too");
        session.Stage([new("create",VariableScope.User,"EXTERNAL_USER_PATH",@"D:\external")],[]);
        session.Undo();assert(session.Mutations().All(item=>item.Name!="EXTERNAL_USER_PATH"),"Undoing staged cleanup removes newly introduced variables instead of creating empty ones");
        var a=V("Path",@"%TOOLS_PATH%;C:\last");var b=V("TOOLS_PATH",@"C:\first;C:\second");
        var nested=new PathEditingSession(a,[a,b]);var moved=nested.Items(b.Scope,b.Name)[0].Id;
        nested.Move(moved,a.Scope,a.Name);nested.Undo();
        assert(nested.Raw(a.Scope,a.Name)==a.RawValue && nested.Raw(b.Scope,b.Name)==b.RawValue,"Undo restores both owners of a recursive path move");
        nested.Update(nested.Items(a.Scope,a.Name)[1].Id,@"C:\mine");
        nested.Observe([V(a.Name,a.RawValue+@";D:\incoming"),b]);
        assert(!nested.CanUndo && nested.Raw(a.Scope,a.Name).Contains(@"D:\incoming"),"External observations invalidate stale undo frames without losing incoming changes");
        var merged=nested.Raw(a.Scope,a.Name);nested.Append(a.Scope,a.Name,@"E:\mine");nested.Undo();
        assert(nested.Raw(a.Scope,a.Name)==merged && nested.HasExternalChanges && nested.IsReviewMode,"Undo within a review retains the external branch and review state");
        nested.Accept(VariableScope.User);assert(!nested.CanUndo && !nested.CanRedo,"Saved edits cannot be replayed through stale draft history");
        Console.WriteLine("PASS: bulk undo/redo, empty separators, stable row identities, draft cleanup, recursive owners, staged variables and live-review history boundaries.");
    }
}
