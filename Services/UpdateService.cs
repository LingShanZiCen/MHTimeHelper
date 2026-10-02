using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Nightforge.Models;

namespace Nightforge.Services;

/// <summary>
/// 远程版本信息。Changes 为按「新增 / 优化 / 修复」分类的更新条目；
/// ExeUrl / Sha256 / Size 供「静默自动更新」下载并校验用，缺失时只能退回手动下载。
/// </summary>
public sealed record UpdateInfo(
    string Version,
    string Url,
    string Notes,
    List<ChangeItem> Changes,
    string ExeUrl = "",
    string Sha256 = "",
    long Size = 0)
{
    /// <summary>是否具备自动下载的条件（至少要给出 exe 直链）。</summary>
    public bool CanAutoDownload => !string.IsNullOrWhiteSpace(ExeUrl);
}

/// <summary>检查结果：成功给出信息，失败给出原因（网络不通、仓库还没发布等）。</summary>
public sealed record UpdateCheckResult(UpdateInfo? Info, string? Error)
{
    public bool Succeeded => Info is not null;
}

/// <summary>
/// 联网检查更新。
/// 主通道读仓库里的 version.json（改一行就能推送新版本通知，不必发 Release）；
/// 备用通道读 GitHub Releases 的 latest 接口，release 一发就能被老版本客户端发现。
/// </summary>
public static class UpdateService
{
    public const string RepoUrl = "https://github.com/LingShanZiCen/MHTimeHelper";
    public const string ReleasesUrl = RepoUrl + "/releases";
    public const string IssuesUrl = RepoUrl + "/issues";

    private const string ManifestUrl = "https://raw.githubusercontent.com/LingShanZiCen/MHTimeHelper/main/version.json";
    private const string ManifestUrlCdn = "https://cdn.jsdelivr.net/gh/LingShanZiCen/MHTimeHelper@main/version.json";
    private const string ManifestUrlFastly = "https://fastly.jsdelivr.net/gh/LingShanZiCen/MHTimeHelper@main/version.json";
    private const string ManifestUrlMaster = "https://raw.githubusercontent.com/LingShanZiCen/MHTimeHelper/master/version.json";
    private const string ApiUrl = "https://api.github.com/repos/LingShanZiCen/MHTimeHelper/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    /// <summary>当前程序版本（取自程序集，与 csproj 的 Version 一致）。</summary>
    public static Version CurrentVersion
        => typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0);

    /// <summary>当前版本号文本，如 1.0.0。</summary>
    public static string CurrentVersionText
    {
        get
        {
            // 优先取 csproj 里写的版本号（1.0.0 这种写法不会被 Version 对象原样保留）
            string? info = typeof(UpdateService).Assembly
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                int cut = info.IndexOf('+');
                string text = (cut > 0 ? info[..cut] : info).Trim();
                if (text.Length > 0)
                {
                    return text;
                }
            }

            Version v = CurrentVersion;
            if (v.Revision > 0)
            {
                return v.ToString();
            }

            return v.Build > 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}.0";
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        // GitHub 接口强制要求 User-Agent，缺了会直接返回 403
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Nightforge-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    /// <summary>
    /// 联网查询最新版本。任何异常都不向外抛，统一收进 Error，避免拖垮主程序。
    /// </summary>
    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        var errors = new List<string>();

        // 支持用环境变量指向自建/镜像的 version.json，便于内网分发与本地调试
        string? overrideUrl = Environment.GetEnvironmentVariable("NIGHTFORGE_UPDATE_MANIFEST");
        bool overridden = !string.IsNullOrWhiteSpace(overrideUrl);
        string[] manifestUrls = overridden
            ? [overrideUrl!.Trim()]
            : [ManifestUrl, ManifestUrlCdn, ManifestUrlFastly, ManifestUrlMaster];

        foreach (string url in manifestUrls)
        {
            try
            {
                using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    errors.Add($"version.json HTTP {(int)response.StatusCode}");
                    continue;
                }

                string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                UpdateInfo? info = ParseManifest(json);
                if (info is null)
                {
                    errors.Add("version.json 内容无法解析");
                    continue;
                }

                return new UpdateCheckResult(info, null);
            }
            catch (Exception ex)
            {
                errors.Add(ex is TaskCanceledException or OperationCanceledException ? "version.json 请求超时" : $"version.json {ex.Message}");
            }
        }

        if (overridden)
        {
            return new UpdateCheckResult(null, string.Join("；", errors));
        }

        try
        {
            using var response = await Http.GetAsync(ApiUrl, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                UpdateInfo? info = ParseRelease(json);
                if (info is not null)
                {
                    return new UpdateCheckResult(info, null);
                }

                errors.Add("Release 信息无法解析");
            }
            else if (response.StatusCode == HttpStatusCode.NotFound)
            {
                errors.Add("仓库暂无 Release");
            }
            else
            {
                errors.Add($"Release 接口 HTTP {(int)response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            errors.Add(ex is TaskCanceledException or OperationCanceledException ? "Release 接口请求超时" : ex.Message);
        }

        return new UpdateCheckResult(null, string.Join("；", errors));
    }

    /// <summary>判断远程版本是否比本机新。</summary>
    public static bool IsNewer(string? remoteVersion, Version? local = null)
    {
        Version? remote = ParseVersion(remoteVersion);
        if (remote is null)
        {
            return false;
        }

        return remote > (local ?? CurrentVersion);
    }

    /// <summary>把 "v1.2.0" / "1.2" / "1.2.0-beta" 统一解析成可比较的版本号。</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string cleaned = text.Trim().TrimStart('v', 'V');
        int cut = cleaned.IndexOfAny(['-', '+', '_', ' ']);
        if (cut > 0)
        {
            cleaned = cleaned[..cut];
        }

        return Version.TryParse(cleaned, out Version? version) ? version : null;
    }

    private static UpdateInfo? ParseManifest(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        string? version = GetString(root, "version");
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        string url = GetString(root, "url") ?? ReleasesUrl;
        string notes = GetString(root, "notes") ?? GetString(root, "changelog") ?? "";
        // 版本清单里可以直接给出 exe 直链、摘要与体积，静默更新据此下载并校验
        string exeUrl = GetString(root, "exeUrl") ?? GetString(root, "exe_url") ?? "";
        string sha256 = GetString(root, "sha256") ?? "";
        long size = GetLong(root, "size");
        return new UpdateInfo(
            version.Trim(), url, notes.Trim(), ParseChanges(root, version.Trim(), notes),
            exeUrl.Trim(), sha256.Trim(), size);
    }

    private static UpdateInfo? ParseRelease(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        string? tag = GetString(root, "tag_name");
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        string url = GetString(root, "html_url") ?? ReleasesUrl;
        string notes = GetString(root, "body") ?? "";
        // 备用通道：从 Release 的 assets 里找 exe，拿到直链、大小与 GitHub 给出的 sha256 摘要
        (string exeUrl, string sha256, long size) = ParseAsset(root);
        return new UpdateInfo(
            tag.Trim(), url, notes.Trim(), ParseChanges(root, tag.Trim(), notes),
            exeUrl, sha256, size);
    }

    /// <summary>从 Release 的 assets 中挑第一个 .exe：返回下载直链、sha256（接口给出时）与字节大小。</summary>
    private static (string ExeUrl, string Sha256, long Size) ParseAsset(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return ("", "", 0);
        }

        foreach (JsonElement asset in assets.EnumerateArray())
        {
            string? name = GetString(asset, "name");
            if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string url = GetString(asset, "browser_download_url") ?? "";
            if (url.Length == 0)
            {
                continue;
            }

            long size = asset.TryGetProperty("size", out JsonElement sizeElement)
                && sizeElement.ValueKind == JsonValueKind.Number
                && sizeElement.TryGetInt64(out long value)
                    ? value
                    : 0;

            // 新版 GitHub 接口会带 digest（形如 "sha256:xxxx"），老接口没有就留空，由下载侧降级为只校验大小
            string digest = GetString(asset, "digest") ?? "";
            string sha256 = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? digest["sha256:".Length..].Trim()
                : "";

            return (url, sha256, size);
        }

        return ("", "", 0);
    }

    private static long GetLong(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt64(out long number)
            ? number
            : 0;

    /// <summary>
    /// 解析更新条目，按优先级取：
    /// 1) 版本清单里的 changes 数组（元素可以是 "新增：xxx" 字符串，也兼容 { "category": "新增", "text": "xxx" } 对象）；
    /// 2) 没有 changes 就把 notes / release 正文按行拆分；
    /// 3) 两者都为空时回退到程序内置日志，保证弹窗永远有内容可展示。
    /// </summary>
    private static List<ChangeItem> ParseChanges(JsonElement root, string version, string notes)
    {
        var items = new List<ChangeItem>();

        if (root.TryGetProperty("changes", out JsonElement changes) && changes.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement element in changes.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    string? line = element.GetString();
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        items.AddRange(ChangeLog.Parse([line]));
                    }
                }
                else if (element.ValueKind == JsonValueKind.Object)
                {
                    string? category = GetString(element, "category") ?? GetString(element, "type");
                    string? text = GetString(element, "text") ?? GetString(element, "content") ?? GetString(element, "title");
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        items.Add(new ChangeItem(
                            string.IsNullOrWhiteSpace(category) ? ChangeLog.DefaultCategory : category.Trim(),
                            text.Trim()));
                    }
                }
            }
        }

        if (items.Count == 0)
        {
            items.AddRange(ChangeLog.Parse(SplitLines(notes)));
        }

        if (items.Count == 0)
        {
            items.AddRange(ChangeLog.Get(version));
        }

        return items;
    }

    private static IEnumerable<string> SplitLines(string? text)
        => string.IsNullOrEmpty(text) ? [] : text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
