using System;
using System.IO;
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
    public void SaveThenLoad_RoundTripsProcessNameAndRegions()
    {
        var original = new AppSettings
        {
            TargetProcessName = "notepad",
            Regions =
            {
                new SavedRegion { Left = 10, Top = 20, Width = 100, Height = 50 },
                new SavedRegion { Left = 200, Top = 5, Width = 30, Height = 30 },
            },
        };

        SettingsStore.Save(original, _tempFilePath);
        var loaded = SettingsStore.Load(_tempFilePath);

        Assert.NotNull(loaded);
        Assert.Equal(original.TargetProcessName, loaded!.TargetProcessName);
        Assert.Equal(original.Regions.Count, loaded.Regions.Count);
        for (int i = 0; i < original.Regions.Count; i++)
        {
            Assert.Equal(original.Regions[i].Left, loaded.Regions[i].Left);
            Assert.Equal(original.Regions[i].Top, loaded.Regions[i].Top);
            Assert.Equal(original.Regions[i].Width, loaded.Regions[i].Width);
            Assert.Equal(original.Regions[i].Height, loaded.Regions[i].Height);
        }
    }

    [Fact]
    public void Save_OverwritesPreviousContent()
    {
        SettingsStore.Save(new AppSettings { TargetProcessName = "first", Regions = { new SavedRegion { Left = 1, Top = 1, Width = 1, Height = 1 } } }, _tempFilePath);
        SettingsStore.Save(new AppSettings { TargetProcessName = "second", Regions = { } }, _tempFilePath);

        var loaded = SettingsStore.Load(_tempFilePath);

        Assert.Equal("second", loaded!.TargetProcessName);
        Assert.Empty(loaded.Regions);
    }

    [Fact]
    public void Load_WhenFileContainsInvalidJson_ReturnsNullInsteadOfThrowing()
    {
        File.WriteAllText(_tempFilePath, "{ this is not valid json");

        Assert.Null(SettingsStore.Load(_tempFilePath));
    }
}
