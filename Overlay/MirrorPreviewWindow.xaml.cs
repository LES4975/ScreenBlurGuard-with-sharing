using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScreenBlurGuard.Native;

namespace ScreenBlurGuard.Overlay;

/// <summary>
/// A normal, independent, taskbar-visible window that periodically captures the target
/// window's content (via <c>PrintWindow</c> + <c>PW_RENDERFULLCONTENT</c>, which correctly
/// captures modern DirectX/DirectComposition-rendered apps too) into a WPF <see cref="Image"/>,
/// with a blur <see cref="System.Windows.Shapes.Rectangle"/> drawn on top of the sensitive
/// sub-region — both as ordinary elements of the SAME window's visual tree.
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

    /// <summary>Repositions the blur patch, in this window's own client-area coordinates.</summary>
    public void UpdateRegion(RECT regionRect)
    {
        Canvas.SetLeft(BlurRect, regionRect.Left);
        Canvas.SetTop(BlurRect, regionRect.Top);
        BlurRect.Width = regionRect.Width;
        BlurRect.Height = regionRect.Height;
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
        }
        finally
        {
            NativeMethods.DeleteObject(hBitmap);
        }
    }
}
