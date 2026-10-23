using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;

namespace Pathman.Core.Shims;

public sealed class AliasPathSetting(PathmanLayout layout)
{
    public bool IsEnabled(IReadOnlyList<EnvironmentVariableInfo> variables, IReadOnlyDictionary<string, string>? runtime = null)
    {
        var path=variables.FirstOrDefault(item=>item.Scope==VariableScope.User && item.Name.Equals("PATH",StringComparison.OrdinalIgnoreCase));
        var resolver=new EnvironmentPathResolver(variables,runtime);
        var target=EnvironmentPathResolver.Normalize(layout.ShimsDirectory);
        return path?.Entries.Any(entry=>resolver.Expand(entry,VariableScope.User) is { IsResolved:true } expansion &&
            expansion.Paths.Any(item=>string.Equals(EnvironmentPathResolver.Normalize(item),target,StringComparison.OrdinalIgnoreCase)))==true;
    }
    public EnvironmentMutation? Plan(bool enabled, IReadOnlyList<EnvironmentVariableInfo> variables, IReadOnlyDictionary<string, string>? runtime = null)
    {
        var path=variables.FirstOrDefault(item=>item.Scope==VariableScope.User && item.Name.Equals("PATH",StringComparison.OrdinalIgnoreCase));
        if(path is null) return enabled ? new("create",VariableScope.User,"Path",layout.ShimsDirectory) : null;
        var resolver=new EnvironmentPathResolver(variables,runtime);
        var target=EnvironmentPathResolver.Normalize(layout.ShimsDirectory);
        var retained=new List<string>();
        foreach(var entry in path.Entries)
        {
            var expansion=resolver.Expand(entry,VariableScope.User);
            var matches=expansion.IsResolved && expansion.Paths.Any(item=>string.Equals(EnvironmentPathResolver.Normalize(item),target,StringComparison.OrdinalIgnoreCase));
            if(matches && expansion.Paths.Count>1) throw new InvalidOperationException("垫片目录包含在多路径引用中，请先将它从被引用变量中移出，再切换程序别名。");
            if(!matches) retained.Add(entry);
        }
        if(enabled) retained.Add(layout.ShimsDirectory);
        var value=string.Join(';',retained);
        return value==path.RawValue ? null : new("set",VariableScope.User,path.Name,value,ExpectedValue:path.RawValue,ExpectedKind:path.Kind);
    }
}
