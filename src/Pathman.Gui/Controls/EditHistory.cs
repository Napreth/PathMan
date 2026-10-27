namespace Pathman.Gui.Controls;

internal sealed class EditHistory
{
    private sealed record Change(Func<Task> Undo,Func<Task> Redo);
    private readonly List<Change> undo=[],redo=[];
    public void Record(Func<Task> backward,Func<Task> forward)
    {undo.Add(new(backward,forward));if(undo.Count>128) undo.RemoveAt(0);redo.Clear();}
    public async Task<bool> ApplyAsync(bool forward)
    {
        var source=forward ? redo : undo;var target=forward ? undo : redo;
        if(source.Count==0) return false;
        var change=source[^1];await (forward ? change.Redo() : change.Undo());
        source.RemoveAt(source.Count-1);target.Add(change);return true;
    }
}
