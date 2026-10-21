namespace Pathman.Gui;

// Each occurrence has its own identity, including duplicate and empty values.
public sealed class EnvironmentEntry(string value)
{
    public string Value { get; } = value;
    public string AutomationName => Value.Length == 0 ? "（空条目）" : Value;
}
