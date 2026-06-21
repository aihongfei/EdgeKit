using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace EdgeKit.App.Controls;

/// <summary>
/// 简易折行面板：水平排列子元素，宽度不足时自动换行，支持水平和垂直间距。
/// </summary>
public sealed class WrapPanel : Panel
{
    public static readonly DependencyProperty HorizontalSpacingProperty = DependencyProperty.Register(
        nameof(HorizontalSpacing), typeof(double), typeof(WrapPanel),
        new PropertyMetadata(0.0, OnSpacingChanged));

    public static readonly DependencyProperty VerticalSpacingProperty = DependencyProperty.Register(
        nameof(VerticalSpacing), typeof(double), typeof(WrapPanel),
        new PropertyMetadata(0.0, OnSpacingChanged));

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    private static void OnSpacingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WrapPanel panel)
        {
            panel.InvalidateMeasure();
            panel.InvalidateArrange();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var availableWidth = double.IsInfinity(availableSize.Width)
            ? double.MaxValue
            : Math.Max(0, availableSize.Width);
        var currentRowWidth = 0.0;
        var currentRowHeight = 0.0;
        var totalWidth = 0.0;
        var totalHeight = 0.0;
        var isRowEmpty = true;

        foreach (UIElement child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var childSize = child.DesiredSize;

            var widthWithSpacing = isRowEmpty ? childSize.Width : childSize.Width + HorizontalSpacing;

            if (!isRowEmpty && currentRowWidth + widthWithSpacing > availableWidth)
            {
                totalWidth = Math.Max(totalWidth, currentRowWidth);
                totalHeight += currentRowHeight + (totalHeight > 0 ? VerticalSpacing : 0);

                currentRowWidth = childSize.Width;
                currentRowHeight = childSize.Height;
                isRowEmpty = false;
            }
            else
            {
                currentRowWidth += widthWithSpacing;
                currentRowHeight = Math.Max(currentRowHeight, childSize.Height);
                isRowEmpty = false;
            }
        }

        totalWidth = Math.Max(totalWidth, currentRowWidth);
        totalHeight += currentRowHeight;

        return new Size(Math.Min(totalWidth, availableWidth), totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var finalWidth = Math.Max(0, finalSize.Width);
        var x = 0.0;
        var y = 0.0;
        var rowHeight = 0.0;
        var isRowEmpty = true;

        foreach (UIElement child in Children)
        {
            var childSize = child.DesiredSize;
            var widthWithSpacing = isRowEmpty ? childSize.Width : childSize.Width + HorizontalSpacing;

            if (!isRowEmpty && x + widthWithSpacing > finalWidth)
            {
                x = 0;
                y += rowHeight + VerticalSpacing;
                rowHeight = 0;
                isRowEmpty = true;
            }

            if (!isRowEmpty)
            {
                x += HorizontalSpacing;
            }

            child.Arrange(new Rect(x, y, childSize.Width, childSize.Height));
            x += childSize.Width;
            rowHeight = Math.Max(rowHeight, childSize.Height);
            isRowEmpty = false;
        }

        return finalSize;
    }
}