using System.IO;
using System.Windows;
using System.Windows.Controls;
using Pathman.Core.EnvironmentVariables;
using Pathman.Gui.Dialogs;

namespace Pathman.Gui.Views;

public partial class SettingsView : UserControl
{
    private readonly EnvironmentWorkspaceStore store;
    private bool updating;
    public event EventHandler? PreferencesChanged;
    public SettingsView(EnvironmentWorkspaceStore store)
    {this.store=store;InitializeComponent();Loaded+=(_,_)=>Update(store.Read());}
    public void Update(EnvironmentWorkspace state)
    {
        updating=true;
        LanguageChoice.SelectedValue=state.Preferences.Language is "zh-CN" or "en-US" ? state.Preferences.Language : "system";
        ThemeChoice.SelectedValue=state.Preferences.Theme is "light" or "dark" ? state.Preferences.Theme : "system";
        AutoCheckUpdates.IsChecked=state.Preferences.AutoCheckUpdates;
        AllowBuiltInVersionTargets.IsChecked=state.AllowBuiltInVersionTargets;
        BuiltInWarning.Visibility=state.AllowBuiltInVersionTargets ? Visibility.Visible : Visibility.Collapsed;
        updating=false;
    }
    private void ChangePreferences(object sender,RoutedEventArgs e)
    {
        if(updating || LanguageChoice?.SelectedValue is not string language || ThemeChoice?.SelectedValue is not string theme || AutoCheckUpdates is null) return;
        try
        {
            store.Update(state=> {state.Preferences.Language=language;state.Preferences.Theme=theme;state.Preferences.AutoCheckUpdates=AutoCheckUpdates.IsChecked==true;});
            SettingsStatus.Text="";PreferencesChanged?.Invoke(this,EventArgs.Empty);
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) {SettingsStatus.Text=error.Message;Update(store.Read());}
    }
    private void OpenAdvanced(object sender,RoutedEventArgs e) {SettingsPage.Visibility=Visibility.Collapsed;AdvancedPage.Visibility=Visibility.Visible;Update(store.Read());}
    private void CloseAdvanced(object sender,RoutedEventArgs e) {AdvancedPage.Visibility=Visibility.Collapsed;SettingsPage.Visibility=Visibility.Visible;}
    private void ChangeBuiltInTargets(object sender,RoutedEventArgs e)
    {
        if(updating) return;var enabled=AllowBuiltInVersionTargets.IsChecked==true;
        if(enabled && new DangerConfirmDialog(Window.GetWindow(this),L.Get("允许选择内置系统变量"),L.Get("更改内置系统变量可能导致 Windows 或应用无法正常运行。确认了解风险后再启用。PATH 仍不可选择。"),L.Get("仍然启用")).ShowDialog()!=true) {Update(store.Read());return;}
        try {store.Update(state=>state.AllowBuiltInVersionTargets=enabled);AdvancedStatus.Text="";Update(store.Read());}
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) {AdvancedStatus.Text=error.Message;Update(store.Read());}
    }
}
