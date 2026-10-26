using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace HunterPdf.Desktop;

/// <summary>A line icon drawn from a 24×24 outline path, stroked in the surrounding text color.</summary>
internal sealed class StrokeIcon : Viewbox
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(StrokeIcon), new PropertyMetadata(null, (d, e) => ((StrokeIcon)d).path.Data = (Geometry?)e.NewValue));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(StrokeIcon), new PropertyMetadata(16d, (d, e) => ((StrokeIcon)d).Width = ((StrokeIcon)d).Height = (double)e.NewValue));

    readonly Path path = new()
    {
        StrokeThickness = 1.75, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round
    };

    public StrokeIcon()
    {
        Width = Height = 16;
        IsHitTestVisible = false;
        VerticalAlignment = VerticalAlignment.Center;
        path.SetBinding(Shape.StrokeProperty, new Binding { Path = new PropertyPath(TextElement.ForegroundProperty), Source = this });
        Child = new Canvas { Width = 24, Height = 24, Children = { path } };
    }

    public Geometry? Data { get => (Geometry?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
}
