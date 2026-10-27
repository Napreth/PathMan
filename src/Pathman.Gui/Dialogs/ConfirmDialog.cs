using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
namespace Pathman.Gui.Dialogs;
public sealed class ConfirmDialog : Window
{
    public ConfirmDialog(Window owner,string title,string message,string confirm)
    {
        Owner=owner; Title=title; Width=440; SizeToContent=SizeToContent.Height; MinHeight=180; ResizeMode=ResizeMode.NoResize; WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new StackPanel { Margin=new Thickness(24) };
        panel.Children.Add(new TextBlock { Text=message,TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,24),MaxWidth=390 });
        var buttons=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right };
        var accept=new Button { Content=confirm,MinWidth=88,Margin=new(0,0,8,0) }; AutomationProperties.SetAutomationId(accept,"ConfirmButton"); accept.Click+=(_,_)=>DialogResult=true;
        buttons.Children.Add(accept); buttons.Children.Add(new Button {Content=L.Get("取消"),IsCancel=true,MinWidth=88}); panel.Children.Add(buttons); Content=panel;
    }
}
