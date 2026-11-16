using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Nightforge.Models;

namespace Nightforge.Services;

/// <summary>
/// 远程版本信息。Changes 为按「新增 / 优化 / 修复」分类的更新条目；
/// ExeUrl / ExeUrls / Sha256 / Size 供「静默自动更新」下载并校验用，缺失时只能退回手动下载。
/// </summary>
public sealed record UpdateInfo(
    string Version,
    string Url,
    string Notes,
    List<ChangeItem> Changes,
    string ExeUrl = "",
    string Sha256 = "",
    long Size = 0,
    IReadOnlyList<string>? ExeUrls = null)
{
    /// <summary>是否具备自动下载的条件（至少要给出一个 exe 下载地址）。</summary>
    public bool CanAutoDownload => AllExeUrls.Count > 0;

    /// <summary>
    /// 全部候选下载地址：清单里显式列出的（通常含镜像）在前，主地址在后，去空去重。
    /// 主地址放最后，是为了让直链不通时不必干等它超时。
    /// </summary>
    public IReadOnlyList<string> AllExeUrls
    {
        get
        {
            var list = new List<string>();

            void Add(string? value)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return;
                }

                string text = value.Trim();
                if (text.Length > 0 && !list.Contains(text, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(text);
                }
            }

            if (ExeUrls is not null)
            {
                foreach (string? item in ExeUrls)
                {
                    Add(item);
                }
            }

            Add(ExeUrl);
            return list;
        }
    }
}

/// <summary>检查结果：成功给出信息，失败给出原因（网络不通、仓库还没发布等）。</summary>
public sealed record UpdateCheckResult(UpdateInfo? Info, string? Error)
{
    public bool Succeeded => Info is not null;
}

/// <summary>
/// 联网检查更新。
/// 主通道用 GitHub Contents 接口直读仓库里的 version.json —— 这个接口不经过 CDN 缓存，
/// 发版后立刻就能读到新版本号；raw / jsDelivr 只作为备用（它们都有几分钟到几小时的缓存，
/// 曾经出现过「新版本已经发了，客户端读到的还是旧清单」的情况）。
/// 最后的兜底是 Releases 的 latest 接口：release 一发就能被老版本客户端发现。
/// </summary>
public static class UpdateService
{
    public const string RepoUrl = "https://github.com/LingShanZiCen/MHTimeHelper";
    public const string ReleasesUrl = RepoUrl + "/releases";
    public const string IssuesUrl = RepoUrl + "/issues";

    /// <summary>主通道：Contents 接口直读（不缓存，永远是最新的清单）。</summary>
    private const string ManifestUrlApi = "https://api.github.com/repos/LingShanZiCen/MHTimeHelper/contents/version.json?ref=main";

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
        // Accept 不在这里统一设置：Contents 接口需要 application/vnd.github.raw 才返回文件原文，
        // 统一设成 application/json 会退化成 base64 元数据。
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Nightforge-Updater/1.0");
        return client;
    }

    /// <summary>
    /// 联网查询最新版本。任何异常都不向外抛，统一收进 Error，避免拖垮主程序。
    ///
    /// 关键点：几个通道的缓存时间不一样（raw 约 5 分钟，jsDelivr 的 @main 可能几小时），
    /// 所以不能「谁先返回就用谁」——否则刚发的新版会被缓存里的旧版挡住。
    /// 这里并发拉全部通道，取版本号最高的那个。
    /// </summary>
    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        var errors = new List<string>();

        // 支持用环境变量指向自建/镜像的 version.json，便于内网分发与本地调试
        string? overrideUrl = Environment.GetEnvironmentVariable("NIGHTFORGE_UPDATE_MANIFEST");
        bool overridden = !string.IsNullOrWhiteSpace(overrideUrl);

        if (overridden)
        {
            (UpdateInfo? single, string? singleError) = await FetchManifestAsync(overrideUrl!.Trim(), ct).ConfigureAwait(false);
            return single is not null
                ? new UpdateCheckResult(single, null)
                : new UpdateCheckResult(null, singleError ?? "version.json 读取失败");
        }

        string[] manifestUrls = [ManifestUrlApi, ManifestUrl, ManifestUrlCdn, ManifestUrlFastly, ManifestUrlMaster];

        Task<(UpdateInfo? Info, string? Error)>[] tasks = manifestUrls
            .Select(url => FetchManifestAsync(url, ct))
            .ToArray();

        (UpdateInfo? Info, string? Error)[] fetched = await Task.WhenAll(tasks).ConfigureAwait(false);

        UpdateInfo? best = null;
        foreach ((UpdateInfo? info, string? error) in fetched)
        {
            if (info is null)
            {
                if (!string.IsNullOrWhiteSpace(error))
                {
                    errors.Add(error!);
                }

                continue;
            }

            // 取版本号最高的那份清单；版本号解析不出来的只能当兜底
            if (best is null || IsHigher(info.Version, best.Version))
            {
                best = info;
            }
        }

        if (best is not null)
        {
            return new UpdateCheckResult(best, null);
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

    /// <summary>
    /// 拉取单个 version.json 并解析。带 no-cache 头，尽量绕开中间缓存；
    /// Contents 接口额外要求 application/vnd.github.raw 才返回文件原文。
    /// 失败只返回原因，不抛异常（并发调用时由调用方汇总）。
    /// </summary>
    private static async Task<(UpdateInfo? Info, string? Error)> FetchManifestAsync(string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
            // Contents 接口对 raw 与 json 返回的结构不同：要原文就明确要 raw
            request.Headers.Accept.ParseAdd("application/vnd.github.raw");

            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (null, $"version.json HTTP {(int)response.StatusCode}");
            }

            string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            UpdateInfo? info = ParseManifest(text);

            // 万一接口还是回了元数据（含 base64 的 content 字段），就地解出来再解析一次
            if (info is null && TryDecodeContentsMetadata(text, out string? decoded))
            {
                info = ParseManifest(decoded!);
            }

            return info is not null ? (info, null) : (null, "version.json 内容无法解析");
        }
        catch (Exception ex)
        {
            return (null, ex is TaskCanceledException or OperationCanceledException
                ? "version.json 请求超时"
                : $"version.json {ex.Message}");
        }
    }

    /// <summary>从 Contents 接口的元数据里取出 base64 的 content 并解码。</summary>
    private static bool TryDecodeContentsMetadata(string text, out string? decoded)
    {
        decoded = null;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (GetString(doc.RootElement, "encoding") is not "base64"
                || GetString(doc.RootElement, "content") is not { Length: > 0 } content)
            {
                return false;
            }

            decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(content.Replace("\n", "")));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>candidate 是否比 reference 更新（版本号解析失败的一方视为更旧）。</summary>
    private static bool IsHigher(string candidate, string reference)
    {
        Version? a = ParseVersion(candidate);
        if (a is null)
        {
            return false;
        }

        Version? b = ParseVersion(reference);
        return b is null || a > b;
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
        // exeUrls：可选的多地址列表（镜像加速 / 容灾）。老版本客户端只认 exeUrl，不影响它们。
        List<string>? exeUrls = ParseStringArray(root, "exeUrls") ?? ParseStringArray(root, "exe_urls");
        return new UpdateInfo(
            version.Trim(), url, notes.Trim(), ParseChanges(root, version.Trim(), notes),
            exeUrl.Trim(), sha256.Trim(), size, exeUrls);
    }

    /// <summary>读取字符串数组字段，元素为空或整个字段不是数组时返回 null。</summary>
    private static List<string>? ParseStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement element) || element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<string>();
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? text = item.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                list.Add(text.Trim());
            }
        }

        return list.Count > 0 ? list : null;
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
        (List<string> exeUrls, string sha256, long size) = ParseAsset(root);
        return new UpdateInfo(
            tag.Trim(), url, notes.Trim(), ParseChanges(root, tag.Trim(), notes),
            exeUrls.Count > 0 ? exeUrls[0] : "", sha256, size, exeUrls);
    }

    /// <summary>从 Release 的 assets 中挑出所有 .exe 的下载直链，返回地址列表、sha256（接口给出时）与字节大小。</summary>
    private static (List<string> Urls, string Sha256, long Size) ParseAsset(JsonElement root)
    {
        var urls = new List<string>();
        if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return (urls, "", 0);
        }

        string sha256 = "";
        long size = 0;

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

            urls.Add(url);
            if (urls.Count > 1)
            {
                // 摘要与体积按第一个 exe 记录，多出来的资产只作为备用地址
                continue;
            }

            size = asset.TryGetProperty("size", out JsonElement sizeElement)
                && sizeElement.ValueKind == JsonValueKind.Number
                && sizeElement.TryGetInt64(out long value)
                    ? value
                    : 0;

            // 新版 GitHub 接口会带 digest（形如 "sha256:xxxx"），老接口没有就留空，由下载侧降级为只校验大小
            string digest = GetString(asset, "digest") ?? "";
            sha256 = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? digest["sha256:".Length..].Trim()
                : "";
        }

        return (urls, sha256, size);
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
