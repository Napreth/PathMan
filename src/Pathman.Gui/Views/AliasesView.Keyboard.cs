using System.Windows;
using System.Windows.Input;
using Pathman.Core.Shims;
using Pathman.Gui.Controls;

namespace Pathman.Gui.Views;

public partial class AliasesView
{
    private readonly EditHistory history=new();
    private void RememberAlias(ShimAlias? before,ShimAlias after)
    {
        if(before==after) return;
        if(before is null)
            history.Record(()=>Task.Run(()=>manager.Remove(after.Name,after.Target)),()=>Task.Run(()=>manager.Add(after.Name,after.Target)));
        else if(before.Name!=after.Name)
            history.Record(()=>Task.Run(()=>manager.Rename(after.Name,before.Name,after.Target)),()=>Task.Run(()=>manager.Rename(before.Name,after.Name,before.Target)));
        else
            history.Record(()=>Task.Run(()=>manager.Update(before.Name,before.Target,after.Target)),()=>Task.Run(()=>manager.Update(after.Name,after.Target,before.Target)));
    }
    private void RememberDeletedAliases(ShimAlias[] deleted)
    {
        history.Record(()=>Task.Run(()=> {foreach(var row in deleted) manager.Add(row.Name,row.Target);}),
            ()=>Task.Run(()=> {foreach(var row in deleted) manager.Remove(row.Name,row.Target);}));
    }
    private async void AliasKeyDown(object sender,KeyEventArgs e)
    {
        if(IsBusy) return;
        if(EditingKeys.Control && e.Key==Key.S) {e.Handled=true;await CommitActive();return;}
        if(EditingKeys.InTextInput(e)) return;
        if(EditingKeys.Control && e.Key==Key.A) {e.Handled=true;Aliases.SelectAll();return;}
        if(!enabled) return;
        if(EditingKeys.Undo(e) || EditingKeys.Redo(e))
        {
            var forward=EditingKeys.Redo(e);e.Handled=true;if(!await CommitActive()) return;
            busy=true;UpdateActions();
            try
            {
                if(await history.ApplyAsync(forward))
                {
                    var saved=await Task.Run(manager.List);rows.Clear();foreach(var alias in saved) rows.Add(new(alias.Name,alias.Target));
                    AliasStatus.Text=L.Get("已保存。");SnapshotChanged?.Invoke(this,EventArgs.Empty);
                }
            }
            catch(Exception error) when(Expected(error)) {AliasStatus.Text=error.Message;}
            finally {busy=false;UpdateActions();Aliases.Focus();}
        }
        else if(e.Key==Key.Delete) {e.Handled=true;DeleteAlias(this,new());}
        else if(e.Key==Key.F2 && Aliases.SelectedItems.Count==1) {e.Handled=true;RenameAlias(this,new());}
        else if(e.Key==Key.Enter && Aliases.SelectedItems.Count==1) {e.Handled=true;EditAlias(this,new());}
        else if(EditingKeys.New(e)) {e.Handled=true;NewAlias(this,new());}
        else if(EditingKeys.Control && e.Key==Key.C) {e.Handled=true;await Copy(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));}
        else if(EditingKeys.Control && e.Key==Key.S) {e.Handled=true;await CommitActive();}
    }
}
