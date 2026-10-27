using Pathman.Core.Models;
using Pathman.Core.Shims;

namespace Pathman.Core.EnvironmentVariables;

public sealed class AliasPathOrderHealthCheck : IEnvironmentHealthCheck
{
    public string Id=>"alias-path-order";public string Title=>L.Get("垫片目录顺序");
    public IEnumerable<EnvironmentHealthIssue> Check(EnvironmentHealthContext context)
    {
        var target=EnvironmentPathResolver.Normalize(context.ShimsDirectory);
        var paths=context.Variables.Where(item=>item.Name.Equals("PATH",StringComparison.OrdinalIgnoreCase)).ToArray();
        var occurrences=new List<(EnvironmentVariableInfo Variable,int Index,int ExpandedIndex,int ExpandedCount)>();
        foreach(var path in paths)
        for(var index=0;index<path.Entries.Count;index++)
        {
            var expanded=context.Resolver.Expand(path.Entries[index],path.Scope);
            if(!expanded.IsResolved) continue;
            for(var child=0;child<expanded.Paths.Count;child++)
                if(string.Equals(EnvironmentPathResolver.Normalize(expanded.Paths[child]),target,StringComparison.OrdinalIgnoreCase)) occurrences.Add((path,index,child,expanded.Paths.Count));
        }
        if(occurrences.Count==0) yield break;
        if(occurrences.Count==1 && occurrences[0] is var only && only.Variable.Scope==VariableScope.User && only.Index==only.Variable.Entries.Count-1 && only.ExpandedIndex==only.ExpandedCount-1) yield break;
        var user=paths.FirstOrDefault(path=>path.Scope==VariableScope.User);var root=user ?? occurrences[0].Variable;
        var sources=paths.SelectMany(context.SourcesFor).DistinctBy(item=>EnvironmentWorkspace.Key(item.Scope,item.Name),StringComparer.OrdinalIgnoreCase).OrderBy(item=>item.Scope).ThenBy(item=>item.Name,StringComparer.OrdinalIgnoreCase).ToArray();
        var issue=new EnvironmentHealthIssue(EnvironmentIssueKind.AliasPathOrder,root.Scope,root.Name,L.Get("垫片目录位于其他路径之前，或出现了多次。")) {
            IdentityMessage="垫片目录位于其他路径之前，或出现了多次。",Suggestion=L.Get("将垫片目录只保留在用户 Path 的最后一行，避免别名遮蔽其他程序。"),Sources=sources,
            Locations=occurrences.Select(item=>new EnvironmentHealthLocation(item.Variable.Scope,item.Variable.Name,item.Index+1,item.Variable.Entries[item.Index],item.Index>0 ? item.Variable.Entries[item.Index-1] : null,item.Index+1<item.Variable.Entries.Count ? item.Variable.Entries[item.Index+1] : null)).Distinct().ToArray()};
        if(occurrences.All(item=>item.Variable.Scope==VariableScope.User && item.ExpandedCount==1))
        {
            var layout=new PathmanLayout(AppContext.BaseDirectory,Path.GetDirectoryName(context.ShimsDirectory)!);
            var mutation=new AliasPathSetting(layout).Plan(true,context.Variables,context.Runtime);
            if(mutation is not null) issue=issue with {Fixes=[new(L.Get("移到用户 PATH 末尾"),[mutation],sources)]};
        }
        yield return issue with {DetailLocations=issue.Locations};
    }
}
