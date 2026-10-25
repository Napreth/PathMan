using System.Windows;
using System.Windows.Controls;

namespace Pathman.Gui.Controls;

public sealed class SettingsLinkCard : Button
{
    public static readonly DependencyProperty IconProperty=DependencyProperty.Register(nameof(Icon),typeof(string),typeof(SettingsLinkCard),new PropertyMetadata("\uE8A5"));
    public string Icon {get=>(string)GetValue(IconProperty);set=>SetValue(IconProperty,value);}
}
