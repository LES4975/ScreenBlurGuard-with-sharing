using System;
using System.IO;
using System.Text.Json;

namespace ScreenBlurGuard.Settings;

public static class SettingsStore
{
    public static readonly string DefaultFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ScreenBlurGuard", "settings.json");

    /// <summary>Best-effort load — a missing or corrupt file is treated as "nothing saved yet".</summary>
    public static AppSettings? Load(string? filePath = null)
    {
        filePath ??= DefaultFilePath;
        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<AppSettings>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Best-effort save — persistence is a convenience, not something that should crash the app.</summary>
    public static void Save(AppSettings settings, string? filePath = null)
    {
        filePath ??= DefaultFilePath;
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
        }
        catch
        {
        }
    }
}
