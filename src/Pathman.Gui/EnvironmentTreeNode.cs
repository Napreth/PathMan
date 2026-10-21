using System.ComponentModel;
using System.Runtime.CompilerServices;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;

namespace Pathman.Gui;

public sealed class EnvironmentTreeNode : INotifyPropertyChanged
{
    private bool expanded;
    private bool selected;
    public EnvironmentVariableInfo? Variable { get; }
    public VariableGroupDefinition Group { get; }
    public VariableScope Scope { get; }
    public string Title => Variable?.Name ?? Group.Name;
    public bool IsGroup => Variable is null;
    public IReadOnlyList<EnvironmentTreeNode> Children { get; }
    public bool IsExpanded { get => expanded; set => Set(ref expanded, value); }
    public bool IsSelected { get => selected; set => Set(ref selected, value); }
    public string Key => IsGroup ? $"group:{Scope}:{Group.Id}" : $"variable:{Scope}:{Variable!.Name}";
    public event PropertyChangedEventHandler? PropertyChanged;
    private EnvironmentTreeNode(EnvironmentVariableInfo variable, VariableGroupDefinition group)
    { Variable = variable; Group = group; Scope = variable.Scope; Children = []; }
    private EnvironmentTreeNode(VariableScope scope, VariableGroupDefinition group, IEnumerable<EnvironmentVariableInfo> variables)
    {
        Scope = scope; Group = group;
        Children = variables.OrderBy(variable => variable.Name, StringComparer.OrdinalIgnoreCase)
            .Select(variable => new EnvironmentTreeNode(variable, group)).ToArray();
    }
    public static IReadOnlyList<EnvironmentTreeNode> Build(VariableScope scope, IReadOnlyList<EnvironmentVariableInfo> all,
        EnvironmentWorkspace state, IReadOnlyDictionary<string, string> runtime)
    {
        var classified = all.Where(variable => variable.Scope == scope).ToLookup(variable => EnvironmentVariableClassifier.Classify(variable, all, state, runtime));
        return state.VisibleGroups(scope).Select(group => new EnvironmentTreeNode(scope, group, classified[group.Id])).ToArray();
    }
    private void Set(ref bool field, bool value, [CallerMemberName] string? name = null)
    { if (field == value) return; field = value; PropertyChanged?.Invoke(this, new(name)); }
}
