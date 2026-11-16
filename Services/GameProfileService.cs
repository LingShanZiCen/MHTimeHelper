using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Nightforge.Models;

namespace Nightforge.Services;

/// <summary>
/// 读写游戏客户端自己的设置文件 <c>LocalData\UserDefault.xml</c>。
/// <para>
/// 只动「画面比例」这一个节点（<c>DeviceScreenResolution</c>），文件的其余内容一个字节都不改，
/// 写之前会先备份一份到程序目录，出问题可以直接还原。
/// </para>
/// <para>
/// 这个键是游戏启动器里「画面比例」那一项真正落盘的位置——全盘只有这一处存它，
/// 所以在这里改等价于在启动器里点一下，只是省了手动操作。
/// </para>
/// </summary>
public static class GameProfileService
{
    private const string RatioNode = "DeviceScreenResolution";
    private const string ProfileRelativePath = @"LocalData\UserDefault.xml";
    private const string BackupFileName = "UserDefault.xml.bak";

    /// <summary>会独占这份设置文件的进程。它们运行时写进去会被内存里的旧值覆盖回来，所以要先拦住。</summary>
    private static readonly string[] BlockingProcesses = ["MyGame_x64r", "MyLauncher_x64r", "MyPreloader_x64r", "MyTabCtrl_x64r"];

    private static readonly Regex RatioRegex = new(
        @"(<" + RatioNode + @">)(.*?)(</" + RatioNode + @">)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>从游戏路径逐级向上找 LocalData\UserDefault.xml。</summary>
    public static string? LocateProfileFile(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            return null;
        }

        string? dir;
        try
        {
            dir = Path.GetDirectoryName(gamePath.Trim().Trim('"'));
        }
        catch (ArgumentException)
        {
            return null;
        }

        // 游戏路径通常是 ...\My\MyLauncher_x64r.exe，设置文件在 ...\My\LocalData\ 下；
        // 用户也可能直接指向 Engine\Binaries\Win64\MyGame_x64r.exe，多向上找几级更稳妥。
        for (int level = 0; level < 4 && !string.IsNullOrEmpty(dir); level++)
        {
            string candidate = Path.Combine(dir, ProfileRelativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    /// <summary>读取当前画面比例（就是配置里存的那个字符串）；读不到返回 null。</summary>
    public static string? ReadRatioKey(string gamePath)
    {
        string? file = LocateProfileFile(gamePath);
        if (file is null)
        {
            return null;
        }

        try
        {
            Match match = RatioRegex.Match(File.ReadAllText(file, Encoding.UTF8));
            return match.Success ? match.Groups[2].Value.Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>读取当前画面比例并换算成选项；读不到返回 null。</summary>
    public static AspectRatioOption? ReadRatio(string gamePath)
        => AspectRatioOption.FromKey(ReadRatioKey(gamePath));

    /// <summary>游戏是否正在运行（正在跑的话比例改了也会被覆盖，必须先关游戏）。</summary>
    public static bool IsGameRunning(out string runningNames)
    {
        var hits = new List<string>();
        foreach (string name in BlockingProcesses)
        {
            try
            {
                if (Process.GetProcessesByName(name).Length > 0)
                {
                    hits.Add(name);
                }
            }
            catch (InvalidOperationException)
            {
                // 进程刚好退出，忽略
            }
        }

        runningNames = string.Join("、", hits);
        return hits.Count > 0;
    }

    /// <summary>
    /// 把画面比例写进游戏设置文件。
    /// 已经是目标值时不写盘，直接算成功；写入后会读回校验一次。
    /// </summary>
    public static bool TryWriteRatio(string gamePath, AspectRatioOption ratio, out string message)
    {
        string? file = LocateProfileFile(gamePath);
        if (file is null)
        {
            message = @"没找到游戏设置文件 LocalData\UserDefault.xml，请确认「游戏路径」指向梦幻西游时空的启动器。";
            return false;
        }

        if (IsGameRunning(out string running))
        {
            message = $"游戏正在运行（{running}），画面比例只能在游戏全部关闭时修改，否则会被游戏自己覆盖。";
            return false;
        }

        try
        {
            string text = File.ReadAllText(file, Encoding.UTF8);
            Match match = RatioRegex.Match(text);
            if (!match.Success)
            {
                message = $"设置文件里没有 {RatioNode} 这一项，游戏可能改过结构，本次跳过。";
                return false;
            }

            if (string.Equals(match.Groups[2].Value.Trim(), ratio.Key, StringComparison.OrdinalIgnoreCase))
            {
                message = $"画面比例本来就是 {ratio.Name}，无需改动。";
                return true;
            }

            EnsureBackup(file);

            Group value = match.Groups[2];
            string updated = text[..value.Index] + ratio.Key + text[(value.Index + value.Length)..];
            File.WriteAllText(file, updated, new UTF8Encoding(false));

            string? check = ReadRatioKey(gamePath);
            if (!string.Equals(check, ratio.Key, StringComparison.OrdinalIgnoreCase))
            {
                message = $"写入后校验不一致（读回「{check ?? "空"}」），已保留备份，可手动还原。";
                return false;
            }

            message = $"画面比例已设为 {ratio.Name}。";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            message = "写入游戏设置失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>首次改动前备份一份到程序目录；已有备份就不覆盖，避免把改过的内容当成原始值。</summary>
    private static void EnsureBackup(string sourceFile)
    {
        try
        {
            string target = Path.Combine(ConfigService.AppDirectory, BackupFileName);
            if (!File.Exists(target))
            {
                File.Copy(sourceFile, target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 备份失败不阻断主流程：真正写盘前的内容用户还能从游戏目录找回来
        }
    }
}
