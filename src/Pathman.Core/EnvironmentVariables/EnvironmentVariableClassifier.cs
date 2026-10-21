using System.Text.RegularExpressions;
using Pathman.Core.Models;

namespace Pathman.Core.EnvironmentVariables;

public static partial class EnvironmentVariableClassifier
{
    private sealed class BuiltInNames { public string[] Names { get; set; } = []; }
    private static readonly HashSet<string> BuiltIns = new(EnvironmentWorkspaceStore.ReadDefault<BuiltInNames>("windows-variables").Names, StringComparer.OrdinalIgnoreCase);
    [GeneratedRegex(@"%([^%]+)%", RegexOptions.CultureInvariant)] private static partial Regex ReferencePattern();
    [GeneratedRegex(@"^(?:[A-Za-z]:[\\/]|\\\\[^\\/]+[\\/][^\\/]+)", RegexOptions.CultureInvariant)] private static partial Regex AbsolutePathPattern();

    public static bool IsPathVariable(EnvironmentVariableInfo variable, IReadOnlyList<EnvironmentVariableInfo> all,
        IReadOnlyDictionary<string, string>? runtime = null)
    {
        if (!variable.Name.EndsWith("PATH", StringComparison.OrdinalIgnoreCase)) return false;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (runtime is not null) foreach (var item in runtime) values[item.Key] = item.Value;
        foreach (var item in all.Where(item => item.Scope == VariableScope.Machine)) values[item.Name] = item.RawValue;
        if (variable.Scope == VariableScope.User)
            foreach (var item in all.Where(item => item.Scope == VariableScope.User)) values[item.Name] = item.RawValue;
        string Expand(string value, HashSet<string> seen, int depth) => depth > 16 ? value : ReferencePattern().Replace(value, match =>
        {
            var name = match.Groups[1].Value;
            if (!values.TryGetValue(name, out var replacement) || !seen.Add(name)) return match.Value;
            var expanded = Expand(replacement, seen, depth + 1); seen.Remove(name); return expanded;
        });
        var entries = variable.Entries.Where(entry => entry.Length > 0).ToArray();
        if (entries.Length == 0 || entries.Any(entry => !AbsolutePathPattern().IsMatch(Expand(entry, new(StringComparer.OrdinalIgnoreCase), 0)))) return false;
        if (entries.Length > 1) return true;
        var reference = $"%{variable.Name}%";
        // A whole PATH entry must be the reference. %NAME%\bin and quoted/embedded references do not qualify.
        return all.Where(item => item.Name.Equals("PATH", StringComparison.OrdinalIgnoreCase) &&
                (item.Scope == variable.Scope || variable.Scope == VariableScope.User))
            .Any(path => path.Entries.Any(entry => entry.Equals(reference, StringComparison.OrdinalIgnoreCase)));
    }

    public static string Classify(EnvironmentVariableInfo variable, IReadOnlyList<EnvironmentVariableInfo> all,
        EnvironmentWorkspace state, IReadOnlyDictionary<string, string>? runtime = null)
    {
        var groups = state.VisibleGroups(variable.Scope).ToArray();
        var key = EnvironmentWorkspace.Key(variable.Scope, variable.Name);
        if (state.Assignments.TryGetValue(key, out var assigned) && groups.Any(group => group.Id == assigned)) return assigned;
        var external = groups.FirstOrDefault(group => group.Rule == "external" && state.MonitoringEnabled && state.ExternalVariables.Contains(key));
        if (external is not null) return external.Id;
        foreach (var group in groups)
        {
            if (group.Rule == "paths" && IsPathVariable(variable, all, runtime) || group.Rule == "builtin" && BuiltIns.Contains(variable.Name)) return group.Id;
        }
        return groups.First(group => group.Rule == "fallback").Id;
    }
}
