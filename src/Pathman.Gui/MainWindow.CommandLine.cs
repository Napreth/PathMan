using Pathman.Core;
using Pathman.Core.Notifications;
using Pathman.Gui.Controls;
using Pathman.Gui.Notifications;

namespace Pathman.Gui;

public partial class MainWindow
{
    private async Task SynchronizeCommandLinePathAsync()
    {
        var error=await versions.SynchronizeCommandLinePathAsync();
        if(error is not null) notifications.Show(new("command-line",L.Get("启用命令行版本切换"),error+"\n"+Osc8Text.Link(L.Get("前往版本管理"),"apppage:versions"),NotificationColor.Red));
        else notifications.Dismiss("command-line");
        await variables.ReloadAsync();
    }
}
