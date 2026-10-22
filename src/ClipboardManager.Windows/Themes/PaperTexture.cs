using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipboardManager.Windows.Themes;

/// <summary>
/// Builds a restrained 信笺 paper grain in memory, so the shell ships no texture bitmap and the
/// tooth stays crisp at every DPI scale.
/// </summary>
/// <remarks>
/// Two constraints shape this.
/// WPF elements have no blend mode, so the "multiply" a CSS design system would reach for
/// is expressed here as a dark fill whose <em>alpha</em> varies. Jittering the colour
/// channels as well would produce coloured static, which on warm stock reads as dirt
/// rather than grain. Because the fill can only darken, the amplitude is kept low enough
/// that the paper still reads as light stock; overshooting this turns the window grey-tan.
/// The tile is per-pixel noise rather than smooth noise, which is harsher per unit of
/// amplitude than a scaled-up texture, so the peak alpha here is deliberately below what a
/// downsampled noise bitmap would use.
/// </remarks>
internal static class PaperTexture
{
    /// <summary>Tile edge in device-independent pixels. A power of two, though tiling does not require it.</summary>
    private const int TileSize = 256;

    /// <summary>
    /// Fixed seed, so the tooth is identical on every launch; a fresh field each time makes
    /// the window appear to crawl when it is reopened.
    /// </summary>
    private const int Seed = 20261016;

    /// <summary>Ink the grain is made of. Warm, so the noise darkens without greying the stock.</summary>
    private const byte InkR = 0x5C;
    private const byte InkG = 0x6E;
    private const byte InkB = 0x76;

    /// <summary>
    /// Peak alpha of a pixel, around 3%. A laid-line banding pass was tried first at roughly
    /// double this and had to be dropped: any period short enough to read as laid paper on
    /// screen lands near the pixel grid and shows up as horizontal striping across the whole
    /// window. Grain alone reads as stock; the striping read as a rendering fault.
    /// </summary>
    private const int MaxGrainAlpha = 8;

    /// <summary>Returns the frozen grain, as an infinitely tiling brush.</summary>
    public static ImageBrush CreatePaperOverlay()
    {
        var stride = TileSize * 4;
        var pixels = new byte[TileSize * TileSize * 4];
        var random = new Random(Seed);

        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            // Only alpha varies: the colour stays fixed so the grain never turns chromatic.
            pixels[offset] = InkB;
            pixels[offset + 1] = InkG;
            pixels[offset + 2] = InkR;
            pixels[offset + 3] = (byte)random.Next(MaxGrainAlpha + 1);
        }

        var tile = BitmapSource.Create(
            TileSize, TileSize, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        tile.Freeze();

        var brush = new ImageBrush(tile)
        {
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, TileSize, TileSize),
            // Native size, top-left anchored: scaling here would blur the grain, and the
            // default centre alignment would shift the tiling phase by a half pixel.
            Stretch = Stretch.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
        };
        brush.Freeze();
        return brush;
    }
}
