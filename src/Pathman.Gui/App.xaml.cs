using System.Windows;
using Pathman.Core.EnvironmentVariables;
using Pathman.Gui.Notifications;
namespace Pathman.Gui;
public partial class App : Application
{
    private UpdateActivation? updateActivation;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if(e.Args.Length>0 && e.Args[0]=="--environment-session") {Shutdown(Task.Run(()=>ElevatedEnvironmentSession.RunWorkerAsync(e.Args)).GetAwaiter().GetResult());return;}
        var layout=AppInfo.Layout();var updateRequested=UpdateActivation.IsRequested(e.Args);
        if(updateRequested && UpdateActivation.TryForward(layout)) {Shutdown();return;}
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => WindowFrame.Install((Window)sender)));
        try
        {
            Appearance.Apply(new EnvironmentWorkspaceStore(layout.DataDirectory).Read().Preferences);var window=new MainWindow();MainWindow=window;
            updateActivation=new(layout,()=> {if(!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(async()=>await window.OpenUpdateDetailsAsync());});
            window.Show();if(updateRequested) Dispatcher.BeginInvoke(async()=>await window.OpenUpdateDetailsAsync());
        }
        catch(Exception error) { MessageBox.Show(error.Message,L.Get("无法启动 PathMan"),MessageBoxButton.OK,MessageBoxImage.Error); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e) {updateActivation?.Dispose();EnvironmentEditHost.Shutdown();base.OnExit(e);}
}
