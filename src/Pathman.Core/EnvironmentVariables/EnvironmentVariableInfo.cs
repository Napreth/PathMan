using Pathman.Core.Models;
using Microsoft.Win32;

namespace Pathman.Core.EnvironmentVariables;

public sealed class EnvironmentVariableInfo
{
    public string Name { get; }
    public string RawValue { get; }
    public VariableScope Scope { get; }
    public bool IsList { get; }
    public IReadOnlyList<string> Entries { get; }
    public RegistryValueKind Kind { get; }

    public EnvironmentVariableInfo(string name, string rawValue, VariableScope scope, RegistryValueKind kind = RegistryValueKind.String)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(rawValue);
        Name = name;
        RawValue = rawValue;
        Scope = scope;
        Kind = kind;
        IsList = name.Equals("PATH", StringComparison.OrdinalIgnoreCase) ||
                 name.Equals("PATHEXT", StringComparison.OrdinalIgnoreCase) || rawValue.Contains(';');
        // Empty entries, whitespace, duplicate entries and order are significant.
        // Splitting is a presentation operation; RawValue remains unchanged.
        Entries = Array.AsReadOnly(IsList ? rawValue.Split(';', StringSplitOptions.None) : [rawValue]);
    }
}
