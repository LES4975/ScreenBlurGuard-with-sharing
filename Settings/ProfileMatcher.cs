using System;
using System.Diagnostics;
using System.Linq;

namespace ScreenBlurGuard.Settings;

public static class ProfileMatcher
{
    /// <summary>
    /// Picks which saved profile the "저장된 설정 불러오기" button should offer: the
    /// most-recently-saved profile (see <see cref="AppSettings.Profiles"/> ordering —
    /// re-saving a profile moves it to the end of the list) whose target process is currently
    /// running. Searching from the end means that if several saved profiles' apps happen to be
    /// running at once, whichever was touched most recently wins over one just saved earlier.
    ///
    /// <paramref name="isProcessRunning"/> is injected so this selection logic can be unit
    /// tested without needing real OS processes.
    /// </summary>
    public static TargetProfile? FindRunningProfile(AppSettings? settings, Func<string, bool> isProcessRunning)
    {
        if (settings is not { Profiles.Count: > 0 })
        {
            return null;
        }

        return settings.Profiles.AsEnumerable().Reverse().FirstOrDefault(p =>
            p.Regions.Count > 0 && isProcessRunning(p.ProcessName));
    }

    /// <summary>Finds a visible top-level window belonging to a running process with this name, if any.</summary>
    public static IntPtr FindRunningWindowByProcessName(string processName)
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
}
