using System;
using System.Collections.Generic;
using System.Windows;
using ScreenBlurGuard.Native;

namespace ScreenBlurGuard.Overlay;

/// <summary>
/// Owns the full lifecycle of one "mirroring session": the <see cref="MirrorPreviewWindow"/>,
/// the <see cref="WinEventHookService"/> tracking the target, and the fixed-pixel-offset
/// region list — extracted out of <c>MainWindow</c> so that class only has to wire UI events
/// and react to <see cref="TargetResized"/>/<see cref="TargetDestroyed"/>, not manage this
/// state itself.
/// </summary>
public sealed class MirrorSession : IDisposable
{
    // The selected sub-regions, each stored as a FIXED pixel offset + size (as a RECT: Left/Top
    // is the offset, Width/Height is the fixed size) from the target window's client-area
    // origin (see CLAUDE.md for why not a proportional fraction). Clipped safely on shrink;
    // a resize is flagged rather than silently trusted.
    private readonly List<RECT> _regionOffsets = new();

    private MirrorPreviewWindow? _mirrorPreview;
    private WinEventHookService? _tracker;
    private int _lastKnownClientWidth = -1;
    private int _lastKnownClientHeight = -1;

    // Persist across Stop()/TryStart() cycles within the same session object, so picking
    // mosaic (or a given intensity) once keeps it selected for the next mirror too, not just
    // the current one. Not saved to disk — resets to defaults on app restart, by design.
    private BlurStyle _style = BlurStyle.Blur;
    private double _intensityPercent = 25;

    public IntPtr TargetHwnd { get; private set; }
    public bool IsActive => _mirrorPreview != null;

    /// <summary>Raised when the target's size changed and blur positions may now be stale.</summary>
    public event Action? TargetResized;

    /// <summary>Raised after the target app closed and this session tore itself down.</summary>
    public event Action? TargetDestroyed;

    /// <summary>
    /// Starts mirroring <paramref name="targetHwnd"/> with the given fixed pixel-offset
    /// regions, tearing down any previous session first. Returns false (leaving no session
    /// active) if the target's client rect can't be read.
    /// </summary>
    public bool TryStart(IntPtr targetHwnd, IReadOnlyList<RECT> regionOffsets)
    {
        Stop();

        if (!NativeMethods.TryGetValidClientRect(targetHwnd, out var clientRect))
        {
            return false;
        }

        TargetHwnd = targetHwnd;
        _regionOffsets.AddRange(regionOffsets);
        _lastKnownClientWidth = clientRect.Width;
        _lastKnownClientHeight = clientRect.Height;

        _mirrorPreview = new MirrorPreviewWindow(targetHwnd, clientRect.Width, clientRect.Height)
        {
            Left = 100,
            Top = 100,
        };
        _mirrorPreview.SetStyle(_style);
        _mirrorPreview.SetIntensity(_intensityPercent);
        _mirrorPreview.Show();

        if (TryComputeRegions(out var regions, out _))
        {
            _mirrorPreview.UpdateRegions(regions);
        }

        _tracker = new WinEventHookService(targetHwnd);
        _tracker.LocationOrSizeChanged += () => Application.Current.Dispatcher.Invoke(OnTargetLocationOrSizeChanged);
        _tracker.TargetDestroyed += () => Application.Current.Dispatcher.Invoke(() =>
        {
            Stop();
            TargetDestroyed?.Invoke();
        });
        _tracker.Start();

        return true;
    }

    /// <summary>Switches the effect for the active mirror (if any) immediately, and for the next one started.</summary>
    public void SetStyle(BlurStyle style)
    {
        _style = style;
        _mirrorPreview?.SetStyle(style);
    }

    /// <summary>Sets the effect's strength (0–100) for the active mirror (if any) immediately, and for the next one started.</summary>
    public void SetIntensity(double intensityPercent)
    {
        _intensityPercent = intensityPercent;
        _mirrorPreview?.SetIntensity(intensityPercent);
    }

    public void Stop()
    {
        _tracker?.Dispose();
        _tracker = null;

        _mirrorPreview?.Close();
        _mirrorPreview = null;

        TargetHwnd = IntPtr.Zero;
        _regionOffsets.Clear();
        _lastKnownClientWidth = -1;
        _lastKnownClientHeight = -1;
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

        if (!NativeMethods.TryGetValidClientRect(TargetHwnd, out var clientRect))
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
        if (_mirrorPreview == null) return;
        if (!NativeMethods.IsWindow(TargetHwnd)) return;
        if (!NativeMethods.GetClientRect(TargetHwnd, out var clientRect)) return;

        _mirrorPreview.ResizeCapture(clientRect.Width, clientRect.Height);

        if (TryComputeRegions(out var regions, out var resized))
        {
            _mirrorPreview.UpdateRegions(regions);
        }

        if (resized)
        {
            TargetResized?.Invoke();
        }
    }

    public void Dispose() => Stop();
}
