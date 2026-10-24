using Pathman.Core.Models;

namespace Pathman.Core.EnvironmentVariables;

public static partial class EnvironmentHealth
{
    private sealed record EntrySource(EnvironmentVariableInfo Variable,int Index)
    {
        public string Key=>$"{EnvironmentWorkspace.Key(Variable.Scope,Variable.Name)}:{Index}";
        public EnvironmentHealthLocation Location()
        {
            var entries=Variable.RawValue.Split(';',StringSplitOptions.None);
            return new(Variable.Scope,Variable.Name,Index+1,entries[Index],Index>0 ? entries[Index-1] : null,Index+1<entries.Length ? entries[Index+1] : null);
        }
    }
    private sealed record Occurrence(string Value,IReadOnlyList<EntrySource> Sources);
    private static void AnalyzeEntries(EnvironmentVariableInfo variable,EnvironmentPathResolver resolver,IReadOnlyList<VariableSnapshot> guards,bool resolved,bool pathLike,List<EnvironmentHealthIssue> issues)
    {
        var occurrences=new List<Occurrence>();
        var ancestry=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Expand(EnvironmentVariableInfo owner,IReadOnlyList<EntrySource> chain,int depth)
        {
            if(depth>32 || occurrences.Count>=4096) return;
            var key=EnvironmentWorkspace.Key(owner.Scope,owner.Name);
            if(!ancestry.Add(key)) return;
            var entries=owner.RawValue.Split(';',StringSplitOptions.None);
            for(var index=0;index<entries.Length && occurrences.Count<4096;index++)
            {
                // An empty referenced value has no editable row of its own. Its
                // parent reference is the removable occurrence in the editor.
                var owners=owner.RawValue.Length==0 ? chain : chain.Append(new EntrySource(owner,index)).ToArray();
                var pure=EnvironmentPathResolver.PureReference(entries[index]);
                var target=pure is null ? null : resolver.Find(pure,variable.Scope);
                if(target is not null && !ancestry.Contains(EnvironmentWorkspace.Key(target.Scope,target.Name)))
                {
                    var start=occurrences.Count; Expand(target,owners,depth+1);
                    var suffix=entries[index][(pure!.Length+2)..];
                    if(suffix.Length>0 && occurrences.Count>start) occurrences[^1]=occurrences[^1] with {Value=occurrences[^1].Value+suffix};
                }
                else
                {
                    var expansion=resolver.Expand(entries[index],variable.Scope);
                    // Direct empty rows remain actionable even if a different
                    // entry in this variable contains an unresolved reference.
                    if(expansion.IsResolved || entries[index].Length==0)
                        occurrences.AddRange(expansion.Paths.Take(4096-occurrences.Count).Select(value=>new Occurrence(value,owners)));
                }
            }
            ancestry.Remove(key);
        }
        Expand(variable,[],0);
        string? Compare(Occurrence entry)=>pathLike ? EnvironmentPathResolver.Normalize(entry.Value) : entry.Value.Trim();
        var bySource=occurrences.SelectMany((occurrence,index)=>occurrence.Sources.Select(source=>(source,index)))
            .GroupBy(item=>item.source.Key,StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group=>group.Key,group=>group.Select(item=>item.index).ToHashSet(),StringComparer.OrdinalIgnoreCase);
        EnvironmentHealthFix Fix(EntrySource source,string label)
        {
            var owner=source.Variable;
            var value=string.Join(';',owner.RawValue.Split(';',StringSplitOptions.None).Where((_,index)=>index!=source.Index));
            return new(label,[new("set",owner.Scope,owner.Name,value,ExpectedValue:owner.RawValue,ExpectedKind:owner.Kind)],guards);
        }
        EnvironmentHealthIssue Finding(EnvironmentIssueKind kind,string message,string suggestion,IEnumerable<Occurrence> items)=>new(kind,variable.Scope,variable.Name,message) {
            Suggestion=suggestion,Sources=guards,Locations=items.Select(item=>item.Sources.LastOrDefault()).OfType<EntrySource>()
                .DistinctBy(source=>source.Key,StringComparer.OrdinalIgnoreCase).Take(8).Select(source=>source.Location()).ToArray()};
        if(pathLike || variable.IsList)
        {
            // Report each editable empty row separately, with its neighbors.
            var empties=occurrences.Where(item=>string.IsNullOrWhiteSpace(item.Value)).ToArray();
            foreach(var group in empties.GroupBy(item=>item.Sources.LastOrDefault()?.Key ?? "",StringComparer.OrdinalIgnoreCase))
            {
                var source=group.First().Sources.Reverse().FirstOrDefault(candidate=>bySource[candidate.Key].All(index=>string.IsNullOrWhiteSpace(occurrences[index].Value)));
                var finding=Finding(EnvironmentIssueKind.EmptyPathEntry,"列表包含空条目。","删除这一行会移除对应的分号位置，其余条目保持原样。",group);
                if(source is not null) finding=finding with {Fixes=[Fix(source,"删除空行")]};
                issues.Add(finding);
            }
        }
        if(!resolved) return;
        var keys=occurrences.Select(Compare).ToArray();
        var totals=keys.Where(key=>!string.IsNullOrEmpty(key)).GroupBy(key=>key!,StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group=>group.Key,group=>group.Count(),StringComparer.OrdinalIgnoreCase);
        var removable=bySource.ToDictionary(pair=>pair.Key,pair=>pair.Value.All(index=>!string.IsNullOrEmpty(keys[index])) &&
            pair.Value.GroupBy(index=>keys[index]!,StringComparer.OrdinalIgnoreCase).All(group=>totals[group.Key]>group.Count()),StringComparer.OrdinalIgnoreCase);
        foreach(var duplicate in Enumerable.Range(0,occurrences.Count).Where(index=>!string.IsNullOrEmpty(keys[index]))
            .GroupBy(index=>keys[index]!,StringComparer.OrdinalIgnoreCase).Where(group=>group.Count()>1))
        {
            var candidates=new Dictionary<string,EntrySource>(StringComparer.OrdinalIgnoreCase);
            foreach(var index in duplicate)
            {
                // Remove only an occurrence whose complete expanded contents
                // remain elsewhere. A reused leaf must not delete every copy.
                var candidate=occurrences[index].Sources.Reverse().FirstOrDefault(source=>removable[source.Key]);
                if(candidate is not null) candidates.TryAdd(candidate.Key,candidate);
            }
            var finding=Finding(EnvironmentIssueKind.Duplicate,$"展开后出现 {duplicate.Count()} 次：{duplicate.Key}",candidates.Count>0 ? "选择要删除的来源；保留的来源及其他路径不会改动。" : "此项来自复用的引用，请进入变量编辑调整引用，避免删除全部副本。",duplicate.Select(index=>occurrences[index]));
            issues.Add(finding with {Fixes=candidates.Values.Take(8).Select(source=>Fix(source,$"删除{(source.Variable.Scope==VariableScope.User ? "用户" : "系统")} {source.Variable.Name} 第 {source.Index+1} 行")).ToArray()});
        }
    }
}
