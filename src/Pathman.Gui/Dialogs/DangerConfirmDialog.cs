using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Pathman.Gui.Controls;

namespace Pathman.Gui.Dialogs;

public sealed class DangerConfirmDialog : Window
{
    public DangerConfirmDialog(Window owner,string title,string message,string confirm)
    {
        Owner=owner;Title=title;Width=500;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new StackPanel {Margin=new(24)};
        panel.Children.Add(new Border {Background=SemanticPalette.Red,CornerRadius=new(6),Padding=new(16),Child=new TextBlock {Text=message,TextWrapping=TextWrapping.Wrap,Foreground=new SolidColorBrush(Color.FromRgb(122,0,0))}});
        var buttons=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new(0,20,0,0)};
        var accept=new Button {Content=confirm,MinWidth=88,Margin=new(0,0,8,0)};AutomationProperties.SetAutomationId(accept,"ConfirmDangerButton");accept.Click+=(_,_)=>DialogResult=true;
        buttons.Children.Add(accept);buttons.Children.Add(new Button {Content="取消",IsCancel=true,MinWidth=88});panel.Children.Add(buttons);Content=panel;
    }
}
