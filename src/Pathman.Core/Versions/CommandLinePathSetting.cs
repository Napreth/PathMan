using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;

namespace Pathman.Core.Versions;

public sealed record CommandLinePathPlan(IReadOnlyList<EnvironmentMutation> Mutations,IReadOnlyList<VariableSnapshot> Preconditions);

public sealed class CommandLinePathSetting(PathmanLayout layout,VariableScope scope)
{
    public static bool IsSoftwareVariable(string name)
    {
        if(!name.StartsWith("SOFTWARE",StringComparison.OrdinalIgnoreCase) || !name.EndsWith("_PATH",StringComparison.OrdinalIgnoreCase)) return false;
        return name.AsSpan(8,name.Length-13).IndexOfAnyExceptInRange('0','9')<0;
    }
    public static string SoftwareVariable(IReadOnlyList<EnvironmentVariableInfo> variables,VariableScope scope)
    {
        var path=variables.FirstOrDefault(item=>item.Scope==scope && IsPath(item.Name));
        return path?.Entries.Select(EnvironmentPathResolver.PureReference).FirstOrDefault(name=>name is not null && IsSoftwareVariable(name))
            ?? variables.Where(item=>item.Scope==scope && IsSoftwareVariable(item.Name)).OrderBy(item=>item.Name.Length).ThenBy(item=>item.Name,StringComparer.OrdinalIgnoreCase).FirstOrDefault()?.Name
            ?? "SOFTWARE_PATH";
    }
    public CommandLinePathPlan Plan(bool enabled,EnvironmentWorkspace state,IReadOnlyList<EnvironmentVariableInfo> variables,IReadOnlyDictionary<string,string>? runtime=null)
    {
        if(scope is not (VariableScope.User or VariableScope.Machine)) throw new ArgumentOutOfRangeException(nameof(scope));
        var path=variables.FirstOrDefault(item=>item.Scope==scope && IsPath(item.Name));
        var resolver=new EnvironmentPathResolver(variables,runtime);
        var target=EnvironmentPathResolver.Normalize(layout.BinDirectory);
        bool Matches(string entry)=>resolver.Expand(entry,scope) is {IsResolved:true,Paths.Count:1} value &&
            string.Equals(EnvironmentPathResolver.Normalize(value.Paths[0]),target,StringComparison.OrdinalIgnoreCase);
        bool Covers(string entry)=>resolver.Expand(entry,scope) is {IsResolved:true} value && value.Paths.Any(item=>
            string.Equals(EnvironmentPathResolver.Normalize(item),target,StringComparison.OrdinalIgnoreCase));
        var sources=(path is null ? [] : resolver.ReferencedVariables(path.RawValue,scope).Prepend(path))
            .Concat(variables.Where(item=>item.Scope==scope && IsSoftwareVariable(item.Name))).Where(item=>item.Scope==scope)
            .DistinctBy(item=>item.Name,StringComparer.OrdinalIgnoreCase).ToArray();
        var mutations=new List<EnvironmentMutation>();
        void Set(EnvironmentVariableInfo? existing,string name,string value)
        {
            if(existing?.RawValue==value) return;
            mutations.Add(new(existing is null ? "create" : "set",scope,existing?.Name ?? name,value,ExpectedValue:existing?.RawValue,ExpectedKind:existing?.Kind));
        }
        if(!enabled)
        {
            // Keep references and their other contents; remove only this CLI's
            // leaf entries, including entries moved into nested path variables.
            foreach(var item in sources)
            {
                var retained=item.Entries.Where(entry=>EnvironmentPathResolver.PureReference(entry) is not null || !Matches(entry)).ToArray();
                if(retained.Length!=item.Entries.Count) Set(item,item.Name,string.Join(';',retained));
            }
        }
        else if(state.MonitoringEnabled && state.IsPathOrganizationEnabled(scope))
        {
            // A nontrivial reference already exposing bin is left intact.
            var direct=path?.Entries.Where(entry=>EnvironmentPathResolver.PureReference(entry) is null && Matches(entry)).ToArray() ?? [];
            if(path?.Entries.Any(Covers)==true && direct.Length==0) return new([],[]);
            var name=SoftwareVariable(variables,scope);
            var software=variables.FirstOrDefault(item=>item.Scope==scope && item.Name.Equals(name,StringComparison.OrdinalIgnoreCase));
            if(software?.Entries.Any(Covers)!=true) Set(software,name,Append(software?.RawValue,layout.BinDirectory));
            if(direct.Length>0) Set(path,path!.Name,string.Join(';',path.Entries.Where(entry=>!direct.Contains(entry,StringComparer.Ordinal))));
            // Missing SOFTWARE*_PATH references are reported by health checks;
            // this mode never inserts a new reference into PATH implicitly.
        }
        else if(path?.Entries.Any(Covers)!=true)
        {
            var entries=path is null || path.RawValue.Length==0 ? new List<string>() : path.Entries.ToList();
            var index=entries.Count;
            if(scope==VariableScope.User && index>0 && resolver.Expand(entries[^1],scope) is {IsResolved:true,Paths.Count:1} last &&
                string.Equals(EnvironmentPathResolver.Normalize(last.Paths[0]),EnvironmentPathResolver.Normalize(layout.ShimsDirectory),StringComparison.OrdinalIgnoreCase)) index--;
            entries.Insert(index,layout.BinDirectory);Set(path,"Path",string.Join(';',entries));
        }
        return new(mutations,sources.Select(item=>new VariableSnapshot(scope,item.Name,item.RawValue,(int)item.Kind)).ToArray());
    }
    private static bool IsPath(string name)=>name.Equals("Path",StringComparison.OrdinalIgnoreCase);
    private static string Append(string? before,string entry)=>string.IsNullOrEmpty(before) ? entry : before+";"+entry;
}
