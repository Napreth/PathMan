using Pathman.Core.Models;

namespace Pathman.Core.EnvironmentVariables;

public interface IEnvironmentHealthCheck
{
    string Id {get;}
    string Title {get;}
    int Order=>0;
    IEnumerable<EnvironmentHealthIssue> Check(EnvironmentHealthContext context);
}
public sealed record DelegateHealthCheck(string Id,string Title,Func<EnvironmentHealthContext,IEnumerable<EnvironmentHealthIssue>> Run) : IEnvironmentHealthCheck
{public IEnumerable<EnvironmentHealthIssue> Check(EnvironmentHealthContext context)=>Run(context);}

public sealed class EnvironmentHealthContext
{
    public IReadOnlyList<EnvironmentVariableInfo> Variables {get;}
    public IReadOnlyDictionary<string,string>? Runtime {get;}
    public EnvironmentPathResolver Resolver {get;}
    public string ShimsDirectory {get;}
    public EnvironmentWorkspace? Workspace {get;}
    public EnvironmentHealthContext(IReadOnlyList<EnvironmentVariableInfo> variables,IReadOnlyDictionary<string,string>? runtime=null,string? shimsDirectory=null,EnvironmentWorkspace? workspace=null)
    {Variables=variables;Runtime=runtime;Resolver=new(variables,runtime);Workspace=workspace;ShimsDirectory=shimsDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PathMan","shims");}
    public IReadOnlyList<VariableSnapshot> SourcesFor(EnvironmentVariableInfo variable)=>Resolver.ReferencedVariables(variable.RawValue,variable.Scope).Prepend(variable)
        .DistinctBy(item=>EnvironmentWorkspace.Key(item.Scope,item.Name),StringComparer.OrdinalIgnoreCase).OrderBy(item=>item.Scope).ThenBy(item=>item.Name,StringComparer.OrdinalIgnoreCase)
        .Select(item=>new VariableSnapshot(item.Scope,item.Name,item.RawValue,(int)item.Kind)).ToArray();
    public EnvironmentHealthLocation Location(EnvironmentVariableInfo variable,int index)
    {
        var entries=variable.RawValue.Split(';',StringSplitOptions.None);
        return new(variable.Scope,variable.Name,index+1,entries[index],index>0 ? entries[index-1] : null,index+1<entries.Length ? entries[index+1] : null);
    }
    public IEnumerable<EnvironmentHealthLocation> ReferenceLocations(EnvironmentVariableInfo variable,string name)=>variable.Entries
        .Select((value,index)=>(value,index)).Where(item=>EnvironmentPathResolver.ReferenceNames(item.value).Contains(name,StringComparer.OrdinalIgnoreCase)).Select(item=>Location(variable,item.index));
    public EnvironmentHealthIssue Issue(EnvironmentVariableInfo variable,EnvironmentIssueKind kind,string message,string suggestion,
        IEnumerable<EnvironmentHealthLocation>? detailLocations=null,string? identityMessage=null)=>new(kind,variable.Scope,variable.Name,message)
    {IdentityMessage=identityMessage ?? (kind switch {EnvironmentIssueKind.RegistryType=>$"含引用的值当前为 {(variable.Kind==Microsoft.Win32.RegistryValueKind.String ? "REG_SZ" : variable.Kind.ToString())}，不会按可展开字符串处理。",EnvironmentIssueKind.ExpansionLimit=>"引用展开超过安全限制。",_=>message}),Suggestion=suggestion,Sources=SourcesFor(variable),DetailLocations=(detailLocations ?? variable.Entries.Select((_,index)=>Location(variable,index))).ToArray()};
}
