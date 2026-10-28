using Pathman.Core.Models;
using Pathman.Core.Versions;

namespace Pathman.Core.EnvironmentVariables;

public sealed class SoftwarePathReferenceHealthCheck : IEnvironmentHealthCheck
{
    public string Id=>"software-path-reference";
    public string Title=>L.Get("软件路径引用");
    public IEnumerable<EnvironmentHealthIssue> Check(EnvironmentHealthContext context)
    {
        foreach(var scope in new[]{VariableScope.User,VariableScope.Machine})
        {
            if(context.Workspace is not {MonitoringEnabled:true} workspace || !workspace.IsPathOrganizationEnabled(scope)) continue;
            var path=context.Variables.FirstOrDefault(item=>item.Scope==scope && item.Name.Equals("Path",StringComparison.OrdinalIgnoreCase));
            if(path?.Entries.Select(EnvironmentPathResolver.PureReference).Any(name=>name is not null && CommandLinePathSetting.IsSoftwareVariable(name))==true) continue;
            var sources=context.Variables.Where(item=>item.Scope==scope && (item==path || CommandLinePathSetting.IsSoftwareVariable(item.Name)))
                .Select(item=>new VariableSnapshot(scope,item.Name,item.RawValue,(int)item.Kind)).ToArray();
            yield return new(EnvironmentIssueKind.Custom,scope,path?.Name ?? "Path",L.Get("已启用自动整理，但 PATH 未引用 SOFTWARE_PATH 或 SOFTWARE数字_PATH。")) {
                IsError=true,IdentityMessage="missing-software-path-reference",Sources=sources,
                Suggestion=L.Get("在相应作用域的 PATH 中添加软件路径变量的纯引用，例如 %SOFTWARE_PATH%。")};
        }
    }
}
