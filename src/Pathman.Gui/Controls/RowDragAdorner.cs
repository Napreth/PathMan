using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Pathman.Gui.Controls;

internal sealed class RowDragAdorner : Adorner
{
    private readonly Border ghost;
    private readonly VisualCollection visuals;
    private readonly TranslateTransform translation = new();
    private Rect? target;
    private double? insertionY;
    private double insertionLeft, insertionWidth;
    public RowDragAdorner(UIElement owner, FrameworkElement row) : base(owner)
    {
        IsHitTestVisible = false;
        var dpi = VisualTreeHelper.GetDpi(row);
        var snapshot = new RenderTargetBitmap(Math.Max(1,(int)Math.Ceiling(row.ActualWidth*dpi.DpiScaleX)),
            Math.Max(1,(int)Math.Ceiling(row.ActualHeight*dpi.DpiScaleY)),96*dpi.DpiScaleX,96*dpi.DpiScaleY,PixelFormats.Pbgra32);
        snapshot.Render(row); snapshot.Freeze();
        ghost = new Border
        {
            Width=row.ActualWidth, Height=row.ActualHeight, Background=(Brush)row.FindResource("CardBackgroundFillColorDefaultBrush"),
            CornerRadius=new(3), Child=new Image { Source=snapshot, Stretch=Stretch.Fill },
            RenderTransform=translation, Opacity=0,
            Effect=new DropShadowEffect { BlurRadius=12, ShadowDepth=3, Opacity=.24 }
        };
        visuals=new VisualCollection(this) {ghost};
        ghost.BeginAnimation(OpacityProperty,new DoubleAnimation(.94,TimeSpan.FromMilliseconds(100)));
    }
    public void Move(Point position, bool animate)
    {
        var duration=TimeSpan.FromMilliseconds(animate ? 45 : 0);
        translation.BeginAnimation(TranslateTransform.XProperty,new DoubleAnimation(position.X,duration));
        translation.BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(position.Y,duration));
    }
    public void ShowTarget(Rect? rectangle, double? line, double left, double width)
    { target=rectangle; insertionY=line; insertionLeft=left; insertionWidth=width; InvalidateVisual(); }
    protected override int VisualChildrenCount => visuals?.Count ?? 0;
    protected override Visual GetVisualChild(int index) => visuals[index];
    protected override Size MeasureOverride(Size constraint) { ghost.Measure(new Size(ghost.Width,ghost.Height)); return AdornedElement.RenderSize; }
    protected override Size ArrangeOverride(Size finalSize) { ghost.Arrange(new Rect(ghost.DesiredSize)); return finalSize; }
    protected override void OnRender(DrawingContext drawing)
    {
        if(target is {} bounds) drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(45,0,120,212)),new Pen(Brushes.DodgerBlue,1.5),bounds,3,3);
        if(insertionY is {} y)
        {
            var pixel=1/VisualTreeHelper.GetDpi(this).DpiScaleY;
            var lineY=(Math.Round(y/pixel)+.5)*pixel;
            drawing.DrawLine(new Pen(Brushes.Black,pixel),new(insertionLeft,lineY),new(insertionLeft+insertionWidth,lineY));
        }
    }
}
