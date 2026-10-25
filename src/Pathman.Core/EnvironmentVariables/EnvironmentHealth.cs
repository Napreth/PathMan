using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Pathman.Core.Models;

namespace Pathman.Core.EnvironmentVariables;

public enum EnvironmentIssueKind { ReferenceCycle, UndefinedReference, UserScopeReference, RegistryType, Duplicate, EmptyPathEntry, ExpansionLimit, AliasPathOrder, Custom }
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
    public bool IsError {get;init;}=Kind is EnvironmentIssueKind.ReferenceCycle or EnvironmentIssueKind.UndefinedReference or EnvironmentIssueKind.UserScopeReference or EnvironmentIssueKind.RegistryType;
    public string CheckId {get;init;}="";
    public string Category {get;init;}="";
    public string Suggestion {get;init;}="";
    public IReadOnlyList<EnvironmentHealthLocation> Locations {get;init;}=[];
    public IReadOnlyList<EnvironmentHealthFix> Fixes {get;init;}=[];
    public IReadOnlyList<VariableSnapshot> Sources {get;init;}=[];
    // Ignore only this exact finding. A changed source value/type is a new finding.
    private object Identity=>Kind==EnvironmentIssueKind.Custom ? new {CheckId,Kind,Scope,Name,Message,Locations,Sources} : new {Kind,Scope,Name,Message,Locations,Sources};
    public string Id=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(Identity))));
}
public sealed record EnvironmentHealthReport(IReadOnlyList<EnvironmentHealthIssue> Issues)
{
    public int Errors=>Issues.Count(item=>item.IsError);
    public int Warnings=>Issues.Count-Errors;
}

public static partial class EnvironmentHealth
{
    private static readonly Lazy<IReadOnlyList<IEnvironmentHealthCheck>> Discovered=new(()=>typeof(EnvironmentHealth).Assembly.GetTypes()
        .Where(type=>!type.IsAbstract && typeof(IEnvironmentHealthCheck).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) is not null)
        .Select(type=>(IEnvironmentHealthCheck)Activator.CreateInstance(type)!).OrderBy(check=>check.Order).ThenBy(check=>check.Id,StringComparer.Ordinal).ToArray());
    public static IReadOnlyList<IEnvironmentHealthCheck> DefaultChecks=>Discovered.Value;
    public static EnvironmentHealthReport Analyze(IReadOnlyList<EnvironmentVariableInfo> variables,IReadOnlyDictionary<string,string>? runtime=null,
        string? shimsDirectory=null,IEnumerable<IEnvironmentHealthCheck>? checks=null)
    {
        var context=new EnvironmentHealthContext(variables,runtime,shimsDirectory);
        var issues=(checks ?? DefaultChecks).SelectMany(check=>check.Check(context).Select(issue=>issue with {
            CheckId=check.Id,Category=string.IsNullOrEmpty(issue.Category) ? check.Title : issue.Category}));
        return new(issues.DistinctBy(issue=>issue.Id).OrderByDescending(issue=>issue.IsError).ThenBy(issue=>issue.Scope)
            .ThenBy(issue=>issue.Name,StringComparer.OrdinalIgnoreCase).ToArray());
    }
}
