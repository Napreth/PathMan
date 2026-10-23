using System.Windows;
using System.Windows.Controls;
using Pathman.Core;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;
namespace Pathman.Gui.Views;
public partial class MonitorView : UserControl
{
    private readonly PathmanLayout layout;
    private readonly EnvironmentWorkspaceStore store;
    private bool updating;
    public MonitorView(PathmanLayout layout, EnvironmentWorkspaceStore store) { this.layout=layout; this.store=store; InitializeComponent(); Update(store.Read()); }
    public void Update(EnvironmentWorkspace state)
    {
        updating=true; StartupEnabled.IsChecked=new StartupManager(layout).IsEnabled; EnableMonitoring.IsChecked=state.MonitoringEnabled; updating=false;
        MonitorStatus.Text=state.MonitoringEnabled ? new BackgroundProcess(layout).IsRunning ? "监控中" : "监控已启用，后台当前未运行" : "监控未启用";
        Changes.ItemsSource=state.Changes.Select(change=>new { TimeText=change.Time.ToString("MM-dd HH:mm:ss"),ScopeText=change.Scope==VariableScope.User ? "用户" : "系统",change.Name,ActionText=change.Action switch { "create"=>"新增","delete"=>"删除","rename"=>"重命名",_=>"修改" },change.Source }).ToArray();
    }
    private void ChangeStartup(object sender,RoutedEventArgs e)
    {
        if(updating) return;
        try { new StartupManager(layout).SetEnabled(StartupEnabled.IsChecked==true); MonitorStatus.Text=""; }
        catch(Exception error) when(error is System.IO.IOException or UnauthorizedAccessException)
        { updating=true; StartupEnabled.IsChecked=!StartupEnabled.IsChecked; updating=false; MonitorStatus.Text=error.Message; }
    }
    private async void ChangeMonitoring(object sender,RoutedEventArgs e)
    {
        if(updating) return;
        var enabled=EnableMonitoring.IsChecked==true; EnableMonitoring.IsEnabled=false;
        try { await Task.Run(()=> { if(enabled) new BackgroundProcess(layout).Start(); new EnvironmentMonitor(store).SetEnabled(enabled); }); Update(store.Read()); }
        catch(Exception error) when(error is System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { updating=true; EnableMonitoring.IsChecked=!enabled; updating=false; MessageBox.Show(Window.GetWindow(this),error.Message,"无法更改监控设置"); }
        finally { EnableMonitoring.IsEnabled=true; }
    }
}
