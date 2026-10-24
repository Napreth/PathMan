using System.Collections.ObjectModel;
using System.Windows.Media;
using Pathman.Gui.Controls;

namespace Pathman.Gui.Notifications;

public enum NotificationColor { Red, Yellow, Blue, Green }
public sealed record AppNotification(string Id,string Title,string Body,NotificationColor Color)
{
    public Brush Background=>Color switch {NotificationColor.Red=>SemanticPalette.Red,NotificationColor.Yellow=>SemanticPalette.Yellow,NotificationColor.Green=>SemanticPalette.Green,_=>SemanticPalette.Blue};
}
public sealed class NotificationCenter
{
    public ObservableCollection<AppNotification> Items {get;}=[];
    public void Show(AppNotification notification)
    {
        var previous=Items.FirstOrDefault(item=>item.Id==notification.Id);
        if(previous is not null) {if(previous==notification) return; Items[Items.IndexOf(previous)]=notification;}
        else Items.Insert(0,notification);
    }
    public void Dismiss(string id) {if(Items.FirstOrDefault(item=>item.Id==id) is {} item) Items.Remove(item);}
}
