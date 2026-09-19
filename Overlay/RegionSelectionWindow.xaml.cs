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
/// region, Escape cancels the whole selection.
/// </summary>
public partial class RegionSelectionWindow : Window
{
    private const int MinSelectionSize = 10;

    private Point? _dragStartScreen;
    private IntPtr? _lockedTargetHwnd;
    private readonly List<RECT> _confirmedRegions = new();
    private readonly List<Rectangle> _confirmedVisuals = new();

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
        _dragStartScreen = PointToScreen(e.GetPosition(this));
        SelectionRect.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelectionRect, e.GetPosition(this).X);
        Canvas.SetTop(SelectionRect, e.GetPosition(this).Y);
        SelectionRect.Width = 0;
        SelectionRect.Height = 0;
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStartScreen == null) return;

        var startLocal = this.PointFromScreen(_dragStartScreen.Value);
        var currentLocal = e.GetPosition(this);

        double left = Math.Min(startLocal.X, currentLocal.X);
        double top = Math.Min(startLocal.Y, currentLocal.Y);
        double width = Math.Abs(currentLocal.X - startLocal.X);
        double height = Math.Abs(currentLocal.Y - startLocal.Y);

        Canvas.SetLeft(SelectionRect, left);
        Canvas.SetTop(SelectionRect, top);
        SelectionRect.Width = width;
        SelectionRect.Height = height;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
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
        var targetHwnd = _lockedTargetHwnd!.Value;

        var topLeftClient = new NativeMethods.POINT { X = left, Y = top };
        var bottomRightClient = new NativeMethods.POINT { X = right, Y = bottom };
        NativeMethods.ScreenToClient(targetHwnd, ref topLeftClient);
        NativeMethods.ScreenToClient(targetHwnd, ref bottomRightClient);

        NativeMethods.GetClientRect(targetHwnd, out var clientRect);

        var clampedRect = new RECT
        {
            Left = Math.Clamp(topLeftClient.X, 0, clientRect.Width),
            Top = Math.Clamp(topLeftClient.Y, 0, clientRect.Height),
            Right = Math.Clamp(bottomRightClient.X, 0, clientRect.Width),
            Bottom = Math.Clamp(bottomRightClient.Y, 0, clientRect.Height),
        };

        if (clampedRect.Width < MinSelectionSize || clampedRect.Height < MinSelectionSize)
        {
            // Dragged mostly/entirely outside the locked target's current client area.
            return;
        }

        _confirmedRegions.Add(clampedRect);
        _confirmedVisuals.Add(AddConfirmedVisual(left, top, right, bottom));
    }

    private Rectangle AddConfirmedVisual(int left, int top, int right, int bottom)
    {
        var topLeftLocal = PointFromScreen(new Point(left, top));
        var bottomRightLocal = PointFromScreen(new Point(right, bottom));

        var visual = new Rectangle
        {
            Stroke = Brushes.LimeGreen,
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(0x40, 0x3A, 0xFF, 0x6B)),
            Width = bottomRightLocal.X - topLeftLocal.X,
            Height = bottomRightLocal.Y - topLeftLocal.Y,
        };
        Canvas.SetLeft(visual, topLeftLocal.X);
        Canvas.SetTop(visual, topLeftLocal.Y);

        SelectionCanvas.Children.Add(visual);
        return visual;
    }

    private void RemoveLastRegion()
    {
        int lastIndex = _confirmedRegions.Count - 1;
        _confirmedRegions.RemoveAt(lastIndex);

        SelectionCanvas.Children.Remove(_confirmedVisuals[lastIndex]);
        _confirmedVisuals.RemoveAt(lastIndex);

        UpdateInstructions();
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
            Regions = _confirmedRegions.ToList(),
        });
        Close();
    }

    private void UpdateInstructions()
    {
        InstructionsText.Text = _confirmedRegions.Count == 0
            ? "드래그해서 블러 영역을 선택하세요. (Esc: 취소)"
            : $"블러 영역 {_confirmedRegions.Count}개 선택됨 — 계속 드래그해서 추가하거나 " +
              "Enter로 완료, Backspace로 마지막 취소, Esc로 전체 취소하세요.";
    }
}
