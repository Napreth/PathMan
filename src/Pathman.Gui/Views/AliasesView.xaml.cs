using System.IO;
using System.Windows;
using System.Windows.Controls;
using Pathman.Core;
using Pathman.Core.Shims;
using Pathman.Core.EnvironmentVariables;
using Microsoft.Win32;
namespace Pathman.Gui.Views;
public partial class AliasesView : UserControl
{
    private readonly PathmanLayout layout;
    private readonly EnvironmentWorkspaceStore store;
    private bool updating;
    public AliasesView(PathmanLayout layout, EnvironmentWorkspaceStore store) { this.layout=layout; this.store=store; InitializeComponent(); BinPath.Text=$"CLI：{layout.BinDirectory}"; ShimsPath.Text=$"垫片：{layout.ShimsDirectory}"; Loaded+=(_,_)=>Refresh(this,new()); }
    private void Refresh(object sender,RoutedEventArgs e)
    {
        try { updating=true; EnableAliases.IsChecked=new AliasPathSetting(layout).IsEnabled(EnvironmentMonitor.ReadAll()); Aliases.ItemsSource=new ShimManager(layout).List(); }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException) { AliasStatus.Text=error.Message; }
        finally { updating=false; }
    }
    private async void ChangeEnabled(object sender,RoutedEventArgs e)
    {
        if(updating) return;
        EnableAliases.IsEnabled=false;
        var enabled=EnableAliases.IsChecked==true;
        try
        {
            var change=new AliasPathSetting(layout).Plan(enabled,EnvironmentMonitor.ReadAll());
            if(change is not null) await EnvironmentEditHost.ApplyAsync(change,store,layout.DataDirectory);
            using var key=Registry.CurrentUser.CreateSubKey(@"Software\PathMan",true);
            if(enabled) key.SetValue("ShimsPathOwned",1,RegistryValueKind.DWord); else key.DeleteValue("ShimsPathOwned",false);
            AliasStatus.Text=enabled ? "垫片目录已放到用户 Path 末尾，新终端生效。" : "已从用户 Path 移除垫片目录，新终端生效。";
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or InvalidOperationException)
        { updating=true; EnableAliases.IsChecked=!enabled; updating=false; AliasStatus.Text=error.Message; }
        finally { EnableAliases.IsEnabled=true; }
    }
}
