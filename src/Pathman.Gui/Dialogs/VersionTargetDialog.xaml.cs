using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;
using Pathman.Core.Versions;

namespace Pathman.Gui.Dialogs;

public sealed class VersionTargetNode : INotifyPropertyChanged
{
    private bool selected;
    public bool IsSelected {get=>selected;set {if(selected==value) return;selected=value;PropertyChanged?.Invoke(this,new(nameof(IsSelected)));}}
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Title {get;}
    public EnvironmentVariableInfo? Variable {get;}
    public bool IsGroup=>Variable is null;
    public bool Enabled=>IsGroup || Reason is null;
    public string? Reason {get;}
    public IReadOnlyList<VersionTargetNode> Children {get;}
    public VersionTargetNode(EnvironmentTreeNode node,bool allowBuiltIns)
    {Title=node.Title;Variable=node.Variable;Reason=Variable is null ? null : VersionTargetPolicy.Restriction(Variable.Name,allowBuiltIns);Children=node.Children.Select(child=>new VersionTargetNode(child,allowBuiltIns)).ToArray();}
}

public partial class VersionTargetDialog : Window
{
    private VersionTargetNode? selected;
    private bool updating;
    public EnvironmentVariableInfo? Target {get;private set;}
    public VersionTargetDialog(Window owner,EnvironmentWorkspace state)
    {
        Owner=owner;InitializeComponent();
        var variables=EnvironmentMonitor.ReadAll();
        var runtime=Environment.GetEnvironmentVariables().Cast<DictionaryEntry>().ToDictionary(pair=>(string)pair.Key,pair=>(string)pair.Value!,StringComparer.OrdinalIgnoreCase);
        UserTargets.ItemsSource=EnvironmentTreeNode.Build(VariableScope.User,variables,state,runtime).Select(node=>new VersionTargetNode(node,state.AllowBuiltInVersionTargets));
        MachineTargets.ItemsSource=EnvironmentTreeNode.Build(VariableScope.Machine,variables,state,runtime).Select(node=>new VersionTargetNode(node,state.AllowBuiltInVersionTargets));
    }
    private void SelectionChanged(object sender,RoutedPropertyChangedEventArgs<object> e)
    {
        if(updating) return;
        updating=true;
        if(selected is not null && selected!=e.NewValue) selected.IsSelected=false;
        selected=e.NewValue as VersionTargetNode;updating=false;
        Target=e.NewValue is VersionTargetNode {Enabled:true,Variable:{} variable} ? variable : null;
        ChooseTargetButton.IsEnabled=Target is not null;
        SelectedTargetText.Text=Target is null ? "" : $"{Target.Name} · {(Target.Scope==VariableScope.User ? L.Get("用户") : L.Get("系统"))}";
        TargetWarning.Visibility=Target is not null && VersionTargetPolicy.IsBuiltIn(Target.Name) ? Visibility.Visible : Visibility.Collapsed;
        TargetWarningText.Text=L.Get("这是内置系统变量。切换其值可能导致 Windows 或应用无法正常运行。");
    }
    private void DoubleClickTarget(object sender,MouseButtonEventArgs e)
    {if(Target is not null && ItemsControl.ContainerFromElement((TreeView)sender,e.OriginalSource as DependencyObject) is TreeViewItem) ChooseTarget(sender,e);}
    private void ChooseTarget(object sender,RoutedEventArgs e) {if(Target is not null) DialogResult=true;}
}
