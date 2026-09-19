using System.Collections.Generic;
using ScreenBlurGuard.Settings;

namespace ScreenBlurGuard.Tests;

public sealed class ProfileMatcherTests
{
    private static TargetProfile Profile(string processName) => new()
    {
        ProcessName = processName,
        Regions = { new SavedRegion { Left = 0, Top = 0, Width = 10, Height = 10 } },
    };

    [Fact]
    public void FindRunningProfile_WhenSettingsIsNull_ReturnsNull()
    {
        Assert.Null(ProfileMatcher.FindRunningProfile(null, _ => true));
    }

    [Fact]
    public void FindRunningProfile_WhenNoProfilesSaved_ReturnsNull()
    {
        var settings = new AppSettings();
        Assert.Null(ProfileMatcher.FindRunningProfile(settings, _ => true));
    }

    [Fact]
    public void FindRunningProfile_WhenNoSavedProcessIsRunning_ReturnsNull()
    {
        var settings = new AppSettings { Profiles = { Profile("notepad"), Profile("chrome") } };
        Assert.Null(ProfileMatcher.FindRunningProfile(settings, _ => false));
    }

    [Fact]
    public void FindRunningProfile_WhenOnlyOneSavedProcessIsRunning_ReturnsIt()
    {
        var settings = new AppSettings { Profiles = { Profile("notepad"), Profile("chrome") } };

        var result = ProfileMatcher.FindRunningProfile(settings, name => name == "notepad");

        Assert.Same(settings.Profiles[0], result);
    }

    /// <summary>
    /// This is the exact scenario that caused a real bug: with both "whale" and "notepad"
    /// running, the button must offer whichever was saved most recently (last in the list),
    /// not whichever happens to be listed first.
    /// </summary>
    [Fact]
    public void FindRunningProfile_WhenMultipleSavedProcessesAreRunning_PrefersMostRecentlySaved()
    {
        var settings = new AppSettings { Profiles = { Profile("whale"), Profile("notepad") } };

        var result = ProfileMatcher.FindRunningProfile(settings, _ => true);

        Assert.Same(settings.Profiles[1], result); // "notepad" — last in the list, most recently saved
    }

    [Fact]
    public void FindRunningProfile_IgnoresProfilesWithNoSavedRegions()
    {
        var emptyProfile = new TargetProfile { ProcessName = "notepad", Regions = new List<SavedRegion>() };
        var settings = new AppSettings { Profiles = { emptyProfile } };

        Assert.Null(ProfileMatcher.FindRunningProfile(settings, _ => true));
    }
}
