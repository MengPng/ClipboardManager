using System.Windows;
using Panel = System.Windows.Controls.Panel;
using Size = System.Windows.Size;

namespace ClipboardManager.Windows.Services;

/// <summary>
/// 名字 + 紧跟其后的标签排成一行：标签按自身宽度显示，名字只拿剩下的宽度（过长由名字自己的省略号截断），
/// 名字短时标签贴着名字，而不是被推到行尾。第一个子元素是名字，其余依次排在后面。
/// </summary>
public sealed class NameTagPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        double tagsWidth = 0, height = 0;
        for (var i = 1; i < InternalChildren.Count; i++)
        {
            var tag = InternalChildren[i];
            tag.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            tagsWidth += tag.DesiredSize.Width;
            height = Math.Max(height, tag.DesiredSize.Height);
        }
        if (InternalChildren.Count == 0) return default;
        var name = InternalChildren[0];
        var nameRoom = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : Math.Max(0, availableSize.Width - tagsWidth);
        name.Measure(new Size(nameRoom, availableSize.Height));
        height = Math.Max(height, name.DesiredSize.Height);
        return new Size(name.DesiredSize.Width + tagsWidth, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count == 0) return finalSize;
        double tagsWidth = 0;
        for (var i = 1; i < InternalChildren.Count; i++) tagsWidth += InternalChildren[i].DesiredSize.Width;
        var name = InternalChildren[0];
        var nameWidth = Math.Min(name.DesiredSize.Width, Math.Max(0, finalSize.Width - tagsWidth));
        name.Arrange(new Rect(0, 0, nameWidth, finalSize.Height));
        var x = nameWidth;
        for (var i = 1; i < InternalChildren.Count; i++)
        {
            var tag = InternalChildren[i];
            tag.Arrange(new Rect(x, 0, tag.DesiredSize.Width, finalSize.Height));
            x += tag.DesiredSize.Width;
        }
        return finalSize;
    }
}
