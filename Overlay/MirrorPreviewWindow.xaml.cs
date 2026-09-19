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
/// second Gaussian-blurred copy of that same frame clipped down to just the sensitive
/// sub-regions and drawn on top — all as ordinary elements of the SAME window's visual tree.
///
/// This is the window the user actually shares in Discord/Zoom/etc — either via "share
/// entire screen" or by picking it directly from a "share a specific app window" list. Since
/// both the mirrored image and the blur patch are plain WPF content in one window (not
/// separate HWNDs competing for z-order, and not a DWM-composited thumbnail that could have
/// its own compositing-order quirks), there is no "airspace"/z-order ambiguity: any capture
/// method sees exactly the same thing a local viewer of this window would.
/// </summary>
public partial class MirrorPreviewWindow : Window
{
    // Strong enough to make text unreadable at typical UI font sizes — this is a privacy
    // guard, so it should err on the side of hiding too much rather than too little.
    private const double BlurRadius = 30;

    private readonly IntPtr _targetHwnd;
    private readonly DispatcherTimer _captureTimer;
    private int _captureWidth;
    private int _captureHeight;

    public MirrorPreviewWindow(IntPtr targetHwnd, int clientWidth, int clientHeight)
    {
        InitializeComponent();
        _targetHwnd = targetHwnd;
        _captureWidth = clientWidth;
        _captureHeight = clientHeight;

        RootCanvas.Width = clientWidth;
        RootCanvas.Height = clientHeight;

        // BlurredMirrorImage is a full duplicate of the captured frame with a Gaussian blur
        // applied to the WHOLE image; BlurredMirrorContainer then clips that already-blurred
        // result down to just the selected regions (see UpdateRegions). The blur must be
        // computed over the full, unclipped frame — putting Effect and Clip on the same
        // element would blur the clipped (mostly transparent) bitmap instead, producing faded
        // edges rather than a clean blurred patch with full surrounding context.
        BlurredMirrorImage.Effect = new BlurEffect { Radius = BlurRadius, KernelType = KernelType.Gaussian };
        BlurredMirrorContainer.Clip = new GeometryGroup();

        _captureTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _captureTimer.Tick += (_, _) => CaptureFrame();
        Loaded += (_, _) => _captureTimer.Start();
        Closed += (_, _) => _captureTimer.Stop();
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
    /// Updates which parts of the (fully blurred, full-size) <see cref="BlurredMirrorImage"/>
    /// are actually visible, by rebuilding <see cref="BlurredMirrorContainer"/>'s clip geometry
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

        BlurredMirrorContainer.Clip = clip;
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
            BlurredMirrorImage.Source = source;
        }
        finally
        {
            NativeMethods.DeleteObject(hBitmap);
        }
    }
}
