using System.ComponentModel;
using System.Runtime.CompilerServices;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;

namespace Pathman.Gui;

public sealed class EnvironmentTreeNode : INotifyPropertyChanged
{
    private bool expanded;
    private bool selected;
    private bool marked;
    public EnvironmentVariableInfo? Variable { get; }
    public VariableGroupDefinition Group { get; }
    public VariableScope Scope { get; }
    public string Title => Variable?.Name ?? Group.Name;
    public bool IsGroup => Variable is null;
    public bool IsPinned { get; }
    public bool CanDelete => !IsGroup || Group.Rule != "fallback";
    public IReadOnlyList<EnvironmentTreeNode> Children { get; }
    public bool IsExpanded { get => expanded; set => Set(ref expanded, value); }
    public bool IsSelected { get => selected; set => Set(ref selected, value); }
    public bool IsMarked { get => marked; set {if(marked==value) return;Set(ref marked,value);PropertyChanged?.Invoke(this,new(nameof(SelectionStatus)));} }
    public string SelectionStatus=>IsMarked ? "已选择" : "";
    public string Key => IsGroup ? $"group:{Scope}:{Group.Id}" : $"variable:{Scope}:{Variable!.Name}";
    public event PropertyChangedEventHandler? PropertyChanged;
    private EnvironmentTreeNode(EnvironmentVariableInfo variable, VariableGroupDefinition group, EnvironmentWorkspace state)
    { Variable = variable; Group = group; Scope = variable.Scope; Children = []; IsPinned = state.IsVariablePinned(Scope, variable.Name); }
    private EnvironmentTreeNode(VariableScope scope, VariableGroupDefinition group, IEnumerable<EnvironmentVariableInfo> variables, EnvironmentWorkspace state)
    {
        Scope = scope; Group = group;
        IsPinned = state.IsGroupPinned(scope, group.Id);
        Children = state.OrderVariables(variables).Select(variable => new EnvironmentTreeNode(variable, group, state)).ToArray();
    }
    public static IReadOnlyList<EnvironmentTreeNode> Build(VariableScope scope, IReadOnlyList<EnvironmentVariableInfo> all,
        EnvironmentWorkspace state, IReadOnlyDictionary<string, string> runtime)
    {
        var classified = all.Where(variable => variable.Scope == scope).ToLookup(variable => EnvironmentVariableClassifier.Classify(variable, all, state, runtime));
        return state.VisibleGroups(scope).Select(group => new EnvironmentTreeNode(scope, group, classified[group.Id], state)).ToArray();
    }
    private void Set(ref bool field, bool value, [CallerMemberName] string? name = null)
    { if (field == value) return; field = value; PropertyChanged?.Invoke(this, new(name)); }
}
