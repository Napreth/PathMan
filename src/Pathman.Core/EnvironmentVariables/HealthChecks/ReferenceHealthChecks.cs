using Microsoft.Win32;
using Pathman.Core.Models;

namespace Pathman.Core.EnvironmentVariables;

public sealed class ReferenceCycleHealthCheck : IEnvironmentHealthCheck
{
    public string Id=>"reference-cycle"; public string Title=>L.Get("引用回环");
    public IEnumerable<EnvironmentHealthIssue> Check(EnvironmentHealthContext context)
    {
        var issues=new List<EnvironmentHealthIssue>();var cycles=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var variable in context.Variables)
        {
            var visiting=new List<EnvironmentVariableInfo>();var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Visit(EnvironmentVariableInfo current,int depth,IReadOnlyList<EnvironmentHealthLocation> chain)
            {
                var key=EnvironmentWorkspace.Key(current.Scope,current.Name);
                var start=visiting.FindIndex(item=>EnvironmentWorkspace.Key(item.Scope,item.Name).Equals(key,StringComparison.OrdinalIgnoreCase));
                if(start>=0)
                {
                    var cycle=visiting.Skip(start).ToArray();var signature=$"{variable.Scope}:"+string.Join('|',cycle.Select(item=>EnvironmentWorkspace.Key(item.Scope,item.Name)).Order(StringComparer.OrdinalIgnoreCase));
                    if(cycles.Add(signature)) issues.Add(context.Issue(variable,EnvironmentIssueKind.ReferenceCycle,L.Get("引用回环：")+string.Join(" → ",cycle.Select(item=>item.Name).Append(current.Name)),L.Get("移除或更正其中一个回指引用。"),chain,identityMessage:"引用回环："+string.Join(" → ",cycle.Select(item=>item.Name).Append(current.Name))));
                    return;
                }
                if(!visited.Add(key) || depth>32) return;
                visiting.Add(current);
                for(var index=0;index<current.Entries.Count;index++)
                    foreach(var name in EnvironmentPathResolver.ReferenceNames(current.Entries[index]))
                        if(context.Resolver.Find(name,variable.Scope) is {} target) Visit(target,depth+1,chain.Append(context.Location(current,index)).ToArray());
                visiting.RemoveAt(visiting.Count-1);
            }
            Visit(variable,0,[]);
        }
        return issues;
    }
}
public sealed class UndefinedReferenceHealthCheck : IEnvironmentHealthCheck
{
    public string Id=>"undefined-reference";public string Title=>L.Get("未定义变量");
    internal static bool IsUserOnly(EnvironmentHealthContext context,string name)=>context.Resolver.Find(name,VariableScope.Machine) is null &&
        (context.Variables.Any(item=>item.Scope==VariableScope.User && item.Name.Equals(name,StringComparison.OrdinalIgnoreCase)) || EnvironmentVariableClassifier.IsUserOnlyBuiltIn(name));
    public IEnumerable<EnvironmentHealthIssue> Check(EnvironmentHealthContext context)
    {
        foreach(var variable in context.Variables)
        foreach(var name in EnvironmentPathResolver.ReferenceNames(variable.RawValue))
            if(!(variable.Scope==VariableScope.Machine && IsUserOnly(context,name)) && !context.Resolver.IsDefined(name,variable.Scope))
                yield return context.Issue(variable,EnvironmentIssueKind.UndefinedReference,L.Format("引用了未定义变量 %{0}%",name),L.Get("创建被引用的变量，或更正引用名称。"),context.ReferenceLocations(variable,name),identityMessage:$"引用了未定义变量 %{name}%");
    }
}
public sealed class UserScopeReferenceHealthCheck : IEnvironmentHealthCheck
{
    public string Id=>"machine-user-reference";public string Title=>L.Get("系统变量引用用户变量");
    public IEnumerable<EnvironmentHealthIssue> Check(EnvironmentHealthContext context)
    {
        foreach(var variable in context.Variables.Where(item=>item.Scope==VariableScope.Machine))
        foreach(var name in EnvironmentPathResolver.ReferenceNames(variable.RawValue).Where(name=>UndefinedReferenceHealthCheck.IsUserOnly(context,name)))
        {
            var issue=context.Issue(variable,EnvironmentIssueKind.UserScopeReference,L.Format("系统变量引用了仅在用户环境中可用的 %{0}%",name),L.Get("改用系统作用域中的变量，或将这个引用移到用户变量。"),context.ReferenceLocations(variable,name),identityMessage:$"系统变量引用了仅在用户环境中可用的 %{name}%");
            var user=context.Variables.FirstOrDefault(item=>item.Scope==VariableScope.User && item.Name.Equals(name,StringComparison.OrdinalIgnoreCase));
            if(user is not null) issue=issue with {Sources=issue.Sources.Append(new(VariableScope.User,user.Name,user.RawValue,(int)user.Kind)).ToArray()};
            yield return issue;
        }
    }
}
public sealed class RegistryTypeHealthCheck : IEnvironmentHealthCheck
{
    public string Id=>"registry-type";public string Title=>L.Get("注册表类型");
    public IEnumerable<EnvironmentHealthIssue> Check(EnvironmentHealthContext context)
    {
        foreach(var variable in context.Variables.Where(item=>EnvironmentPathResolver.ReferenceNames(item.RawValue).Count>0 && item.Kind!=RegistryValueKind.ExpandString))
            yield return context.Issue(variable,EnvironmentIssueKind.RegistryType,L.Format("含引用的值当前为 {0}，不会按可展开字符串处理。",variable.Kind switch {RegistryValueKind.String=>"REG_SZ",_=>variable.Kind.ToString()}),L.Get("保持原始内容，将注册表类型改为 REG_EXPAND_SZ。"),variable.Entries.Select((value,index)=>(value,index)).Where(item=>EnvironmentPathResolver.ReferenceNames(item.value).Count>0).Select(item=>context.Location(variable,item.index))) with {
                Fixes=[new(L.Get("修正类型"),[new("set",variable.Scope,variable.Name,variable.RawValue,ExpectedValue:variable.RawValue,ExpectedKind:variable.Kind)],context.SourcesFor(variable))]};
    }
}
public sealed class PathEntriesHealthCheck : IEnvironmentHealthCheck
{
    public string Id=>"path-entries";public string Title=>L.Get("路径条目");
    public IEnumerable<EnvironmentHealthIssue> Check(EnvironmentHealthContext context)
    {
        var issues=new List<EnvironmentHealthIssue>();
        foreach(var variable in context.Variables)
        {
            var expanded=context.Resolver.Expand(variable.RawValue,variable.Scope);
            if(expanded.LimitExceeded) issues.Add(context.Issue(variable,EnvironmentIssueKind.ExpansionLimit,L.Get("引用展开超过安全限制。"),L.Get("减少引用层级或拆分过长的变量内容。")) with {Category=L.Get("引用层级")});
            var pathLike=variable.Name.EndsWith("PATH",StringComparison.OrdinalIgnoreCase);
            if((variable.IsList || pathLike) && variable.RawValue.Length>0) EnvironmentHealth.AnalyzeEntries(variable,context.Resolver,context.SourcesFor(variable),expanded.IsResolved,pathLike,issues);
        }
        return issues.Select(issue=>issue with {Category=issue.Kind==EnvironmentIssueKind.EmptyPathEntry ? L.Get("空条目") : issue.Kind==EnvironmentIssueKind.Duplicate ? L.Get("重复条目") : issue.Category});
    }
}
