using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using ScreenBlurGuard.Native;
using ScreenBlurGuard.Overlay;
using ScreenBlurGuard.Settings;

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

    // The selected sub-regions, each stored as a FIXED pixel offset + size (as a RECT: Left/Top
    // is the offset, Width/Height is the fixed size) from the target window's client-area
    // origin (see plan doc for why not a proportional fraction). Clipped safely on shrink;
    // a resize is flagged rather than silently trusted.
    private readonly List<RECT> _regionOffsets = new();
    private int _lastKnownClientWidth = -1;
    private int _lastKnownClientHeight = -1;

    // The saved profile (if any) whose process is currently running — set by PrepareRestoreButton,
    // consumed by OnRestoreSettingsClick. Each target app gets its own profile in the settings
    // file (keyed by process name) so saving one app's regions never overwrites another's.
    private TargetProfile? _matchedProfile;

    public MainWindow()
    {
        InitializeComponent();
        RefreshRestoreButton();
    }

    /// <summary>
    /// Loads saved profiles and, if any of their target processes is currently running,
    /// enables the "저장된 설정 불러오기" button so the user can skip re-selecting regions by
    /// hand. If more than one saved profile's app happens to be running at once, only the
    /// first match is offered (the UI is a single button, not a picker).
    ///
    /// Called not just at startup but also right after a fresh selection is saved — the button
    /// otherwise kept showing whatever was true when the app launched (e.g. still pointing at
    /// Notepad after the user had just re-selected regions on a browser instead), since nothing
    /// re-evaluated it mid-session.
    /// </summary>
    private void RefreshRestoreButton()
    {
        _matchedProfile = null;
        RestoreSettingsButton.IsEnabled = false;
        RestoreSettingsButton.Content = "저장된 설정 불러오기";

        var settings = SettingsStore.Load();
        if (settings is not { Profiles.Count: > 0 })
        {
            return;
        }

        // Search from the most-recently-saved end first (see SaveCurrentSettings), so that if
        // several saved profiles' apps happen to be running at once, the one the user touched
        // last wins over one just saved earlier in the session.
        _matchedProfile = settings.Profiles.AsEnumerable().Reverse().FirstOrDefault(p =>
            p.Regions.Count > 0 && FindRunningWindowByProcessName(p.ProcessName) != IntPtr.Zero);

        if (_matchedProfile == null)
        {
            StatusText.Text = $"저장된 설정이 {settings.Profiles.Count}개 있지만 해당 앱이 실행 중이 아닙니다.";
            return;
        }

        RestoreSettingsButton.IsEnabled = true;
        RestoreSettingsButton.Content = $"저장된 설정 불러오기 ({_matchedProfile.ProcessName}, 영역 {_matchedProfile.Regions.Count}개)";
    }

    private static IntPtr FindRunningWindowByProcessName(string processName)
    {
        foreach (var process in Process.GetProcessesByName(processName))
        {
            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                return process.MainWindowHandle;
            }
        }

        return IntPtr.Zero;
    }

    private void OnRestoreSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_matchedProfile == null)
        {
            return;
        }

        var hwnd = FindRunningWindowByProcessName(_matchedProfile.ProcessName);
        if (hwnd == IntPtr.Zero)
        {
            StatusText.Text = "저장된 대상 앱을 찾지 못했습니다. 먼저 해당 앱을 실행하세요.";
            return;
        }

        RemoveOverlays();
        _targetHwnd = hwnd;

        if (!NativeMethods.GetClientRect(_targetHwnd, out var clientRect) ||
            clientRect.Width <= 0 || clientRect.Height <= 0)
        {
            StatusText.Text = "대상 창의 클라이언트 영역을 가져오지 못했습니다.";
            return;
        }

        _regionOffsets.Clear();
        _regionOffsets.AddRange(_matchedProfile.Regions.Select(r => new RECT
        {
            Left = r.Left,
            Top = r.Top,
            Right = r.Left + r.Width,
            Bottom = r.Top + r.Height,
        }));
        _lastKnownClientWidth = clientRect.Width;
        _lastKnownClientHeight = clientRect.Height;

        CreateMirrorPreview(clientRect);
    }

    /// <summary>
    /// Upserts the current target's profile into the saved settings by process name, leaving
    /// every other saved app's profile untouched — saving a browser's regions must not wipe
    /// out a previously saved Notepad profile, or vice versa. The updated profile is moved to
    /// the END of the list, marking it as the most recently used one: if multiple saved
    /// profiles' apps are running at once, <see cref="RefreshRestoreButton"/> prefers whichever
    /// was touched most recently rather than whichever happens to be listed first.
    /// </summary>
    private void SaveCurrentSettings()
    {
        if (NativeMethods.GetWindowThreadProcessId(_targetHwnd, out uint pid) == 0 || pid == 0)
        {
            return;
        }

        string processName;
        try
        {
            processName = Process.GetProcessById((int)pid).ProcessName;
        }
        catch (ArgumentException)
        {
            return;
        }

        var settings = SettingsStore.Load() ?? new AppSettings();
        var profile = settings.Profiles.FirstOrDefault(p =>
            string.Equals(p.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
        if (profile != null)
        {
            settings.Profiles.Remove(profile);
        }
        else
        {
            profile = new TargetProfile { ProcessName = processName };
        }

        profile.Regions = _regionOffsets.Select(r => new SavedRegion
        {
            Left = r.Left,
            Top = r.Top,
            Width = r.Width,
            Height = r.Height,
        }).ToList();
        settings.Profiles.Add(profile);

        SettingsStore.Save(settings);
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

        _regionOffsets.Clear();
        _regionOffsets.AddRange(result.Regions);
        _lastKnownClientWidth = clientRect.Width;
        _lastKnownClientHeight = clientRect.Height;

        SaveCurrentSettings();
        RefreshRestoreButton();
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

        if (TryComputeRegions(out var regions, out _))
        {
            _mirrorPreview.UpdateRegions(regions);
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
    /// Recomputes every sub-region's current client-relative geometry from its stored fixed
    /// pixel offset/size and the target's *current* client rect, dropping/clipping safely if
    /// the target has shrunk.
    /// </summary>
    private bool TryComputeRegions(out List<RECT> regions, out bool resized)
    {
        regions = new List<RECT>();
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

        foreach (var offset in _regionOffsets)
        {
            int left = Math.Clamp(offset.Left, 0, clientRect.Width);
            int top = Math.Clamp(offset.Top, 0, clientRect.Height);
            int right = Math.Clamp(offset.Right, 0, clientRect.Width);
            int bottom = Math.Clamp(offset.Bottom, 0, clientRect.Height);

            if (right - left <= 0 || bottom - top <= 0)
            {
                continue;
            }

            regions.Add(new RECT { Left = left, Top = top, Right = right, Bottom = bottom });
        }

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

            if (TryComputeRegions(out var regions, out var resized))
            {
                _mirrorPreview.UpdateRegions(regions);
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
        _regionOffsets.Clear();
        _lastKnownClientWidth = -1;
        _lastKnownClientHeight = -1;
    }
}
