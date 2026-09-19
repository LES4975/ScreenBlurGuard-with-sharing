using System.Collections.Generic;

namespace ScreenBlurGuard.Settings;

public sealed class SavedRegion
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>
/// Persisted across app restarts as JSON. The target window itself can't be persisted (its
/// HWND is only valid for the process's lifetime), so it's re-identified on the next launch
/// by matching a currently running process with the same executable name.
/// </summary>
public sealed class AppSettings
{
    public string? TargetProcessName { get; set; }
    public List<SavedRegion> Regions { get; set; } = new();
}
