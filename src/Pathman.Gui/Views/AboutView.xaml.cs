using System.Windows;
using System.Windows.Controls;
using Pathman.Core;
namespace Pathman.Gui.Views;
public partial class AboutView : UserControl
{
    private readonly PathmanLayout layout;
    private bool updating=true;
    public AboutView(PathmanLayout layout) { this.layout=layout; InitializeComponent(); StartupEnabled.IsChecked=new StartupManager(layout).IsEnabled; VersionText.Text=$"{typeof(App).Assembly.GetName().Version?.ToString(3)} · pre-alpha"; ConfigPath.Text=$"配置：{System.IO.Path.Combine(layout.DataDirectory,"environment.json")}"; updating=false; }
    private void ChangeStartup(object sender,RoutedEventArgs e)
    {
        if(updating) return;
        try { new StartupManager(layout).SetEnabled(StartupEnabled.IsChecked==true); }
        catch(Exception error) when(error is System.IO.IOException or UnauthorizedAccessException) { updating=true; StartupEnabled.IsChecked=!StartupEnabled.IsChecked; updating=false; MessageBox.Show(error.Message,"无法更改登录自启"); }
    }
}
