using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using ScreenBlurGuard.Native;

namespace ScreenBlurGuard.Overlay;

/// <summary>
/// A normal, independent, taskbar-visible window that periodically captures the target
/// window's content (via <c>PrintWindow</c> + <c>PW_RENDERFULLCONTENT</c>, which correctly
/// captures modern DirectX/DirectComposition-rendered apps too) into a WPF Image, with a
/// second copy of that same frame — blurred or pixelated into a mosaic, see
/// <see cref="BlurStyle"/> — clipped down to just the sensitive sub-regions and drawn on top,
/// all as ordinary elements of the SAME window's visual tree.
///
/// This is the window the user actually shares in Discord/Zoom/etc — either via "share
/// entire screen" or by picking it directly from a "share a specific app window" list. Since
/// both the mirrored image and the obscured patch are plain WPF content in one window (not
/// separate HWNDs competing for z-order, and not a DWM-composited thumbnail that could have
/// its own compositing-order quirks), there is no "airspace"/z-order ambiguity: any capture
/// method sees exactly the same thing a local viewer of this window would.
/// </summary>
public partial class MirrorPreviewWindow : Window
{
    // The intensity slider is a single 0–100 scale shared by both styles; these are the real
    // units it maps to at each end. Bounds chosen so 0 is still a *visible* privacy guard
    // (not "off") and 100 is strong enough to make text unreadable at typical UI font sizes —
    // this is a privacy guard, so even the low end shouldn't be mistaken for no protection.
    internal const double MinBlurRadius = 5;
    internal const double MaxBlurRadius = 60;
    internal const int MinMosaicBlockPixels = 4;
    internal const int MaxMosaicBlockPixels = 40;

    internal static double ComputeBlurRadius(double intensityPercent) =>
        Lerp(MinBlurRadius, MaxBlurRadius, NormalizePercent(intensityPercent));

    internal static int ComputeMosaicBlockPixels(double intensityPercent) =>
        (int)Math.Round(Lerp(MinMosaicBlockPixels, MaxMosaicBlockPixels, NormalizePercent(intensityPercent)));

    private static double NormalizePercent(double percent) => Math.Clamp(percent, 0, 100) / 100.0;
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private readonly BlurEffect _blurEffect = new() { KernelType = KernelType.Gaussian };
    private readonly IntPtr _targetHwnd;
    private readonly DispatcherTimer _captureTimer;
    private int _captureWidth;
    private int _captureHeight;
    private BlurStyle _style = BlurStyle.Blur;
    private double _intensityPercent = 25;
    private BitmapSource? _lastCapturedSource;

    /// <summary>
    /// Fires after every re-render (each capture tick, and each immediate style/intensity
    /// switch) with a snapshot of exactly what this window is currently showing — the same
    /// pixels a screen share would send. Lets <c>MainWindow</c> show a live "what you're
    /// sharing" thumbnail without duplicating the blur/mosaic compositing logic.
    /// </summary>
    public event Action<BitmapSource>? FrameRendered;

    public MirrorPreviewWindow(IntPtr targetHwnd, int clientWidth, int clientHeight)
    {
        InitializeComponent();
        _targetHwnd = targetHwnd;
        _captureWidth = clientWidth;
        _captureHeight = clientHeight;

        RootCanvas.Width = clientWidth;
        RootCanvas.Height = clientHeight;

        ObscuredMirrorContainer.Clip = new GeometryGroup();

        // ShowActivated="False" (see XAML) only stops this window from stealing keyboard
        // focus — it does NOT stop Windows from still placing a brand-new top-level window at
        // the front of the z-order, so it was still popping up in front of (covering) whatever
        // the user was just looking at. Explicitly sending it to the bottom of the z-order
        // fixes that — but a single attempt at SourceInitialized (HWND just created, before
        // first paint) turned out not to reliably stick, something later in Show()'s own
        // pipeline was putting it back in front. Repeating the same call at Loaded (after the
        // window is actually shown and laid out) closes that race. Safe here because this app
        // is only ever screen-shared via "share a specific window" (confirmed with the user) —
        // that capture mode reads the window's composited surface regardless of on-screen
        // occlusion, unlike "share entire screen", which would leak the real target's content
        // if this window were sent behind it while relying on that mode.
        SourceInitialized += (_, _) => SendToBackOfZOrder();
        Loaded += (_, _) => SendToBackOfZOrder();

        _captureTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _captureTimer.Tick += (_, _) => CaptureFrame();
        Loaded += (_, _) => _captureTimer.Start();
        Closed += (_, _) => _captureTimer.Stop();
    }

    /// <summary>
    /// Explicitly pushes this window to the bottom of the desktop-wide z-order. Public so the
    /// caller can also invoke it right after <see cref="Window.Show"/> returns, as one more
    /// attempt beyond the internal SourceInitialized/Loaded ones — see the constructor's
    /// comment for why a single attempt wasn't reliable enough.
    /// </summary>
    public void SendToBackOfZOrder()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_BOTTOM, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>
    /// Switches between blurring and pixelating the selected regions. Re-renders immediately
    /// from the last captured frame rather than waiting for the next capture tick, so the
    /// switch feels instant.
    /// </summary>
    public void SetStyle(BlurStyle style)
    {
        if (_style == style) return;
        _style = style;

        if (_lastCapturedSource != null)
        {
            ApplyObscuredRendering(_lastCapturedSource);
        }
    }

    /// <summary>
    /// Sets how strong the current style's effect is, as a 0–100 scale (see
    /// <see cref="ComputeBlurRadius"/>/<see cref="ComputeMosaicBlockPixels"/> for what that
    /// maps to for each style). Re-renders immediately from the last captured frame.
    /// </summary>
    public void SetIntensity(double intensityPercent)
    {
        intensityPercent = Math.Clamp(intensityPercent, 0, 100);
        if (Math.Abs(_intensityPercent - intensityPercent) < 0.01) return;
        _intensityPercent = intensityPercent;

        if (_lastCapturedSource != null)
        {
            ApplyObscuredRendering(_lastCapturedSource);
        }
    }

    /// <summary>Updates the mirrored area's pixel size (e.g. after the target resizes).</summary>
    public void ResizeCapture(int clientWidth, int clientHeight)
    {
        _captureWidth = clientWidth;
        _captureHeight = clientHeight;
        RootCanvas.Width = clientWidth;
        RootCanvas.Height = clientHeight;
    }

    /// <summary>
    /// Updates which parts of the (fully obscured, full-size) <see cref="ObscuredMirrorImage"/>
    /// are actually visible, by rebuilding <see cref="ObscuredMirrorContainer"/>'s clip geometry
    /// from the given region rects — in this window's own client-area coordinates.
    /// </summary>
    public void UpdateRegions(IReadOnlyList<RECT> regions)
    {
        var clip = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var region in regions)
        {
            clip.Children.Add(new RectangleGeometry(
                new Rect(region.Left, region.Top, region.Width, region.Height)));
        }

        ObscuredMirrorContainer.Clip = clip;
    }

    /// <summary>
    /// Renders <paramref name="fullResSource"/> into <see cref="ObscuredMirrorImage"/> per the
    /// current <see cref="_style"/>. Blur mode applies a Gaussian blur to the full-resolution
    /// frame (so it always has full surrounding context to sample from — the clip on
    /// <see cref="ObscuredMirrorContainer"/> is what limits what's actually visible, not this
    /// image's own bounds). Mosaic mode instead shrinks the frame down to blocky low-res pixels
    /// and stretches it back up with nearest-neighbor scaling (no smoothing), so it needs
    /// explicit Stretch/size — unlike blur mode, where the image is shown at its natural
    /// (already full) resolution.
    /// </summary>
    private void ApplyObscuredRendering(BitmapSource fullResSource)
    {
        if (_style == BlurStyle.Blur)
        {
            _blurEffect.Radius = ComputeBlurRadius(_intensityPercent);
            ObscuredMirrorImage.Effect = _blurEffect;
            ObscuredMirrorImage.Stretch = Stretch.None;
            ObscuredMirrorImage.Width = double.NaN;
            ObscuredMirrorImage.Height = double.NaN;
            ObscuredMirrorImage.Source = fullResSource;
        }
        else
        {
            var mosaicScale = 1.0 / ComputeMosaicBlockPixels(_intensityPercent);
            var mosaicSource = new TransformedBitmap(fullResSource, new ScaleTransform(mosaicScale, mosaicScale));
            mosaicSource.Freeze();

            ObscuredMirrorImage.Effect = null;
            RenderOptions.SetBitmapScalingMode(ObscuredMirrorImage, BitmapScalingMode.NearestNeighbor);
            ObscuredMirrorImage.Stretch = Stretch.Fill;
            ObscuredMirrorImage.Width = _captureWidth;
            ObscuredMirrorImage.Height = _captureHeight;
            ObscuredMirrorImage.Source = mosaicSource;
        }

        RaiseFrameRendered();
    }

    /// <summary>
    /// Snapshots the already-composited <see cref="RootCanvas"/> (original + obscured regions,
    /// exactly as shown/shared) into a bitmap for <see cref="FrameRendered"/> subscribers.
    /// </summary>
    private void RaiseFrameRendered()
    {
        if (FrameRendered == null || _captureWidth <= 0 || _captureHeight <= 0) return;

        var snapshot = new RenderTargetBitmap(_captureWidth, _captureHeight, 96, 96, PixelFormats.Pbgra32);
        snapshot.Render(RootCanvas);
        snapshot.Freeze();
        FrameRendered.Invoke(snapshot);
    }

    private void CaptureFrame()
    {
        if (_captureWidth <= 0 || _captureHeight <= 0) return;
        if (!NativeMethods.IsWindow(_targetHwnd)) return;

        using var bitmap = new System.Drawing.Bitmap(_captureWidth, _captureHeight);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            var hdc = graphics.GetHdc();
            try
            {
                NativeMethods.PrintWindow(_targetHwnd, hdc, NativeMethods.PW_RENDERFULLCONTENT);
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }
        }

        var hBitmap = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            MirrorImage.Source = source;
            _lastCapturedSource = source;
            ApplyObscuredRendering(source);
        }
        finally
        {
            NativeMethods.DeleteObject(hBitmap);
        }
    }
}
