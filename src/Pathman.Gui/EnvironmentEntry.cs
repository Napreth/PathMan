namespace Pathman.Gui;

// Each occurrence has its own identity, including duplicate and empty values.
public sealed record EnvironmentReferenceLink(string Name, Pathman.Core.Models.VariableScope? Scope);
public sealed class EnvironmentEntry(string value, bool isDraft = false, IReadOnlyList<EnvironmentReferenceLink>? references = null,
    Pathman.Core.EnvironmentVariables.PathEditorRow? row = null, bool placeholder = false)
{
    public string Value { get; } = value;
    public bool IsDraft { get; } = isDraft;
    public bool IsGhost=>Row?.IsGhost==true;
    public bool CanEdit=>!IsGhost;
    public bool IsReview=>Row?.IsReview==true;
    public string DiffPrefix=>Row?.Origin switch {Pathman.Core.EnvironmentVariables.EnvironmentEditOrigin.ExternalAdded or Pathman.Core.EnvironmentVariables.EnvironmentEditOrigin.UserAdded=>"+",Pathman.Core.EnvironmentVariables.EnvironmentEditOrigin.ExternalRemoved or Pathman.Core.EnvironmentVariables.EnvironmentEditOrigin.UserRemoved=>"-",_=>""};
    public bool IsExternalAdded=>Row?.Origin==Pathman.Core.EnvironmentVariables.EnvironmentEditOrigin.ExternalAdded;
    public bool IsExternalRemoved=>Row?.Origin==Pathman.Core.EnvironmentVariables.EnvironmentEditOrigin.ExternalRemoved;
    public bool IsUserChange=>Row?.Origin is Pathman.Core.EnvironmentVariables.EnvironmentEditOrigin.UserAdded or Pathman.Core.EnvironmentVariables.EnvironmentEditOrigin.UserRemoved;
    public IReadOnlyList<EnvironmentReferenceLink> References { get; } = references ?? [];
    public bool HasReferences => References.Count > 0;
    public Pathman.Core.EnvironmentVariables.PathEditorRow? Row { get; } = row;
    public Guid ItemId => Row?.Item.Id ?? Guid.Empty;
    public string ViewKey => Row?.ViewKey ?? "";
    public bool IsGroup => Row?.IsGroup == true;
    public bool IsExpanded => Row?.IsExpanded == true;
    public bool CanExpand => IsGroup && Row?.IsCycle != true;
    public string ExpandActionName => (IsExpanded ? "折叠 " : "展开 ")+Value;
    public bool IsDragPlaceholder {get;}=placeholder;
    public System.Windows.Thickness Indent => new((Row?.Depth ?? 0)*16,0,0,0);
    public string GroupToolTip => Row?.IsCycle==true ? "引用回环或层级过深，已停止展开" : "展开或折叠引用的路径变量";
    public string ReferenceToolTip => "转到：" + string.Join("、", References.Select(item => item.Name));
    public string AutomationName => DiffPrefix+(Value.Length == 0 ? "（空条目）" : Value);
}
