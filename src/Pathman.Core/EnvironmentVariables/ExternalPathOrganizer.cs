using Pathman.Core.Models;

namespace Pathman.Core.EnvironmentVariables;

public sealed record ExternalPathOrganizationPlan(VariableScope Scope,VariableSnapshot? BeforePath,
    IReadOnlyList<EnvironmentMutation> Mutations,IReadOnlyList<VariableSnapshot> Preconditions,int RemovedDuplicates,int MovedEntries,string Policy)
{
    public string Id {get;init;}=Guid.NewGuid().ToString("N");
    public bool ChangesPathOrder {get;init;}
    public string? Warning {get;init;}
}

public static class ExternalPathOrganizer
{
    public static string GroupName(VariableScope scope)=>scope==VariableScope.User ? "EXTERNEL_USER_PATH" : "EXTERNEL_PATH";
    public static string Policy(EnvironmentWorkspace state,VariableScope scope)=>$"{scope}:{state.IsPathOrganizationEnabled(scope)}:{state.ShouldRemovePathDuplicates(scope)}:{state.ShouldMovePathEntries(scope)}";
    public static ExternalPathOrganizationPlan Plan(VariableScope scope,VariableSnapshot? beforePath,
        IReadOnlyList<EnvironmentVariableInfo> current,EnvironmentWorkspace state,IReadOnlyDictionary<string,string>? runtime=null)
    {
        ExternalPathOrganizationPlan Empty(string? warning=null)=>new(scope,beforePath,[],[],0,0,Policy(state,scope)) {Warning=warning};
        if(!state.IsPathOrganizationEnabled(scope) || !state.ShouldRemovePathDuplicates(scope) && !state.ShouldMovePathEntries(scope)) return Empty();
        var groupName=GroupName(scope);
        var path=current.FirstOrDefault(item=>item.Scope==scope && item.Name.Equals("Path",StringComparison.OrdinalIgnoreCase));
        if(path is null) return Empty();
        var resolver=new EnvironmentPathResolver(current,runtime);
        string Key(string entry)
        {
            var expanded=resolver.Expand(entry,scope);
            return expanded.IsResolved ? string.Join('\0',expanded.Paths.Select(value=>EnvironmentPathResolver.Normalize(value) ?? value.Trim())) : entry.Trim();
        }
        string[] Keys(string entry,VariableScope entryScope)
        {
            var expanded=resolver.Expand(entry,entryScope);
            return expanded.IsResolved && expanded.Paths.All(value=>EnvironmentPathResolver.Normalize(value) is not null)
                ? expanded.Paths.Select(value=>EnvironmentPathResolver.Normalize(value)!).ToArray() : [];
        }
        var old=(beforePath?.Value.Split(';',StringSplitOptions.None) ?? []).GroupBy(Key,StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group=>group.Key,group=>group.Count(),StringComparer.OrdinalIgnoreCase);
        var added=new HashSet<int>();
        for(var index=0;index<path.Entries.Count;index++)
        {
            var key=Key(path.Entries[index]);
            if(old.TryGetValue(key,out var count) && count>0) old[key]=count-1;else added.Add(index);
        }
        if(added.Count==0) return Empty();
        var coverage=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if(scope==VariableScope.User)
            foreach(var machine in current.Where(item=>item.Scope==VariableScope.Machine && item.Name.Equals("Path",StringComparison.OrdinalIgnoreCase))) coverage.UnionWith(Keys(machine.RawValue,VariableScope.Machine));
        foreach(var index in Enumerable.Range(0,path.Entries.Count).Where(index=>!added.Contains(index))) coverage.UnionWith(Keys(path.Entries[index],scope));
        var removed=new HashSet<int>();
        foreach(var index in added.Order())
        {
            var keys=Keys(path.Entries[index],scope);
            if(state.ShouldRemovePathDuplicates(scope) && keys.Length>0 && keys.All(coverage.Contains)) removed.Add(index);
            else coverage.UnionWith(keys);
        }
        var moved=state.ShouldMovePathEntries(scope) ? added.Where(index=>!removed.Contains(index) && !string.IsNullOrWhiteSpace(path.Entries[index]) &&
            !EnvironmentPathResolver.ReferenceNames(path.Entries[index]).Contains(groupName,StringComparer.OrdinalIgnoreCase)).Order().ToArray() : [];
        var group=current.FirstOrDefault(item=>item.Scope==scope && item.Name.Equals(groupName,StringComparison.OrdinalIgnoreCase));
        var mutations=new List<EnvironmentMutation>();
        var newPath="";string? warning=null;
        var canMove=moved.Length>0;
        if(canMove)
        {
            var newGroup=string.Join(';',(group is null ? [] : group.Entries).Concat(moved.Select(index=>path.Entries[index])));
            var candidate=current.Where(item=>item!=group).Append(new EnvironmentVariableInfo(groupName,newGroup,scope)).ToArray();
            var expanded=new EnvironmentPathResolver(candidate,runtime).Expand(newGroup,scope);
            if(!expanded.IsResolved || EnvironmentPathResolver.ReferenceNames(newGroup).Any(name=>name.Equals("Path",StringComparison.OrdinalIgnoreCase)))
            {canMove=false;warning="外部新增 PATH 项包含无法安全展开的引用，已保留原位置。";}
            else mutations.Add(new(group is null ? "create" : "set",scope,groupName,newGroup,ExpectedValue:group?.RawValue,ExpectedKind:group?.Kind));
        }
        var hasReference=path.Entries.Select((entry,index)=>(entry,index)).Any(item=>!removed.Contains(item.index) && EnvironmentPathResolver.PureReference(item.entry)?.Equals(groupName,StringComparison.OrdinalIgnoreCase)==true);
        var result=new List<string>();
        for(var index=0;index<path.Entries.Count;index++)
        {
            if(removed.Contains(index)) continue;
            if(canMove && moved.Contains(index))
            {if(!hasReference) {result.Add($"%{groupName}%");hasReference=true;}continue;}
            result.Add(path.Entries[index]);
        }
        newPath=string.Join(';',result);
        if(newPath!=path.RawValue) mutations.Add(new("set",scope,path.Name,newPath,ExpectedValue:path.RawValue,ExpectedKind:path.Kind));
        var guards=new[] {path}.Concat(resolver.ReferencedVariables(path.RawValue,scope)).Concat(group is null ? [] : new[] {group}.Concat(resolver.ReferencedVariables(group.RawValue,scope)))
            .Concat(scope==VariableScope.User ? current.Where(item=>item.Scope==VariableScope.Machine && item.Name.Equals("Path",StringComparison.OrdinalIgnoreCase)).SelectMany(item=>new[] {item}.Concat(resolver.ReferencedVariables(item.RawValue,VariableScope.Machine))) : [])
            .DistinctBy(item=>EnvironmentWorkspace.Key(item.Scope,item.Name),StringComparer.OrdinalIgnoreCase).Select(item=>new VariableSnapshot(item.Scope,item.Name,item.RawValue,(int)item.Kind)).ToArray();
        var updated=current.Where(item=>!mutations.Any(change=>change.Scope==item.Scope && change.Name.Equals(item.Name,StringComparison.OrdinalIgnoreCase)))
            .Concat(mutations.Select(change=>new EnvironmentVariableInfo(change.Name,change.Value ?? "",change.Scope))).ToArray();
        var order=PathOrderComparison.Compare(path.RawValue,newPath,scope,current,updated,runtime);
        return new(scope,beforePath,mutations,guards,removed.Count,canMove ? moved.Length : 0,Policy(state,scope)) {ChangesPathOrder=order.Changed,Warning=warning};
    }
}
