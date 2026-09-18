using System;
using System.Windows;
using ScreenBlurGuard.Native;
using ScreenBlurGuard.Overlay;

namespace ScreenBlurGuard;

/// <summary>
/// Interaction logic for MainWindow.xaml
///
/// Architecture note (see plan doc for full history): earlier prototypes tried overlaying
/// blur/mirror windows directly on top of the target app's own window (failed for Discord's
/// "share a specific app window" mode due to cross-process compositing/"airspace" issues),
/// then tried a separate mirror window with a DWM-thumbnail background + a separately-owned
/// blur window on top of it (still failed the same way, since a window-specific capture of
/// the mirror window doesn't include a *separate* owned window sitting on top of it either).
///
/// Current design: ONE self-contained window (<see cref="MirrorPreviewWindow"/>) that
/// periodically captures the target via PrintWindow into a WPF Image, with the blur
/// rectangle drawn as an ordinary sibling element in the SAME visual tree — no separate
/// HWNDs, no z-order ambiguity, works identically for "entire screen" and "specific window"
/// sharing. The user shares THIS mirror window (not the real app).
/// </summary>
public partial class MainWindow : Window
{
    private MirrorPreviewWindow? _mirrorPreview;
    private WinEventHookService? _tracker;
    private IntPtr _targetHwnd;

    // The selected sub-region, stored as a FIXED pixel offset + size from the target
    // window's client-area origin (see plan doc for why not a proportional fraction).
    // Clipped safely on shrink; a resize is flagged rather than silently trusted.
    private int _offsetLeft, _offsetTop, _regionWidth, _regionHeight;
    private int _lastKnownClientWidth = -1;
    private int _lastKnownClientHeight = -1;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnSelectRegionClick(object sender, RoutedEventArgs e)
    {
        RemoveOverlays();

        Hide();

        var selectionWindow = new RegionSelectionWindow();
        selectionWindow.Completed += OnSelectionCompleted;
        selectionWindow.Cancelled += OnSelectionCancelled;
        selectionWindow.Closed += (_, _) => Show();
        selectionWindow.Show();
    }

    private void OnSelectionCompleted(RegionSelectionResult result)
    {
        _targetHwnd = result.TargetHwnd;

        if (!NativeMethods.GetClientRect(_targetHwnd, out var clientRect) ||
            clientRect.Width <= 0 || clientRect.Height <= 0)
        {
            StatusText.Text = "대상 창의 클라이언트 영역을 가져오지 못했습니다.";
            return;
        }

        _offsetLeft = result.ClientRect.Left;
        _offsetTop = result.ClientRect.Top;
        _regionWidth = result.ClientRect.Width;
        _regionHeight = result.ClientRect.Height;
        _lastKnownClientWidth = clientRect.Width;
        _lastKnownClientHeight = clientRect.Height;

        CreateMirrorPreview(clientRect);
    }

    private void OnSelectionCancelled()
    {
        StatusText.Text = "영역 선택이 취소되었습니다.";
    }

    private void CreateMirrorPreview(RECT clientRect)
    {
        _mirrorPreview = new MirrorPreviewWindow(_targetHwnd, clientRect.Width, clientRect.Height)
        {
            Left = 100,
            Top = 100,
        };
        _mirrorPreview.Show();

        if (TryComputeRegion(out var region, out _))
        {
            _mirrorPreview.UpdateRegion(region);
        }

        _tracker = new WinEventHookService(_targetHwnd);
        _tracker.LocationOrSizeChanged += OnTargetLocationOrSizeChanged;
        _tracker.TargetDestroyed += OnTargetDestroyed;
        _tracker.Start();

        StatusText.Text = "미러링 창이 생성되었습니다. Discord 등에서 화면 공유 시 " +
                           "이 '공유용 미러' 창을 선택해서 공유하세요 (전체 화면 공유도 가능합니다) — " +
                           "실제 작업 앱을 직접 공유하지 마세요, 블러가 적용되지 않습니다.";
    }

    /// <summary>
    /// Recomputes the sub-region's current client-relative geometry from its stored fixed
    /// pixel offset/size and the target's *current* client rect, clipping safely if the
    /// target has shrunk.
    /// </summary>
    private bool TryComputeRegion(out RECT regionClientRect, out bool resized)
    {
        regionClientRect = default;
        resized = false;

        if (!NativeMethods.GetClientRect(_targetHwnd, out var clientRect) ||
            clientRect.Width <= 0 || clientRect.Height <= 0)
        {
            return false;
        }

        resized = _lastKnownClientWidth >= 0 &&
                  (clientRect.Width != _lastKnownClientWidth || clientRect.Height != _lastKnownClientHeight);
        _lastKnownClientWidth = clientRect.Width;
        _lastKnownClientHeight = clientRect.Height;

        int left = Math.Clamp(_offsetLeft, 0, clientRect.Width);
        int top = Math.Clamp(_offsetTop, 0, clientRect.Height);
        int right = Math.Clamp(_offsetLeft + _regionWidth, 0, clientRect.Width);
        int bottom = Math.Clamp(_offsetTop + _regionHeight, 0, clientRect.Height);

        if (right - left <= 0 || bottom - top <= 0)
        {
            return false;
        }

        regionClientRect = new RECT { Left = left, Top = top, Right = right, Bottom = bottom };
        return true;
    }

    private void OnTargetLocationOrSizeChanged()
    {
        Dispatcher.Invoke(() =>
        {
            if (_mirrorPreview == null) return;
            if (!NativeMethods.IsWindow(_targetHwnd)) return;
            if (!NativeMethods.GetClientRect(_targetHwnd, out var clientRect)) return;

            _mirrorPreview.ResizeCapture(clientRect.Width, clientRect.Height);

            if (TryComputeRegion(out var region, out var resized))
            {
                _mirrorPreview.UpdateRegion(region);
            }

            if (resized)
            {
                StatusText.Text = "⚠ 대상 창 크기가 변경되었습니다. 블러 위치가 실제 콘텐츠와 어긋났을 수 있으니 영역을 다시 선택해 확인하세요.";
            }
        });
    }

    private void OnTargetDestroyed()
    {
        Dispatcher.Invoke(() =>
        {
            RemoveOverlays();
            StatusText.Text = "대상 앱이 종료되어 미러링 창을 자동으로 정리했습니다.";
        });
    }

    private void OnRemoveOverlayClick(object sender, RoutedEventArgs e)
    {
        RemoveOverlays();
        StatusText.Text = "미러링 창 제거됨.";
    }

    private void RemoveOverlays()
    {
        _tracker?.Dispose();
        _tracker = null;

        _mirrorPreview?.Close();
        _mirrorPreview = null;

        _targetHwnd = IntPtr.Zero;
        _lastKnownClientWidth = -1;
        _lastKnownClientHeight = -1;
    }
}
