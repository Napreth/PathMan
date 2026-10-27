using System.Windows;
using System.Windows.Controls;

namespace Pathman.Gui.Controls;

public sealed class SettingsLinkCard : Button
{
    public static readonly DependencyProperty DetailProperty=DependencyProperty.Register(nameof(Detail),typeof(string),typeof(SettingsLinkCard),new PropertyMetadata(""));
    public string Detail {get=>(string)GetValue(DetailProperty);set=>SetValue(DetailProperty,value);}
    public static readonly DependencyProperty IconProperty=DependencyProperty.Register(nameof(Icon),typeof(string),typeof(SettingsLinkCard),new PropertyMetadata("\uE8A5"));
    public string Icon {get=>(string)GetValue(IconProperty);set=>SetValue(IconProperty,value);}
}
