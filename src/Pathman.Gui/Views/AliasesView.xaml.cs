using System.IO;
using System.Windows;
using System.Windows.Controls;
using Pathman.Core;
using Pathman.Core.Shims;
namespace Pathman.Gui.Views;
public partial class AliasesView : UserControl
{
    private readonly PathmanLayout layout;
    public AliasesView(PathmanLayout layout) { this.layout=layout; InitializeComponent(); BinPath.Text=$"CLI：{layout.BinDirectory}"; ShimsPath.Text=$"垫片：{layout.ShimsDirectory}"; Refresh(this,new()); }
    private void Refresh(object sender,RoutedEventArgs e)
    { try { Aliases.ItemsSource=new ShimManager(layout).List(); } catch(Exception error) when(error is IOException or UnauthorizedAccessException) { MessageBox.Show(error.Message,"无法读取程序别名"); } }
}
