using Pathman.Core.Models;

namespace Pathman.Core.EnvironmentVariables;

public enum EnvironmentIssueKind { ReferenceCycle, UndefinedReference, Duplicate, EmptyPathEntry, ExpansionLimit }
public sealed record EnvironmentHealthIssue(EnvironmentIssueKind Kind, VariableScope Scope, string Name, string Message)
{
    public bool IsError => Kind is EnvironmentIssueKind.ReferenceCycle or EnvironmentIssueKind.UndefinedReference;
}
public sealed record EnvironmentHealthReport(IReadOnlyList<EnvironmentHealthIssue> Issues)
{
    public int Errors => Issues.Count(item => item.IsError);
    public int Warnings => Issues.Count - Errors;
}

public static class EnvironmentHealth
{
    public static EnvironmentHealthReport Analyze(IReadOnlyList<EnvironmentVariableInfo> variables, IReadOnlyDictionary<string,string>? runtime = null)
    {
        var resolver = new EnvironmentPathResolver(variables, runtime);
        var issues = new List<EnvironmentHealthIssue>();
        var cycles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in variables)
        {
            var visiting = new List<EnvironmentVariableInfo>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Visit(EnvironmentVariableInfo current, int depth)
            {
                var key = EnvironmentWorkspace.Key(current.Scope,current.Name);
                var cycleStart = visiting.FindIndex(item => EnvironmentWorkspace.Key(item.Scope,item.Name).Equals(key,StringComparison.OrdinalIgnoreCase));
                if (cycleStart >= 0)
                {
                    var cycle = visiting.Skip(cycleStart).ToArray();
                    var signature = $"{variable.Scope}:" + string.Join('|',cycle.Select(item=>EnvironmentWorkspace.Key(item.Scope,item.Name)).Order(StringComparer.OrdinalIgnoreCase));
                    if (cycles.Add(signature)) issues.Add(new(EnvironmentIssueKind.ReferenceCycle,current.Scope,current.Name,
                        "引用回环：" + string.Join(" → ",cycle.Select(item=>item.Name).Append(current.Name))));
                    return;
                }
                if (!visited.Add(key) || depth > 32) return;
                visiting.Add(current);
                foreach (var name in EnvironmentPathResolver.ReferenceNames(current.RawValue))
                    if (resolver.Find(name,variable.Scope) is { } target) Visit(target,depth+1);
                visiting.RemoveAt(visiting.Count-1);
            }
            Visit(variable,0);
            foreach (var name in EnvironmentPathResolver.ReferenceNames(variable.RawValue).Where(name=>!resolver.IsDefined(name,variable.Scope)))
                issues.Add(new(EnvironmentIssueKind.UndefinedReference,variable.Scope,variable.Name,$"引用了未定义变量 %{name}%"));
            var expanded = resolver.Expand(variable.RawValue,variable.Scope);
            if (expanded.Error?.Contains("超过限制",StringComparison.Ordinal)==true)
                issues.Add(new(EnvironmentIssueKind.ExpansionLimit,variable.Scope,variable.Name,"引用展开超过安全限制，请检查引用层级及内容长度。"));
            var pathLike = variable.Name.EndsWith("PATH",StringComparison.OrdinalIgnoreCase);
            if (!variable.IsList && !pathLike) continue;
            if (pathLike && variable.RawValue.Length>0 && (variable.Entries.Any(string.IsNullOrEmpty) || expanded.IsResolved && expanded.Paths.Any(string.IsNullOrWhiteSpace)))
                issues.Add(new(EnvironmentIssueKind.EmptyPathEntry,variable.Scope,variable.Name,"路径列表包含空条目。"));
            if (!expanded.IsResolved) continue;
            var comparable = expanded.Paths.Where(item=>item.Length>0).Select(item=>pathLike ? EnvironmentPathResolver.Normalize(item) : item.Trim())
                .Where(item=>!string.IsNullOrEmpty(item));
            foreach (var repeated in comparable.GroupBy(item=>item!,StringComparer.OrdinalIgnoreCase).Where(group=>group.Count()>1))
                issues.Add(new(EnvironmentIssueKind.Duplicate,variable.Scope,variable.Name,$"展开后出现 {repeated.Count()} 次：{repeated.Key}"));
        }
        return new(issues.Distinct().OrderByDescending(issue=>issue.IsError).ThenBy(issue=>issue.Scope).ThenBy(issue=>issue.Name,StringComparer.OrdinalIgnoreCase).ToArray());
    }
}
