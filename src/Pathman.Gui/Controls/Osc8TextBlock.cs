using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Pathman.Core.Notifications;

namespace Pathman.Gui.Controls;

public sealed class AppLinkEventArgs(string target) : RoutedEventArgs(Osc8TextBlock.LinkRequestedEvent)
{public string Target {get;}=target;}
public sealed class Osc8TextBlock : TextBlock
{
    public static readonly DependencyProperty BodyProperty=DependencyProperty.Register(nameof(Body),typeof(string),typeof(Osc8TextBlock),new PropertyMetadata("",Changed));
    public static readonly RoutedEvent LinkRequestedEvent=EventManager.RegisterRoutedEvent(nameof(LinkRequested),RoutingStrategy.Bubble,typeof(EventHandler<AppLinkEventArgs>),typeof(Osc8TextBlock));
    public string Body {get=>(string)GetValue(BodyProperty);set=>SetValue(BodyProperty,value);}
    public event EventHandler<AppLinkEventArgs> LinkRequested {add=>AddHandler(LinkRequestedEvent,value);remove=>RemoveHandler(LinkRequestedEvent,value);}
    public Osc8TextBlock() {TextWrapping=TextWrapping.Wrap;}
    private static void Changed(DependencyObject sender,DependencyPropertyChangedEventArgs e)
    {
        var control=(Osc8TextBlock)sender; control.Inlines.Clear();
        foreach(var span in Osc8Text.Parse(control.Body))
        {
            if(span.Link is not {} target) {control.Inlines.Add(new Run(span.Text)); continue;}
            var link=new Hyperlink(new Run(span.Text)) {ToolTip=target};
            link.Click+=(_,args)=> {args.Handled=true; control.RaiseEvent(new AppLinkEventArgs(target));};
            control.Inlines.Add(link);
        }
    }
}
