using System.Windows;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Localization;

namespace Pathman.Gui;

internal static class Appearance
{
    public static string Theme {get;private set;}="system";
    public static bool Apply(ApplicationPreferences preferences)
    {
        var changed=Text.SetLanguage(preferences.Language);Theme=preferences.Theme;
#pragma warning disable WPF0001
        Application.Current.ThemeMode=Theme switch {"dark"=>ThemeMode.Dark,"light"=>ThemeMode.Light,_=>ThemeMode.System};
#pragma warning restore WPF0001
        foreach(Window window in Application.Current.Windows)
        {window.Language=System.Windows.Markup.XmlLanguage.GetLanguage(Text.Current.CultureName);WindowFrame.Refresh(window);}
        return changed;
    }
}
