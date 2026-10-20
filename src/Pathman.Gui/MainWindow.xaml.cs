using System.Windows;
using Pathman.Core;
using Pathman.Core.Shims;

namespace Pathman.Gui;

public partial class MainWindow : Window
{
    private readonly PathmanLayout layout = PathmanLayout.Discover();

    public MainWindow()
    {
        InitializeComponent();
        BinPath.Text = $"程序工具目录：{layout.BinDirectory}";
        ShimsPath.Text = $"用户别名目录：{layout.ShimsDirectory}";
        StartupEnabled.IsChecked = new StartupManager(layout).IsEnabled;
        RefreshAliases(this, new RoutedEventArgs());
    }

    private void RefreshAliases(object sender, RoutedEventArgs e)
    {
        try { Aliases.ItemsSource = new ShimManager(layout).List(); }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, exception.Message, "无法读取程序别名", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ChangeStartup(object sender, RoutedEventArgs e)
    {
        var startup = new StartupManager(layout);
        try { startup.SetEnabled(StartupEnabled.IsChecked == true); }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            StartupEnabled.IsChecked = startup.IsEnabled;
            MessageBox.Show(this, exception.Message, "无法更改登录自启", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
