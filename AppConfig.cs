namespace Nightforge.Models;

/// <summary>
/// 配置模型。字段名与旧版 AutoHotkey 脚本的 config.ini 完全一致，
/// 老用户直接复用原配置文件即可，无需重新填写。
/// </summary>
public sealed class AppConfig
{
    // ---- [Settings] ----
    public string GamePath { get; set; } = "";
    public string WinTitle { get; set; } = "梦幻西游";
    public int LastCount { get; set; } = 1;
    public string SyncPath { get; set; } = "";
    public int WinWidth { get; set; } = 800;
    public int WinHeight { get; set; } = 600;
    public int WaitSec { get; set; } = 7;
    public string ResMode { get; set; } = "自定义宽高";
    public string ResPreset { get; set; } = "1920x1080";

    // ---- [Settings] 更新检查 ----
    /// <summary>启动时联网检查 GitHub 上是否有新版本。</summary>
    public bool CheckUpdateOnStart { get; set; } = true;

    /// <summary>用户点过「本版本不再提示」的版本号，对该版本不再弹窗。</summary>
    public string SkipVersion { get; set; } = "";

    // ---- [Hotkeys] ----
    // 默认值需避开系统与其他软件已占用的组合（如 Ctrl+Alt+M / Ctrl+Alt+R 常被占用，注册会失败）
    public string HotkeyFront { get; set; } = "Ctrl+Alt+T";
    public string HotkeyMinimize { get; set; } = "Ctrl+Alt+B";
    public string HotkeyGetSize { get; set; } = "Ctrl+Alt+S";

    public AppConfig Clone() => (AppConfig)MemberwiseClone();

    /// <summary>分辨率预设可选项。</summary>
    public static readonly string[] PresetOptions =
    [
        "1920x1080", "1600x900", "1440x900", "1280x720", "1024x768"
    ];

    /// <summary>分辨率模式可选项。</summary>
    public static readonly string[] ModeOptions =
    [
        "自定义宽高", "使用预设", "不改变窗口大小"
    ];
}
