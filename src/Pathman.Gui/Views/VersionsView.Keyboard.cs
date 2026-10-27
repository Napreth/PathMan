using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Versions;
using Pathman.Gui.Controls;

namespace Pathman.Gui.Views;

public partial class VersionsView
{
    private readonly EditHistory history=new();
    private static string VersionConfig(EnvironmentWorkspace state)=>JsonSerializer.Serialize(state.VersionProjects,EnvironmentWorkspaceStore.JsonOptions);
    private Task RestoreVersionConfig(string expected,string value)=>Task.Run(()=>store.Update(state=>
    {
        if(VersionConfig(state)!=expected) throw new InvalidOperationException("版本配置已被其他程序修改，无法撤销或重做。请重新载入后操作。");
        state.VersionProjects=JsonSerializer.Deserialize<List<VersionProject>>(value,EnvironmentWorkspaceStore.JsonOptions)!;
    }));
    private async void VersionKeyDown(object sender,KeyEventArgs e)
    {
        if(IsBusy) return;
        if(EditingKeys.Control && e.Key==Key.S) {e.Handled=true;await CommitActive();return;}
        if(EditingKeys.InTextInput(e)) return;
        var left=ProjectsList.IsKeyboardFocusWithin;
        if(EditingKeys.Control && e.Key==Key.A) {e.Handled=true;if(left) ProjectsList.SelectAll();else VersionsList.SelectAll();return;}
        if(EditingKeys.Undo(e) || EditingKeys.Redo(e))
        {
            var forward=EditingKeys.Redo(e);e.Handled=true;if(!await CommitActive()) return;
            busy=true;UpdateActions();
            try {if(await history.ApplyAsync(forward)) refreshPending=true;}
            catch(Exception error) when(Expected(error)) {VersionStatus.Text=error.Message;}
            finally {busy=false;await ReloadAsync();}
        }
        else if(e.Key==Key.Delete)
        {
            e.Handled=true;
            if(!left) {DeleteVersion(this,new());return;}
            var selected=ProjectsList.SelectedItems.OfType<ProjectRow>().ToArray();
            if(selected.Length==0) return;
            if(activeEditor?.DataContext is ProjectRow editing && selected.Contains(editing)) activeEditor.CancelEdit();else if(!await CommitActive()) return;
            await Metadata(state=> {foreach(var row in selected.Where(row=>!row.IsDraft)) VersionCatalog.Delete(state,row.Name);},()=> {foreach(var row in selected) projects.Remove(row);});await ReloadAsync();
        }
        else if(EditingKeys.New(e)) {e.Handled=true;if(left) NewProject(this,new());else if(AddVersionButton.IsEnabled) AddVersion(this,new());}
        else if(e.Key==Key.F2)
        {
            e.Handled=true;
            if(left && ProjectsList.SelectedItems.Count==1 && Selected is {} row && await CommitActive()) FindCell(ProjectsList,row,"project")?.BeginEdit();
            else if(!left && RenameVersionButton.IsEnabled) RenameVersion(this,new());
        }
        else if(e.Key==Key.Enter && !left && EditVersionButton.IsEnabled) {e.Handled=true;EditVersion(this,new());}
        else if(EditingKeys.Control && e.Key==Key.C)
        {
            e.Handled=true;
            if(!left) CopyVersion(this,new());
            else try {Clipboard.SetDataObject(string.Join(Environment.NewLine,ProjectsList.SelectedItems.OfType<ProjectRow>().OrderBy(projects.IndexOf).Select(row=>$"{row.Name}\t{row.BindingText}")),true);}
                catch(System.Runtime.InteropServices.ExternalException) {VersionStatus.Text="剪贴板暂时不可用。";}
        }
        else if(EditingKeys.Control && e.Key==Key.S) {e.Handled=true;await CommitActive();}
    }
}
