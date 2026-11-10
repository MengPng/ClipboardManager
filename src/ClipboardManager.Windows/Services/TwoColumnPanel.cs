using System.Windows;
using Panel = System.Windows.Controls.Panel;
using Size = System.Windows.Size;

namespace ClipboardManager.Windows.Services;

/// <summary>
/// 两列卡片：每行两个，等宽；每行高度取这一行里较高的那张（同一行的卡片等高），
/// 不像 UniformGrid 那样让所有行都和最高的一行一样高。
/// </summary>
public sealed class TwoColumnPanel : Panel
{
    public static readonly DependencyProperty ColumnGapProperty = DependencyProperty.Register(
        nameof(ColumnGap), typeof(double), typeof(TwoColumnPanel),
        new FrameworkPropertyMetadata(8d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty RowGapProperty = DependencyProperty.Register(
        nameof(RowGap), typeof(double), typeof(TwoColumnPanel),
        new FrameworkPropertyMetadata(8d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ColumnGap
    {
        get => (double)GetValue(ColumnGapProperty);
        set => SetValue(ColumnGapProperty, value);
    }

    public double RowGap
    {
        get => (double)GetValue(RowGapProperty);
        set => SetValue(RowGapProperty, value);
    }

    private double ColumnWidth(double width) => Math.Max(0, (width - ColumnGap) / 2);

    protected override Size MeasureOverride(Size availableSize)
    {
        var column = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : ColumnWidth(availableSize.Width);
        double height = 0, widest = 0;
        for (var i = 0; i < InternalChildren.Count; i += 2)
        {
            var rowHeight = 0d;
            for (var j = i; j < Math.Min(i + 2, InternalChildren.Count); j++)
            {
                var child = InternalChildren[j];
                child.Measure(new Size(column, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
                widest = Math.Max(widest, child.DesiredSize.Width);
            }
            height += rowHeight + (i == 0 ? 0 : RowGap);
        }
        var width = double.IsInfinity(availableSize.Width) ? widest * 2 + ColumnGap : availableSize.Width;
        return new Size(InternalChildren.Count == 0 ? 0 : width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var column = ColumnWidth(finalSize.Width);
        var y = 0d;
        for (var i = 0; i < InternalChildren.Count; i += 2)
        {
            var last = Math.Min(i + 2, InternalChildren.Count);
            var rowHeight = 0d;
            for (var j = i; j < last; j++) rowHeight = Math.Max(rowHeight, InternalChildren[j].DesiredSize.Height);
            for (var j = i; j < last; j++)
                InternalChildren[j].Arrange(new Rect((j - i) * (column + ColumnGap), y, column, rowHeight));
            y += rowHeight + RowGap;
        }
        return finalSize;
    }
}
