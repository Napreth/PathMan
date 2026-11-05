using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pathman.Core;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;

namespace Pathman.Gui.Views;

public partial class MonitorView
{
    private string? targetError;
    private void RefreshTargets(EnvironmentWorkspace state,GlobalMonitorSettings global)
    {
        void Groups(ComboBox choice,VariableScope scope)
        {
            if(choice.IsKeyboardFocusWithin || choice.IsDropDownOpen) return;
            var items=state.Groups.Where(group=>group.Scope is null || group.Scope==scope).ToArray();
            var selected=items.FirstOrDefault(group=>group.Id==state.ExternalGroupId(scope)) ?? items.FirstOrDefault(group=>group.Rule=="external");
            choice.ItemsSource=items;choice.SelectedValue=selected?.Id;
        }
        Groups(UserExternalGroup,VariableScope.User);Groups(MachineExternalGroup,VariableScope.Machine);
        var variables=EnvironmentMonitor.ReadAll();
        void Targets(ComboBox choice,VariableScope scope,string selected)
        {
            if(choice.IsKeyboardFocusWithin || choice.IsDropDownOpen) return;
            choice.ItemsSource=variables.Where(item=>item.Scope==scope && ExternalPathOrganizer.IsValidTarget(item.Name)).Select(item=>item.Name)
                .Append(ExternalPathOrganizer.GroupName(scope)).Append(selected).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            choice.Text=selected;
        }
        Targets(UserPathTarget,VariableScope.User,ExternalPathOrganizer.GroupName(VariableScope.User,state));
        Targets(MachinePathTarget,VariableScope.Machine,ExternalPathOrganizer.GroupName(VariableScope.Machine,state));
        Targets(GlobalPathTarget,VariableScope.Machine,global.ExternalMachinePathVariable);
        UserPathTarget.IsEnabled=state.MoveExternalUserPathEntries;
        MachinePathTarget.IsEnabled=state.MoveExternalMachinePathEntries;
        GlobalPathTarget.IsEnabled=global.MoveExternalMachinePathEntries;
    }
    private void GroupTargetChanged(object sender,SelectionChangedEventArgs e)
    {
        if(updating || busy || sender is not ComboBox {SelectedValue:string id} choice) return;
        try {store.Update(state=>
        {
            if(choice==UserExternalGroup) state.ExternalUserGroupId=id;else state.ExternalMachineGroupId=id;
        });}
        catch(Exception error) when(Expected(error)) {ShowError(error);return;}
        OptionsChanged?.Invoke(this,EventArgs.Empty);
    }
    private void PathTargetChanged(object? sender,EventArgs e)
    {
        if(updating || busy || sender is not ComboBox choice) return;
        var target=choice.Text.Trim();
        var state=store.Read();var global=GlobalMonitoring.ReadSettings();
        var previous=choice==UserPathTarget ? ExternalPathOrganizer.GroupName(VariableScope.User,state)
            : choice==MachinePathTarget ? ExternalPathOrganizer.GroupName(VariableScope.Machine,state) : global.ExternalMachinePathVariable;
        if(target.Equals(previous,StringComparison.OrdinalIgnoreCase)) {targetError=null;RefreshNotice(state);return;}
        try {ExternalPathOrganizer.ValidateTarget(target);}
        catch(ArgumentException error) {targetError=error.Message;ShowError(error);return;}
        targetError=null;
        if(choice==GlobalPathTarget) {ChangeGlobalOptions(choice,new RoutedEventArgs());return;}
        busy=true;
        try
        {
            store.Update(saved=>
            {
                var scope=choice==UserPathTarget ? VariableScope.User : VariableScope.Machine;
                if(scope==VariableScope.User) saved.ExternalUserPathVariable=target;else saved.ExternalMachinePathVariable=target;
                saved.PendingPathOrganizations.RemoveAll(plan=>plan.Scope==scope);
            });
            busy=false;Update(store.Read());OptionsChanged?.Invoke(this,EventArgs.Empty);
        }
        catch(Exception error) when(Expected(error)) {ShowError(error);}
        finally {busy=false;}
    }
    private void PathTargetLostFocus(object sender,KeyboardFocusChangedEventArgs e)
    {
        if(sender is ComboBox {IsDropDownOpen:false} choice && !choice.IsKeyboardFocusWithin) PathTargetChanged(choice,EventArgs.Empty);
    }
    private void PathTargetKeyDown(object sender,KeyEventArgs e)
    {if(e.Key==Key.Enter) {e.Handled=true;PathTargetChanged(sender,EventArgs.Empty);}}
}
