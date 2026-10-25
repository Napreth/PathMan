using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Pathman.Gui;

public sealed class AliasRow(string name,string target,bool draft=false) : INotifyPropertyChanged
{
    private string name=name,target=target;private bool canEdit;
    public string Name {get=>name;set {name=value;Changed();}}
    public string Target {get=>target;set {target=value;Changed();}}
    public bool IsDraft {get;set;}=draft;
    public bool CanEdit {get=>canEdit;set {canEdit=value;Changed();}}
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName]string? name=null)=>PropertyChanged?.Invoke(this,new(name));
}
