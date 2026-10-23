using System.Windows;
using System.Windows.Controls;
using Pathman.Core;
namespace Pathman.Gui.Views;
public partial class AboutView : UserControl
{
    public AboutView(PathmanLayout layout) { InitializeComponent(); VersionText.Text=$"{typeof(App).Assembly.GetName().Version?.ToString(3)} · pre-alpha"; ConfigPath.Text=$"配置：{System.IO.Path.Combine(layout.DataDirectory,"environment.json")}"; }
}
