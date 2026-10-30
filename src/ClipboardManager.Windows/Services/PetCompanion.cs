using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClipboardManager.Windows.Services;

/// <summary>
/// Footer pixel-pet companion. Three states: <see cref="PetState.Idle"/> while
/// listening (breathing loop with a blink), <see cref="PetState.Sleeping"/> while
/// paused, and <see cref="PetState.Hop"/>, a one-shot celebration on new captures.
///
/// Frames are pre-rendered transparent PNGs produced by
/// scripts/build-pet-assets.py from two approved base sprites; this control only
/// cycles them, so identity cannot drift. The control is non-interactive
/// (no focus, no hit test) per the panel's keyboard-discipline rules.
/// </summary>
public sealed class PetCompanion : System.Windows.Controls.Control
{
    public enum PetState { Idle, Sleeping, Hop, Carry }

    private const int FrameIntervalMs = 120;
    private const string AssetFolder = "Assets.Pet.";

    private static readonly string[] IdleFrames = Enumerable.Range(0, 8).Select(i => $"idle-{i}.png").ToArray();
    private static readonly string[] SleepFrames = Enumerable.Range(0, 6).Select(i => $"sleep-{i}.png").ToArray();
    private static readonly string[] HopFrames = Enumerable.Range(0, 6).Select(i => $"hop-{i}.png").ToArray();
    private static readonly string[] CarryFrames = Enumerable.Range(0, 2).Select(i => $"carry-{i}.png").ToArray();

    private static BitmapSource?[][]? _frames;
    private readonly DispatcherTimer _timer;
    private PetState _state = PetState.Idle;
    private PetState _resumeState = PetState.Idle;
    private int _frameIndex;
    private bool _assetsMissing;

    public PetCompanion()
    {
        Focusable = false;
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FrameIntervalMs) };
        _timer.Tick += (_, _) => Advance();
        // Animate only while actually shown: the tray app is resident, and a hidden
        // panel or pet window must not keep an 8 fps render loop alive.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && !_assetsMissing) _timer.Start();
            else _timer.Stop();
        };
    }

    /// <summary>Paused => sleeping; otherwise awake and breathing.</summary>
    public void SetListening(bool listening)
    {
        var target = listening ? PetState.Idle : PetState.Sleeping;
        if (_state == PetState.Carry) { _resumeState = target; return; }
        if (_state == target) return;
        _resumeState = target;
        EnterState(target);
    }

    /// <summary>Drag armed: the pet is lifted and dangles until released.</summary>
    public void SetCarried(bool carried)
    {
        if (_assetsMissing) return;
        if (carried && _state != PetState.Carry)
        {
            _resumeState = _state == PetState.Sleeping ? PetState.Sleeping : PetState.Idle;
            EnterState(PetState.Carry);
        }
        else if (!carried && _state == PetState.Carry)
        {
            EnterState(_resumeState);
        }
    }

    /// <summary>One happy hop, then back to the ambient state. Safe to call anytime.</summary>
    public void NotifyCopied()
    {
        if (_assetsMissing || _state == PetState.Hop || _state == PetState.Carry) return;
        _resumeState = _state == PetState.Sleeping ? PetState.Sleeping : PetState.Idle;
        EnterState(PetState.Hop);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (_assetsMissing) return;
        var frame = CurrentFrame();
        if (frame is not null)
            drawingContext.DrawImage(frame, new Rect(0, 0, ActualWidth, ActualHeight));
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        EnsureFrames();
    }

    private static BitmapSource?[][] Frames()
    {
        if (_frames is null)
        {
            // EmbeddedResource on this control's own assembly: no Application, ResourceAssembly
            // or pack-URI context required, so the pet renders in the real app, in LayoutCheck's
            // loose-XAML host, and in any future test harness.
            var assembly = typeof(PetCompanion).Assembly;
            var resourceNames = assembly.GetManifestResourceNames();
            BitmapSource?[] LoadAll(string[] names)
            {
                var row = new BitmapSource?[names.Length];
                for (var i = 0; i < names.Length; i++)
                {
                    var resourceName = resourceNames.SingleOrDefault(candidate => candidate.EndsWith(AssetFolder + names[i], StringComparison.Ordinal));
                    if (resourceName is null) throw new InvalidOperationException($"Pet frame missing from assembly resources: {names[i]}");
                    using var stream = assembly.GetManifestResourceStream(resourceName)!;
                    var image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    image.Freeze();
                    row[i] = image;
                }
                return row;
            }
            _frames = new[] { LoadAll(IdleFrames), LoadAll(SleepFrames), LoadAll(HopFrames), LoadAll(CarryFrames) };
        }
        return _frames;
    }

    private void EnsureFrames()
    {
        if (_frames is not null) return;
        try
        {
            _ = Frames();
            Serilog.Log.Information("Panel pet frames loaded: idle={Idle} sleep={Sleep} hop={Hop}",
                _frames![0].Length, _frames[1].Length, _frames[2].Length);
        }
        catch (Exception error)
        {
            // Assets were not generated for this build; disappear quietly — but never
            // silently: an invisible pet must leave a fingerprint in the log.
            LoadError = error;
            _assetsMissing = true;
            Visibility = Visibility.Collapsed;
            Serilog.Log.Error(error, "Panel pet frames failed to load; pet hidden");
        }
    }

    /// <summary>Last frame-load failure, if any; surfaced by the LayoutCheck probe.</summary>
    public static Exception? LoadError { get; private set; }

    private BitmapSource? CurrentFrame()
    {
        var rows = _frames;
        if (rows is null) return null;
        var row = _state switch
        {
            PetState.Sleeping => rows[1],
            PetState.Hop => rows[2],
            PetState.Carry => rows[3],
            _ => rows[0],
        };
        return row[_frameIndex % row.Length];
    }

    private void EnterState(PetState state)
    {
        _state = state;
        _frameIndex = 0;
        InvalidateVisual();
    }

    private void Advance()
    {
        if (_assetsMissing) { _timer.Stop(); return; }
        _frameIndex++;
        var rowLength = _state switch
        {
            PetState.Sleeping => SleepFrames.Length,
            PetState.Hop => HopFrames.Length,
            PetState.Carry => CarryFrames.Length,
            _ => IdleFrames.Length,
        };
        if (_state == PetState.Hop && _frameIndex >= rowLength)
        {
            // One-shot: settle back into the ambient state.
            EnterState(_resumeState);
            return;
        }
        InvalidateVisual();
    }
}
