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

    private const string Version105 = "1.0.5";
    private const string Version104 = "1.0.4";
    private const string Version103 = "1.0.3";
    private const string Version102 = "1.0.2";
    private const string Version101 = "1.0.1";
    private const string Version100 = "1.0.0";

    /// <summary>内置日志：版本号（不含 v 前缀）→ 条目原文。</summary>
    private static readonly Dictionary<string, string[]> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        [Version105] =
        [
            "新增：自动登录——把账号、服务器、角色名填进去，程序自己认字找按钮位置再点，不写死坐标，窗口大小或分辨率变了也不会点偏",
            "新增：自动登录设置按窗口分别保存，五开时每个窗口记各自的账号、服务器和角色，下次直接点「开始登录」",
            "新增：换账号——窗口记着的是别的号时，会点账号框右边那个三角，从客户端记住的账号列表里挑出目标账号登录，不用密码、也不用敲键盘（同一个账号前缀不同邮箱也能分清，比如 @126 和 @163）",
            "新增：目标服务器不在服务器列表默认那几页里时，会用左上角的搜索框自动搜出来再点（中文直接打字输入，不需要切换输入法）",
            "新增：登录过程实时显示每一步「当前在哪一屏、在找什么、找到没有、点在哪」，卡住时能一眼看出停在哪一屏",
            "修复：游戏窗口被别的窗口挡住时，截到的其实是盖在上面的那个窗口，于是认不出任何按钮、还会误判成「已经进游戏了」——现在截图前会先把游戏窗口切到最前面，确认真的切过去了再截",
            "修复：游戏以管理员权限运行时，本程序发出的模拟点击会被系统直接丢掉（表现为点了完全没反应）——现在会先检查权限，不对等时明确提示以管理员身份重新运行",
            "修复：游戏窗口被其他窗口挡住时，点击可能落到别的程序上——现在先切前台并确认真的切过去了，切不过去就跳过这一步而不是乱点",
            "修复：自动适配时窗口标题栏会被顶到屏幕外（只开一个 16:9 窗口时必然出现），最小化/关闭按钮点不到、窗口也拖不回来——现在排列前会先给标题栏和边框预留空间",
            "修复：排列完成后增加兜底，任何窗口只要超出工作区就整体挪回来（只挪不缩，尺寸和比例都不受影响）",
            "修复：升级后残留的 Nightforge_old.exe（约 67 MB）以前只在特定方式启动时才清理，现在每次启动都会顺手清掉",
            "修复：自动下载全部线路都失败时界面上毫无反应，现在会弹窗说明原因，可直接打开下载页，也可让它按断点继续重试",
            "修复：搜索服务器时中文打不进游戏的搜索框（框里只留一个空心方块）——这个客户端要的是 GBK 字节，而系统级输入递过去的是 Unicode；现在按「输入法字节 → GBK 双字节 → Unicode → 粘贴」四种方式逐条尝试并逐条抓图验证，哪种能进字就用哪种",
            "修复：点「上次登录的服务器」卡片时会点到卡片下面的装饰文字（认成「一：0，．毒嚹，产生0」那种乱码），卡片一点没动、登录直接失败——现在只挑纯汉字的服务器名，认不出来就按「登录游戏」正上方 12% 的几何位置点，并且允许再点一次",
            "修复：服务器列表页点「搜索框」结果点到了左边栏的「新服推荐」——搜索时截的图比窗口客户区多留了半屏，坐标却按客户区高度换算，位置整体往下偏了一截；现在搜索全程只按客户区那一帧取点，每次点击前都在当帧重新认一次搜索框",
            "修复：服务器页被误当成账号页，导致程序点了「登录游戏」直接进上次的角色、跳过选角色——现在账号页只按登录小窗判定，游戏画面上出现什么都不算",
            "修复：认不出内容的画面上会乱点（实测点到了游戏里的「巡逻」）——现在这种画面一律不点，并区分「还在加载」和「已经进游戏」",
            "修复：角色名是彩色艺术字，认错一两个字就选不中角色——现在按「点击登录」正上方的名字做近似匹配，认成「甜桩。」也能认出「甜柚」，且不会点到隔壁角色的按钮",
            "修复：点完「登录游戏」只等 2 秒，游戏还在连服务器就判定没反应、接着重复点——现在这类按钮等 4~4.5 秒，重试之间还会逐步拉长间隔",
            "新增：点击前先检查游戏窗口是否还有响应。游戏卡死时会直接提示「请关掉游戏重开」，不再对着卡住的窗口连点",
        ],
        [Version104] =
        [
            "修复：网络连不上 GitHub 下载页时「已经发现新版本，却一直卡着下不动」——现在会自动切换多条加速线路，一条不通换下一条",
            "优化：下载改为断点续传，中断后从上次的位置继续，不再从 0 重新下 70 MB",
            "优化：单条线路失败会先在原地重试一次再换线路，网络临时抖动不再导致整次更新作废",
            "优化：下载超时判定改为「连续一段时间收不到数据」才算失败，慢速网络也能下完（此前有 10 分钟总时长硬限制）",
            "新增：更新过程写入程序目录下的 update.log，检查、下载、替换每一步都有记录，出问题可直接查看原因",
            "新增：版本清单支持配置多条下载地址（exeUrls），镜像与官方直链可同时列出，客户端自动择优",
            "修复：几个清单通道的缓存时间不一致，刚发布的新版本可能读不到（实测 raw 缓存几分钟、jsDelivr 更长）——改为直读 GitHub 文件接口（不缓存），并同时比对多个通道取版本号最高的那个",
        ],
        [Version103] =
        [
            "新增：画面比例可直接在软件里改（16:9 / 4:3 / 竖屏，含 4K 变体），改动会写进游戏自己的设置文件，效果等同于在启动器里切换",
            "新增：「从游戏读取」按钮与启动时自动读取，软件里显示的比例始终是游戏当前真正在用的那个",
            "新增：分辨率模式增加「自动适配（按比例）」——按启动数量和画面比例反算窗口尺寸，保证不重叠、不超出屏幕，且客户区正好等于所选比例",
            "新增：窗口间距可调（0–60 像素），排列不再只能紧贴",
            "新增：一键退出游戏——按正常流程点 × 并确认，逐个窗口处理，日志实时显示进度与耗时",
            "优化：一键退出改为并行处理，多个窗口同时点 ×、共用一段等待，5 个窗口从 20 多秒降到 5 秒级",
            "优化：退出时抢前台改用 AttachThreadInput，且只有确认抢到前台才发按键，避免按键打到别的程序上",
            "优化：窗口识别增加进程名兜底（MyLauncher_x64r / MyGame_x64r），标题变了或还在加载的窗口也能被找到",
            "修复：多个游戏窗口时偶发「少认出一个窗口」，退出与排列都只处理 N-1 个的问题",
            "优化：界面整体重做——深色青紫主题、自绘标题栏、全套自定义控件样式，日志区可收起",
            "优化：应用图标换成自有角色图标，exe / 任务栏 / 托盘 / 标题栏四处统一",
            "优化：窗口按客户区对齐并补偿标题栏与边框，排出来的位置与尺寸和设定值完全一致，不再整体偏右下",
            "修复：5 个窗口在 1024×768 等尺寸下会大面积互相压住的问题（此前坐标按配置尺寸计算，与实际窗口大小脱节）",
            "修复：「不改变窗口大小」模式下用旧尺寸算位置、导致窗口被排到屏幕外或错位的问题",
            "优化：排列结果若有重叠或越界，日志里会直接提示怎么改",
        ],
        [Version102] =
        [
            "新增：自动下载更新——发现新版本后在后台把新版整包下好，下次打开软件自动换成新版，不用再去网页下载",
            "新增：设置里新增「自动下载更新」开关，关闭后仍只提示下载地址，由你自行决定",
            "新增：新版下载完成后可选择「立即重启更新」马上生效，或「下次启动更新」不打断当前使用",
            "优化：新版下载完成后自动校验文件大小与 SHA-256，校验不通过直接丢弃并保留旧版本",
            "优化：程序放在只读目录或没有写入权限时，自动退回「跳转网页手动下载」，不再卡住",
        ],
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
