using System.Collections;
using System.Windows;
using System.Windows.Controls;
using Pathman.Core;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;
using Pathman.Gui.Dialogs;

namespace Pathman.Gui.Views;

public partial class MonitorView
{
    private sealed record TargetChoice(string Value,string Label,string OwnerName,bool IsCreate=false);
    private string? targetError,unavailableTarget;
    private static VariableScope TargetScope(ComboBox choice)=>choice.Name.StartsWith("User",StringComparison.Ordinal) ? VariableScope.User : VariableScope.Machine;
    private static bool IsCategory(ComboBox choice)=>choice.Name.EndsWith("ExternalGroup",StringComparison.Ordinal);
    private static IReadOnlyDictionary<string,string> RuntimeValues()=>Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
        .Where(item=>item.Key is string && item.Value is string).ToDictionary(item=>(string)item.Key,item=>(string)item.Value!,StringComparer.OrdinalIgnoreCase);
    private void RefreshTargets(EnvironmentWorkspace state,GlobalMonitorSettings global,bool force=false)
    {
        unavailableTarget=null;
        foreach(var choice in new[]{UserExternalGroup,MachineExternalGroup})
        {
            if(!force && (choice.IsKeyboardFocusWithin || choice.IsDropDownOpen)) continue;
            var scope=TargetScope(choice);
            choice.ItemsSource=MonitoringDestinations.Categories(state,scope).Select(group=>new TargetChoice(group.Id,$"“{group.DisplayName}”",choice.Name))
                .Append(new("",L.Get("新建分类…"),choice.Name,true)).ToArray();
            choice.SelectedValue=state.ExternalGroupId(scope);
        }
        var variables=EnvironmentMonitor.ReadAll();var runtime=RuntimeValues();
        foreach(var choice in new[]{UserPathTarget,MachinePathTarget,GlobalPathTarget})
        {
            if(!force && (choice.IsKeyboardFocusWithin || choice.IsDropDownOpen)) continue;
            var scope=TargetScope(choice);
            var selected=choice==GlobalPathTarget ? global.ExternalMachinePathVariable : ExternalPathOrganizer.GroupName(scope,state);
            choice.ItemsSource=MonitoringDestinations.PathVariables(state,variables,scope,runtime).Select(name=>new TargetChoice(name,name,choice.Name))
                .Append(new("",L.Get("新建路径变量…"),choice.Name,true)).ToArray();
            choice.SelectedValue=selected;
            var active=choice==GlobalPathTarget ? global.Enabled && global.AutoArrangeMachinePath && global.MoveExternalMachinePathEntries
                : state.MonitoringEnabled && state.IsPathOrganizationEnabled(scope) && state.ShouldMovePathEntries(scope) && (scope!=VariableScope.Machine || !global.Enabled);
            if(choice.SelectedIndex<0 && active) unavailableTarget=L.Format("整理目标“{0}”不在路径变量分类中，请重新选择。",selected);
        }
        UserPathTarget.IsEnabled=state.MoveExternalUserPathEntries;
        MachinePathTarget.IsEnabled=state.MoveExternalMachinePathEntries;
        GlobalPathTarget.IsEnabled=global.MoveExternalMachinePathEntries;
    }
    private void ForceRefreshTargets()
    {
        var previous=updating;updating=true;
        try {RefreshTargets(store.Read(),GlobalMonitoring.ReadSettings(),true);}
        finally {updating=previous;}
        RefreshNotice(store.Read());
    }
    private async void GroupTargetChanged(object sender,SelectionChangedEventArgs e)
    {
        if(updating || busy || sender is not ComboBox {SelectedItem:TargetChoice option} choice) return;
        if(option.IsCreate) {await CreateTargetAsync(choice);return;}
        var current=store.Read();
        if((choice==UserExternalGroup ? current.ExternalUserGroupId : current.ExternalMachineGroupId)==option.Value) return;
        try {store.Update(state=>
        {
            if(choice==UserExternalGroup) state.ExternalUserGroupId=option.Value;else state.ExternalMachineGroupId=option.Value;
        });targetError=null;ForceRefreshTargets();RefreshNotice(store.Read());OptionsChanged?.Invoke(this,EventArgs.Empty);}
        catch(Exception error) when(Expected(error)) {targetError=error.Message;ShowError(error);}
    }
    private async void PathTargetChanged(object sender,SelectionChangedEventArgs e)
    {
        if(updating || busy || sender is not ComboBox {SelectedItem:TargetChoice option} choice) return;
        if(option.IsCreate) {await CreateTargetAsync(choice);return;}
        var state=store.Read();var previous=choice==GlobalPathTarget ? GlobalMonitoring.ReadSettings().ExternalMachinePathVariable
            : TargetScope(choice)==VariableScope.User ? state.ExternalUserPathVariable : state.ExternalMachinePathVariable;
        if(string.Equals(previous,option.Value,StringComparison.OrdinalIgnoreCase)) return;
        busy=true;SettingsPanel.IsEnabled=false;
        try {await PersistPathTargetAsync(choice,option.Value);targetError=null;}
        catch(Exception error) when(Expected(error)) {targetError=error.Message;ShowError(error);}
        finally {busy=false;SettingsPanel.IsEnabled=true;Update(store.Read());ForceRefreshTargets();}
        OptionsChanged?.Invoke(this,EventArgs.Empty);
    }
    private async Task PersistPathTargetAsync(ComboBox choice,string target)
    {
        ExternalPathOrganizer.ValidateTarget(target);
        if(choice==GlobalPathTarget)
        {
            var settings=GlobalMonitoring.ReadSettings();
            if(!settings.ExternalMachinePathVariable.Equals(target,StringComparison.OrdinalIgnoreCase))
                await Task.Run(()=>new GlobalMonitoring(layout).Configure(settings with {ExternalMachinePathVariable=target}));
            return;
        }
        store.Update(state=>
        {
            var scope=TargetScope(choice);
            if(scope==VariableScope.User) state.ExternalUserPathVariable=target;else state.ExternalMachinePathVariable=target;
            state.PendingPathOrganizations.RemoveAll(plan=>plan.Scope==scope);
        });
    }
    private async void CreateTargetClicked(object sender,RoutedEventArgs e)
    {
        e.Handled=true;
        if(sender is Button {Tag:string name} && FindName(name) is ComboBox choice) await CreateTargetAsync(choice);
    }
    private async Task CreateTargetAsync(ComboBox choice)
    {
        if(updating || busy) return;
        busy=true;SettingsPanel.IsEnabled=false;var changed=false;
        try
        {
            choice.IsDropDownOpen=false;ForceRefreshTargets();
            var scope=TargetScope(choice);var state=store.Read();var owner=Window.GetWindow(this);
            if(IsCategory(choice))
            {
                string? Validate(string name)=>string.IsNullOrWhiteSpace(name) || name.Trim().Length>80 ? L.Get("分组名不能为空，最长 80 个字符。")
                    : MonitoringDestinations.Categories(state,scope).Any(group=>group.Name.Equals(name.Trim(),StringComparison.OrdinalIgnoreCase) || group.DisplayName.Equals(name.Trim(),StringComparison.OrdinalIgnoreCase)) ? L.Get("已存在同名分类。") : null;
                var dialog=new TextDialog(owner,L.Get("新建分类"),L.Get("名称"),"",validate:Validate);
                if(dialog.ShowDialog()!=true) return;
                var group=new VariableGroupDefinition {Name=dialog.Value.Trim(),Scope=scope};
                store.Update(saved=> {saved.Groups.Add(group);if(scope==VariableScope.User) saved.ExternalUserGroupId=group.Id;else saved.ExternalMachineGroupId=group.Id;});changed=true;
            }
            else
            {
                var group=state.Groups.FirstOrDefault(item=>item.Rule=="paths" && (item.Scope is null || item.Scope==scope))
                    ?? new VariableGroupDefinition {Id=state.Groups.Any(item=>item.Id=="paths") ? Guid.NewGuid().ToString("N") : "paths",Name="路径变量",Rule="paths"};
                string? Validate(string name)=>!ExternalPathOrganizer.IsValidTarget(name) ? L.Get("请选择有效的路径变量，不能使用 PATH 或系统内置变量。")
                    : EnvironmentMonitor.ReadAll().Any(variable=>variable.Scope==scope && variable.Name.Equals(name.Trim(),StringComparison.OrdinalIgnoreCase)) ? L.Get("变量已存在。") : null;
                var dialog=new VariableDialog(owner,scope,[group],list:true,fixedGroupId:group.Id,validateName:Validate) {Title=L.Get(scope==VariableScope.User ? "新建用户路径变量" : "新建系统路径变量")};
                if(dialog.ShowDialog()!=true) return;
                var name=dialog.VariableName.Trim();
                await EnvironmentEditHost.ApplyAsync(new("create",scope,name,dialog.RawValue),store,layout.DataDirectory);
                store.Update(saved=> {if(saved.Groups.All(item=>item.Id!=group.Id)) saved.Groups.Add(group);saved.Assignments[EnvironmentWorkspace.Key(scope,name)]=group.Id;});changed=true;
                await PersistPathTargetAsync(choice,name);
            }
            targetError=null;
        }
        catch(Exception error) when(Expected(error)) {targetError=error.Message;ShowError(error);}
        finally {busy=false;SettingsPanel.IsEnabled=true;Update(store.Read());ForceRefreshTargets();}
        if(changed) OptionsChanged?.Invoke(this,EventArgs.Empty);
    }
}
