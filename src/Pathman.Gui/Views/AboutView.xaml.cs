using System.IO;
using System.Windows;
using System.Windows.Controls;
using Pathman.Core;
using Pathman.Core.EnvironmentVariables;
using Pathman.Gui.Dialogs;
namespace Pathman.Gui.Views;
public partial class AboutView : UserControl
{
    private readonly EnvironmentWorkspaceStore store;
    private bool updating;
    public AboutView(PathmanLayout layout,EnvironmentWorkspaceStore store)
    {this.store=store;InitializeComponent();VersionText.Text=$"{typeof(App).Assembly.GetName().Version?.ToString(3)} · pre-alpha";ConfigPath.Text=$"配置：{System.IO.Path.Combine(layout.DataDirectory,"environment.json")}";Loaded+=(_,_)=>Update(store.Read());}
    public void Update(EnvironmentWorkspace state)
    {updating=true;AllowBuiltInVersionTargets.IsChecked=state.AllowBuiltInVersionTargets;BuiltInWarning.Visibility=state.AllowBuiltInVersionTargets ? Visibility.Visible : Visibility.Collapsed;updating=false;}
    private void OpenAdvanced(object sender,RoutedEventArgs e) {AboutPage.Visibility=Visibility.Collapsed;AdvancedPage.Visibility=Visibility.Visible;Update(store.Read());}
    private void CloseAdvanced(object sender,RoutedEventArgs e) {AdvancedPage.Visibility=Visibility.Collapsed;AboutPage.Visibility=Visibility.Visible;}
    private void ChangeBuiltInTargets(object sender,RoutedEventArgs e)
    {
        if(updating) return;var enabled=AllowBuiltInVersionTargets.IsChecked==true;
        if(enabled && new DangerConfirmDialog(Window.GetWindow(this),"允许选择内置系统变量","更改内置系统变量可能导致 Windows 或应用无法正常运行。确认了解风险后再启用。PATH 仍不可选择。","仍然启用").ShowDialog()!=true) {Update(store.Read());return;}
        try {store.Update(state=>state.AllowBuiltInVersionTargets=enabled);AdvancedStatus.Text="";Update(store.Read());}
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) {AdvancedStatus.Text=error.Message;Update(store.Read());}
    }
}
