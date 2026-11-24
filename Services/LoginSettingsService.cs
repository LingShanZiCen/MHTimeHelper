using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Nightforge.Models;

namespace Nightforge.Services;

/// <summary>
/// 自动登录设置的读写。
/// 单独存 login.json：内容是「每个窗口登录到哪」，塞进 config.ini 会很难看。
/// </summary>
public static class LoginSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // 中文直接写出来，用户想手改也看得懂
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string FilePath => Path.Combine(ConfigService.AppDirectory, "login.json");

    /// <summary>窗口数量上限，与「一键启动」的 1–5 保持一致。</summary>
    public const int MaxWindows = 5;

    public static LoginSettings Load()
    {
        LoginSettings settings;

        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath, Encoding.UTF8);
                settings = JsonSerializer.Deserialize<LoginSettings>(json, JsonOptions) ?? CreateDefault();
            }
            else
            {
                settings = CreateDefault();
            }
        }
        catch
        {
            // 文件被改坏时退回默认值，不能因为一个配置文件让程序起不来
            settings = CreateDefault();
        }

        settings.Normalize();
        return settings;
    }

    public static void Save(LoginSettings settings)
    {
        settings.Normalize();

        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions), new UTF8Encoding(false));
        }
        catch
        {
            // 存不下去只影响下次还得重填，不阻断当前操作
        }
    }

    public static LoginSettings CreateDefault()
    {
        var settings = new LoginSettings
        {
            Enabled = false,
            StartDelaySec = 8,
            StepTimeoutSec = 15,
            AutoClickLogin = true
        };

        settings.Normalize();
        return settings;
    }
}
