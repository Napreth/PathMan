using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Pathman.Gui.Dialogs;

public sealed class LicenseDialog : Window
{
    public LicenseDialog(Window owner)
    {
        Owner=owner;Title="MIT license";Width=740;Height=570;MinWidth=500;MinHeight=360;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        using var stream=typeof(App).Assembly.GetManifestResourceStream("PathMan.LICENSE")!;using var reader=new StreamReader(stream);
        var grid=new Grid {Margin=new Thickness(24)};grid.RowDefinitions.Add(new());grid.RowDefinitions.Add(new() {Height=GridLength.Auto});
        var text=new TextBox {Text=reader.ReadToEnd(),IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Padding=new Thickness(16)};
        System.Windows.Automation.AutomationProperties.SetAutomationId(text,"LicenseOriginalText");grid.Children.Add(text);
        var close=new Button {Content=L.Get("关闭"),IsCancel=true,MinWidth=88,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,16,0,0)};
        close.Click+=(_,_)=>Close();Grid.SetRow(close,1);grid.Children.Add(close);Content=grid;
    }
}
