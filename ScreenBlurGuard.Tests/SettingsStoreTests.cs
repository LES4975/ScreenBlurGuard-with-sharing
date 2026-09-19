using System;
using System.IO;
using System.Linq;
using ScreenBlurGuard.Settings;

namespace ScreenBlurGuard.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _tempFilePath = Path.Combine(Path.GetTempPath(), $"sbg_settings_test_{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_tempFilePath))
        {
            File.Delete(_tempFilePath);
        }
    }

    [Fact]
    public void Load_WhenFileDoesNotExist_ReturnsNull()
    {
        Assert.Null(SettingsStore.Load(_tempFilePath));
    }

    [Fact]
    public void SaveThenLoad_RoundTripsProcessNameAndRegionsAcrossMultipleProfiles()
    {
        var original = new AppSettings
        {
            Profiles =
            {
                new TargetProfile
                {
                    ProcessName = "notepad",
                    Regions =
                    {
                        new SavedRegion { Left = 10, Top = 20, Width = 100, Height = 50 },
                        new SavedRegion { Left = 200, Top = 5, Width = 30, Height = 30 },
                    },
                },
                new TargetProfile
                {
                    ProcessName = "chrome",
                    Regions = { new SavedRegion { Left = 1, Top = 2, Width = 3, Height = 4 } },
                },
            },
        };

        SettingsStore.Save(original, _tempFilePath);
        var loaded = SettingsStore.Load(_tempFilePath);

        Assert.NotNull(loaded);
        Assert.Equal(original.Profiles.Count, loaded!.Profiles.Count);
        for (int i = 0; i < original.Profiles.Count; i++)
        {
            Assert.Equal(original.Profiles[i].ProcessName, loaded.Profiles[i].ProcessName);
            Assert.Equal(original.Profiles[i].Regions.Count, loaded.Profiles[i].Regions.Count);
            for (int j = 0; j < original.Profiles[i].Regions.Count; j++)
            {
                Assert.Equal(original.Profiles[i].Regions[j].Left, loaded.Profiles[i].Regions[j].Left);
                Assert.Equal(original.Profiles[i].Regions[j].Top, loaded.Profiles[i].Regions[j].Top);
                Assert.Equal(original.Profiles[i].Regions[j].Width, loaded.Profiles[i].Regions[j].Width);
                Assert.Equal(original.Profiles[i].Regions[j].Height, loaded.Profiles[i].Regions[j].Height);
            }
        }
    }

    /// <summary>
    /// Regression test for a real reported bug: saving regions for a new target app (e.g. a
    /// browser) used to overwrite the single, global saved settings — losing a previously
    /// saved profile for a different app (e.g. Notepad) entirely. This exercises the same
    /// load-upsert-save pattern <c>MainWindow.SaveCurrentSettings</c> uses, at the persistence
    /// layer, independent of any WPF window.
    /// </summary>
    [Fact]
    public void SavingProfileForSecondProcess_DoesNotDropFirstProcessProfile()
    {
        UpsertProfile("notepad", new SavedRegion { Left = 1, Top = 1, Width = 10, Height = 10 });
        UpsertProfile("chrome", new SavedRegion { Left = 2, Top = 2, Width = 20, Height = 20 });

        var loaded = SettingsStore.Load(_tempFilePath)!;

        Assert.Equal(2, loaded.Profiles.Count);
        var notepad = loaded.Profiles.Single(p => p.ProcessName == "notepad");
        var chrome = loaded.Profiles.Single(p => p.ProcessName == "chrome");
        Assert.Single(notepad.Regions);
        Assert.Single(chrome.Regions);
        Assert.Equal(10, notepad.Regions[0].Width);
        Assert.Equal(20, chrome.Regions[0].Width);
    }

    [Fact]
    public void SavingProfileAgainForSameProcess_ReplacesOnlyThatProfilesRegions()
    {
        UpsertProfile("notepad", new SavedRegion { Left = 1, Top = 1, Width = 10, Height = 10 });
        UpsertProfile("chrome", new SavedRegion { Left = 2, Top = 2, Width = 20, Height = 20 });
        UpsertProfile("notepad", new SavedRegion { Left = 9, Top = 9, Width = 99, Height = 99 });

        var loaded = SettingsStore.Load(_tempFilePath)!;

        Assert.Equal(2, loaded.Profiles.Count);
        var notepad = loaded.Profiles.Single(p => p.ProcessName == "notepad");
        var chrome = loaded.Profiles.Single(p => p.ProcessName == "chrome");
        Assert.Equal(99, notepad.Regions.Single().Width);
        Assert.Equal(20, chrome.Regions.Single().Width); // untouched by the notepad re-save
    }

    [Fact]
    public void Load_WhenFileContainsInvalidJson_ReturnsNullInsteadOfThrowing()
    {
        File.WriteAllText(_tempFilePath, "{ this is not valid json");

        Assert.Null(SettingsStore.Load(_tempFilePath));
    }

    /// <summary>
    /// Regression test for a real reported bug: after mirroring "whale" (a browser) and then
    /// selecting regions on Notepad, the "저장된 설정 불러오기" button kept showing whale's
    /// profile as long as whale was still running, because re-saving an existing profile
    /// updated it in place instead of marking it as the most recently used one — so a plain
    /// "first profile whose app is running" search kept finding whichever app was saved
    /// *first*, not last. Fixed by moving the touched profile to the end of the list on every
    /// save; this test locks in that ordering contract at the persistence layer.
    /// </summary>
    [Fact]
    public void SavingProfileAgain_MovesItToEndOfProfileList()
    {
        UpsertProfile("whale", new SavedRegion { Left = 1, Top = 1, Width = 10, Height = 10 });
        UpsertProfile("notepad", new SavedRegion { Left = 2, Top = 2, Width = 20, Height = 20 });

        var loaded = SettingsStore.Load(_tempFilePath)!;

        Assert.Equal(new[] { "whale", "notepad" }, loaded.Profiles.Select(p => p.ProcessName));

        // Re-saving whale afterward should move it back to the end, ahead of notepad again.
        UpsertProfile("whale", new SavedRegion { Left = 3, Top = 3, Width = 30, Height = 30 });
        loaded = SettingsStore.Load(_tempFilePath)!;

        Assert.Equal(new[] { "notepad", "whale" }, loaded.Profiles.Select(p => p.ProcessName));
    }

    // Mirrors MainWindow.SaveCurrentSettings' load-upsert-save pattern, including moving the
    // touched profile to the end of the list (see SavingProfileAgain_MovesItToEndOfProfileList).
    private void UpsertProfile(string processName, SavedRegion region)
    {
        var settings = SettingsStore.Load(_tempFilePath) ?? new AppSettings();
        var profile = settings.Profiles.FirstOrDefault(p => p.ProcessName == processName);
        if (profile != null)
        {
            settings.Profiles.Remove(profile);
        }
        else
        {
            profile = new TargetProfile { ProcessName = processName };
        }

        profile.Regions = new() { region };
        settings.Profiles.Add(profile);
        SettingsStore.Save(settings, _tempFilePath);
    }
}
