namespace Nightforge.Models;

/// <summary>一条更新条目：分类（新增 / 优化 / 修复 / 更新）+ 说明文本。</summary>
public sealed class ChangeItem
{
    /// <summary>分类，取值见 <see cref="ChangeLog.Categories"/>；无法归类时归入「更新」。</summary>
    public string Category { get; set; } = ChangeLog.DefaultCategory;

    /// <summary>条目正文。</summary>
    public string Text { get; set; } = "";

    public ChangeItem()
    {
    }

    public ChangeItem(string category, string text)
    {
        Category = category;
        Text = text;
    }

    public override string ToString() => $"{Category}：{Text}";
}

/// <summary>
/// 程序内置的更新日志。
/// 服务器 version.json 里的 changes / notes 解析不出来时，用这里的文案兜底，
/// 保证哪怕网络不通，「更新完成后弹窗」也能把这一版改了什么讲清楚。
/// </summary>
public static class ChangeLog
{
    /// <summary>无法识别分类时的兜底分组名。</summary>
    public const string DefaultCategory = "更新";

    /// <summary>固定展示顺序：新增、优化、修复、更新。</summary>
    public static IReadOnlyList<string> Categories { get; } = ["新增", "优化", "修复", DefaultCategory];

    private const string Version101 = "1.0.1";
    private const string Version100 = "1.0.0";

    /// <summary>内置日志：版本号（不含 v 前缀）→ 条目原文。</summary>
    private static readonly Dictionary<string, string[]> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        [Version101] =
        [
            "新增：软件更新后首次启动自动弹出「本次更新内容」窗口，清楚列出这一版更新了什么、优化了什么",
            "新增：主界面新增「更新日志」按钮，随时查看当前版本的更新内容",
            "新增：更新提示弹窗与更新日志窗口按「新增 / 优化 / 修复」分类展示更新说明",
            "优化：更新说明优先读取服务器版本清单，网络不通时自动使用程序内置日志兜底",
            "优化：更新弹窗高度随内容自适应，条目多时内部滚动",
            "修复：版本号在部分位置显示为 1.0 而不是 1.0.0 的问题",
        ],
        [Version100] =
        [
            "新增：C# / WPF 重写，一键多开、窗口排列、前置全部、老板键、热键全部保留",
            "新增：启动时联网检查更新",
            "修复：热键录制与开机注册问题",
        ],
    };

    /// <summary>取指定版本的内置更新日志；未知版本返回空列表。</summary>
    public static IReadOnlyList<ChangeItem> Get(string version)
    {
        string key = Normalize(version);
        return key.Length > 0 && BuiltIn.TryGetValue(key, out string[]? lines)
            ? Parse(lines)
            : [];
    }

    /// <summary>
    /// 把 "新增：xxx" / "优化:xxx" / "修复：xxx" 这类字符串按第一个中英文冒号拆成分类 + 正文。
    /// 没有冒号的整行归入「更新」；空行与以 # 开头的行忽略。
    /// </summary>
    public static IReadOnlyList<ChangeItem> Parse(IEnumerable<string>? raw)
    {
        var items = new List<ChangeItem>();
        if (raw is null)
        {
            return items;
        }

        foreach (string? line in raw)
        {
            if (line is null)
            {
                continue;
            }

            string text = line.Trim();
            if (text.Length == 0 || text.StartsWith('#'))
            {
                continue;
            }

            int cut = text.IndexOfAny(['：', ':']);
            if (cut > 0)
            {
                string category = text[..cut].Trim();
                string body = text[(cut + 1)..].Trim();
                if (body.Length == 0)
                {
                    continue;
                }

                items.Add(new ChangeItem(category.Length == 0 ? DefaultCategory : category, body));
            }
            else
            {
                items.Add(new ChangeItem(DefaultCategory, text));
            }
        }

        return items;
    }

    /// <summary>
    /// 按 <see cref="Categories"/> 的顺序分组，保证界面上「新增 / 优化 / 修复」始终按固定次序出现。
    /// 未知分类追加在固定分组之后，且只出现在实际存在时。
    /// </summary>
    public static IReadOnlyList<(string Category, List<ChangeItem> Items)> GroupedByCategory(IReadOnlyList<ChangeItem> items)
    {
        var buckets = new List<(string Category, List<ChangeItem> Items)>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (string category in Categories)
        {
            index[category] = buckets.Count;
            buckets.Add((category, []));
        }

        foreach (ChangeItem item in items)
        {
            string category = item.Category.Trim();
            if (category.Length == 0)
            {
                category = DefaultCategory;
            }

            if (!index.TryGetValue(category, out int at))
            {
                at = buckets.Count;
                index[category] = at;
                buckets.Add((category, []));
            }

            buckets[at].Items.Add(item);
        }

        buckets.RemoveAll(b => b.Items.Count == 0);
        return buckets;
    }

    /// <summary>版本号归一化：去掉首尾空白与开头的 v / V 前缀。</summary>
    private static string Normalize(string? version)
        => string.IsNullOrWhiteSpace(version) ? "" : version.Trim().TrimStart('v', 'V').Trim();
}
