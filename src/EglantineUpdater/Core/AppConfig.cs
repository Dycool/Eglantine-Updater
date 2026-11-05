using System;
using System.IO;
using System.Text.Json;

namespace EglantineUpdater.Core;

/// <summary>User settings, stored in %APPDATA%\EglantineUpdater\config.json.</summary>
public sealed class AppConfig
{
    public const string DefaultAssetPattern = @"^eglantine-windows-x86_64-pgo.*\.zip$";

    public string InstallPath { get; set; } = "";
    public string InstalledTag { get; set; } = "";
    public string InstalledName { get; set; } = "";
    public DateTimeOffset? InstalledPublishedAt { get; set; }
    public string SkippedTag { get; set; } = "";
    public string LastNotifiedTag { get; set; } = "";
    public string AssetPattern { get; set; } = DefaultAssetPattern;
    public int CheckIntervalMinutes { get; set; } = 360;
    public DateTimeOffset? LastCheck { get; set; }
    public bool StartupConfigured { get; set; }

    public static string DataDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EglantineUpdater");

    public static string FilePath => Path.Combine(DataDirectory, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppConfig Load()
    {
        AppConfig cfg;
        try
        {
            cfg = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), JsonOptions) ?? new AppConfig()
                : new AppConfig();
        }
        catch (Exception ex)
        {
            Log.Write($"Config unreadable, using defaults: {ex.Message}");
            cfg = new AppConfig();
        }
        if (cfg.CheckIntervalMinutes < 15) cfg.CheckIntervalMinutes = 360;
        if (string.IsNullOrWhiteSpace(cfg.AssetPattern)) cfg.AssetPattern = DefaultAssetPattern;
        return cfg;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write($"Could not save config: {ex.Message}");
        }
    }

    /// <summary>True when <paramref name="latest"/> is newer than what is recorded as installed.</summary>
    public bool IsUpdateAvailable(ReleaseInfo? latest)
    {
        if (latest is null) return false;
        if (string.IsNullOrEmpty(InstalledTag)) return true;
        if (string.Equals(latest.Tag, InstalledTag, StringComparison.Ordinal)) return false;
        return InstalledPublishedAt is null || latest.Published > InstalledPublishedAt.Value;
    }

    public void MarkInstalled(ReleaseInfo release)
    {
        InstalledTag = release.Tag;
        InstalledName = release.Name;
        InstalledPublishedAt = release.Published;
    }

    public void ClearInstalled()
    {
        InstalledTag = "";
        InstalledName = "";
        InstalledPublishedAt = null;
        LastNotifiedTag = "";
    }
}
