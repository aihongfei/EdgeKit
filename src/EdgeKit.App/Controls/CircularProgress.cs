using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;

namespace EdgeKit.App.Controls;

public sealed class CircularProgress : Grid
{
    private readonly ShapePath _bgPath;
    private readonly ShapePath _fillPath;
    private readonly TextBlock _centerText;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(CircularProgress),
        new PropertyMetadata(0.0, OnVisualPropertyChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(CircularProgress),
        new PropertyMetadata(100.0, OnVisualPropertyChanged));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(CircularProgress),
        new PropertyMetadata(0.0, OnVisualPropertyChanged));

    public static readonly DependencyProperty RingThicknessProperty = DependencyProperty.Register(
        nameof(RingThickness), typeof(double), typeof(CircularProgress),
        new PropertyMetadata(4.0, OnVisualPropertyChanged));

    public static readonly DependencyProperty RingBrushProperty = DependencyProperty.Register(
        nameof(RingBrush), typeof(Brush), typeof(CircularProgress),
        new PropertyMetadata(null, OnVisualPropertyChanged));

    public static readonly DependencyProperty UnfilledRingBrushProperty = DependencyProperty.Register(
        nameof(UnfilledRingBrush), typeof(Brush), typeof(CircularProgress),
        new PropertyMetadata(null, OnVisualPropertyChanged));

    public static readonly DependencyProperty CenterTextProperty = DependencyProperty.Register(
        nameof(CenterText), typeof(string), typeof(CircularProgress),
        new PropertyMetadata(string.Empty, OnVisualPropertyChanged));

    public static readonly DependencyProperty CenterTextSizeProperty = DependencyProperty.Register(
        nameof(CenterTextSize), typeof(double), typeof(CircularProgress),
        new PropertyMetadata(10.0, OnVisualPropertyChanged));

    public CircularProgress()
    {
        _bgPath = new ShapePath
        {
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Fill = null
        };
        _fillPath = new ShapePath
        {
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Fill = null
        };
        _centerText = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };

        Children.Add(_bgPath);
        Children.Add(_fillPath);
        Children.Add(_centerText);

        SizeChanged += (_, _) => Render();
        Loaded += (_, _) => Render();
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double RingThickness
    {
        get => (double)GetValue(RingThicknessProperty);
        set => SetValue(RingThicknessProperty, value);
    }

    public Brush? RingBrush
    {
        get => (Brush?)GetValue(RingBrushProperty);
        set => SetValue(RingBrushProperty, value);
    }

    public Brush? UnfilledRingBrush
    {
        get => (Brush?)GetValue(UnfilledRingBrushProperty);
        set => SetValue(UnfilledRingBrushProperty, value);
    }

    public string CenterText
    {
        get => (string)GetValue(CenterTextProperty);
        set => SetValue(CenterTextProperty, value);
    }

    public double CenterTextSize
    {
        get => (double)GetValue(CenterTextSizeProperty);
        set => SetValue(CenterTextSizeProperty, value);
    }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CircularProgress cp)
        {
            cp.Render();
        }
    }

    private void Render()
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0 || !IsLoaded)
        {
            return;
        }

        var thickness = RingThickness;
        var radius = (size - thickness) / 2.0;
        var center = size / 2.0;

        _bgPath.Stroke = UnfilledRingBrush ?? new SolidColorBrush(global::Windows.UI.Color.FromArgb(64, 128, 128, 128));
        _bgPath.StrokeThickness = thickness;
        _bgPath.Data = BuildRingGeometry(center, center, radius, 0, 360);

        var range = Maximum - Minimum;
        var ratio = range > 0 ? Math.Clamp((Value - Minimum) / range, 0, 1) : 0;
        var angle = ratio * 360;

        _fillPath.Stroke = RingBrush ?? new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 59, 130, 246));
        _fillPath.StrokeThickness = thickness;
        _fillPath.Data = BuildRingGeometry(center, center, radius, -90, angle);

        _centerText.Text = CenterText;
        _centerText.FontSize = CenterTextSize;
        _centerText.Foreground = (Brush)Application.Current.Resources["EdgeTextBrush"];
    }

    private static Geometry BuildRingGeometry(double cx, double cy, double r, double startAngle, double sweepAngle)
    {
        if (sweepAngle <= 0)
        {
            return new PathGeometry();
        }

        if (sweepAngle >= 360)
        {
            sweepAngle = 359.99;
        }

        var startRad = startAngle * Math.PI / 180.0;
        var startX = cx + r * Math.Cos(startRad);
        var startY = cy + r * Math.Sin(startRad);

        var endAngle = startAngle + sweepAngle;
        var endRad = endAngle * Math.PI / 180.0;
        var endX = cx + r * Math.Cos(endRad);
        var endY = cy + r * Math.Sin(endRad);

        var largeArc = sweepAngle > 180 ? 1 : 0;

        var figure = new PathFigure
        {
            StartPoint = new Point(startX, startY),
            IsClosed = false
        };
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(endX, endY),
            Size = new Size(r, r),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = largeArc == 1
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }
}