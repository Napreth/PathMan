using System.Windows;
namespace Pathman.Gui;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0] == "--apply-environment") { Shutdown(EnvironmentEditHost.Run(e.Args[1])); return; }
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => WindowFrame.Install((Window)sender)));
        try { var window = new MainWindow(); MainWindow=window; window.Show(); }
        catch(Exception error) { MessageBox.Show(error.Message,"无法启动 PathMan",MessageBoxButton.OK,MessageBoxImage.Error); Shutdown(1); }
    }
}
