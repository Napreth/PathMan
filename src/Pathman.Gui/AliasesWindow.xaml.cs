using System.Windows;
using Pathman.Core;
using Pathman.Core.Shims;

namespace Pathman.Gui;

public partial class AliasesWindow : Window
{
    private readonly PathmanLayout layout;

    public AliasesWindow(PathmanLayout layout)
    {
        this.layout = layout;
        InitializeComponent();
        BinPath.Text = $"CLI 目录：{layout.BinDirectory}";
        ShimsPath.Text = $"别名目录：{layout.ShimsDirectory}";
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
}
