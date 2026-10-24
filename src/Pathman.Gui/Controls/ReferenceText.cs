using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Pathman.Gui.Controls;

public static class SemanticPalette
{
    public static SolidColorBrush Blue { get; }=Brush("#DDF4FF");
    public static SolidColorBrush Yellow { get; }=Brush("#FFF7DD");
    public static SolidColorBrush Red { get; }=Brush("#FFDDDD");
    public static SolidColorBrush Green { get; }=Brush("#DDFFE6");
    private static SolidColorBrush Brush(string color) {var brush=new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush;}
}

public sealed partial class ReferenceText : UserControl
{
    public static readonly DependencyProperty TextProperty=DependencyProperty.Register(nameof(Text),typeof(string),typeof(ReferenceText),new PropertyMetadata("",Changed));
    public static readonly DependencyProperty TextWrappingProperty=DependencyProperty.Register(nameof(TextWrapping),typeof(TextWrapping),typeof(ReferenceText),new PropertyMetadata(System.Windows.TextWrapping.NoWrap,Changed));
    public string Text {get=>(string)GetValue(TextProperty);set=>SetValue(TextProperty,value);}
    public TextWrapping TextWrapping {get=>(TextWrapping)GetValue(TextWrappingProperty);set=>SetValue(TextWrappingProperty,value);}
    private readonly TextBlock label=new() {TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center};
    [GeneratedRegex(@"%[^%\r\n]+%",RegexOptions.CultureInvariant)] internal static partial Regex References();
    public ReferenceText() {Focusable=false; ClipToBounds=true; Content=label; VerticalContentAlignment=VerticalAlignment.Center;}
    private static void Changed(DependencyObject sender,DependencyPropertyChangedEventArgs e)=>((ReferenceText)sender).RenderText();
    private void RenderText()
    {
        label.Inlines.Clear();
        label.TextWrapping=TextWrapping;
        System.Windows.Automation.AutomationProperties.SetName(label,Text);
        var position=0;
        foreach(Match match in References().Matches(Text))
        {
            if(match.Index>position) label.Inlines.Add(new Run(Text[position..match.Index]));
            label.Inlines.Add(new InlineUIContainer(new Border {
                Background=SemanticPalette.Yellow,CornerRadius=new(3),Padding=new(1,0,1,0),
                Child=new TextBlock {Text=match.Value,Foreground=Brushes.Black,VerticalAlignment=VerticalAlignment.Center}
            }) {BaselineAlignment=BaselineAlignment.Center});
            position=match.Index+match.Length;
        }
        if(position<Text.Length) label.Inlines.Add(new Run(Text[position..]));
    }
}

// Draw under the native TextBoxView so caret, selection, IME and undo remain
// native. Rectangles come from the actual shaped text, including wrapped lines.
public sealed class ReferenceTextBox : TextBox
{
    public ReferenceTextBox()
    {
        TextChanged+=(_,_)=>InvalidateVisual(); SizeChanged+=(_,_)=>InvalidateVisual();
        AddHandler(ScrollViewer.ScrollChangedEvent,new ScrollChangedEventHandler((_,_)=>InvalidateVisual()));
    }
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if(!IsVisible || ActualWidth<=0 || ActualHeight<=0) return;
        drawing.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        foreach(Match match in ReferenceText.References().Matches(Text))
        {
            Rect? line=null;
            for(var index=match.Index;index<match.Index+match.Length;index++)
            {
                var first=GetRectFromCharacterIndex(index); var last=GetRectFromCharacterIndex(index,true);
                if(first.IsEmpty || last.IsEmpty) continue;
                var rectangle=new Rect(first.TopLeft,new Point(Math.Max(first.Right,last.Right),first.Bottom));
                if(line is {} previous && Math.Abs(previous.Top-rectangle.Top)>.5) {Paint(previous); line=rectangle;}
                else {line=line is {} current ? Rect.Union(current,rectangle) : rectangle;}
            }
            if(line is {} final) Paint(final);
        }
        drawing.Pop();
        void Paint(Rect rectangle) {rectangle.Inflate(1,0); drawing.DrawRoundedRectangle(SemanticPalette.Yellow,null,rectangle,3,3);}
    }
}
