using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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

    /// <summary>
    /// 单条地址最多试几次。失败后会用同一个地址「续传」重试，而不是从头再下 70MB。
    /// </summary>
    private const int AttemptsPerUrl = 2;

    /// <summary>多久收不到数据就认为这条连接已经死了（秒）。</summary>
    private const int IdleTimeoutSeconds = 45;

    /// <summary>
    /// GitHub 加速镜像前缀。原始地址直接拼在后面即可，例如
    /// https://gh-proxy.com/https://github.com/xxx/releases/download/v1/n.exe
    /// 这些只是「同一份文件的另一条路」，拿到文件后照旧做 SHA-256 校验，
    /// 镜像被动过手脚会被直接丢弃，所以多一条路不等于降低安全性。
    /// </summary>
    private static readonly string[] Mirrors =
    [
        "https://gh-proxy.com/",
        "https://ghfast.top/",
        "https://ghproxy.net/",
        "https://ghproxy.cc/",
        "https://gh-proxy.net/"
    ];

    private static readonly HttpClient Http = CreateClient();

    /// <summary>下载暂存目录。</summary>
    public static string UpdateDir => Path.Combine(ConfigService.AppDirectory, UpdateDirName);

    /// <summary>下载完成后的暂存文件路径。</summary>
    public static string StagedPath => Path.Combine(UpdateDir, StagedFileName);

    private static HttpClient CreateClient()
    {
        // 不再给整个下载设总时长上限：70 MB 在慢网络下必然超过 10 分钟，
        // 真正该判断的是「还在不在收数据」，这个由每次读取的空闲超时负责。
        var client = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
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
    /// 多地址轮换 + 断点续传 + 空闲超时重试，任何单点网络问题都不至于让整包白下。
    /// 全程不抛异常（除调用方主动取消），失败只影响「更新没做成」。
    /// </summary>
    public static async Task<string?> DownloadAsync(UpdateInfo info, Action<string> log, CancellationToken ct = default)
    {
        // 界面日志与磁盘日志一起写，出问题时用户能直接把 update.log 发过来
        void Note(string message)
        {
            log(message);
            UpdateLog.Write(message);
        }

        if (!info.CanAutoDownload)
        {
            Note("服务器没有提供新版安装包地址，无法自动更新");
            return "服务器没有提供新版安装包地址";
        }

        if (!IsAppDirectoryWritable())
        {
            Note("程序目录不可写，无法自动更新");
            return "程序目录不可写（可能放在 Program Files 等受保护位置）";
        }

        if (info.Size > 0)
        {
            Note($"新版体积约 {info.Size / 1024.0 / 1024.0:F1} MB");
        }

        Directory.CreateDirectory(UpdateDir);

        List<string> urls = BuildDownloadUrls(info);
        Note($"下载地址候选 {urls.Count} 条（含镜像），将按顺序尝试，失败自动切换并支持断点续传");

        var failures = new List<string>();

        foreach (string url in urls)
        {
            for (int attempt = 1; attempt <= AttemptsPerUrl; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    string? error = await DownloadFromAsync(url, info, Note, ct).ConfigureAwait(false);
                    if (error is null)
                    {
                        File.Move(StagedPath + ".part", StagedPath, true);
                        Note($"下载完成：{StagedFileName}");
                        return null;
                    }

                    failures.Add($"{HostOf(url)}：{error}");
                    Note($"{HostOf(url)} 第 {attempt} 次失败：{error}");

                    if (error.StartsWith("校验", StringComparison.Ordinal))
                    {
                        // 内容不对，续传没有意义，清掉重新下
                        TryDelete(StagedPath + ".part");
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failures.Add($"{HostOf(url)}：{ex.Message}");
                    Note($"{HostOf(url)} 出错：{ex.Message}");
                }

                if (attempt < AttemptsPerUrl)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                }
            }

            Note("换下一个下载地址继续尝试");
        }

        // .part 故意保留：下次启动能接着续传，不必从 0 开始
        string reason = failures.Count == 0 ? "没有可用的下载地址" : string.Join("；", failures.TakeLast(3));
        Note($"自动下载失败：{reason}");
        return $"全部下载地址都失败了（{reason}）";
    }

    /// <summary>
    /// 从单个地址下载或续传到 .part，完成后校验大小与 SHA-256。
    /// 返回 null 表示文件已完整落地；返回文本为失败原因。
    /// </summary>
    private static async Task<string?> DownloadFromAsync(string url, UpdateInfo info, Action<string> note, CancellationToken ct)
    {
        string temp = StagedPath + ".part";
        long offset = File.Exists(temp) ? new FileInfo(temp).Length : 0;

        if (offset > 0 && info.Size > 0 && offset >= info.Size)
        {
            // 文件已经够大（上次其实下完了，或是别的东西），清掉重来
            TryDelete(temp);
            offset = 0;
        }

        if (offset > 0)
        {
            note($"发现未完成的分片，从 {offset / 1024.0 / 1024.0:F1} MB 处继续");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
        }

        HttpResponseMessage response;
        using (var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            headerCts.CancelAfter(TimeSpan.FromSeconds(IdleTimeoutSeconds));
            try
            {
                response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return $"连接 {IdleTimeoutSeconds} 秒内没有响应";
            }
            catch (HttpRequestException ex)
            {
                return $"连接失败：{ex.Message}";
            }
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && offset > 0)
            {
                TryDelete(temp);
                return "续传位置已失效，已清空重试";
            }

            if (!response.IsSuccessStatusCode)
            {
                return $"HTTP {(int)response.StatusCode}";
            }

            bool resumed = response.StatusCode == HttpStatusCode.PartialContent;
            if (offset > 0 && !resumed)
            {
                note("该地址不支持断点续传，改为从头下载");
                offset = 0;
            }

            long contentLength = response.Content.Headers.ContentLength ?? 0;
            if (contentLength > 0 && info.Size > 0 && offset + contentLength != info.Size)
            {
                // 回来的不是目标文件（镜像换过内容、版本对不上），别浪费带宽
                return $"返回体积不对（{offset + contentLength} 字节，应为 {info.Size} 字节）";
            }

            long total = info.Size > 0 ? info.Size : offset + contentLength;
            note(offset > 0
                ? $"继续下载（已 {offset / 1024.0 / 1024.0:F1} MB）"
                : $"开始下载（{HostOf(url)}）");

            await using Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (var target = new FileStream(temp, offset > 0 ? FileMode.Append : FileMode.Create,
                             FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true))
            {
                byte[] buffer = new byte[CopyBufferSize];
                long done = offset;
                int lastPercent = total > 0 ? (int)(done * 100 / total) : 0;

                while (true)
                {
                    int read;
                    using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        readCts.CancelAfter(TimeSpan.FromSeconds(IdleTimeoutSeconds));
                        try
                        {
                            read = await source.ReadAsync(buffer, readCts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            return $"连续 {IdleTimeoutSeconds} 秒没有收到数据，连接已中断";
                        }
                        catch (IOException ex)
                        {
                            return $"读取中断：{ex.Message}";
                        }
                    }

                    if (read <= 0)
                    {
                        break;
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;

                    if (total > 0)
                    {
                        int percent = (int)(done * 100 / total);
                        if (percent >= lastPercent + 25 || percent >= 100)
                        {
                            lastPercent = percent;
                            note($"下载进度 {percent}%（{done / 1024.0 / 1024.0:F1} / {total / 1024.0 / 1024.0:F1} MB）");
                        }
                    }
                }

                await target.FlushAsync(ct).ConfigureAwait(false);
            }
        }

        long length = new FileInfo(temp).Length;
        if (info.Size > 0 && length < info.Size)
        {
            // 连接被提前关掉，保留分片，下一次从这个位置续传
            return $"只收到 {length / 1024.0 / 1024.0:F1} MB（应为 {info.Size / 1024.0 / 1024.0:F1} MB）";
        }

        if (info.Size > 0 && length != info.Size)
        {
            return $"文件大小对不上（下载 {length} 字节，应为 {info.Size} 字节）";
        }

        string? expected = NormalizeSha256(info.Sha256);
        if (expected is not null)
        {
            string actual = await ComputeSha256Async(temp, ct).ConfigureAwait(false);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                return "校验值不匹配，文件可能不完整或被改动";
            }

            note("校验通过（SHA-256 一致）");
        }
        else
        {
            note("服务器未提供 SHA-256，已按文件大小校验");
        }

        return null;
    }

    /// <summary>
    /// 组装下载地址候选：清单给的显式地址（一般已含镜像）→ 为 GitHub 直链生成的镜像 → 原始直链。
    /// 直链排最后，因为部分地区根本连不上，放前面只会白等一次超时。
    /// </summary>
    private static List<string> BuildDownloadUrls(UpdateInfo info)
    {
        var result = new List<string>();
        var githubUrls = new List<string>();

        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            string text = value.Trim();
            if (text.Length > 0 && !result.Contains(text, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(text);
            }
        }

        foreach (string url in info.AllExeUrls)
        {
            if (IsGitHubUrl(url))
            {
                githubUrls.Add(url);
            }
            else
            {
                Add(url);
            }
        }

        foreach (string url in githubUrls)
        {
            foreach (string mirror in Mirrors)
            {
                Add(mirror + url);
            }
        }

        foreach (string url in githubUrls)
        {
            Add(url);
        }

        return result;
    }

    private static bool IsGitHubUrl(string url)
        => url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)
           || url.StartsWith("https://objects.githubusercontent.com/", StringComparison.OrdinalIgnoreCase)
           || url.StartsWith("https://raw.githubusercontent.com/", StringComparison.OrdinalIgnoreCase);

    private static string HostOf(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.Host : url;

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

        UpdateLog.Write($"开始替换程序文件：{currentExe}");

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
            }
            catch
            {
                // 新文件没能就位：把旧版本改回去，保证程序还能启动
                File.Move(backup, currentExe, true);
                UpdateLog.Write("复制新版本失败，已回滚到旧版本");
                throw;
            }

            TryDelete(StagedPath);

            Process.Start(new ProcessStartInfo(currentExe, CleanupArg)
            {
                UseShellExecute = true,
                WorkingDirectory = dir
            });

            UpdateLog.Write("已替换为新版本，正在重启");
            return null;
        }
        catch (Exception ex)
        {
            UpdateLog.Write($"替换失败：{ex.Message}");
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
