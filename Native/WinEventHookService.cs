using System;
using System.Collections.Generic;

namespace ScreenBlurGuard.Native;

/// <summary>
/// Tracks a single target window's move/resize, minimize/restore, and close events
/// using SetWinEventHook, and raises managed events for the caller to react to.
/// </summary>
public sealed class WinEventHookService : IDisposable
{
    // WINEVENT_OUTOFCONTEXT callbacks are dispatched asynchronously through the hooking
    // thread's message queue. Calling UnhookWinEvent (e.g. from Dispose, itself often
    // triggered from inside a callback reacting to EVENT_OBJECT_DESTROY) only stops *future*
    // hook registrations from firing — it does not cancel a callback that was already queued
    // before the unhook. If the instance's own field holding the delegate (`_callback`) was
    // by then the delegate's only remaining root and got nulled out (e.g. the owner's
    // reference was cleared right after Dispose), the GC is free to collect it, and that
    // already-queued callback then crashes the process with "callback was made on a garbage
    // collected delegate". Rooting every callback delegate here for the process's lifetime
    // closes that race; instances are created rarely (once per mirror session), so never
    // releasing this list is a negligible, acceptable cost.
    private static readonly List<NativeMethods.WinEventDelegate> RootedCallbacks = new();

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
        _callback = OnWinEvent;
        RootedCallbacks.Add(_callback);
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
