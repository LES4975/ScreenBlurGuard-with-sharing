using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ScreenBlurGuard.Native;

namespace ScreenBlurGuard.Overlay;

public sealed class RegionSelectionResult
{
    public required IntPtr TargetHwnd { get; init; }
    public required IReadOnlyList<RECT> Regions { get; init; }
}

/// <summary>
/// Full-screen drag-to-select overlay. The FIRST drag identifies the target app (by
/// hit-testing the point under it) and locks it; every subsequent drag adds another blur
/// region within that same locked target, since the app only ever targets one app window at
/// a time but supports multiple regions inside it. Enter finishes, Backspace undoes the last
/// region, Escape cancels the whole selection. Every confirmed region shows 8 drag handles
/// (see <see cref="HandleDirection"/>) so it can be resized in place, and dragging its
/// interior moves it. Backspace undoes only the most recently added region; right-clicking
/// any region deletes exactly that one regardless of add order.
/// </summary>
public partial class RegionSelectionWindow : Window
{
    private const int MinSelectionSize = 10;
    private const double HandleSize = 8;

    internal enum HandleDirection { NW, N, NE, W, E, SW, S, SE }

    /// <summary>One confirmed region: its target-client-space rect plus its on-screen visuals.</summary>
    private sealed class ConfirmedRegion
    {
        public required RECT ClientRect { get; set; }
        public required Rectangle Body { get; init; }
        public required Dictionary<HandleDirection, Rectangle> Handles { get; init; }
    }

    private Point? _dragStartScreen;
    private IntPtr? _lockedTargetHwnd;
    private readonly List<ConfirmedRegion> _confirmedRegions = new();

    private ConfirmedRegion? _resizingRegion;
    private HandleDirection _resizingHandle;
    private Rect _resizeStartLocalRect;

    private ConfirmedRegion? _movingRegion;
    private Point _moveStartMouseLocal;
    private Rect _moveStartLocalRect;

    public event Action<RegionSelectionResult>? Completed;
    public event Action? Cancelled;

    public RegionSelectionWindow()
    {
        InitializeComponent();

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseRightButtonDown += OnMouseRightButtonDown;
        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Cancelled?.Invoke();
                Close();
                break;
            case Key.Enter:
                FinishSelection();
                break;
            case Key.Back when _confirmedRegions.Count > 0:
                RemoveLastRegion();
                break;
        }
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var localPos = e.GetPosition(this);

        if (TryHitTestHandle(localPos, out var region, out var handle))
        {
            _resizingRegion = region;
            _resizingHandle = handle;
            _resizeStartLocalRect = new Rect(
                Canvas.GetLeft(region.Body), Canvas.GetTop(region.Body), region.Body.Width, region.Body.Height);
            CaptureMouse();
            return;
        }

        // A drag starting *inside* an already-confirmed region moves it instead of starting a
        // brand new region on top of it — accidentally drawing a stray extra region when aiming
        // for a resize handle (and missing) was a reported annoyance.
        if (TryHitTestBody(localPos, out var bodyRegion))
        {
            _movingRegion = bodyRegion;
            _moveStartMouseLocal = localPos;
            _moveStartLocalRect = new Rect(
                Canvas.GetLeft(bodyRegion.Body), Canvas.GetTop(bodyRegion.Body), bodyRegion.Body.Width, bodyRegion.Body.Height);
            CaptureMouse();
            return;
        }

        _dragStartScreen = PointToScreen(localPos);
        SelectionRect.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelectionRect, localPos.X);
        Canvas.SetTop(SelectionRect, localPos.Y);
        SelectionRect.Width = 0;
        SelectionRect.Height = 0;
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var localPos = e.GetPosition(this);

        if (_resizingRegion != null)
        {
            var newRect = ComputeResizedRect(_resizeStartLocalRect, _resizingHandle, localPos, MinSelectionSize);
            PositionRegionVisuals(_resizingRegion, newRect);
            return;
        }

        if (_movingRegion != null)
        {
            var newRect = ComputeMovedRect(_moveStartLocalRect, _moveStartMouseLocal, localPos);
            PositionRegionVisuals(_movingRegion, newRect);
            return;
        }

        if (_dragStartScreen == null)
        {
            UpdateHoverCursor(localPos);
            return;
        }

        var startLocal = PointFromScreen(_dragStartScreen.Value);

        double left = Math.Min(startLocal.X, localPos.X);
        double top = Math.Min(startLocal.Y, localPos.Y);
        double width = Math.Abs(localPos.X - startLocal.X);
        double height = Math.Abs(localPos.Y - startLocal.Y);

        Canvas.SetLeft(SelectionRect, left);
        Canvas.SetTop(SelectionRect, top);
        SelectionRect.Width = width;
        SelectionRect.Height = height;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_resizingRegion != null)
        {
            ReleaseMouseCapture();
            FinishRegionEdit(_resizingRegion);
            _resizingRegion = null;
            return;
        }

        if (_movingRegion != null)
        {
            ReleaseMouseCapture();
            FinishRegionEdit(_movingRegion);
            _movingRegion = null;
            return;
        }

        if (_dragStartScreen == null) return;
        ReleaseMouseCapture();

        var endScreen = PointToScreen(e.GetPosition(this));
        var startScreen = _dragStartScreen.Value;
        _dragStartScreen = null;
        SelectionRect.Visibility = Visibility.Collapsed;

        int left = (int)Math.Round(Math.Min(startScreen.X, endScreen.X));
        int top = (int)Math.Round(Math.Min(startScreen.Y, endScreen.Y));
        int right = (int)Math.Round(Math.Max(startScreen.X, endScreen.X));
        int bottom = (int)Math.Round(Math.Max(startScreen.Y, endScreen.Y));

        if (right - left < MinSelectionSize || bottom - top < MinSelectionSize)
        {
            // Treat as an accidental click, not a real selection — keep the session open.
            return;
        }

        if (_lockedTargetHwnd == null && !TryLockTarget(left, top, right, bottom))
        {
            Cancelled?.Invoke();
            Close();
            return;
        }

        TryAddRegion(left, top, right, bottom);
        UpdateInstructions();
    }

    /// <summary>Right-clicking a confirmed region deletes just that one — an alternative to Backspace, which only undoes the last.</summary>
    private void OnMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_resizingRegion != null || _movingRegion != null || _dragStartScreen != null)
        {
            return; // mid-drag with the left button; ignore a stray right-click
        }

        if (TryHitTestBody(e.GetPosition(this), out var region))
        {
            RemoveRegion(region);
            UpdateInstructions();
        }
    }

    /// <summary>
    /// This selection window itself is full-screen and topmost, so WindowFromPoint would
    /// otherwise hit *it* instead of the real target window underneath. Hide it just for the
    /// point test, then show it again so further drags can add more regions.
    /// </summary>
    private bool TryLockTarget(int left, int top, int right, int bottom)
    {
        Hide();

        var centerScreen = new NativeMethods.POINT { X = (left + right) / 2, Y = (top + bottom) / 2 };
        var hitHwnd = NativeMethods.WindowFromPoint(centerScreen);
        if (hitHwnd == IntPtr.Zero)
        {
            return false;
        }

        var targetHwnd = NativeMethods.GetAncestor(hitHwnd, NativeMethods.GA_ROOT);
        if (targetHwnd == IntPtr.Zero)
        {
            targetHwnd = hitHwnd;
        }

        _lockedTargetHwnd = targetHwnd;

        Show();
        Activate();
        return true;
    }

    private void TryAddRegion(int left, int top, int right, int bottom)
    {
        var clampedRect = ScreenRectToClampedClientRect(left, top, right, bottom);
        if (clampedRect is not { } rect || rect.Width < MinSelectionSize || rect.Height < MinSelectionSize)
        {
            // Dragged mostly/entirely outside the locked target's current client area.
            return;
        }

        _confirmedRegions.Add(AddConfirmedRegion(rect));
    }

    /// <summary>Converts a screen-space rect to the locked target's client space, clamped to its current client rect.</summary>
    private RECT? ScreenRectToClampedClientRect(int left, int top, int right, int bottom)
    {
        var targetHwnd = _lockedTargetHwnd!.Value;

        var topLeftClient = new NativeMethods.POINT { X = left, Y = top };
        var bottomRightClient = new NativeMethods.POINT { X = right, Y = bottom };
        NativeMethods.ScreenToClient(targetHwnd, ref topLeftClient);
        NativeMethods.ScreenToClient(targetHwnd, ref bottomRightClient);

        if (!NativeMethods.GetClientRect(targetHwnd, out var clientRect))
        {
            return null;
        }

        return new RECT
        {
            Left = Math.Clamp(topLeftClient.X, 0, clientRect.Width),
            Top = Math.Clamp(topLeftClient.Y, 0, clientRect.Height),
            Right = Math.Clamp(bottomRightClient.X, 0, clientRect.Width),
            Bottom = Math.Clamp(bottomRightClient.Y, 0, clientRect.Height),
        };
    }

    private ConfirmedRegion AddConfirmedRegion(RECT clientRect)
    {
        var localRect = ClientRectToLocalRect(clientRect);

        var body = new Rectangle
        {
            Stroke = Brushes.LimeGreen,
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(0x40, 0x3A, 0xFF, 0x6B)),
        };
        SelectionCanvas.Children.Add(body);

        var handles = Enum.GetValues<HandleDirection>().ToDictionary(h => h, CreateHandle);
        foreach (var handle in handles.Values)
        {
            SelectionCanvas.Children.Add(handle);
        }

        var region = new ConfirmedRegion { ClientRect = clientRect, Body = body, Handles = handles };
        PositionRegionVisuals(region, localRect);
        return region;
    }

    private Rectangle CreateHandle(HandleDirection direction) => new()
    {
        Width = HandleSize,
        Height = HandleSize,
        Fill = Brushes.White,
        Stroke = Brushes.LimeGreen,
        StrokeThickness = 1.5,
        Cursor = direction switch
        {
            HandleDirection.NW or HandleDirection.SE => Cursors.SizeNWSE,
            HandleDirection.NE or HandleDirection.SW => Cursors.SizeNESW,
            HandleDirection.N or HandleDirection.S => Cursors.SizeNS,
            _ => Cursors.SizeWE,
        },
    };

    /// <summary>Repositions a region's body and all 8 handles to match <paramref name="localRect"/> (window-local coords).</summary>
    private void PositionRegionVisuals(ConfirmedRegion region, Rect localRect)
    {
        Canvas.SetLeft(region.Body, localRect.Left);
        Canvas.SetTop(region.Body, localRect.Top);
        region.Body.Width = localRect.Width;
        region.Body.Height = localRect.Height;

        double midX = localRect.Left + localRect.Width / 2;
        double midY = localRect.Top + localRect.Height / 2;
        var centers = new Dictionary<HandleDirection, Point>
        {
            [HandleDirection.NW] = new(localRect.Left, localRect.Top),
            [HandleDirection.N] = new(midX, localRect.Top),
            [HandleDirection.NE] = new(localRect.Right, localRect.Top),
            [HandleDirection.W] = new(localRect.Left, midY),
            [HandleDirection.E] = new(localRect.Right, midY),
            [HandleDirection.SW] = new(localRect.Left, localRect.Bottom),
            [HandleDirection.S] = new(midX, localRect.Bottom),
            [HandleDirection.SE] = new(localRect.Right, localRect.Bottom),
        };

        foreach (var (direction, handle) in region.Handles)
        {
            var center = centers[direction];
            Canvas.SetLeft(handle, center.X - HandleSize / 2);
            Canvas.SetTop(handle, center.Y - HandleSize / 2);
        }
    }

    private bool TryHitTestHandle(Point localPos, out ConfirmedRegion region, out HandleDirection direction)
    {
        foreach (var candidate in _confirmedRegions)
        {
            foreach (var (dir, handle) in candidate.Handles)
            {
                var rect = new Rect(Canvas.GetLeft(handle), Canvas.GetTop(handle), HandleSize, HandleSize);
                if (rect.Contains(localPos))
                {
                    region = candidate;
                    direction = dir;
                    return true;
                }
            }
        }

        region = null!;
        direction = default;
        return false;
    }

    /// <summary>Hit-tests a confirmed region's body (not its handles), topmost/most-recently-added first.</summary>
    private bool TryHitTestBody(Point localPos, out ConfirmedRegion region)
    {
        for (int i = _confirmedRegions.Count - 1; i >= 0; i--)
        {
            var candidate = _confirmedRegions[i];
            var rect = new Rect(
                Canvas.GetLeft(candidate.Body), Canvas.GetTop(candidate.Body),
                candidate.Body.Width, candidate.Body.Height);
            if (rect.Contains(localPos))
            {
                region = candidate;
                return true;
            }
        }

        region = null!;
        return false;
    }

    private void UpdateHoverCursor(Point localPos)
    {
        if (TryHitTestHandle(localPos, out _, out _))
        {
            Cursor = Cursors.Arrow; // the actual directional resize cursor comes from the handle element itself
        }
        else if (TryHitTestBody(localPos, out _))
        {
            Cursor = Cursors.SizeAll;
        }
        else
        {
            Cursor = Cursors.Cross;
        }
    }

    /// <summary>Computes the new local rect as a resize handle is dragged, anchored at the opposite side/corner.</summary>
    internal static Rect ComputeResizedRect(Rect startRect, HandleDirection handle, Point mouseLocal, double minSize)
    {
        double left = startRect.Left, top = startRect.Top, right = startRect.Right, bottom = startRect.Bottom;

        bool movesLeft = handle is HandleDirection.NW or HandleDirection.W or HandleDirection.SW;
        bool movesRight = handle is HandleDirection.NE or HandleDirection.E or HandleDirection.SE;
        bool movesTop = handle is HandleDirection.NW or HandleDirection.N or HandleDirection.NE;
        bool movesBottom = handle is HandleDirection.SW or HandleDirection.S or HandleDirection.SE;

        if (movesLeft) left = Math.Min(mouseLocal.X, right - minSize);
        if (movesRight) right = Math.Max(mouseLocal.X, left + minSize);
        if (movesTop) top = Math.Min(mouseLocal.Y, bottom - minSize);
        if (movesBottom) bottom = Math.Max(mouseLocal.Y, top + minSize);

        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>Offsets a region's start-of-drag rect by however far the mouse has moved since — size never changes.</summary>
    internal static Rect ComputeMovedRect(Rect startRect, Point startMouseLocal, Point currentMouseLocal)
    {
        var delta = currentMouseLocal - startMouseLocal;
        return new Rect(startRect.Left + delta.X, startRect.Top + delta.Y, startRect.Width, startRect.Height);
    }

    /// <summary>
    /// Finalizes either a resize or a move: reads the region's current (already live-updated)
    /// local rect off its Body element, converts it to the target's client space, clamps it,
    /// and stores it — both edit kinds end up needing exactly this same conversion.
    /// </summary>
    private void FinishRegionEdit(ConfirmedRegion region)
    {
        var localRect = new Rect(
            Canvas.GetLeft(region.Body), Canvas.GetTop(region.Body), region.Body.Width, region.Body.Height);

        var topLeftScreen = PointToScreen(new Point(localRect.Left, localRect.Top));
        var bottomRightScreen = PointToScreen(new Point(localRect.Right, localRect.Bottom));

        var clampedRect = ScreenRectToClampedClientRect(
            (int)Math.Round(topLeftScreen.X), (int)Math.Round(topLeftScreen.Y),
            (int)Math.Round(bottomRightScreen.X), (int)Math.Round(bottomRightScreen.Y));

        if (clampedRect is not { } rect || rect.Width < MinSelectionSize || rect.Height < MinSelectionSize)
        {
            // The target's client area changed mid-drag and no longer fits this resize —
            // revert to the region's last known-good rect rather than leaving it broken.
            PositionRegionVisuals(region, ClientRectToLocalRect(region.ClientRect));
            return;
        }

        region.ClientRect = rect;
        PositionRegionVisuals(region, ClientRectToLocalRect(rect));
    }

    private Rect ClientRectToLocalRect(RECT clientRect)
    {
        var targetHwnd = _lockedTargetHwnd!.Value;

        var topLeftClient = new NativeMethods.POINT { X = clientRect.Left, Y = clientRect.Top };
        var bottomRightClient = new NativeMethods.POINT { X = clientRect.Right, Y = clientRect.Bottom };
        NativeMethods.ClientToScreen(targetHwnd, ref topLeftClient);
        NativeMethods.ClientToScreen(targetHwnd, ref bottomRightClient);

        var topLeftLocal = PointFromScreen(new Point(topLeftClient.X, topLeftClient.Y));
        var bottomRightLocal = PointFromScreen(new Point(bottomRightClient.X, bottomRightClient.Y));
        return new Rect(topLeftLocal, bottomRightLocal);
    }

    private void RemoveLastRegion()
    {
        RemoveRegion(_confirmedRegions[^1]);
        UpdateInstructions();
    }

    /// <summary>Removes one specific region (wherever it is in the list) and its visuals.</summary>
    private void RemoveRegion(ConfirmedRegion region)
    {
        _confirmedRegions.Remove(region);

        SelectionCanvas.Children.Remove(region.Body);
        foreach (var handle in region.Handles.Values)
        {
            SelectionCanvas.Children.Remove(handle);
        }
    }

    private void FinishSelection()
    {
        if (_lockedTargetHwnd == null || _confirmedRegions.Count == 0)
        {
            return;
        }

        Completed?.Invoke(new RegionSelectionResult
        {
            TargetHwnd = _lockedTargetHwnd.Value,
            Regions = _confirmedRegions.Select(r => r.ClientRect).ToList(),
        });
        Close();
    }

    private void UpdateInstructions()
    {
        InstructionsText.Text = _confirmedRegions.Count == 0
            ? "드래그해서 블러 영역을 선택하세요. (Esc: 취소)"
            : $"블러 영역 {_confirmedRegions.Count}개 선택됨 — 손잡이를 드래그해 크기 조절, 영역 안쪽을 드래그해 이동, " +
              "우클릭으로 해당 영역 삭제, 빈 곳을 드래그해서 추가, Enter로 완료, Backspace로 마지막 취소, Esc로 전체 취소하세요.";
    }
}
