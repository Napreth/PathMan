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
    public EnvironmentHealthContext(IReadOnlyList<EnvironmentVariableInfo> variables,IReadOnlyDictionary<string,string>? runtime=null,string? shimsDirectory=null)
    {Variables=variables;Runtime=runtime;Resolver=new(variables,runtime);ShimsDirectory=shimsDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PathMan","shims");}
    public IReadOnlyList<VariableSnapshot> SourcesFor(EnvironmentVariableInfo variable)=>Resolver.ReferencedVariables(variable.RawValue,variable.Scope).Prepend(variable)
        .DistinctBy(item=>EnvironmentWorkspace.Key(item.Scope,item.Name),StringComparer.OrdinalIgnoreCase).OrderBy(item=>item.Scope).ThenBy(item=>item.Name,StringComparer.OrdinalIgnoreCase)
        .Select(item=>new VariableSnapshot(item.Scope,item.Name,item.RawValue,(int)item.Kind)).ToArray();
    public EnvironmentHealthIssue Issue(EnvironmentVariableInfo variable,EnvironmentIssueKind kind,string message,string suggestion)=>new(kind,variable.Scope,variable.Name,message)
    {Suggestion=suggestion,Sources=SourcesFor(variable)};
}
