using Pathman.Core.Models;

namespace Pathman.Core.EnvironmentVariables;

public sealed class PathOrganizationReferenceHealthCheck : IEnvironmentHealthCheck
{
    public string Id=>"path-organization-reference";
    public string Title=>L.Get("整理目标路径引用");
    public IEnumerable<EnvironmentHealthIssue> Check(EnvironmentHealthContext context)
    {
        foreach(var scope in new[]{VariableScope.User,VariableScope.Machine})
        {
            if(context.Workspace is not {MonitoringEnabled:true} workspace || !workspace.IsPathOrganizationEnabled(scope) || !workspace.ShouldMovePathEntries(scope)) continue;
            var target=ExternalPathOrganizer.GroupName(scope,workspace);
            var group=context.Variables.FirstOrDefault(item=>item.Scope==scope && item.Name.Equals(target,StringComparison.OrdinalIgnoreCase));
            if(string.IsNullOrEmpty(group?.RawValue)) continue;
            var path=context.Variables.FirstOrDefault(item=>item.Scope==scope && item.Name.Equals("Path",StringComparison.OrdinalIgnoreCase));
            if(path?.Entries.Select(EnvironmentPathResolver.PureReference).Any(name=>name?.Equals(target,StringComparison.OrdinalIgnoreCase)==true)==true) continue;
            var sources=context.Variables.Where(item=>item.Scope==scope && (item==path || item==group))
                .Select(item=>new VariableSnapshot(scope,item.Name,item.RawValue,(int)item.Kind)).ToArray();
            yield return new(EnvironmentIssueKind.Custom,scope,path?.Name ?? "Path",L.Format("整理目标 {0} 中已有路径，但 PATH 尚未引用它。",target)) {
                IsError=true,IdentityMessage="missing-path-target-reference:"+target.ToUpperInvariant(),Sources=sources,
                Suggestion=L.Format("在相应作用域的 PATH 中添加 %{0}%。",target)};
        }
    }
}
