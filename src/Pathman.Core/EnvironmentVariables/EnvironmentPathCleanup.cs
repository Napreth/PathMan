using Microsoft.Win32;
using Pathman.Core.Models;

namespace Pathman.Core.EnvironmentVariables;

public sealed record PathCleanupScopePlan(VariableScope Scope, IReadOnlyList<EnvironmentMutation> Mutations,
    IReadOnlyList<VariableSnapshot> Preconditions, int RemovedDuplicates, int MovedEntries, string? SoftwareVariable);
public sealed record EnvironmentPathCleanupPlan(IReadOnlyList<PathCleanupScopePlan> Scopes)
{
    public bool HasChanges => Scopes.Any(scope => scope.Mutations.Count > 0);
}

public static class EnvironmentPathCleanup
{
    private sealed class WindowsPaths { public string[] Paths { get; set; } = []; }
    private sealed record Entry(string Raw, EnvironmentPathExpansion Expansion, string[] Keys, bool PathReference);
    private static readonly string[] BuiltInPaths = EnvironmentWorkspaceStore.ReadDefault<WindowsPaths>("windows-paths").Paths;

    public static EnvironmentPathCleanupPlan Analyze(IReadOnlyList<EnvironmentVariableInfo> variables,
        IReadOnlyDictionary<string, string>? runtime = null)
    {
        var resolver = new EnvironmentPathResolver(variables, runtime);
        var plans = new List<PathCleanupScopePlan>();
        var reserved = variables.Select(item => item.Name).Concat(runtime?.Keys ?? [])
            .Concat(variables.SelectMany(item => EnvironmentPathResolver.ReferenceNames(item.RawValue))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var scope in new[] { VariableScope.Machine, VariableScope.User })
        {
            var path = variables.FirstOrDefault(item => item.Scope == scope && item.Name.Equals("PATH", StringComparison.OrdinalIgnoreCase));
            if (path is null) continue;
            var references = resolver.ReferencedVariables(path.RawValue, scope);
            var guards = new[] { path }.Concat(references).Distinct().Select(item => new VariableSnapshot(item.Scope, item.Name, item.RawValue, (int)item.Kind)).ToArray();
            string[] Keys(EnvironmentPathExpansion expansion) => expansion.IsResolved && expansion.Paths.All(item => EnvironmentPathResolver.Normalize(item) is not null)
                ? expansion.Paths.Select(item => EnvironmentPathResolver.Normalize(item)!).ToArray() : [];

            var candidates = new List<Entry>();
            var seenReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var removed = 0;
            foreach (var raw in path.Entries)
            {
                var expansion = resolver.Expand(raw, scope);
                var pure = EnvironmentPathResolver.PureReference(raw);
                var pathReference = pure is not null || resolver.ReferencedVariables(raw, scope).Any(item => item.Name.EndsWith("PATH", StringComparison.OrdinalIgnoreCase) && !item.Name.Equals("PATH", StringComparison.OrdinalIgnoreCase));
                if (pathReference && !seenReferences.Add(pure is null ? raw : $"%{pure}%")) { removed++; continue; }
                candidates.Add(new(raw, expansion, Keys(expansion), pathReference));
            }
            var referenceEntries = candidates.Where(entry => entry.PathReference).ToArray();
            var referenceCoverage = referenceEntries.SelectMany(entry => entry.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var retained = new List<Entry>();
            foreach (var entry in candidates)
            {
                if (!entry.PathReference && entry.Keys.Length > 0 && entry.Keys.All(key => referenceCoverage.Contains(key) || seen.Contains(key)))
                { removed++; continue; }
                retained.Add(entry);
                if (!entry.PathReference) seen.UnionWith(entry.Keys);
            }

            var builtIns = BuiltInPaths.SelectMany(item => resolver.Expand(item, scope).Paths).Select(EnvironmentPathResolver.Normalize)
                .Where(item => item is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var moved = scope == VariableScope.Machine ? retained.Where(entry => entry.Raw.Length > 0 &&
                EnvironmentPathResolver.PureReference(entry.Raw) is null && !(entry.Keys.Length > 0 && entry.Keys.All(builtIns.Contains))).ToArray() : [];
            var mutations = new List<EnvironmentMutation>();
            string? softwareName = null;
            if (moved.Length > 0)
            {
                var number = 0;
                do { softwareName = number == 0 ? "SOFTWARE_PATH" : $"SOFTWARE{number}_PATH"; number++; } while (!reserved.Add(softwareName));
                mutations.Add(new("create", scope, softwareName, string.Join(';', moved.Select(entry => entry.Raw))));
            }
            var newPath = string.Join(';', retained.Except(moved).Select(entry => entry.Raw).Concat(softwareName is null ? [] : new[] { $"%{softwareName}%" }));
            if (newPath != path.RawValue) mutations.Add(new("set", scope, path.Name, newPath, ExpectedValue: path.RawValue, ExpectedKind: path.Kind));
            plans.Add(new(scope, mutations, guards, removed, moved.Length, softwareName));
        }
        return new(plans);
    }
}
