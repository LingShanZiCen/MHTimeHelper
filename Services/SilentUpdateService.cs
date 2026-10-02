using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Nightforge.Models;

namespace Nightforge.Services;

/// <summary>
/// 全量静默更新：把新版本整包换掉，用户只负责再启动一次。
///
/// 为什么必须重启：程序是单文件自包含 exe，正在运行的文件无法覆盖自己。
/// 所以流程拆成两段——
///   1) 本次运行：后台把新版本下到 程序目录\update\Nightforge_new.exe，校验通过后在 config.ini 里登记为「待应用」；
///   2) 下次启动：本类把当前 exe 改名成 Nightforge_old.exe、把新 exe 放到原位置，再拉起新版本，
///      新版本带 --cleanup-old 参数启动，顺手把备份清掉。
/// 整个过程中旧版本始终可用，任何一步失败都只是「更新没做成」，不会让程序起不来。
/// </summary>
public static class SilentUpdateService
{
    /// <summary>下载暂存目录（位于程序目录下，与 exe、config.ini 同级）。</summary>
    public const string UpdateDirName = "update";

    /// <summary>下载完成后暂存的文件名。</summary>
    public const string StagedFileName = "Nightforge_new.exe";

    /// <summary>替换时旧版本备份的后缀。</summary>
    public const string BackupSuffix = "_old.exe";

    /// <summary>新版本启动时携带的参数：表示刚从旧版本替换过来，需要清理备份文件。</summary>
    public const string CleanupArg = "--cleanup-old";

    private const int CopyBufferSize = 81920;

    private static readonly HttpClient Http = CreateClient();

    /// <summary>下载暂存目录。</summary>
    public static string UpdateDir => Path.Combine(ConfigService.AppDirectory, UpdateDirName);

    /// <summary>下载完成后的暂存文件路径。</summary>
    public static string StagedPath => Path.Combine(UpdateDir, StagedFileName);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub 强制要求 User-Agent，缺了会直接 403
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Nightforge-Updater/1.0");
        return client;
    }

    /// <summary>
    /// 程序目录是否可写。绿色版放在 Program Files、只读盘符或受控目录时不可写，
    /// 此时无法自动更新，只能退回「打开下载页手动下载」。
    /// </summary>
    public static bool IsAppDirectoryWritable()
    {
        try
        {
            Directory.CreateDirectory(UpdateDir);
            string probe = Path.Combine(UpdateDir, $".probe_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>config.ini 里登记的待应用更新是否仍然有效（版本号非空且文件还在）。</summary>
    public static bool HasPendingUpdate(AppConfig? cfg)
        => cfg is not null
           && !string.IsNullOrWhiteSpace(cfg.PendingUpdateVersion)
           && File.Exists(StagedPath);

    /// <summary>
    /// 后台下载新版本到暂存区并校验。返回 null 表示成功，返回文本为失败原因。
    /// 全程不抛异常，失败只影响「更新没做成」。
    /// </summary>
    public static async Task<string?> DownloadAsync(UpdateInfo info, Action<string> log, CancellationToken ct = default)
    {
        if (!info.CanAutoDownload)
        {
            return "服务器没有提供新版安装包地址";
        }

        if (!IsAppDirectoryWritable())
        {
            return "程序目录不可写（可能放在 Program Files 等受保护位置）";
        }

        if (info.Size > 0)
        {
            log($"新版体积约 {info.Size / 1024.0 / 1024.0:F1} MB");
        }

        Directory.CreateDirectory(UpdateDir);
        string temp = StagedPath + ".part";

        try
        {
            TryDelete(temp);

            using (var response = await Http.GetAsync(info.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    return $"下载失败，HTTP {(int)response.StatusCode}";
                }

                long total = response.Content.Headers.ContentLength ?? info.Size;
                await using Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true);

                byte[] buffer = new byte[CopyBufferSize];
                long done = 0;
                int lastReported = 0;

                while (true)
                {
                    int read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;

                    if (total > 0)
                    {
                        int percent = (int)(done * 100 / total);
                        if (percent >= lastReported + 20)
                        {
                            lastReported = percent;
                            log($"下载进度 {percent}%（{done / 1024.0 / 1024.0:F1} / {total / 1024.0 / 1024.0:F1} MB）");
                        }
                    }
                }

                await target.FlushAsync(ct).ConfigureAwait(false);
            }

            long length = new FileInfo(temp).Length;
            if (info.Size > 0 && length != info.Size)
            {
                TryDelete(temp);
                return $"文件大小对不上（下载 {length} 字节，应为 {info.Size} 字节），已丢弃";
            }

            string? expected = NormalizeSha256(info.Sha256);
            if (expected is not null)
            {
                string actual = await ComputeSha256Async(temp, ct).ConfigureAwait(false);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(temp);
                    return "校验值不匹配，文件可能不完整或被篡改，已丢弃";
                }

                log("校验通过（SHA-256 一致）");
            }
            else
            {
                log("服务器未提供 SHA-256，已按文件大小校验");
            }

            File.Move(temp, StagedPath, true);
            return null;
        }
        catch (Exception ex)
        {
            TryDelete(temp);
            return ex is TaskCanceledException or OperationCanceledException ? "下载超时" : ex.Message;
        }
    }

    /// <summary>
    /// 把已下载的新版本换成当前程序：当前 exe 改名备份 → 新 exe 就位 → 拉起新版本。
    /// 返回 null 表示替换成功（调用方必须立即退出，让新版本接手）；返回文本表示失败原因（调用方照常启动旧版本）。
    /// </summary>
    public static string? ApplyPendingUpdate()
    {
        string? currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
        {
            return "取不到当前程序路径";
        }

        if (!File.Exists(StagedPath))
        {
            return "待更新的文件不存在";
        }

        string dir = Path.GetDirectoryName(currentExe) ?? ConfigService.AppDirectory;
        string backup = Path.Combine(dir, Path.GetFileNameWithoutExtension(currentExe) + BackupSuffix);

        try
        {
            // 上一轮遗留的备份已不在运行，直接清掉
            if (File.Exists(backup))
            {
                File.Delete(backup);
            }

            // 运行中的 exe 允许改名、不允许覆盖，所以先把旧版本挪开
            File.Move(currentExe, backup);

            try
            {
                File.Copy(StagedPath, currentExe, true);
                TryDelete(StagedPath);
            }
            catch
            {
                // 新文件没能就位：把旧版本改回去，保证程序还能启动
                File.Move(backup, currentExe, true);
                throw;
            }

            Process.Start(new ProcessStartInfo(currentExe, CleanupArg)
            {
                UseShellExecute = true,
                WorkingDirectory = dir
            });

            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>新版本启动后清理：删掉旧版本备份与 update 目录里的残留文件。</summary>
    public static void CleanupOldFiles(Action<string>? log = null)
    {
        string dir = ConfigService.AppDirectory;

        foreach (string file in SafeEnumerate(dir, "*" + BackupSuffix))
        {
            // 旧进程可能还没彻底退出，重试几次再放弃
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    File.Delete(file);
                    log?.Invoke($"已清理旧版本文件：{Path.GetFileName(file)}");
                    break;
                }
                catch
                {
                    Thread.Sleep(300);
                }
            }
        }

        foreach (string file in SafeEnumerate(UpdateDir, "*"))
        {
            TryDelete(file);
        }
    }

    /// <summary>替换完成后清空 config.ini 里的待应用登记。</summary>
    public static void ClearPendingState(AppConfig cfg)
    {
        cfg.PendingUpdateVersion = "";
        cfg.PendingUpdateFile = "";
        ConfigService.Save(cfg);
    }

    /// <summary>记录待应用更新：写进 config.ini，下次启动时生效。</summary>
    public static void MarkPending(AppConfig cfg, string version)
    {
        cfg.PendingUpdateVersion = version;
        cfg.PendingUpdateFile = StagedPath;
        ConfigService.Save(cfg);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>把 "sha256:xxx" / 带空格大写 的摘要统一成小写十六进制；无效或缺失返回 null。</summary>
    private static string? NormalizeSha256(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string text = raw.Trim();
        if (text.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            text = text["sha256:".Length..];
        }

        text = text.Replace(" ", "").Replace("-", "").Trim().ToLowerInvariant();
        return text.Length == 64 && text.All(Uri.IsHexDigit) ? text : null;
    }

    private static IEnumerable<string> SafeEnumerate(string dir, string pattern)
    {
        try
        {
            return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, pattern) : [];
        }
        catch
        {
            return [];
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 删不掉不影响程序运行，留着下次启动再清
        }
    }
}
