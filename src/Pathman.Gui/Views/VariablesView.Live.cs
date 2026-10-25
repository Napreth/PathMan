using System.Windows;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;

namespace Pathman.Gui.Views;

public partial class VariablesView
{
    private bool temporaryCleanup,reviewIncoming;
    private string? temporaryCleanupKey;
    public void NotifyIncoming(IReadOnlyList<EnvironmentVariableInfo> current)
    {
        if(HasInlineEditor && !EnvironmentSnapshotWatcher.Equal(allVariables,current))
        {reviewIncoming=true;StatusText.Text="检测到外部更改，结束当前行输入后显示对比。";}
    }
    public async Task<bool> ObserveRegistryAsync(IReadOnlyList<EnvironmentVariableInfo> current)
    {
        if(EnvironmentSnapshotWatcher.Equal(allVariables,current)) return true;
        if(busy || dragging || pathDragging || HasInlineEditor) {NotifyIncoming(current);return false;}
        if(editingSession is not {} session || !HasUnsavedChanges && !session.IsReviewMode && !reviewIncoming && !temporaryCleanup)
        {
            await ReloadAsync(external:true);
            if(CleanupPanel.Visibility==Visibility.Visible) {cleanupPlan=EnvironmentPathCleanup.Analyze(allVariables,RuntimeValues());ShowCleanupPlan();}
            return true;
        }
        busy=true;UpdateActions();
        try
        {
            var snapshot=await Task.Run(()=>(State:store.Read(),Variables:EnvironmentMonitor.ReadAll()));
            var state=snapshot.State;current=snapshot.Variables;
            session.Observe(current);allVariables=current;reviewIncoming=false;
            if(temporaryCleanup) StageExternalCleanup(state);
            var oldGroups=userGroups.Concat(systemGroups).ToArray();
            var open=oldGroups.Where(group=>group.IsExpanded).Select(group=>group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var selectedKey=selected?.Key;
            updating=true;
            userGroups=EnvironmentTreeNode.Build(VariableScope.User,current,state,RuntimeValues());
            systemGroups=EnvironmentTreeNode.Build(VariableScope.Machine,current,state,RuntimeValues());
            foreach(var group in userGroups.Concat(systemGroups)) group.IsExpanded=open.Contains(group.Key);
            UserVariablesTree.ItemsSource=userGroups;SystemVariablesTree.ItemsSource=systemGroups;
            selected=userGroups.Concat(systemGroups).SelectMany(group=>new[] {group}.Concat(group.Children)).FirstOrDefault(node=>node.Key==selectedKey) ?? selected;
            if(selected is not null) selected.IsSelected=true;
            if(session.IsReviewMode) listMode=true;
            RefreshPathEntries();ScalarValueBox.Text=CurrentValue;
            LoadedRevision=state.Revision;
            StatusText.Text=session.HasConflicts ? "外部修改与本地草稿重叠；已保留本地内容，请核对红、绿、蓝三种更改。" : "外部更改已合并到草稿；红色删除、绿色新增、蓝色为本地更改。保存后写入。";
            SnapshotChanged?.Invoke(this,EventArgs.Empty);
        }
        catch(Exception error) when(Expected(error)) {StatusText.Text=error.Message;return false;}
        finally {updating=false;busy=false;SetDirty();UpdateValuePresentation();}
        return true;
    }
    private bool CanCleanSelected=>editingSession?.Root.Name.EndsWith("PATH",StringComparison.OrdinalIgnoreCase)==true;
    private void StageExternalCleanup(EnvironmentWorkspace? settings=null)
    {
        if(editingSession is not {} session || !CanCleanSelected) return;
        settings ??= store.Read();
        // This is a user-requested draft transformation, not a registry writer.
        // The local branch is the baseline, so the user's own additions stay put.
        var root=session.Root;
        settings.AutoArrangeUserPath=settings.AutoArrangeMachinePath=true;
        var before=new VariableSnapshot(root.Scope,root.Name,session.UserBranch(root.Scope,root.Name),(int)root.Kind);
        var plan=ExternalPathOrganizer.Plan(root.Scope,before,session.CurrentVariables(),settings,RuntimeValues(),root.Name);
        session.Stage(plan.Mutations,plan.Preconditions);
        StatusText.Text=plan.Warning ?? (plan.ChangesPathOrder ? "已整理外部新增项的草稿；搜索顺序会改变，请核对后保存。" : "已整理外部新增项的草稿，保存后写入。");
    }
    private async void UndoExternalChanges(object sender,RoutedEventArgs e)
    {
        if(busy || activeEditor?.IsEditing==true && !await activeEditor.CommitAsync() || editingSession is not {} session) return;
        session.UndoExternal();RefreshPathEntries();updating=true;ScalarValueBox.Text=CurrentValue;updating=false;SetDirty();
        StatusText.Text="已撤回外部更改的草稿，本地编辑已保留；保存后写入。";
    }
    private async void CleanExternalChanges(object sender,RoutedEventArgs e)
    {
        if(busy || activeEditor?.IsEditing==true && !await activeEditor.CommitAsync()) return;
        try {StageExternalCleanup();RefreshPathEntries();updating=true;ScalarValueBox.Text=CurrentValue;updating=false;SetDirty();}
        catch(Exception error) when(Expected(error)) {StatusText.Text=error.Message;}
    }
    private void ChangeTemporaryCleanup(object sender,RoutedEventArgs e)
    {
        if(updating) return;
        temporaryCleanup=TemporaryCleanupButton.IsChecked==true;
        temporaryCleanupKey=temporaryCleanup ? selected?.Key : null;
        if(temporaryCleanup && editingSession?.HasExternalChanges==true) CleanExternalChanges(sender,e);
    }
}
