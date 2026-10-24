using System.Text.RegularExpressions;
using Pathman.Core.Models;

namespace Pathman.Core.EnvironmentVariables;

public sealed record EnvironmentPathExpansion(IReadOnlyList<string> Paths, string? Error)
{
    public bool IsResolved => Error is null;
}

// Resolve a saved scope, not the (possibly stale or elevated) process PATH.
public sealed partial class EnvironmentPathResolver
{
    private readonly IReadOnlyList<EnvironmentVariableInfo> variables;
    private readonly IReadOnlyDictionary<string, string> runtime;
    public EnvironmentPathResolver(IReadOnlyList<EnvironmentVariableInfo> variables, IReadOnlyDictionary<string, string>? runtime = null)
    {
        this.variables = variables;
        this.runtime = runtime ?? Environment.GetEnvironmentVariables().Keys.Cast<string>()
            .Where(EnvironmentVariableClassifier.IsWindowsBuiltIn).ToDictionary(name => name, name => Environment.GetEnvironmentVariable(name) ?? "", StringComparer.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"%([^%]+)%", RegexOptions.CultureInvariant)] private static partial Regex ReferencesPattern();
    [GeneratedRegex(@"^%([^%]+)%[\\/]*$", RegexOptions.CultureInvariant)] private static partial Regex PureReferencePattern();
    public static string? PureReference(string value) => PureReferencePattern().Match(value) is { Success: true } match ? match.Groups[1].Value : null;
    public static IReadOnlyList<string> ReferenceNames(string value) => ReferencesPattern().Matches(value)
        .Select(match => match.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public EnvironmentVariableInfo? Find(string name, VariableScope scope) =>
        (scope == VariableScope.User ? variables.FirstOrDefault(item => item.Scope == VariableScope.User && item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) : null)
        ?? variables.FirstOrDefault(item => item.Scope == VariableScope.Machine && item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    public bool IsDefined(string name, VariableScope scope) => Find(name, scope) is not null ||
        (scope!=VariableScope.Machine || !EnvironmentVariableClassifier.IsUserOnlyBuiltIn(name)) &&
        EnvironmentVariableClassifier.IsWindowsBuiltIn(name) && !variables.Any(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) &&
        runtime.Any(item => item.Key.Equals(name, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<EnvironmentVariableInfo> ReferencedVariables(string value, VariableScope scope)
    {
        var result = new List<EnvironmentVariableInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string text, int depth)
        {
            if (depth > 32) return;
            foreach (var name in ReferenceNames(text))
            {
                var variable = Find(name, scope);
                if (variable is null || !seen.Add(EnvironmentWorkspace.Key(variable.Scope, name))) continue;
                result.Add(variable); Visit(variable.RawValue, depth + 1);
            }
        }
        Visit(value, 0); return result;
    }

    public EnvironmentPathExpansion Expand(string value, VariableScope scope)
    {
        string? error = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var replacements = 0;
        string ExpandValue(string text, int depth)
        {
            if (depth > 32 || text.Length > 262144 || ++replacements > 4096)
            { error ??= "引用展开超过限制"; return text; }
            return ReferencesPattern().Replace(text, match =>
            {
                if (error is not null) return match.Value;
                var name = match.Groups[1].Value;
                if (!seen.Add(name)) { error = $"循环引用 %{name}%"; return match.Value; }
                var variable = Find(name, scope);
                string? replacement = variable?.RawValue;
                // A user-defined name must never leak from the runtime into a machine value.
                if (replacement is null && (scope!=VariableScope.Machine || !EnvironmentVariableClassifier.IsUserOnlyBuiltIn(name)) && EnvironmentVariableClassifier.IsWindowsBuiltIn(name) && !variables.Any(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    replacement = runtime.FirstOrDefault(item => item.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
                if (replacement is null) { error = $"找不到引用 %{name}%"; seen.Remove(name); return match.Value; }
                var expanded = ExpandValue(replacement, depth + 1); seen.Remove(name);
                if (expanded.Length > 262144) { error ??= "引用展开超过限制"; return match.Value; }
                return expanded;
            });
        }
        var expanded = ExpandValue(value, 0);
        if (expanded.Length > 262144) error ??= "引用展开超过限制";
        return new(expanded.Split(';', StringSplitOptions.None), error);
    }

    public static string? Normalize(string path)
    {
        var text = path.Trim().Replace('/', '\\');
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') text = text[1..^1];
        if (text.Length == 0 || text.Contains('%') || !Path.IsPathFullyQualified(text)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(text)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
