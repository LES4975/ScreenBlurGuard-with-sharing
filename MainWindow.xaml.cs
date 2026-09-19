using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenBlurGuard.Native;
using ScreenBlurGuard.Overlay;
using ScreenBlurGuard.Settings;

namespace ScreenBlurGuard;

/// <summary>
/// Interaction logic for MainWindow.xaml
///
/// Architecture note (see CLAUDE.md for full history): earlier prototypes tried overlaying
/// blur/mirror windows directly on top of the target app's own window (failed for Discord's
/// "share a specific app window" mode due to cross-process compositing/"airspace" issues),
/// then tried a separate mirror window with a DWM-thumbnail background + a separately-owned
/// blur window on top of it (still failed the same way, since a window-specific capture of
/// the mirror window doesn't include a *separate* owned window sitting on top of it either).
///
/// Current design: ONE self-contained window (<see cref="MirrorPreviewWindow"/>) that
/// periodically captures the target via PrintWindow into a WPF Image, with a blurred copy of
/// that same frame drawn on top of the sensitive sub-regions — no separate HWNDs, no z-order
/// ambiguity, works identically for "entire screen" and "specific window" sharing. The user
/// shares THIS mirror window (not the real app). <see cref="MirrorSession"/> owns that
/// window's lifecycle plus the target-tracking hook; this class only wires UI events to it.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MirrorSession _mirrorSession = new();

    // The saved profile (if any) whose process is currently running — set by RefreshRestoreButton,
    // consumed by OnRestoreSettingsClick. Each target app gets its own profile in the settings
    // file (keyed by process name) so saving one app's regions never overwrites another's.
    private TargetProfile? _matchedProfile;

    public MainWindow()
    {
        InitializeComponent();
        _mirrorSession.TargetResized += OnMirrorTargetResized;
        _mirrorSession.TargetDestroyed += OnMirrorTargetDestroyed;
        _mirrorSession.FrameRendered += frame => SetPreviewFrame(frame);

        // Wired here rather than via XAML Checked="..." so IsChecked="True" on BlurStyleRadio
        // doesn't fire before MosaicStyleRadio (and _mirrorSession) exist yet.
        BlurStyleRadio.Checked += (_, _) => _mirrorSession.SetStyle(BlurStyle.Blur);
        MosaicStyleRadio.Checked += (_, _) => _mirrorSession.SetStyle(BlurStyle.Mosaic);

        RefreshRestoreButton();
    }

    private void OnIntensityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        IntensityValueText.Text = $"{(int)Math.Round(e.NewValue)}%";
        _mirrorSession.SetIntensity(e.NewValue);
    }

    /// <summary>
    /// WPF's stock Slider only "pages" toward a track click by LargeChange; clicking a spot on
    /// the track should instead jump the handle straight there. Left un-handled (and skipped
    /// for clicks on the Thumb itself) so the Track's own logic still runs afterward and
    /// naturally picks up the drag if the user keeps the button held down.
    /// </summary>
    private void OnIntensitySliderPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Thumb)
        {
            return;
        }

        double ratio = Math.Clamp(e.GetPosition(IntensitySlider).X / IntensitySlider.ActualWidth, 0, 1);
        IntensitySlider.Value = IntensitySlider.Minimum + ratio * (IntensitySlider.Maximum - IntensitySlider.Minimum);
    }

    /// <summary>Shows (or, with null, clears back to the placeholder) the live "what you're sharing" thumbnail.</summary>
    private void SetPreviewFrame(BitmapSource? frame)
    {
        PreviewImage.Source = frame;
        PreviewPlaceholderText.Visibility = frame == null ? Visibility.Visible : Visibility.Collapsed;
    }

    private enum StatusLevel { Neutral, Active, Warning, Error }

    private void SetStatus(string text, StatusLevel level = StatusLevel.Neutral)
    {
        StatusText.Text = text;
        (Color background, Color foreground) = level switch
        {
            StatusLevel.Active => (Color.FromRgb(0xE3, 0xF6, 0xE8), Color.FromRgb(0x1E, 0x7B, 0x34)),
            StatusLevel.Warning => (Color.FromRgb(0xFF, 0xF3, 0xCD), Color.FromRgb(0x8A, 0x6D, 0x00)),
            StatusLevel.Error => (Color.FromRgb(0xFC, 0xE4, 0xE4), Color.FromRgb(0xB0, 0x00, 0x20)),
            _ => (Color.FromRgb(0xEF, 0xEF, 0xEF), Color.FromRgb(0x33, 0x33, 0x33)),
        };
        StatusBorder.Background = new SolidColorBrush(background);
        StatusText.Foreground = new SolidColorBrush(foreground);
    }

    /// <summary>
    /// Loads saved profiles and, if any of their target processes is currently running,
    /// enables the "저장된 설정 불러오기" button so the user can skip re-selecting regions by
    /// hand. If more than one saved profile's app happens to be running at once,
    /// <see cref="ProfileMatcher.FindRunningProfile"/> offers the most recently saved one.
    ///
    /// Called not just at startup but also right after a fresh selection is saved — the button
    /// otherwise kept showing whatever was true when the app launched (e.g. still pointing at
    /// Notepad after the user had just re-selected regions on a browser instead), since nothing
    /// re-evaluated it mid-session.
    /// </summary>
    private void RefreshRestoreButton()
    {
        RestoreSettingsButton.IsEnabled = false;
        RestoreSettingsButton.Content = "저장된 설정 불러오기";

        var settings = SettingsStore.Load();
        _matchedProfile = ProfileMatcher.FindRunningProfile(settings,
            processName => ProfileMatcher.FindRunningWindowByProcessName(processName) != IntPtr.Zero);

        if (_matchedProfile == null)
        {
            if (settings is { Profiles.Count: > 0 })
            {
                SetStatus($"저장된 설정이 {settings.Profiles.Count}개 있지만 해당 앱이 실행 중이 아닙니다.");
            }
            return;
        }

        RestoreSettingsButton.IsEnabled = true;
        RestoreSettingsButton.Content = $"저장된 설정 불러오기 ({_matchedProfile.ProcessName}, 영역 {_matchedProfile.Regions.Count}개)";
    }

    private void OnRestoreSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_matchedProfile == null)
        {
            return;
        }

        var hwnd = ProfileMatcher.FindRunningWindowByProcessName(_matchedProfile.ProcessName);
        if (hwnd == IntPtr.Zero)
        {
            SetStatus("저장된 대상 앱을 찾지 못했습니다. 먼저 해당 앱을 실행하세요.", StatusLevel.Error);
            return;
        }

        StartMirroring(hwnd, _matchedProfile.Regions.Select(r => r.ToRect()).ToList());
    }

    /// <summary>
    /// Upserts the current target's profile into the saved settings by process name, leaving
    /// every other saved app's profile untouched — saving a browser's regions must not wipe
    /// out a previously saved Notepad profile, or vice versa. The updated profile is moved to
    /// the END of the list, marking it as the most recently used one (see
    /// <see cref="ProfileMatcher.FindRunningProfile"/>).
    /// </summary>
    private static void SaveSettings(IntPtr targetHwnd, IReadOnlyList<RECT> regions)
    {
        if (NativeMethods.GetWindowThreadProcessId(targetHwnd, out uint pid) == 0 || pid == 0)
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

        profile.Regions = regions.Select(SavedRegion.FromRect).ToList();
        settings.Profiles.Add(profile);

        SettingsStore.Save(settings);
    }

    private void OnSelectRegionClick(object sender, RoutedEventArgs e)
    {
        _mirrorSession.Stop();
        RemoveOverlayButton.IsEnabled = false;
        SetPreviewFrame(null);

        Hide();

        var selectionWindow = new RegionSelectionWindow();
        selectionWindow.Completed += OnSelectionCompleted;
        selectionWindow.Cancelled += OnSelectionCancelled;
        selectionWindow.Closed += (_, _) => Show();
        selectionWindow.Show();
    }

    private void OnSelectionCompleted(RegionSelectionResult result)
    {
        SaveSettings(result.TargetHwnd, result.Regions);
        RefreshRestoreButton();
        StartMirroring(result.TargetHwnd, result.Regions);
    }

    private void OnSelectionCancelled()
    {
        SetStatus("영역 선택이 취소되었습니다.");
    }

    private void StartMirroring(IntPtr targetHwnd, IReadOnlyList<RECT> regions)
    {
        if (!_mirrorSession.TryStart(targetHwnd, regions))
        {
            SetStatus("대상 창의 클라이언트 영역을 가져오지 못했습니다.", StatusLevel.Error);
            return;
        }

        RemoveOverlayButton.IsEnabled = true;
        SetStatus("미러링 창이 생성되었습니다. 화면 공유 시 " +
                  "이 '공유용 미러' 창을 선택해서 공유하세요 (전체 화면 공유도 가능합니다) — " +
                  "실제 작업 앱을 직접 공유하지 마세요, 블러가 적용되지 않습니다.", StatusLevel.Active);
    }

    private void OnMirrorTargetResized()
    {
        SetStatus("⚠ 대상 창 크기가 변경되었습니다. 블러 위치가 실제 콘텐츠와 어긋났을 수 있으니 영역을 다시 선택해 확인하세요.", StatusLevel.Warning);
    }

    private void OnMirrorTargetDestroyed()
    {
        RemoveOverlayButton.IsEnabled = false;
        SetPreviewFrame(null);
        SetStatus("대상 앱이 종료되어 미러링 창을 자동으로 정리했습니다.");
    }

    private void OnRemoveOverlayClick(object sender, RoutedEventArgs e)
    {
        _mirrorSession.Stop();
        RemoveOverlayButton.IsEnabled = false;
        SetPreviewFrame(null);
        SetStatus("미러링 창 제거됨.");
    }
}
