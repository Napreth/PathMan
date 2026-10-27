using Pathman.Core.Models;

namespace Pathman.Core.EnvironmentVariables;

public sealed class PathEditorItem(string value,bool draft=false)
{
    public Guid Id { get; internal set; }=Guid.NewGuid();
    public string Value { get; internal set; }=value;
    public bool IsDraft { get; internal set; }=draft;
}
public sealed record PathEditorRow(PathEditorItem Item,VariableScope OwnerScope,string OwnerName,int Depth,string ViewKey,
    VariableScope? TargetScope,string? TargetName,bool IsGroup,bool IsCycle,bool IsExpanded,
    EnvironmentEditOrigin Origin=EnvironmentEditOrigin.None,bool IsGhost=false,bool IsReview=false);

// The tree is a projection of variable values. A reference row moves as one raw
// entry; expanding it never copies or flattens the referenced variable's value.
public sealed class PathEditingSession
{
    private sealed record EditState(Dictionary<string,EnvironmentVariableInfo> Originals,Dictionary<string,List<PathEditorItem>> Lists,
        Dictionary<string,EnvironmentLiveValue> Live,HashSet<string> Missing,Dictionary<string,VariableSnapshot> Guards,bool Review);
    private readonly List<EditState> undo=[],redo=[];
    private int historyBatch;
    public bool CanUndo=>undo.Count>0;
    public bool CanRedo=>redo.Count>0;
    private EditState CaptureState()=>new(new(originals,StringComparer.OrdinalIgnoreCase),
        lists.ToDictionary(pair=>pair.Key,pair=>pair.Value.Where(item=>!item.IsDraft).Select(item=>new PathEditorItem(item.Value) {Id=item.Id}).ToList(),StringComparer.OrdinalIgnoreCase),
        live.ToDictionary(pair=>pair.Key,pair=>pair.Value.Copy(),StringComparer.OrdinalIgnoreCase),new(missing,StringComparer.OrdinalIgnoreCase),new(extraGuards,StringComparer.OrdinalIgnoreCase),IsReviewMode);
    private void Remember()
    {
        if(historyBatch>0) return;
        undo.Add(CaptureState());if(undo.Count>128) undo.RemoveAt(0);redo.Clear();
    }
    private void Restore(EditState state)
    {
        originals.Clear();foreach(var pair in state.Originals) originals.Add(pair.Key,pair.Value);
        lists.Clear();foreach(var pair in state.Lists) lists.Add(pair.Key,pair.Value.Select(item=>new PathEditorItem(item.Value) {Id=item.Id}).ToList());
        live.Clear();foreach(var pair in state.Live) live.Add(pair.Key,pair.Value.Copy());
        missing.Clear();missing.UnionWith(state.Missing);extraGuards.Clear();foreach(var pair in state.Guards) extraGuards.Add(pair.Key,pair.Value);
        IsReviewMode=state.Review;Root=originals[Key(Root.Scope,Root.Name)];
    }
    public bool Undo()
    {if(!CanUndo) return false;redo.Add(CaptureState());var state=undo[^1];undo.RemoveAt(undo.Count-1);Restore(state);return true;}
    public bool Redo()
    {if(!CanRedo) return false;undo.Add(CaptureState());var state=redo[^1];redo.RemoveAt(redo.Count-1);Restore(state);return true;}
    private readonly Dictionary<string,EnvironmentVariableInfo> originals;
    private readonly Dictionary<string,List<PathEditorItem>> lists=new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,EnvironmentLiveValue> live=new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> missing=new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,VariableSnapshot> extraGuards=new(StringComparer.OrdinalIgnoreCase);
    public bool IsReviewMode {get;private set;}
    public bool HasExternalChanges=>live.Values.Any(value=>value.HasExternalChanges);
    public bool HasConflicts=>live.Values.Any(value=>value.HasConflicts);
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
            live[key]=new(raw);
        }
        return items;
    }
    public string Raw(VariableScope scope,string name)=>lists.TryGetValue(Key(scope,name),out var items)
        ? string.Join(';',items.Where(item=>!item.IsDraft).Select(item=>item.Value)) : originals[Key(scope,name)].RawValue;
    public void SetRaw(VariableScope scope,string name,string raw)
    {
        if(Raw(scope,name)==raw) return;
        Remember();
        lists[Key(scope,name)]=raw.Length==0 ? [] : raw.Split(';',StringSplitOptions.None).Select(value=>new PathEditorItem(value)).ToList();
        Capture(scope,name);
    }
    public IReadOnlyList<EnvironmentVariableInfo> CurrentVariables()=>originals.Values.Select(item=>new EnvironmentVariableInfo(item.Name,Raw(item.Scope,item.Name),item.Scope,item.Kind)).ToArray();
    public bool HasChanges=>live.Values.Any(value=>value.HasLocalChanges) || lists.Any(pair=>missing.Contains(pair.Key) || Raw(originals[pair.Key].Scope,originals[pair.Key].Name)!=originals[pair.Key].RawValue);
    public IReadOnlyList<EnvironmentMutation> Mutations()=>lists.Keys.Select(key=>originals[key]).Where(item=>missing.Contains(Key(item.Scope,item.Name)) || Raw(item.Scope,item.Name)!=item.RawValue)
        .Select(item=>new EnvironmentMutation(missing.Contains(Key(item.Scope,item.Name)) ? "create" : "set",item.Scope,item.Name,Raw(item.Scope,item.Name),ExpectedValue:missing.Contains(Key(item.Scope,item.Name)) ? null : item.RawValue,ExpectedKind:missing.Contains(Key(item.Scope,item.Name)) ? null : item.Kind)).ToArray();
    public IReadOnlyList<VariableSnapshot> Preconditions(VariableScope scope)=>lists.Keys.Where(key=>!missing.Contains(key)).Select(key=>originals[key]).Where(item=>item.Scope==scope)
        .Select(item=>new VariableSnapshot(item.Scope,item.Name,item.RawValue,(int)item.Kind)).Concat(extraGuards.Values.Where(item=>scope==VariableScope.User || item.Scope==scope))
        .DistinctBy(item=>Key(item.Scope,item.Name),StringComparer.OrdinalIgnoreCase).ToArray();
    public void Accept(VariableScope scope)
    {
        undo.Clear();redo.Clear();
        foreach(var original in originals.Values.Where(item=>item.Scope==scope && Raw(scope,item.Name)!=item.RawValue).ToArray())
            originals[Key(scope,original.Name)]=new(original.Name,Raw(scope,original.Name),scope,EnvironmentPathResolver.ReferenceNames(Raw(scope,original.Name)).Count>0 ? Microsoft.Win32.RegistryValueKind.ExpandString : original.Kind);
        Root=originals[Key(Root.Scope,Root.Name)];
        foreach(var pair in live.Where(pair=>originals[pair.Key].Scope==scope)) {pair.Value.Accept();missing.Remove(pair.Key);}
        if(!HasExternalChanges) IsReviewMode=false;
    }
    private void Capture(VariableScope scope,string name)
    {
        var key=Key(scope,name);
        if(!live.TryGetValue(key,out var value)) live[key]=value=new(originals[key].RawValue);
        value.Edit(Raw(scope,name));
    }
    private void ReplaceWorking(string key,string raw)
    {
        var previous=lists[key].Where(item=>!item.IsDraft).GroupBy(item=>item.Value).ToDictionary(group=>group.Key,group=>new Queue<PathEditorItem>(group),StringComparer.Ordinal);
        var drafts=lists[key].Where(item=>item.IsDraft).ToArray();
        lists[key]=(raw.Length==0 ? Enumerable.Empty<PathEditorItem>() : raw.Split(';',StringSplitOptions.None).Select(text=>previous.TryGetValue(text,out var queue) && queue.Count>0 ? queue.Dequeue() : new PathEditorItem(text))).Concat(drafts).ToList();
    }
    public void Observe(IReadOnlyList<EnvironmentVariableInfo> current)
    {
        undo.Clear();redo.Clear();
        var values=current.ToDictionary(item=>Key(item.Scope,item.Name),StringComparer.OrdinalIgnoreCase);
        foreach(var key in originals.Keys.ToArray())
        {
            var original=originals[key];var exists=values.TryGetValue(key,out var latest);
            if(!exists) {latest=new(original.Name,"",original.Scope,original.Kind);missing.Add(key);} else missing.Remove(key);
            if(live.TryGetValue(key,out var value) && (latest!.RawValue!=value.External || latest.Kind!=original.Kind))
            {value.Observe(latest.RawValue);ReplaceWorking(key,value.Working);IsReviewMode=true;}
            originals[key]=latest!;
        }
        foreach(var pair in values) if(!originals.ContainsKey(pair.Key)) originals[pair.Key]=pair.Value;
        Root=originals[Key(Root.Scope,Root.Name)];
        extraGuards.Clear();
    }
    public void UndoExternal()
    {Remember();foreach(var pair in live) {pair.Value.UndoExternal();ReplaceWorking(pair.Key,pair.Value.Working);}}
    public void FinishReview()
    {foreach(var value in live.Values) value.Accept();IsReviewMode=false;}
    public string UserBranch(VariableScope scope,string name)=>live.TryGetValue(Key(scope,name),out var value) ? value.Local : Raw(scope,name);
    public void Stage(IReadOnlyList<EnvironmentMutation> mutations,IReadOnlyList<VariableSnapshot> guards)
    {
        if(mutations.Count==0) return;
        Remember();historyBatch++;
        try {
        foreach(var mutation in mutations)
        {
            var key=Key(mutation.Scope,mutation.Name);
            if(!originals.ContainsKey(key)) {originals[key]=new(mutation.Name,"",mutation.Scope);missing.Add(key);Items(mutation.Scope,mutation.Name);}
            SetRaw(mutation.Scope,mutation.Name,mutation.Value ?? "");
        }
        foreach(var guard in guards) if(!lists.ContainsKey(Key(guard.Scope,guard.Name))) extraGuards[Key(guard.Scope,guard.Name)]=guard;
        } finally {historyBatch--;}
    }
    public PathEditorItem Append(VariableScope scope,string name,string value="",bool draft=false)
    { if(!draft) Remember();var item=new PathEditorItem(value,draft); Items(scope,name).Add(item);Capture(scope,name);return item; }
    public void Update(Guid id,string value,bool wasChanged=true)
    {
        if(value.IndexOfAny([';','\0','\r','\n'])>=0) throw new ArgumentException("单个条目不能包含分号、空字符或换行。");
        var source=Locate(id);
        if((!source.Item.IsDraft || value.Length>0) && (source.Item.Value!=value || value.Length==0 && wasChanged)) Remember();
        if(value.Length==0 && (source.Item.IsDraft || source.Item.Value.Length>0 || wasChanged)) source.Items.Remove(source.Item);
        else {source.Item.Value=value; source.Item.IsDraft=false;}
        Capture(source.Scope,source.Name);
    }
    public void Remove(Guid id) {var source=Locate(id);if(!source.Item.IsDraft) Remember();source.Items.Remove(source.Item);Capture(source.Scope,source.Name);}
    public void RemoveMany(IEnumerable<Guid> ids)
    {
        var items=ids.Distinct().ToArray();if(items.Length==0) return;
        foreach(var id in items) Locate(id);
        Remember();historyBatch++;
        try {foreach(var id in items) Remove(id);} finally {historyBatch--;}
    }
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
        Remember();
        source.Items.Remove(source.Item);
        var index=beforeId is null ? target.Count : target.FindIndex(item=>item.Id==beforeId);
        target.Insert(index,source.Item);
        Capture(source.Scope,source.Name);if(source.Name!=targetName) Capture(targetScope,targetName);
    }
    public IReadOnlyList<PathEditorRow> VisibleRows(ISet<string> expanded,int limit=8192)
    {
        var result=new List<PathEditorRow>();
        var variables=CurrentVariables(); var resolver=new EnvironmentPathResolver(variables,runtime);
        var ancestry=new HashSet<string>(StringComparer.OrdinalIgnoreCase) {Key(Root.Scope,Root.Name)};
        void Add(VariableScope scope,string name,int depth,string prefix)
        {
            var key=Key(scope,name);var items=Items(scope,name);
            var visible=IsReviewMode ? live[key].Review().Select(line=>(Item:line.WorkingIndex is {} i && i<items.Count ? items.Where(item=>!item.IsDraft).ElementAt(i) : new PathEditorItem(line.Text),Origin:line.Origin,Ghost:line.WorkingIndex is null)).Concat(items.Where(item=>item.IsDraft).Select(item=>(Item:item,Origin:EnvironmentEditOrigin.UserAdded,Ghost:false))) : items.Select(item=>(Item:item,Origin:EnvironmentEditOrigin.None,Ghost:false));
            var ghostIndex=0;
            foreach(var occurrence in visible)
            {
                var item=occurrence.Item;
                if(result.Count>=limit) return;
                var viewKey=prefix+"/"+(occurrence.Ghost ? "ghost:"+(ghostIndex++) : item.Id.ToString("N"));
                var pure=EnvironmentPathResolver.PureReference(item.Value);
                var target=pure is null ? null : resolver.Find(pure,Root.Scope);
                var group=!occurrence.Ghost && target?.Name.EndsWith("PATH",StringComparison.OrdinalIgnoreCase)==true;
                var cycle=group && (depth>=32 || ancestry.Contains(Key(target!.Scope,target.Name)));
                var open=group && !cycle && expanded.Contains(viewKey);
                result.Add(new(item,scope,name,depth,viewKey,target?.Scope,target?.Name,group,cycle,open,occurrence.Origin,occurrence.Ghost,IsReviewMode));
                if(open)
                {
                    ancestry.Add(Key(target!.Scope,target.Name)); Add(target.Scope,target.Name,depth+1,viewKey); ancestry.Remove(Key(target.Scope,target.Name));
                }
            }
        }
        Add(Root.Scope,Root.Name,0,Key(Root.Scope,Root.Name)); return result;
    }
}
