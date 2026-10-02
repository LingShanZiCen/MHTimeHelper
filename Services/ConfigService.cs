using System.Globalization;
using System.IO;
using System.Text;
using Nightforge.Models;

namespace Nightforge.Services;

/// <summary>config.ini 读写。字段名与旧版 AHK 脚本保持一致，可直接沿用老配置文件。</summary>
public static class ConfigService
{
    private const string SettingsSection = "Settings";
    private const string HotkeysSection = "Hotkeys";

    public static string AppDirectory
    {
        get
        {
            string? dir = Path.GetDirectoryName(Environment.ProcessPath);
            return string.IsNullOrEmpty(dir) ? AppContext.BaseDirectory : dir;
        }
    }

    public static string ConfigPath => Path.Combine(AppDirectory, "config.ini");

    public static AppConfig Load()
    {
        var cfg = new AppConfig();
        string path = ConfigPath;
        if (!File.Exists(path))
        {
            return cfg;
        }

        cfg.GamePath = GetString(SettingsSection, "GamePath", cfg.GamePath, path);
        cfg.WinTitle = GetString(SettingsSection, "WinTitle", cfg.WinTitle, path);
        cfg.LastCount = GetInt(SettingsSection, "LastCount", cfg.LastCount, path);
        cfg.SyncPath = GetString(SettingsSection, "SyncPath", cfg.SyncPath, path);
        cfg.WinWidth = GetInt(SettingsSection, "WinWidth", cfg.WinWidth, path);
        cfg.WinHeight = GetInt(SettingsSection, "WinHeight", cfg.WinHeight, path);
        cfg.WaitSec = GetInt(SettingsSection, "WaitSec", cfg.WaitSec, path);
        cfg.ResMode = GetString(SettingsSection, "ResMode", cfg.ResMode, path);
        cfg.ResPreset = GetString(SettingsSection, "ResPreset", cfg.ResPreset, path);
        cfg.CheckUpdateOnStart = GetInt(SettingsSection, "CheckUpdateOnStart", cfg.CheckUpdateOnStart ? 1 : 0, path) != 0;
        cfg.SkipVersion = GetString(SettingsSection, "SkipVersion", cfg.SkipVersion, path);
        cfg.LastRunVersion = GetString(SettingsSection, "LastRunVersion", cfg.LastRunVersion, path);
        cfg.AutoDownloadUpdate = GetInt(SettingsSection, "AutoDownloadUpdate", cfg.AutoDownloadUpdate ? 1 : 0, path) != 0;
        cfg.PendingUpdateVersion = GetString(SettingsSection, "PendingUpdateVersion", cfg.PendingUpdateVersion, path);
        cfg.PendingUpdateFile = GetString(SettingsSection, "PendingUpdateFile", cfg.PendingUpdateFile, path);

        cfg.HotkeyFront = GetString(HotkeysSection, "Front", cfg.HotkeyFront, path);
        cfg.HotkeyMinimize = GetString(HotkeysSection, "Minimize", cfg.HotkeyMinimize, path);
        cfg.HotkeyGetSize = GetString(HotkeysSection, "GetSize", cfg.HotkeyGetSize, path);

        if (cfg.LastCount < 1) cfg.LastCount = 1;
        if (cfg.LastCount > 5) cfg.LastCount = 5;
        if (cfg.WinWidth < 200) cfg.WinWidth = 800;
        if (cfg.WinHeight < 200) cfg.WinHeight = 600;
        if (cfg.WaitSec < 1) cfg.WaitSec = 7;
        if (cfg.WaitSec > 120) cfg.WaitSec = 120;

        return cfg;
    }

    public static void Save(AppConfig cfg)
    {
        string path = ConfigPath;

        WriteString(SettingsSection, "GamePath", cfg.GamePath, path);
        WriteString(SettingsSection, "WinTitle", cfg.WinTitle, path);
        WriteString(SettingsSection, "LastCount", cfg.LastCount.ToString(CultureInfo.InvariantCulture), path);
        WriteString(SettingsSection, "SyncPath", cfg.SyncPath, path);
        WriteString(SettingsSection, "WinWidth", cfg.WinWidth.ToString(CultureInfo.InvariantCulture), path);
        WriteString(SettingsSection, "WinHeight", cfg.WinHeight.ToString(CultureInfo.InvariantCulture), path);
        WriteString(SettingsSection, "WaitSec", cfg.WaitSec.ToString(CultureInfo.InvariantCulture), path);
        WriteString(SettingsSection, "ResMode", cfg.ResMode, path);
        WriteString(SettingsSection, "ResPreset", cfg.ResPreset, path);
        WriteString(SettingsSection, "CheckUpdateOnStart", cfg.CheckUpdateOnStart ? "1" : "0", path);
        WriteString(SettingsSection, "SkipVersion", cfg.SkipVersion, path);
        WriteString(SettingsSection, "LastRunVersion", cfg.LastRunVersion, path);
        WriteString(SettingsSection, "AutoDownloadUpdate", cfg.AutoDownloadUpdate ? "1" : "0", path);
        WriteString(SettingsSection, "PendingUpdateVersion", cfg.PendingUpdateVersion, path);
        WriteString(SettingsSection, "PendingUpdateFile", cfg.PendingUpdateFile, path);

        WriteString(HotkeysSection, "Front", cfg.HotkeyFront, path);
        WriteString(HotkeysSection, "Minimize", cfg.HotkeyMinimize, path);
        WriteString(HotkeysSection, "GetSize", cfg.HotkeyGetSize, path);
    }

    private static string GetString(string section, string key, string fallback, string path)
    {
        var sb = new StringBuilder(512);
        int len = NativeMethods.GetPrivateProfileString(section, key, fallback, sb, sb.Capacity, path);
        return len <= 0 ? fallback : sb.ToString();
    }

    private static int GetInt(string section, string key, int fallback, string path)
    {
        string raw = GetString(section, key, fallback.ToString(CultureInfo.InvariantCulture), path);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;
    }

    private static void WriteString(string section, string key, string value, string path)
    {
        NativeMethods.WritePrivateProfileString(section, key, value, path);
    }
}
