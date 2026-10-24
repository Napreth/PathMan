using Pathman.Core.Models;

namespace Pathman.Core.EnvironmentVariables;

public sealed class PathEditorItem(string value,bool draft=false)
{
    public Guid Id { get; }=Guid.NewGuid();
    public string Value { get; internal set; }=value;
    public bool IsDraft { get; internal set; }=draft;
}
public sealed record PathEditorRow(PathEditorItem Item,VariableScope OwnerScope,string OwnerName,int Depth,string ViewKey,
    VariableScope? TargetScope,string? TargetName,bool IsGroup,bool IsCycle,bool IsExpanded);

// The tree is a projection of variable values. A reference row moves as one raw
// entry; expanding it never copies or flattens the referenced variable's value.
public sealed class PathEditingSession
{
    private readonly Dictionary<string,EnvironmentVariableInfo> originals;
    private readonly Dictionary<string,List<PathEditorItem>> lists=new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<string,string>? runtime;
    public EnvironmentVariableInfo Root { get; private set; }
    public PathEditingSession(EnvironmentVariableInfo root,IReadOnlyList<EnvironmentVariableInfo> variables,IReadOnlyDictionary<string,string>? runtime=null)
    {
        Root=root; this.runtime=runtime;
        originals=variables.ToDictionary(item=>EnvironmentWorkspace.Key(item.Scope,item.Name),StringComparer.OrdinalIgnoreCase);
        originals[EnvironmentWorkspace.Key(root.Scope,root.Name)]=root;
        Items(root.Scope,root.Name);
    }
    private static string Key(VariableScope scope,string name)=>EnvironmentWorkspace.Key(scope,name);
    public IReadOnlyList<EnvironmentVariableInfo> Originals=>originals.Values.ToArray();
    public List<PathEditorItem> Items(VariableScope scope,string name)
    {
        var key=Key(scope,name);
        if(!lists.TryGetValue(key,out var items))
        {
            var raw=originals[key].RawValue;
            lists[key]=items=raw.Length==0 ? [] : raw.Split(';',StringSplitOptions.None).Select(value=>new PathEditorItem(value)).ToList();
        }
        return items;
    }
    public string Raw(VariableScope scope,string name)=>lists.TryGetValue(Key(scope,name),out var items)
        ? string.Join(';',items.Where(item=>!item.IsDraft).Select(item=>item.Value)) : originals[Key(scope,name)].RawValue;
    public void SetRaw(VariableScope scope,string name,string raw)
    {
        if(Raw(scope,name)==raw) return;
        lists[Key(scope,name)]=raw.Length==0 ? [] : raw.Split(';',StringSplitOptions.None).Select(value=>new PathEditorItem(value)).ToList();
    }
    public IReadOnlyList<EnvironmentVariableInfo> CurrentVariables()=>originals.Values.Select(item=>new EnvironmentVariableInfo(item.Name,Raw(item.Scope,item.Name),item.Scope,item.Kind)).ToArray();
    public bool HasChanges=>lists.Any(pair=>Raw(originals[pair.Key].Scope,originals[pair.Key].Name)!=originals[pair.Key].RawValue);
    public IReadOnlyList<EnvironmentMutation> Mutations()=>lists.Keys.Select(key=>originals[key]).Where(item=>Raw(item.Scope,item.Name)!=item.RawValue)
        .Select(item=>new EnvironmentMutation("set",item.Scope,item.Name,Raw(item.Scope,item.Name),ExpectedValue:item.RawValue,ExpectedKind:item.Kind)).ToArray();
    public IReadOnlyList<VariableSnapshot> Preconditions(VariableScope scope)=>lists.Keys.Select(key=>originals[key]).Where(item=>item.Scope==scope)
        .Select(item=>new VariableSnapshot(item.Scope,item.Name,item.RawValue,(int)item.Kind)).ToArray();
    public void Accept(VariableScope scope)
    {
        foreach(var original in originals.Values.Where(item=>item.Scope==scope && Raw(scope,item.Name)!=item.RawValue).ToArray())
            originals[Key(scope,original.Name)]=new(original.Name,Raw(scope,original.Name),scope,EnvironmentPathResolver.ReferenceNames(Raw(scope,original.Name)).Count>0 ? Microsoft.Win32.RegistryValueKind.ExpandString : original.Kind);
        Root=originals[Key(Root.Scope,Root.Name)];
    }
    public PathEditorItem Append(VariableScope scope,string name,string value="",bool draft=false)
    { var item=new PathEditorItem(value,draft); Items(scope,name).Add(item); return item; }
    public void Update(Guid id,string value,bool wasChanged=true)
    {
        if(value.IndexOfAny([';','\0','\r','\n'])>=0) throw new ArgumentException("单个条目不能包含分号、空字符或换行。");
        var source=Locate(id);
        if(value.Length==0 && (source.Item.IsDraft || source.Item.Value.Length>0 || wasChanged)) source.Items.Remove(source.Item);
        else {source.Item.Value=value; source.Item.IsDraft=false;}
    }
    public void Remove(Guid id) {var source=Locate(id); source.Items.Remove(source.Item);}
    public (VariableScope Scope,string Name,List<PathEditorItem> Items,PathEditorItem Item) Locate(Guid id)
    {
        foreach(var pair in lists) if(pair.Value.FirstOrDefault(item=>item.Id==id) is {} item)
        { var original=originals[pair.Key]; return(original.Scope,original.Name,pair.Value,item); }
        throw new ArgumentException("条目已变化，请重新选择。");
    }
    public string? ValidateMove(Guid itemId,VariableScope targetScope,string targetName,Guid? beforeId)
    {
        var source=Locate(itemId);
        if(source.Scope!=targetScope) return "不能在用户和系统作用域之间拖动路径。";
        if(!originals.ContainsKey(Key(targetScope,targetName))) return "目标路径变量不存在。";
        var target=Items(targetScope,targetName);
        if(beforeId is {} before && !target.Any(item=>item.Id==before)) return "插入位置已变化。";
        var resolver=new EnvironmentPathResolver(CurrentVariables(),runtime);
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool Reaches(string value,int depth=0)
        {
            if(depth>64 || seen.Count>4096) return true;
            foreach(var name in EnvironmentPathResolver.ReferenceNames(value))
            {
                var variable=resolver.Find(name,targetScope);
                if(variable is null) continue;
                if(variable.Scope==targetScope && variable.Name.Equals(targetName,StringComparison.OrdinalIgnoreCase)) return true;
                if(seen.Add(Key(variable.Scope,variable.Name)) && Reaches(variable.RawValue,depth+1)) return true;
            }
            return false;
        }
        return Reaches(source.Item.Value) ? "此位置会产生引用回环或超出引用层级限制。" : null;
    }
    public void Move(Guid itemId,VariableScope targetScope,string targetName,Guid? beforeId=null)
    {
        if(ValidateMove(itemId,targetScope,targetName,beforeId) is {} error) throw new ArgumentException(error);
        if(beforeId==itemId) return;
        var source=Locate(itemId); var target=Items(targetScope,targetName);
        source.Items.Remove(source.Item);
        var index=beforeId is null ? target.Count : target.FindIndex(item=>item.Id==beforeId);
        target.Insert(index,source.Item);
    }
    public IReadOnlyList<PathEditorRow> VisibleRows(ISet<string> expanded,int limit=8192)
    {
        var result=new List<PathEditorRow>();
        var variables=CurrentVariables(); var resolver=new EnvironmentPathResolver(variables,runtime);
        var ancestry=new HashSet<string>(StringComparer.OrdinalIgnoreCase) {Key(Root.Scope,Root.Name)};
        void Add(VariableScope scope,string name,int depth,string prefix)
        {
            foreach(var item in Items(scope,name))
            {
                if(result.Count>=limit) return;
                var viewKey=prefix+"/"+item.Id.ToString("N");
                var pure=EnvironmentPathResolver.PureReference(item.Value);
                var target=pure is null ? null : resolver.Find(pure,Root.Scope);
                var group=target?.Name.EndsWith("PATH",StringComparison.OrdinalIgnoreCase)==true;
                var cycle=group && (depth>=32 || ancestry.Contains(Key(target!.Scope,target.Name)));
                var open=group && !cycle && expanded.Contains(viewKey);
                result.Add(new(item,scope,name,depth,viewKey,target?.Scope,target?.Name,group,cycle,open));
                if(open)
                {
                    ancestry.Add(Key(target!.Scope,target.Name)); Add(target.Scope,target.Name,depth+1,viewKey); ancestry.Remove(Key(target.Scope,target.Name));
                }
            }
        }
        Add(Root.Scope,Root.Name,0,Key(Root.Scope,Root.Name)); return result;
    }
}
