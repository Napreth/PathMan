namespace Pathman.Core.Models;

public enum VariableCategory { Path, System, Tool, Other }
public enum VariableScope { User, Machine }

public sealed record VariableVersion(string Name, string Value);

public sealed record ManagedVariable(
    string Name,
    string Value,
    VariableCategory Category,
    VariableScope Scope,
    string? Alias = null,
    string? ActiveVersion = null,
    IReadOnlyList<VariableVersion>? Versions = null);

public sealed record PathEntry(string Value, bool Enabled = true);

public sealed record PathGroup(
    string Name,
    VariableScope Scope,
    IReadOnlyList<PathEntry> Entries,
    bool Enabled = true);
