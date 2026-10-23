namespace Pathman.Gui;

// Each occurrence has its own identity, including duplicate and empty values.
public sealed record EnvironmentReferenceLink(string Name, Pathman.Core.Models.VariableScope? Scope);
public sealed class EnvironmentEntry(string value, bool isDraft = false, IReadOnlyList<EnvironmentReferenceLink>? references = null)
{
    public string Value { get; } = value;
    public bool IsDraft { get; } = isDraft;
    public IReadOnlyList<EnvironmentReferenceLink> References { get; } = references ?? [];
    public bool HasReferences => References.Count > 0;
    public string ReferenceToolTip => "转到：" + string.Join("、", References.Select(item => item.Name));
    public string AutomationName => Value.Length == 0 ? "（空条目）" : Value;
}
