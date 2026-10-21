using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AppleMusicPresence;

public sealed record AppSettings
{
    public string ApplicationId { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public bool StartWithWindows { get; init; }
    public string Country { get; init; } = RegionInfo.CurrentRegion.TwoLetterISORegionName;
    public static bool IsValidId(string? id) => Regex.IsMatch(id ?? "", @"^[0-9]{17,20}$");
    public string SafeCountry => Regex.IsMatch(Country ?? "", "^[A-Za-z]{2}$") ? Country!.ToUpperInvariant() : "US";
}

public sealed class SettingsStore
{
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppleMusicPresence");
    private readonly string path;
    public SettingsStore(string? path = null) => this.path = path ?? Path.Combine(DataDirectory, "settings.json");
    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(path)) return new();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Error("Could not load settings; using defaults", e);
            return new();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }
}

public static class WindowsStartup
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AppleMusicPresence";
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(ValueName) is string;
    }
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --background");
        else
            key.DeleteValue(ValueName, false);
    }
}

public static class AppLog
{
    private static readonly object Sync = new();
    public static void Error(string context, Exception e)
    {
        // Errors only. No account information or listening history is written to this log.
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(SettingsStore.DataDirectory);
                var path = Path.Combine(SettingsStore.DataDirectory, "app.log");
                if (File.Exists(path) && new FileInfo(path).Length > 512_000) File.Move(path, path + ".old", true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:u} {context}: {e.GetType().Name} — {e.Message}\n");
            }
        }
        catch { /* Logging must never prevent shutdown or recovery. */ }
    }
}
