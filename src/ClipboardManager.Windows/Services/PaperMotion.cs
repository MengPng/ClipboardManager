using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClipboardManager.Windows.Services;

/// <summary>
/// 夹夹主题（活页版）的纸感动效：卡片行悬停上浮、按下下沉、选中回弹与夹子弹入，页签荧光笔划过，新记录落下。
/// </summary>
/// <remarks>
/// Storyboard 里不能用 DynamicResource，所以动效不写在模板里，而由这里在事件发生时读当前主题键再启动：
/// <c>RowHoverLift</c> / <c>RowPressSink</c>（悬停、按下）、<c>SelectionPop</c>（选中）、<c>NavSelectWipe</c>（页签）、
/// <c>RowDropDistance</c>（新记录）。Palette.xaml 中这些键都是「无效果」值，命中时直接返回，
/// 不挂变换、不创建任何动画，标准 / Kami / Kubai 的像素与行为不变。
/// 只动 RenderTransform 与 Opacity，不碰布局属性，列表虚拟化不受影响；系统关闭客户区动画时直接到终态。
/// </remarks>
public static class PaperMotion
{
    private static readonly Duration HoverDuration = TimeSpan.FromMilliseconds(120);
    private static readonly Duration PressDuration = TimeSpan.FromMilliseconds(90);
    private static readonly Duration SettleDuration = TimeSpan.FromMilliseconds(300);
    private static readonly Duration PopDuration = TimeSpan.FromMilliseconds(180);
    private static readonly Duration WipeDuration = TimeSpan.FromMilliseconds(180);
    private static readonly Duration DropDuration = TimeSpan.FromMilliseconds(340);

    // 设计稿的越界贝塞尔在 WPF 里用 BackEase 近似：settle ≈ Amplitude 0.3，pop ≈ 0.45。
    private static readonly IEasingFunction EaseOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction Settle = Frozen(new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction Pop = Frozen(new BackEase { Amplitude = 0.45, EasingMode = EasingMode.EaseOut });

    public static readonly DependencyProperty CardRowProperty = DependencyProperty.RegisterAttached(
        "CardRow", typeof(bool), typeof(PaperMotion), new PropertyMetadata(false, OnCardRowChanged));

    public static readonly DependencyProperty NavTabProperty = DependencyProperty.RegisterAttached(
        "NavTab", typeof(bool), typeof(PaperMotion), new PropertyMetadata(false, OnNavTabChanged));

    /// <summary>按下状态只在本类内部使用，不进入模板触发器。</summary>
    private static readonly DependencyProperty IsPressedProperty = DependencyProperty.RegisterAttached(
        "IsPressed", typeof(bool), typeof(PaperMotion), new PropertyMetadata(false));

    public static bool GetCardRow(DependencyObject element) => (bool)element.GetValue(CardRowProperty);
    public static void SetCardRow(DependencyObject element, bool value) => element.SetValue(CardRowProperty, value);
    public static bool GetNavTab(DependencyObject element) => (bool)element.GetValue(NavTabProperty);
    public static void SetNavTab(DependencyObject element, bool value) => element.SetValue(NavTabProperty, value);

    /// <summary>系统「显示动画」关闭时（远程桌面、无障碍设置）所有时长为 0。</summary>
    private static bool Animate => SystemParameters.ClientAreaAnimation;

    private static void OnCardRowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBoxItem item) return;
        if ((bool)e.NewValue)
        {
            item.MouseEnter += OnRowPointerChanged;
            item.MouseLeave += OnRowPointerChanged;
            item.PreviewMouseLeftButtonDown += OnRowPressed;
            item.PreviewMouseLeftButtonUp += OnRowReleased;
            item.LostMouseCapture += OnRowReleased;
            item.Selected += OnRowSelected;
            item.Unselected += OnRowPointerChanged;
        }
        else
        {
            item.MouseEnter -= OnRowPointerChanged;
            item.MouseLeave -= OnRowPointerChanged;
            item.PreviewMouseLeftButtonDown -= OnRowPressed;
            item.PreviewMouseLeftButtonUp -= OnRowReleased;
            item.LostMouseCapture -= OnRowReleased;
            item.Selected -= OnRowSelected;
            item.Unselected -= OnRowPointerChanged;
        }
    }

    private static void OnNavTabChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TabItem tab) return;
        if ((bool)e.NewValue) tab.AddHandler(Selector.SelectedEvent, (RoutedEventHandler)OnTabSelected);
        else tab.RemoveHandler(Selector.SelectedEvent, (RoutedEventHandler)OnTabSelected);
    }

    private static void OnRowPointerChanged(object sender, RoutedEventArgs e)
    {
        if (sender is ListBoxItem item) UpdateRow(item, HoverDuration, EaseOut);
    }

    private static void OnRowPressed(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBoxItem item) return;
        item.SetValue(IsPressedProperty, true);
        UpdateRow(item, PressDuration, EaseOut);
    }

    private static void OnRowReleased(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBoxItem item || !(bool)item.GetValue(IsPressedProperty)) return;
        item.SetValue(IsPressedProperty, false);
        UpdateRow(item, HoverDuration, EaseOut);
    }

    private static void OnRowSelected(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBoxItem item || !ReferenceEquals(e.OriginalSource, item)) return;
        // 刷新列表时新容器在加载前就被设为选中：只有已显示的行才播放，避免每次捕获都弹一次夹子。
        var visible = item.IsLoaded && item.IsVisible;
        if (visible && Flag(item, "SelectionPop"))
        {
            UpdateRow(item, SettleDuration, Settle);
            PopClip(item);
        }
        else
        {
            UpdateRow(item, HoverDuration, EaseOut);
        }
    }

    /// <summary>把卡片位移与悬停影推到当前状态应有的终值；主题键为 0 时不挂任何变换。</summary>
    private static void UpdateRow(ListBoxItem item, Duration duration, IEasingFunction easing)
    {
        var lift = Number(item, "RowHoverLift");
        var sink = Number(item, "RowPressSink");
        var card = Part<FrameworkElement>(item, "Card");
        var hoverShadow = Part<UIElement>(item, "HoverShadow");
        if (card is null) return;

        if (lift == 0 && sink == 0)
        {
            // 从夹夹切到其他主题后，已挂上的变换归零并收起悬停影，之后不再动它。
            if (card.RenderTransform is TranslateTransform stale)
            {
                stale.BeginAnimation(TranslateTransform.YProperty, null);
                stale.Y = 0;
            }
            if (hoverShadow is not null)
            {
                hoverShadow.BeginAnimation(UIElement.OpacityProperty, null);
                hoverShadow.Opacity = 0;
                hoverShadow.Visibility = Visibility.Collapsed;
            }
            return;
        }

        var pressed = (bool)item.GetValue(IsPressedProperty);
        var hovered = item.IsMouseOver && !item.IsSelected;
        var targetY = pressed ? sink : hovered ? lift : 0;
        var targetShadow = hovered && !pressed ? 1.0 : 0.0;

        To(EnsureTranslate(card), TranslateTransform.YProperty, targetY, duration, easing);
        if (hoverShadow is not null)
        {
            if (targetShadow > 0) hoverShadow.Visibility = Visibility.Visible;
            To(hoverShadow, UIElement.OpacityProperty, targetShadow, duration, easing);
        }
    }

    private static void PopClip(ListBoxItem item)
    {
        var clip = Part<UIElement>(item, "Clip");
        if (clip is null || !Animate) return;
        if (clip.RenderTransform is not TransformGroup { Children.Count: 2 } group ||
            group.Children[0] is not ScaleTransform || group.Children[1] is not RotateTransform)
        {
            group = new TransformGroup();
            group.Children.Add(new ScaleTransform());
            group.Children.Add(new RotateTransform());
            clip.RenderTransform = group;
        }
        var scale = (ScaleTransform)group.Children[0];
        var rotate = (RotateTransform)group.Children[1];
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, From(0.4, 1, PopDuration, Pop));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, From(0.4, 1, PopDuration, Pop));
        rotate.BeginAnimation(RotateTransform.AngleProperty, From(-14, 0, PopDuration, Pop));
    }

    private static void OnTabSelected(object sender, RoutedEventArgs e)
    {
        if (sender is not TabItem tab || !ReferenceEquals(e.OriginalSource, tab)) return;
        if (!Animate || !tab.IsLoaded || !tab.IsVisible || !Flag(tab, "NavSelectWipe")) return;
        if (Part<UIElement>(tab, "Highlight") is not { } highlight) return;
        if (highlight.RenderTransform is not ScaleTransform scale || scale.IsFrozen)
        {
            scale = new ScaleTransform();
            highlight.RenderTransform = scale;
        }
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, From(0, 1, WipeDuration, EaseOut));
    }

    /// <summary>
    /// 新捕获的记录插入列表顶部后调用：从上方 <c>RowDropDistance</c> 落下、透明度 0→1。
    /// 只给捕获后的那一条用；滚动、筛选与普通刷新不要调用。主题键为 0 或系统关闭动画时什么也不做。
    /// </summary>
    public static void PlayDrop(ListBoxItem item)
    {
        var distance = Number(item, "RowDropDistance");
        if (distance == 0 || !Animate) return;
        if (item.RenderTransform is not TranslateTransform shift || shift.IsFrozen)
        {
            shift = new TranslateTransform();
            item.RenderTransform = shift;
        }
        shift.BeginAnimation(TranslateTransform.YProperty, From(-distance, 0, DropDuration, Settle));
        item.BeginAnimation(UIElement.OpacityProperty, From(0, 1, DropDuration, EaseOut));
    }

    private static TranslateTransform EnsureTranslate(UIElement element)
    {
        if (element.RenderTransform is TranslateTransform shift && !shift.IsFrozen) return shift;
        shift = new TranslateTransform();
        element.RenderTransform = shift;
        return shift;
    }

    private static void To(IAnimatable target, DependencyProperty property, double value, Duration duration, IEasingFunction easing)
    {
        if (!Animate)
        {
            target.BeginAnimation(property, null);
            ((DependencyObject)target).SetValue(property, value);
            return;
        }
        // HoldEnd 停在终值，下一次 BeginAnimation 从当前值接着走（SnapshotAndReplace）。
        target.BeginAnimation(property, new DoubleAnimation(value, duration) { EasingFunction = easing });
    }

    private static DoubleAnimation From(double from, double to, Duration duration, IEasingFunction easing) =>
        new(from, to, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop };

    private static T? Part<T>(System.Windows.Controls.Control control, string name) where T : class =>
        control.Template?.FindName(name, control) as T;

    private static double Number(FrameworkElement element, string key) =>
        element.TryFindResource(key) is double value ? value : 0;

    private static bool Flag(FrameworkElement element, string key) =>
        element.TryFindResource(key) is true;

    private static IEasingFunction Frozen(EasingFunctionBase easing)
    {
        easing.Freeze();
        return easing;
    }
}
