namespace Pathman.Gui;

// Each occurrence has its own identity, including duplicate and empty values.
public sealed record EnvironmentReferenceLink(string Name, Pathman.Core.Models.VariableScope? Scope);
public sealed class EnvironmentEntry(string value, bool isDraft = false, IReadOnlyList<EnvironmentReferenceLink>? references = null,
    Pathman.Core.EnvironmentVariables.PathEditorRow? row = null, bool placeholder = false) : System.ComponentModel.INotifyPropertyChanged
{
    public string Value { get; private set; } = value;
    public bool IsDraft { get; private set; } = isDraft;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public void Update(Pathman.Core.EnvironmentVariables.PathEditorRow row,IReadOnlyList<EnvironmentReferenceLink> references,bool placeholder)
    {Row=row;Value=row.Item.Value;IsDraft=row.Item.IsDraft;References=references;IsDragPlaceholder=placeholder;PropertyChanged?.Invoke(this,new(null));}
    public bool IsGhost=>Row?.IsGhost==true;
    public bool CanEdit=>!IsGhost;
    public bool IsReview=>Row?.IsReview==true;
    public string DiffPrefix=>Row?.Origin switch {Pathman.Core.EnvironmentVariables.EnvironmentEditOrigin.ExternalAdded or Pathman.Core.EnvironmentVariables.EnvironmentEditOrigin.UserAdded=>"+",Pathman.Core.EnvironmentVariables.EnvironmentEditOrigin.ExternalRemoved or Pathman.Core.EnvironmentVariables.EnvironmentEditOrigin.UserRemoved=>"-",_=>""};
    public bool IsExternalAdded=>Row?.Origin==Pathman.Core.EnvironmentVariables.EnvironmentEditOrigin.ExternalAdded;
    public bool IsExternalRemoved=>Row?.Origin==Pathman.Core.EnvironmentVariables.EnvironmentEditOrigin.ExternalRemoved;
    public bool IsUserChange=>Row?.Origin is Pathman.Core.EnvironmentVariables.EnvironmentEditOrigin.UserAdded or Pathman.Core.EnvironmentVariables.EnvironmentEditOrigin.UserRemoved;
    public IReadOnlyList<EnvironmentReferenceLink> References { get; private set; } = references ?? [];
    public bool HasReferences => References.Count > 0;
    public Pathman.Core.EnvironmentVariables.PathEditorRow? Row { get; private set; } = row;
    public Guid ItemId => Row?.Item.Id ?? Guid.Empty;
    public string ViewKey => Row?.ViewKey ?? "";
    public bool IsGroup => Row?.IsGroup == true;
    public bool IsExpanded => Row?.IsExpanded == true;
    public bool CanExpand => IsGroup && Row?.IsCycle != true;
    public string ExpandActionName => (IsExpanded ? L.Get("折叠 ") : L.Get("展开 "))+Value;
    public bool IsDragPlaceholder {get;private set;}=placeholder;
    public System.Windows.Thickness Indent => new((Row?.Depth ?? 0)*16,0,0,0);
    public string GroupToolTip => Row?.IsCycle==true ? L.Get("引用回环或层级过深，已停止展开") : L.Get("展开或折叠引用的路径变量");
    public string ReferenceToolTip => L.Get("转到：") + string.Join("、", References.Select(item => item.Name));
    public string AutomationName => DiffPrefix+(Value.Length == 0 ? L.Get("（空条目）") : Value);
}
