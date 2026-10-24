using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Pathman.Core.Models;
using Pathman.Gui.Controls;

namespace Pathman.Gui.Views;

public partial class VariablesView
{
    private async void EntryJumpRequested(object? sender, EventArgs e)
    {
        if (sender is not EditableRow { DataContext: EnvironmentEntry entry } row || busy) return;
        if (entry.References.Count == 1)
        {
            var reference = entry.References[0];
            if (reference.Scope is { } scope) await OpenVariable(scope, reference.Name);
            else StatusText.Text=$"当前变量列表中没有 {reference.Name}。";
            return;
        }
        var menu = new ContextMenu { PlacementTarget=row, Placement=PlacementMode.Bottom };
        foreach (var reference in entry.References)
        {
            var label=reference.Scope is { } scope ? $"{(scope==VariableScope.User ? "用户" : "系统")} · {reference.Name}" : $"{reference.Name}（未列出）";
            var item = new MenuItem { Header=new TextBlock { Text=label }, IsEnabled=reference.Scope is not null };
            System.Windows.Automation.AutomationProperties.SetName(item,label);
            item.Click += async (_, _) => await OpenVariable(reference.Scope!.Value, reference.Name);
            menu.Items.Add(item);
        }
        menu.IsOpen=true;
    }
    public async Task<bool> NavigateToVariableAsync(VariableScope scope,string name)
    {
        if(!await ConfirmLeaveAsync()) return false;
        if(allVariables.Count==0) await ReloadAsync();
        return await RevealVariable(scope,name);
    }
    private async Task OpenVariable(VariableScope scope, string name)
    {
        if (!await ConfirmLeaveAsync()) return;
        await RevealVariable(scope,name);
    }
    private async Task<bool> RevealVariable(VariableScope scope,string name)
    {
        var groups=scope==VariableScope.User ? userGroups : systemGroups;
        var group=groups.FirstOrDefault(group=>group.Children.Any(node=>node.Title.Equals(name,StringComparison.OrdinalIgnoreCase)));
        var node=group?.Children.FirstOrDefault(node=>node.Title.Equals(name,StringComparison.OrdinalIgnoreCase));
        if (node is null) { StatusText.Text=$"找不到变量 {name}，请刷新后重试。"; return false; }
        EditorPanel.Visibility=Visibility.Visible; CleanupPanel.Visibility=Visibility.Collapsed;
        group!.IsExpanded=true; ShowSelection(node);
        var tree=scope==VariableScope.User ? UserVariablesTree : SystemVariablesTree;
        await Dispatcher.InvokeAsync(()=> { tree.UpdateLayout(); Container(tree,node)?.BringIntoView(); },DispatcherPriority.Loaded);
        return true;
    }
}
