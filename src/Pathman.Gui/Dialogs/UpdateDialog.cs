using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Pathman.Core.Updates;

namespace Pathman.Gui.Dialogs;

public sealed class UpdateDialog : Window
{
    private readonly CancellationTokenSource cancellation=new();
    public UpdateDialog(Window owner,AvailableRelease release,ApplicationUpdateService updates,Func<string,Task<bool>> install)
    {
        Owner=owner;Title=L.Get("更新");Width=760;Height=600;MinWidth=520;MinHeight=380;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var grid=new Grid {Margin=new(24)};
        grid.RowDefinitions.Add(new() {Height=GridLength.Auto});grid.RowDefinitions.Add(new());
        grid.RowDefinitions.Add(new() {Height=GridLength.Auto});grid.RowDefinitions.Add(new() {Height=GridLength.Auto});
        var title=new TextBlock {Text=release.Title,FontSize=24,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,20)};
        AutomationProperties.SetAutomationId(title,"ReleaseTitle");grid.Children.Add(title);
        var body=new TextBox {Text=release.Body,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Padding=new(16)};
        AutomationProperties.SetAutomationId(body,"ReleaseBody");Grid.SetRow(body,1);grid.Children.Add(body);
        var status=new TextBlock {TextWrapping=TextWrapping.Wrap,Margin=new(0,12,0,0)};
        AutomationProperties.SetAutomationId(status,"UpdateDownloadStatus");Grid.SetRow(status,2);grid.Children.Add(status);
        var buttons=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new(0,16,0,0)};
        var cancel=new Button {Content=L.Get("取消"),IsCancel=true,MinWidth=88,Margin=new(0,0,8,0)};
        var update=new Button {Content=L.Get("安装更新"),MinWidth=104};
        AutomationProperties.SetAutomationId(update,"InstallUpdateButton");
        update.Click+=async(_,_)=>
        {
            update.IsEnabled=false;status.Text=L.Get("正在下载……");
            try
            {
                var installer=await updates.DownloadAsync(release,cancellation.Token);
                if(cancellation.IsCancellationRequested) return;
                if(installer is null)
                {
                    if(release.Page.Scheme==Uri.UriSchemeHttps) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(release.Page.AbsoluteUri) {UseShellExecute=true});
                    DialogResult=true;
                }
                else if(await install(installer)) {if(IsVisible) DialogResult=true;}
                else status.Text="";
            }
            catch(OperationCanceledException) {if(IsVisible) status.Text="";}
            catch(Exception error) when(error is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {if(IsVisible) status.Text=error is InvalidDataException ? error.Message : L.Get("更新未完成，请稍后重试。");}
            finally {if(IsVisible) update.IsEnabled=true;}
        };
        buttons.Children.Add(cancel);buttons.Children.Add(update);Grid.SetRow(buttons,3);grid.Children.Add(buttons);Content=grid;
        Closed+=(_,_)=>cancellation.Cancel();
    }
}
