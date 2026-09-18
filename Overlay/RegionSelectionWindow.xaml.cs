using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ScreenBlurGuard.Native;

namespace ScreenBlurGuard.Overlay;

public sealed class RegionSelectionResult
{
    public required IntPtr TargetHwnd { get; init; }
    public required RECT ClientRect { get; init; }
}

public partial class RegionSelectionWindow : Window
{
    private const int MinSelectionSize = 10;

    private Point? _dragStartScreen;

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
        if (e.Key == Key.Escape)
        {
            Cancelled?.Invoke();
            Close();
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

        int left = (int)Math.Round(Math.Min(startScreen.X, endScreen.X));
        int top = (int)Math.Round(Math.Min(startScreen.Y, endScreen.Y));
        int right = (int)Math.Round(Math.Max(startScreen.X, endScreen.X));
        int bottom = (int)Math.Round(Math.Max(startScreen.Y, endScreen.Y));

        if (right - left < MinSelectionSize || bottom - top < MinSelectionSize)
        {
            // Treat as an accidental click, not a real selection — stay open and let the user retry.
            SelectionRect.Visibility = Visibility.Collapsed;
            return;
        }

        // This selection window itself is full-screen and topmost, so WindowFromPoint would
        // otherwise hit *it* (or close and hit whatever destroys next) instead of the real
        // target window underneath. Hide it first so the point test sees through to the target.
        Hide();

        var centerScreen = new NativeMethods.POINT { X = (left + right) / 2, Y = (top + bottom) / 2 };
        var hitHwnd = NativeMethods.WindowFromPoint(centerScreen);
        if (hitHwnd == IntPtr.Zero)
        {
            Cancelled?.Invoke();
            Close();
            return;
        }

        var targetHwnd = NativeMethods.GetAncestor(hitHwnd, NativeMethods.GA_ROOT);
        if (targetHwnd == IntPtr.Zero)
        {
            targetHwnd = hitHwnd;
        }

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
            Cancelled?.Invoke();
            Close();
            return;
        }

        Completed?.Invoke(new RegionSelectionResult { TargetHwnd = targetHwnd, ClientRect = clampedRect });
        Close();
    }
}
