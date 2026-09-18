using System;

namespace ScreenBlurGuard.Native;

/// <summary>
/// Tracks a single target window's move/resize, minimize/restore, and close events
/// using SetWinEventHook, and raises managed events for the caller to react to.
/// </summary>
public sealed class WinEventHookService : IDisposable
{
    private readonly IntPtr _targetHwnd;
    private readonly NativeMethods.WinEventDelegate _callback;
    private IntPtr _objectHook;
    private IntPtr _minimizeHook;
    private bool _disposed;

    public event Action? LocationOrSizeChanged;
    public event Action<bool>? VisibilityChanged; // true = visible/restored, false = hidden/minimized
    public event Action? TargetDestroyed;

    public WinEventHookService(IntPtr targetHwnd)
    {
        _targetHwnd = targetHwnd;
        // Keep a strong reference to the delegate for the lifetime of the hook —
        // otherwise the GC can collect it while native code still holds the function pointer.
        _callback = OnWinEvent;
    }

    public void Start()
    {
        NativeMethods.GetWindowThreadProcessId(_targetHwnd, out var processId);

        _objectHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_CREATE,
            NativeMethods.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero,
            _callback,
            processId,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);

        // Minimize/restore is reported via EVENT_SYSTEM_MINIMIZESTART/END, NOT EVENT_OBJECT_HIDE/SHOW —
        // needs its own hook since the event ID range isn't contiguous with the object events above.
        _minimizeHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_MINIMIZESTART,
            NativeMethods.EVENT_SYSTEM_MINIMIZEEND,
            IntPtr.Zero,
            _callback,
            processId,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);

        if (_objectHook == IntPtr.Zero || _minimizeHook == IntPtr.Zero)
        {
            throw new InvalidOperationException("SetWinEventHook failed.");
        }
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (hwnd != _targetHwnd || idObject != NativeMethods.OBJID_WINDOW || idChild != NativeMethods.CHILDID_SELF)
        {
            return;
        }

        switch (eventType)
        {
            case NativeMethods.EVENT_OBJECT_LOCATIONCHANGE:
                if (!NativeMethods.IsIconic(_targetHwnd))
                {
                    LocationOrSizeChanged?.Invoke();
                }
                break;

            case NativeMethods.EVENT_OBJECT_SHOW:
                VisibilityChanged?.Invoke(true);
                break;

            case NativeMethods.EVENT_OBJECT_HIDE:
                VisibilityChanged?.Invoke(false);
                break;

            case NativeMethods.EVENT_SYSTEM_MINIMIZESTART:
                VisibilityChanged?.Invoke(false);
                break;

            case NativeMethods.EVENT_SYSTEM_MINIMIZEEND:
                VisibilityChanged?.Invoke(true);
                break;

            case NativeMethods.EVENT_OBJECT_DESTROY:
                TargetDestroyed?.Invoke();
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_objectHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_objectHook);
            _objectHook = IntPtr.Zero;
        }

        if (_minimizeHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_minimizeHook);
            _minimizeHook = IntPtr.Zero;
        }
    }
}
