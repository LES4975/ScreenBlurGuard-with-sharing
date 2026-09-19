using System;
using System.Reflection;
using ScreenBlurGuard.Native;

namespace ScreenBlurGuard.Tests;

/// <summary>
/// Regression test for a crash observed in real usage: "Process terminated. A callback was
/// made on a garbage collected delegate of type 'NativeMethods+WinEventDelegate'." Root cause:
/// WINEVENT_OUTOFCONTEXT callbacks are dispatched asynchronously via the message queue, so a
/// callback already queued before Dispose()/unhook can still fire afterward — if nothing but
/// the (now-dropped) owning instance was keeping the delegate alive by then, the GC was free
/// to collect it first. The fix roots every callback delegate in a static list for the
/// process's lifetime; this test exercises that rooting directly, independent of any real
/// SetWinEventHook/native timing (which isn't something a unit test can reliably drive).
/// </summary>
public sealed class WinEventHookServiceTests
{
    [Fact]
    public void CallbackDelegate_StaysAliveAfterOwningInstanceIsDropped()
    {
        var weakCallback = CreateServiceAndGetWeakCallbackReference();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.True(weakCallback.IsAlive, "The WinEventHook callback delegate was garbage " +
            "collected even though a live hook could still receive a queued native callback " +
            "for it — this is exactly the condition that crashed the app in real usage.");
    }

    // Isolated into its own method so the local `service`/`callback` variables actually go
    // out of scope before we force a collection in the caller.
    private static WeakReference CreateServiceAndGetWeakCallbackReference()
    {
        // IntPtr.Zero is fine here — the constructor only stores fields, it doesn't call
        // SetWinEventHook (that happens in Start(), which this test deliberately never calls).
        var service = new WinEventHookService(IntPtr.Zero);

        var callbackField = typeof(WinEventHookService).GetField("_callback", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var callback = callbackField.GetValue(service);

        return new WeakReference(callback);
    }
}
