using System.Windows.Media;
using ClipboardManager.Windows.Themes;

namespace ClipboardManager.Windows.Services;

/// <summary>
/// 内容区纸纹，供 XAML 以 <c>{x:Static services:PaperGrain.Brush}</c> 引用；显隐由主题键 <c>PaperGrainVisibility</c> 决定
/// （只有夹夹主题为 Visible）。颗粒由 <see cref="PaperTexture"/> 按固定种子生成，冻结后全窗口共用一份。
/// </summary>
public static class PaperGrain
{
    public static System.Windows.Media.Brush Brush { get; } = PaperTexture.CreatePaperOverlay();
}
