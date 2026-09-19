using System.Collections.Generic;
using ScreenBlurGuard.Native;

namespace ScreenBlurGuard.Settings;

public sealed class SavedRegion
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public static SavedRegion FromRect(RECT rect) => new()
    {
        Left = rect.Left,
        Top = rect.Top,
        Width = rect.Width,
        Height = rect.Height,
    };

    public RECT ToRect() => RECT.FromLeftTopWidthHeight(Left, Top, Width, Height);
}

/// <summary>
/// One target app's saved region set. The target window itself can't be persisted (its HWND
/// is only valid for the process's lifetime), so it's re-identified on the next launch by
/// matching a currently running process with the same executable name.
/// </summary>
public sealed class TargetProfile
{
    public string ProcessName { get; set; } = "";
    public List<SavedRegion> Regions { get; set; } = new();
}

/// <summary>
/// Persisted across app restarts as JSON. Keeps one profile per target process name — saving
/// regions for a new target (e.g. a browser) must not wipe out a previously saved profile for
/// a different one (e.g. Notepad); each is looked up independently by process name.
/// </summary>
public sealed class AppSettings
{
    public List<TargetProfile> Profiles { get; set; } = new();
}
