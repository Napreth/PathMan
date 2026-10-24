using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Pathman.Core.Models;

namespace Pathman.Core.EnvironmentVariables;

public enum EnvironmentIssueKind { ReferenceCycle, UndefinedReference, UserScopeReference, RegistryType, Duplicate, EmptyPathEntry, ExpansionLimit }
public sealed record EnvironmentHealthLocation(VariableScope Scope,string Name,int Line,string Value,string? Previous,string? Next);
public sealed record EnvironmentHealthFix(string Label,IReadOnlyList<EnvironmentMutation> Mutations,IReadOnlyList<VariableSnapshot> Preconditions)
{
    public void Validate(IReadOnlyList<EnvironmentVariableInfo> current)
    {
        foreach(var guard in Preconditions)
            if(!current.Any(item=>item.Scope==guard.Scope && item.Name.Equals(guard.Name,StringComparison.OrdinalIgnoreCase) && item.RawValue==guard.Value && (int)item.Kind==guard.Kind))
                throw new IOException($"{guard.Name} 已变化，请重新检测后再操作。");
    }
}
public sealed record EnvironmentHealthIssue(EnvironmentIssueKind Kind,VariableScope Scope,string Name,string Message)
{
    public bool IsError=>Kind is EnvironmentIssueKind.ReferenceCycle or EnvironmentIssueKind.UndefinedReference or EnvironmentIssueKind.UserScopeReference or EnvironmentIssueKind.RegistryType;
    public string Suggestion {get;init;}="";
    public IReadOnlyList<EnvironmentHealthLocation> Locations {get;init;}=[];
    public IReadOnlyList<EnvironmentHealthFix> Fixes {get;init;}=[];
    public IReadOnlyList<VariableSnapshot> Sources {get;init;}=[];
    // Ignore only this exact finding. A changed source value/type is a new finding.
    public string Id=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new {Kind,Scope,Name,Message,Locations,Sources}))));
}
public sealed record EnvironmentHealthReport(IReadOnlyList<EnvironmentHealthIssue> Issues)
{
    public int Errors=>Issues.Count(item=>item.IsError);
    public int Warnings=>Issues.Count-Errors;
}

public static partial class EnvironmentHealth
{
    public static EnvironmentHealthReport Analyze(IReadOnlyList<EnvironmentVariableInfo> variables,IReadOnlyDictionary<string,string>? runtime=null)
    {
        var resolver=new EnvironmentPathResolver(variables,runtime);
        var issues=new List<EnvironmentHealthIssue>(); var cycles=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var variable in variables)
        {
            var sources=resolver.ReferencedVariables(variable.RawValue,variable.Scope).Prepend(variable)
                .DistinctBy(item=>EnvironmentWorkspace.Key(item.Scope,item.Name),StringComparer.OrdinalIgnoreCase)
                .OrderBy(item=>item.Scope).ThenBy(item=>item.Name,StringComparer.OrdinalIgnoreCase)
                .Select(item=>new VariableSnapshot(item.Scope,item.Name,item.RawValue,(int)item.Kind)).ToArray();
            EnvironmentHealthIssue Issue(EnvironmentIssueKind kind,string message,string suggestion)=>new(kind,variable.Scope,variable.Name,message) {Suggestion=suggestion,Sources=sources};
            var visiting=new List<EnvironmentVariableInfo>(); var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Visit(EnvironmentVariableInfo current,int depth)
            {
                var key=EnvironmentWorkspace.Key(current.Scope,current.Name);
                var cycleStart=visiting.FindIndex(item=>EnvironmentWorkspace.Key(item.Scope,item.Name).Equals(key,StringComparison.OrdinalIgnoreCase));
                if(cycleStart>=0)
                {
                    var cycle=visiting.Skip(cycleStart).ToArray();
                    var signature=$"{variable.Scope}:"+string.Join('|',cycle.Select(item=>EnvironmentWorkspace.Key(item.Scope,item.Name)).Order(StringComparer.OrdinalIgnoreCase));
                    if(cycles.Add(signature)) issues.Add(Issue(EnvironmentIssueKind.ReferenceCycle,"引用回环："+string.Join(" → ",cycle.Select(item=>item.Name).Append(current.Name)),"移除或更正其中一个回指引用。"));
                    return;
                }
                if(!visited.Add(key) || depth>32) return;
                visiting.Add(current);
                foreach(var name in EnvironmentPathResolver.ReferenceNames(current.RawValue)) if(resolver.Find(name,variable.Scope) is {} target) Visit(target,depth+1);
                visiting.RemoveAt(visiting.Count-1);
            }
            Visit(variable,0);
            var references=EnvironmentPathResolver.ReferenceNames(variable.RawValue);
            foreach(var name in references)
            {
                if(variable.Scope==VariableScope.Machine && resolver.Find(name,VariableScope.Machine) is null &&
                    (variables.Any(item=>item.Scope==VariableScope.User && item.Name.Equals(name,StringComparison.OrdinalIgnoreCase)) || EnvironmentVariableClassifier.IsUserOnlyBuiltIn(name)))
                    issues.Add(Issue(EnvironmentIssueKind.UserScopeReference,$"系统变量引用了仅在用户环境中可用的 %{name}%","改用系统作用域中的变量，或将这个引用移到用户变量。"));
                else if(!resolver.IsDefined(name,variable.Scope)) issues.Add(Issue(EnvironmentIssueKind.UndefinedReference,$"引用了未定义变量 %{name}%","创建被引用的变量，或更正引用名称。"));
            }
            if(references.Count>0 && variable.Kind!=RegistryValueKind.ExpandString)
                issues.Add(Issue(EnvironmentIssueKind.RegistryType,$"含引用的值当前为 {variable.Kind switch {RegistryValueKind.String=>"REG_SZ",_=>variable.Kind.ToString()}}，不会按可展开字符串处理。","保持原始内容，将注册表类型改为 REG_EXPAND_SZ。") with {
                    Fixes=[new("修正类型",[new("set",variable.Scope,variable.Name,variable.RawValue,ExpectedValue:variable.RawValue,ExpectedKind:variable.Kind)],sources)]});
            var expanded=resolver.Expand(variable.RawValue,variable.Scope);
            if(expanded.Error?.Contains("超过限制",StringComparison.Ordinal)==true)
                issues.Add(Issue(EnvironmentIssueKind.ExpansionLimit,"引用展开超过安全限制。","减少引用层级或拆分过长的变量内容。"));
            var pathLike=variable.Name.EndsWith("PATH",StringComparison.OrdinalIgnoreCase);
            if((!variable.IsList && !pathLike) || variable.RawValue.Length==0) continue;
            AnalyzeEntries(variable,resolver,sources,expanded.IsResolved,pathLike,issues);
        }
        return new(issues.DistinctBy(issue=>issue.Id).OrderByDescending(issue=>issue.IsError).ThenBy(issue=>issue.Scope).ThenBy(issue=>issue.Name,StringComparer.OrdinalIgnoreCase).ToArray());
    }
}
