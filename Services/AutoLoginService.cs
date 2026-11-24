using System.Text;
using System.IO;
using Nightforge.Models;

namespace Nightforge.Services;

/// <summary>
/// 自动登录：你只填「账号 / 服务器 / 角色」这几个字，程序把窗口画面截下来认字，
/// 在哪找到就点哪，不再依赖写死的坐标。
///
/// 认字用的是系统自带 OCR，找字用模糊匹配（游戏里的艺术字常被认错一两个字），
/// 所以「红 颜 知 己」「0红颜知己」这种都能对上。
/// </summary>
public sealed class AutoLoginService
{
    /// <summary>
    /// 角色名的近似匹配门槛。
    /// 角色名是彩色描边艺术字，「甜柚」实测会被认成「甜桩。」——只对上一个字，相似度 0.5。
    /// 所以不能要求 0.68，得放宽；但配对时只比「点击登录」正上方那一行，
    /// 不会跨卡片乱点，放宽也不至于进错号。
    /// </summary>
    private const double FuzzyRoleThreshold = 0.45;

    /// <summary>游戏内界面的典型按钮。认出几个就说明不是在登录流程里了。</summary>
    private static readonly string[] GameUiKeywords =
        ["包裹", "师门", "队伍", "好友", "骑乘", "技能", "商城", "排行", "挂机"];

    /// <summary>两次认字之间的间隔。一轮「抓图 + 识别」大约 0.2 秒，再加这点间隔不至于把 CPU 占满。</summary>
    private const int PollIntervalMs = 450;

    /// <summary>
    /// 连续多久认不出任何登录字样，就认定已经进了游戏。
    /// 点完按钮后要加载、切场景，中途画面是空的，所以这个值要够宽（20 秒）。
    /// </summary>
    private const double UnknownScreenToleranceSeconds = 20;

    private readonly LogService _log;

    public AutoLoginService(LogService log) => _log = log;

    /// <summary>
    /// 让某个窗口登录到指定角色。返回 null 表示走完了，否则是中断原因。
    /// </summary>
    public async Task<string?> LoginAsync(
        IntPtr hwnd,
        WindowLoginTarget target,
        LoginSettings settings,
        Action<string> report,
        CancellationToken ct)
    {
        if (!OcrService.IsAvailable)
        {
            return OcrService.UnavailableReason;
        }

        if (!NativeMethods.IsWindow(hwnd))
        {
            return "窗口已经不存在";
        }

        // 权限不对等时，系统会把本程序发出的模拟鼠标/键盘全部丢掉，表现为「点了没反应」。
        // 与其让用户对着不动弹的界面猜，不如直接说清楚。
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint gamePid);
        bool? gameElevated = NativeMethods.IsProcessElevated(gamePid);
        bool selfElevated = NativeMethods.IsProcessElevated((uint)Environment.ProcessId) == true;

        if (gameElevated == true && !selfElevated)
        {
            return "游戏是以管理员权限启动的，本程序不是，系统会拦掉本程序的模拟点击。"
                   + "请关掉本程序，右键「以管理员身份运行」后重试。";
        }

        if (NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            await Task.Delay(300, ct).ConfigureAwait(true);
        }

        ForceForeground(hwnd);
        await Task.Delay(300, ct).ConfigureAwait(true);

        if (settings.StartDelaySec > 0)
        {
            report($"等待 {settings.StartDelaySec} 秒，让登录界面加载完…");
            await Task.Delay(settings.StartDelaySec * 1000, ct).ConfigureAwait(true);
        }

        string accountKey = target.Account.Split('@')[0].Trim();
        string server = target.Server.Trim();
        string character = target.Character.Trim();

        // 登录过程中界面会自己跳：账号页 → 服务器页 → 角色页 → 游戏内，
        // 失败时还会往回退。所以不按固定顺序硬点，而是每一轮先认清楚当前停在哪一屏，再决定点哪。
        int stepTimeout = settings.StepTimeoutSec;
        DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(stepTimeout, 10) * 3 + 15);

        string lastScreen = "";
        string lastClickKey = "";
        int sameClickTimes = 0;
        DateTime? unknownSince = null;
        int captureFailures = 0;

        // 这一轮该点哪个窗口：平时是游戏主窗口，账号页那一屏换成悬浮窗
        IntPtr clickTarget = hwnd;

        // 点过「进入游戏」的时刻。刚点完的几秒里悬浮窗还没关，不能接着重复点。
        DateTime? enterClickedAt = null;

        // 点击失败的确切原因（比如游戏卡死）。上层直接用它报错，别再说「没有反应」这种废话。
        string? fatalReason = null;

        while (!ct.IsCancellationRequested)
        {
            if (DateTime.UtcNow > deadline)
            {
                return "登录超时：画面一直没出现可以点的按钮，可能网络卡住，或停在没见过的界面";
            }

            // 账号页是主窗口之外的独立悬浮窗（直播 / 录屏里根本看不到它）。
            // 有它就只认它：游戏画面里永远不会出现「邮箱 + 进入游戏」，
            // 拿游戏画面的文字去判账号页，只会把服务器页误判成账号页、然后乱点。
            IntPtr overlay = LoginOverlay.Find(hwnd);
            bool framedFromOverlay = false;
            ScreenCapturer.Frame? frame = null;

            if (overlay != IntPtr.Zero)
            {
                frame = ScreenCapturer.Capture(overlay, out _);
                if (frame is not null)
                {
                    framedFromOverlay = true;
                }
            }

            string captureError = "";
            if (frame is null)
            {
                frame = ScreenCapturer.Capture(hwnd, out captureError);
            }

            if (frame is null)
            {
                // 抓不到可信画面时，绝不能拿「认不出」当「已经进游戏了」——那样会假装成功
                captureFailures++;
                if (captureFailures == 1)
                {
                    report($"抓不到游戏画面：{captureError}");
                }

                if (captureFailures >= 8)
                {
                    return $"抓不到游戏画面（{captureError}）。请把游戏窗口切到最前面、别被别的窗口挡住，再重试。";
                }

                await Task.Delay(PollIntervalMs, ct).ConfigureAwait(true);
                continue;
            }

            captureFailures = 0;
            clickTarget = framedFromOverlay ? overlay : hwnd;

            (Found found, List<TextLine> lines) = await RecognizeFoundAsync(frame.Value, accountKey, server)
                .ConfigureAwait(true);

            // 关键：账号页的判据只能来自悬浮窗。游戏画面里出现「账号」两个字、出现 @ 都不算。
            bool onAccountScreen = framedFromOverlay && found.IsAccountScreen;

            TextLine? roleButton = found.Role;
            TextLine? enterButton = found.Enter;
            TextLine? serverButton = found.ServerButton;
            TextLine? serverLine = found.Server;

            string screen = onAccountScreen ? "账号页"
                : roleButton is not null ? "角色页"
                : serverLine is not null || serverButton is not null ? "服务器页"
                : framedFromOverlay ? "登录窗加载中"
                : "未知";

            if (screen != lastScreen)
            {
                report($"当前界面：{screen}");
                lastScreen = screen;
            }

            // 「这一屏认不出」要能持续一段时间才算真的不在登录流程里。
            // 点完按钮后游戏会加载好几秒，中途画面是空的，按次数数会太早收工。
            if (screen == "未知")
            {
                unknownSince ??= DateTime.UtcNow;
            }
            else
            {
                unknownSince = null;
            }

            // 同一屏同一个按钮连点三次画面都没变，说明这一下点不动，别再空转
            async Task<bool> ClickOnceAsync(TextLine line, string description, string key, int waitAfterMs = 2000)
            {
                // 动手之前先问一句「这窗口还活着吗」。
                // 游戏卡死的时候窗口还在、画面还是最后一帧，点多少下都没反应，
                // 继续连点只会看起来像在乱操作，不如立刻说清楚。
                if (!NativeMethods.IsWindowResponsive(clickTarget))
                {
                    fatalReason = "游戏窗口没有响应（看起来已经卡住了）。请把这个游戏窗口关掉、重新打开，再点开始登录。";
                    report(fatalReason);
                    return false;
                }

                if (key == lastClickKey)
                {
                    sameClickTimes++;
                    if (sameClickTimes >= 3)
                    {
                        report($"「{description}」连点 {sameClickTimes} 次画面都没变，先停在这里");
                        fatalReason ??= $"「{description}」连点了 {sameClickTimes} 次画面都没有变化。请看一眼游戏窗口是不是卡住了。";
                        return false;
                    }

                    // 画面没变多半是在加载（连服务器 / 切场景），越急着点越乱
                    int waitMs = 2500 * sameClickTimes;
                    report($"「{description}」还没反应，等 {waitMs / 1000.0:F0} 秒再试（游戏加载慢时很常见）");
                    await Task.Delay(waitMs, ct).ConfigureAwait(true);
                }
                else
                {
                    lastClickKey = key;
                    sameClickTimes = 0;
                }

                await ClickLineAsync(clickTarget, line, description, report, ct).ConfigureAwait(true);

                // 点击后给游戏反应时间。连服务器、切场景都要好几秒，
                // 等太短就会把「还在加载」当成「没点动」，然后对着加载画面重复点。
                await Task.Delay(waitAfterMs, ct).ConfigureAwait(true);
                return true;
            }

            // 1) 账号页（只在悬浮窗上判）：点「进入游戏」
            if (onAccountScreen)
            {
                // 刚点过就等它关窗，别对着还没消失的按钮连点
                if (enterClickedAt is not null
                    && (DateTime.UtcNow - enterClickedAt.Value).TotalSeconds < 8)
                {
                    await Task.Delay(PollIntervalMs, ct).ConfigureAwait(true);
                    continue;
                }

                if (enterButton is not null)
                {
                    if (!await ClickOnceAsync(enterButton, "进入游戏", "enter", 4000).ConfigureAwait(true))
                    {
                        return fatalReason ?? "「进入游戏」点了好几次都没有反应";
                    }

                    enterClickedAt = DateTime.UtcNow;
                    continue;
                }

                // 按钮是图片字，认成「豆求腭〖」这种；位置兜底必须基于当前这一帧，
                // 不能重新抓图——重抓可能抓到别的窗口，坐标就全错了。
                TextLine? fallback = FindBottomCenteredButton(lines);
                if (fallback is not null)
                {
                    report($"按钮文字认不出来（识别为「{fallback.Text}」），按位置点这个主按钮。");
                    if (!await ClickOnceAsync(fallback, "主按钮（按位置）", "enter-fallback").ConfigureAwait(true))
                    {
                        return fatalReason ?? "主按钮点了好几次都没有反应";
                    }

                    enterClickedAt = DateTime.UtcNow;
                    continue;
                }

                await Task.Delay(PollIntervalMs, ct).ConfigureAwait(true);
                continue;
            }

            // 2) 角色列表：一屏可能有好几个「点击登录」，必须按角色名就近配对，绝不点别人的
            if (roleButton is not null)
            {
                if (character.Length > 0)
                {
                    (bool ok, string? failReason) = await ClickRoleAsync(clickTarget, character, stepTimeout, report, ct)
                        .ConfigureAwait(true);
                    if (!ok)
                    {
                        return failReason;
                    }
                }
                else
                {
                    report("没有填角色名，点第一个「点击登录」（若进错角色，请把角色名填上）。");
                    if (!await ClickOnceAsync(roleButton, "点击登录", "role-any").ConfigureAwait(true))
                    {
                        return fatalReason ?? "「点击登录」点了好几次都没有反应";
                    }
                }

                await Task.Delay(1200, ct).ConfigureAwait(true);
                continue;
            }

            // 3) 服务器页：角色列表要点服务器名字才会展开，所以优先点名字。
            //    点下面的「登录游戏」会直接进上次玩过的角色，跳过选角色——那正是之前进错号的原因。
            if (serverLine is not null)
            {
                if (!await ClickOnceAsync(serverLine, $"服务器「{server}」", "server").ConfigureAwait(true))
                {
                    return fatalReason ?? $"点了好几次「{server}」都没有反应";
                }

                continue;
            }

            // 4) 只有「登录游戏」、没有服务器名（还没选过服务器的启动页）
            if (serverButton is not null)
            {
                if (!await ClickOnceAsync(serverButton, "登录游戏", "server-enter", 4500).ConfigureAwait(true))
                {
                    return fatalReason ?? "「登录游戏」点了好几次都没有反应";
                }

                continue;
            }

            // 5) 认不出任何登录字样：多半已经进游戏了。这一屏绝不点击。
            double unknownSeconds = unknownSince is null
                ? 0
                : (DateTime.UtcNow - unknownSince.Value).TotalSeconds;

            // 已经在游戏里：认出一堆游戏内按钮，却一个登录字样也没有。
            // 这时不该干等 20 秒再宣布「已进游戏」，直接把话说清楚。
            if (unknownSeconds >= 2)
            {
                int gameUiHits = GameUiKeywords.Count(
                    keyword => lines.Any(l => OcrService.Normalize(l.Text).Contains(keyword, StringComparison.Ordinal)));

                if (gameUiHits >= 3)
                {
                    report("画面里已经是游戏内的界面（认出包裹 / 任务 / 队伍这些按钮），这个账号当前已登录，不需要再登录。");
                    report("要换角色的话，先在游戏里退出到登录界面，再点开始登录。");
                    return null;
                }
            }

            // 已经从账号页点过「进入游戏」，说明流程确实走完了，进游戏就快一点收工；
            // 一开始就认不出（游戏还没起来）才用长容差，免得把慢启动误判成失败。
            double tolerance = enterClickedAt is null ? UnknownScreenToleranceSeconds : 8;
            if (unknownSeconds >= tolerance)
            {
                // 把这一屏存下来，万一判断错了（其实还停在某个没见过的界面），有图可查
                string dump = Path.Combine(Path.GetTempPath(), "nightforge_unknown.bmp");
                string? saved = ScreenCapturer.SaveBmp(frame.Value, dump);

                report($"连续 {unknownSeconds:F0} 秒没找到任何登录按钮，判断已经进到游戏里了。");
                if (saved is not null)
                {
                    report($"这一屏的画面已存到 {saved}，如果其实没进游戏，把它发我。");
                }

                return null;
            }

            await Task.Delay(PollIntervalMs, ct).ConfigureAwait(true);
        }

        return "已取消";
    }

    /// <summary>只做识别、不点击，用来在设置界面里试跑「能不能认出这些字」。</summary>
    public async Task<List<TextLine>> RecognizeAsync(IntPtr hwnd, CancellationToken ct)
    {
        ScreenCapturer.Frame? frame = ScreenCapturer.Capture(hwnd);
        if (frame is null)
        {
            return [];
        }

        return await OcrService.RecognizeAsync(frame.Value).ConfigureAwait(true);
    }

    /// <summary>在限定时间内反复认字，直到找到任意一个目标文字。</summary>
    private async Task<TextLine?> WaitForAnyAsync(
        IntPtr hwnd,
        IReadOnlyList<string> targets,
        int timeoutSec,
        Action<string>? report,
        CancellationToken ct)
    {
        DateTime deadline = DateTime.Now.AddSeconds(Math.Max(1, timeoutSec));
        bool reported = false;

        while (DateTime.Now <= deadline)
        {
            ct.ThrowIfCancellationRequested();

            ScreenCapturer.Frame? frame = ScreenCapturer.Capture(hwnd);
            if (frame is not null)
            {
                (TextLine? hit, int lineCount) = await MatchInFrameAsync(frame.Value, targets).ConfigureAwait(true);
                if (hit is not null)
                {
                    return hit;
                }

                if (lineCount == 0 && !reported)
                {
                    reported = true;
                    report?.Invoke("画面里没认出文字，可能还在加载，继续等…");
                }
            }

            await Task.Delay(PollIntervalMs, ct).ConfigureAwait(true);
        }

        return null;
    }

    /// <summary>
    /// 先在快通道里找，找不到再补一条慢通道。
    /// 慢通道贵（约 1.5 秒），但深底白字的按钮只有它能认出来，所以只在必要时才跑。
    /// </summary>
    private static async Task<(TextLine? Hit, int LineCount)> MatchInFrameAsync(
        ScreenCapturer.Frame frame,
        IReadOnlyList<string> targets)
    {
        List<TextLine> lines = await OcrService.RecognizeAsync(frame).ConfigureAwait(false);
        TextLine? hit = MatchAny(lines, targets);
        if (hit is not null)
        {
            return (hit, lines.Count);
        }

        List<TextLine> thorough = await OcrService.RecognizeAsync(frame, thorough: true).ConfigureAwait(false);
        return (MatchAny(thorough, targets), thorough.Count);
    }

    private static TextLine? MatchAny(IReadOnlyList<TextLine> lines, IReadOnlyList<string> targets)
    {
        foreach (string target in targets)
        {
            TextLine? hit = FindBest(lines, target);
            if (hit is not null)
            {
                return hit;
            }
        }

        return null;
    }

    /// <summary>在一批识别结果里找最像目标文字的那一行（设置界面「试一下」也用它）。</summary>
    public static TextLine? FindBest(IReadOnlyList<TextLine> lines, string target, double? threshold = null)
    {
        string normalizedTarget = OcrService.Normalize(target);
        if (normalizedTarget.Length == 0)
        {
            return null;
        }

        // 短名字（两个字）容易被别的词蹭到，要求更严
        double required = threshold ?? (normalizedTarget.Length <= 2 ? 0.9 : 0.68);

        // 要点的都是按钮或名字，都很短；底部公告 / 用户协议那种长句子偶尔会「凑巧凑够字」，
        // 实测「进入游戏」会被防沉迷公告那行命中（能凑出 3/4 个字），点下去就点到屏幕底部了
        int maxLength = normalizedTarget.Length * 2 + 2;

        TextLine? best = null;
        double bestScore = 0;

        foreach (TextLine line in lines)
        {
            string normalizedLine = OcrService.Normalize(line.Text);
            if (normalizedLine.Length > maxLength)
            {
                continue;
            }

            double score = OcrService.Similarity(normalizedLine, normalizedTarget);
            if (score > bestScore)
            {
                bestScore = score;
                best = line;
            }
        }

        return bestScore >= required ? best : null;
    }

    /// <summary>
    /// 在多行里找「离锚点最近」的目标行。
    /// 角色列表一屏有好几个「点击登录」，只认第一个会进错角色，
    /// 所以先认准角色名，再点它正下方那个按钮。
    /// </summary>
    public static TextLine? FindNearest(IReadOnlyList<TextLine> lines, TextLine anchor, string target)
    {
        string normalizedTarget = OcrService.Normalize(target);
        if (normalizedTarget.Length == 0)
        {
            return null;
        }

        int maxLength = normalizedTarget.Length * 2 + 2;

        TextLine? best = null;
        double bestDistance = double.MaxValue;

        foreach (TextLine line in lines)
        {
            string normalizedLine = OcrService.Normalize(line.Text);
            if (normalizedLine.Length > maxLength)
            {
                continue;
            }

            // 按钮文字要基本对上（比 FindBest 松一点：按钮容易被认差一两个字）
            double score = OcrService.Similarity(normalizedLine, normalizedTarget);
            if (score < 0.6)
            {
                continue;
            }

            // 优先同一列（角色名和它下面的「点击登录」竖着排），横向偏太多就是别的角色了
            double dx = Math.Abs(line.CenterX - anchor.CenterX);
            if (dx > 15)
            {
                continue;
            }

            // 距离：横向偏一点点算权重，主要还是看纵向差
            double distance = Math.Abs(line.CenterY - anchor.CenterY) + dx * 0.5;
            if (line.CenterY < anchor.CenterY)
            {
                distance += 20; // 在上面的基本是上一个角色的按钮，不优先
            }

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = line;
            }
        }

        return best;
    }

    /// <summary>
    /// 账号界面（已记住账号、只剩一个主按钮）的兜底识别。
    /// 先确认画面确实像账号界面（有邮箱或「账号」字样），再按位置找那个按钮，
    /// 免得在别的界面上乱点。
    /// </summary>
    private static async Task<TextLine?> FindAccountScreenButtonAsync(IntPtr hwnd, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        ScreenCapturer.Frame? frame = ScreenCapturer.Capture(hwnd);
        if (frame is null)
        {
            return null;
        }

        // 这条路径本来就是给「文字认不出的按钮」兜底，直接用慢通道，多花点时间没关系
        List<TextLine> lines = await OcrService.RecognizeAsync(frame.Value, thorough: true).ConfigureAwait(true);
        if (lines.Count == 0)
        {
            return null;
        }

        bool looksLikeAccount = lines.Any(l => l.Text.Contains('@'))
                                || lines.Any(l => OcrService.Normalize(l.Text).Contains("账号"));
        return looksLikeAccount ? FindBottomCenteredButton(lines) : null;
    }

    /// <summary>
    /// 找画面下半部、水平居中、文字很短的那一行，当作主按钮。
    /// 取最靠下的一个（账号界面上「使用其他账号登录」在上面，主按钮在它下面）。
    /// </summary>
    public static TextLine? FindBottomCenteredButton(IReadOnlyList<TextLine> lines)
    {
        TextLine? best = null;

        foreach (TextLine line in lines)
        {
            int length = OcrService.Normalize(line.Text).Length;
            if (length < 2 || length > 14)
            {
                continue;
            }

            if (Math.Abs(line.CenterX - 50) > 12)
            {
                continue;
            }

            if (line.CenterY < 55 || line.CenterY > 88)
            {
                continue;
            }

            if (best is null || line.CenterY > best.CenterY)
            {
                best = line;
            }
        }

        return best;
    }

    /// <summary>
    /// 服务器页上没有「点击登录」，只有「服务器名 + 登录游戏」。角色列表要点服务器名字才会出来，
    /// 所以在没填服务器的情况下，点「登录游戏」一般会直接进上次玩的角色——这也算达到目的。
    /// 填了服务器时不会走到这里（那一步已经点过名字了）。
    /// </summary>
    private async Task AdvanceToRoleScreenAsync(IntPtr hwnd, Action<string> report, CancellationToken ct)
    {
        if (await WaitForAnyAsync(hwnd, ["点击登录"], 1, null, ct).ConfigureAwait(true) is not null)
        {
            return;
        }

        TextLine? forward = await WaitForAnyAsync(hwnd, ["登录游戏", "进入游戏"], 3, null, ct)
            .ConfigureAwait(true);
        if (forward is null)
        {
            return;
        }

        report($"没看到角色列表，先点「{forward.Text}」直接进（想要指定角色，请在设置里填上服务器名）");
        await ClickLineAsync(hwnd, forward, forward.Text, report, ct).ConfigureAwait(true);
        await Task.Delay(1200, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// 认准角色名，点它正下方那个「点击登录」。
    /// 一屏有好几个角色卡片时，靠「名字 + 就近按钮」配对，绝不点别人的。
    /// </summary>
    private async Task<(bool Ok, string? FailReason)> ClickRoleAsync(
        IntPtr hwnd,
        string character,
        int timeoutSec,
        Action<string> report,
        CancellationToken ct)
    {
        DateTime deadline = DateTime.Now.AddSeconds(Math.Max(1, timeoutSec));
        bool saidWaiting = false;
        string? fuzzyProblem = null;
        List<string> seenNames = [];

        while (DateTime.Now <= deadline)
        {
            ct.ThrowIfCancellationRequested();

            ScreenCapturer.Frame? frame = ScreenCapturer.Capture(hwnd);
            if (frame is null)
            {
                await Task.Delay(PollIntervalMs, ct).ConfigureAwait(true);
                continue;
            }

            // 角色页的名字和「点击登录」都是彩色描边字，只有加强通道认得出，直接走加强通道
            List<TextLine> lines = await OcrService.RecognizeAsync(frame.Value, thorough: true)
                .ConfigureAwait(true);

            seenNames = lines
                .Where(line => line.Text.Length > 0)
                .Select(line => line.Text)
                .ToList();

            // 1) 名字认对了就直连，最稳
            TextLine? role = FindBest(lines, character);
            if (role is not null)
            {
                TextLine? button = FindNearest(lines, role, "点击登录");
                if (button is not null)
                {
                    await ClickLineAsync(hwnd, button, $"角色「{character}」下方的点击登录", report, ct)
                        .ConfigureAwait(true);
                    await Task.Delay(700, ct).ConfigureAwait(true);
                    return (true, null);
                }

                // 没看到按钮：可能是选中态才有，先点一下名字，再看有没有冒出来
                report($"找到角色「{character}」，但没看到它的「点击登录」，先点名字试试。");
                await ClickLineAsync(hwnd, role, $"角色「{character}」", report, ct).ConfigureAwait(true);
                await Task.Delay(900, ct).ConfigureAwait(true);

                ScreenCapturer.Frame? after = ScreenCapturer.Capture(hwnd);
                if (after is not null)
                {
                    List<TextLine> again = await OcrService.RecognizeAsync(after.Value).ConfigureAwait(true);
                    TextLine? button2 = FindNearest(again, role, "点击登录");
                    if (button2 is not null)
                    {
                        await ClickLineAsync(hwnd, button2, "点击登录", report, ct).ConfigureAwait(true);
                        await Task.Delay(700, ct).ConfigureAwait(true);
                    }
                }

                return (true, null);
            }

            // 2) 名字认不全（「甜柚」实测被认成「甜桩。」）：按「点击登录」+ 正上方名字配对
            (TextLine? fuzzyButton, string? recognizedName, double score, string? problem) =
                FindRoleButtonFuzzy(lines, character);

            if (problem is not null)
            {
                fuzzyProblem = problem;
            }
            else if (fuzzyButton is not null)
            {
                report($"按近似名字认出了角色「{recognizedName}」（与「{character}」相似度 {score:P0}），点它下面的「点击登录」");
                await ClickLineAsync(hwnd, fuzzyButton, $"角色「{character}」下方的点击登录", report, ct)
                    .ConfigureAwait(true);
                await Task.Delay(700, ct).ConfigureAwait(true);
                return (true, null);
            }

            if (!saidWaiting)
            {
                saidWaiting = true;
                report($"等角色「{character}」出现…");
            }

            await Task.Delay(PollIntervalMs, ct).ConfigureAwait(true);
        }

        if (fuzzyProblem is not null)
        {
            return (false, fuzzyProblem);
        }

        string hint = seenNames.Count > 0
            ? $"这一屏认到的文字里最像的名字是「{string.Join("」「", seenNames.Where(n => n.Length <= 10).Take(6))}」，"
            : "";

        return (false, $"没找到角色「{character}」。{hint}"
                       + "可以把角色名填成卡片上显示的名字；如果这一屏还没有角色卡片，说明还没进到选角色界面。");
    }

    private async Task ClickLineAsync(
        IntPtr hwnd,
        TextLine line,
        string description,
        Action<string> report,
        CancellationToken ct)
    {
        if (!NativeMethods.GetClientRect(hwnd, out NativeMethods.RECT rc))
        {
            report($"点击{description}失败：读不到窗口客户区");
            return;
        }

        var origin = new NativeMethods.POINT { X = 0, Y = 0 };
        if (!NativeMethods.ClientToScreen(hwnd, ref origin))
        {
            report($"点击{description}失败：窗口坐标换算失败");
            return;
        }

        int width = rc.Right - rc.Left;
        int height = rc.Bottom - rc.Top;

        int x = origin.X + (int)Math.Round(width * line.CenterX / 100.0);
        int y = origin.Y + (int)Math.Round(height * line.CenterY / 100.0);

        x = Math.Clamp(x, origin.X + 2, origin.X + width - 3);
        y = Math.Clamp(y, origin.Y + 2, origin.Y + height - 3);

        // 点之前再确认一次前台：游戏窗口被挡在后面时，点击会落到别的程序上，
        // 所以抢不到前台就宁可这一步不点，让上层提示用户，也不要乱点
        if (!ForceForeground(hwnd))
        {
            report($"点击{description}失败：游戏窗口抢不到前台（被其他程序挡着？先手动点一下游戏窗口再试）");
            return;
        }

        await Task.Delay(150, ct).ConfigureAwait(true);

        if (NativeMethods.GetForegroundWindow() != hwnd)
        {
            report($"点击{description}失败：点击前游戏窗口又失去前台，已放弃这一次点击");
            return;
        }

        report($"点击{description}（识别为「{line.Text}」，位置 {line.CenterX:F1}% , {line.CenterY:F1}%）");
        Click(x, y);
    }

    /// <summary>移动光标并点一下左键。引擎界面只认真实输入，所以走系统输入队列而不是发消息。</summary>
    private static void Click(int x, int y)
    {
        NativeMethods.SetCursorPos(x, y);
        Thread.Sleep(40);
        NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(50);
        NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }

    /// <summary>一屏的判定结果：每个登录相关字样有没有命中，命中了在哪。</summary>
    public readonly record struct Found(
        TextLine? Role,
        TextLine? Enter,
        TextLine? ServerButton,
        TextLine? SwitchAccount,
        TextLine? Account,
        TextLine? Server,
        bool HasEmail)
    {
        public bool Anything => Role is not null || Enter is not null || ServerButton is not null
                                || SwitchAccount is not null || Account is not null || HasEmail;

        public bool IsAccountScreen => Account is not null || SwitchAccount is not null || HasEmail;
    }

    /// <summary>
    /// 认字 + 判定，不点击。给登录流程用，也给「识别测试」和命令行探针用，
    /// 保证看到的结果和真正跑起来时一模一样。
    /// </summary>
    public async Task<(Found Found, List<TextLine> Lines)> RecognizeFoundAsync(
        ScreenCapturer.Frame frame,
        string accountKey,
        string server)
    {
        List<TextLine> lines = await OcrService.RecognizeAsync(frame).ConfigureAwait(true);
        Found found = FindAll(lines, accountKey, server);

        // 快通道一个判定词都没认到时，换加强通道（放大 2 倍 + 反色）再认一次。
        // 深底白字、金色描边的按钮常常只有加强通道能认出来，直接判「已进游戏」就是误报成功。
        if (!found.Anything)
        {
            List<TextLine> strong = await OcrService.RecognizeAsync(frame, thorough: true).ConfigureAwait(true);
            Found retry = FindAll(strong, accountKey, server);
            if (retry.Anything)
            {
                lines = strong;
                found = retry;
            }
        }

        // 服务器名认出来了、角色按钮没认出来：这一屏很可能是「服务器列表 + 角色列表」合在一起，
        // 而「点击登录」是彩色字，只有加强通道认得出。不补这一次，就会把角色页当成服务器页、
        // 再点一遍服务器名（实测就是这样把已经展开的角色列表又点回去了）。
        if (found.Server is not null && found.Role is null && !found.IsAccountScreen)
        {
            List<TextLine> strong = await OcrService.RecognizeAsync(frame, thorough: true).ConfigureAwait(true);
            Found retry = FindAll(strong, accountKey, server);
            if (retry.Role is not null)
            {
                lines = strong;
                found = retry;
            }
        }

        // 账号页的「进入游戏」是白字深底，快通道经常认不出来（只认得出上面的邮箱）。
        // 既然已经确定停在这一屏，就换加强通道把它找准——总比按位置瞎点稳妥。
        if (found.IsAccountScreen && found.Enter is null)
        {
            List<TextLine> strong = await OcrService.RecognizeAsync(frame, thorough: true).ConfigureAwait(true);
            Found retry = FindAll(strong, accountKey, server);
            if (retry.Enter is not null)
            {
                lines = strong;
                found = retry;
            }
        }

        return (found, lines);
    }

    /// <summary>判定这一屏是什么，并说明下一步该点哪。</summary>
    public async Task<string> DescribeScreenAsync(IntPtr hwnd, string accountKey, string server)
    {
        IntPtr overlay = LoginOverlay.Find(hwnd);
        IntPtr captureTarget = overlay != IntPtr.Zero ? overlay : hwnd;

        ScreenCapturer.Frame? frame = ScreenCapturer.Capture(captureTarget, out string error);
        if (frame is null)
        {
            return "抓不到画面：" + error;
        }

        (Found found, List<TextLine> lines) = await RecognizeFoundAsync(frame.Value, accountKey, server)
            .ConfigureAwait(true);

        // 与主流程同一套口径：账号页只能来自悬浮窗帧
        bool fromOverlay = overlay != IntPtr.Zero && captureTarget == overlay;
        string screen = fromOverlay && found.IsAccountScreen ? "账号页"
            : found.Role is not null ? "角色页"
            : found.ServerButton is not null || found.Server is not null ? "服务器页"
            : fromOverlay ? "登录窗加载中"
            : "未知";

        var sb = new StringBuilder();
        sb.AppendLine(LoginOverlay.Describe(overlay));
        sb.AppendLine($"截图来源 {(captureTarget == overlay && overlay != IntPtr.Zero ? "账号悬浮窗" : "游戏窗口")}，画面 {frame.Value.Width}×{frame.Value.Height}，认出 {lines.Count} 行");
        sb.AppendLine($"判定：{screen}");
        sb.AppendLine($"  点击登录：{Where(found.Role)}");
        sb.AppendLine($"  进入游戏：{Where(found.Enter)}");
        sb.AppendLine($"  登录游戏：{Where(found.ServerButton)}");
        sb.AppendLine($"  使用其他账号登录：{Where(found.SwitchAccount)}");
        sb.AppendLine($"  账号行：{Where(found.Account)}");
        sb.AppendLine($"  服务器行：{Where(found.Server)}");
        return sb.ToString();

        static string Where(TextLine? line)
            => line is null ? "没找到" : $"「{line.Text}」{line.CenterX:F1}% , {line.CenterY:F1}%";
    }

    /// <summary>把一屏的所有判定词一次算齐，换通道重算时不用抄一遍。</summary>
    private static Found FindAll(List<TextLine> lines, string accountKey, string server)
        => new(
            FindBest(lines, "点击登录"),
            FindBest(lines, "进入游戏"),
            FindBest(lines, "登录游戏"),
            FindBest(lines, "使用其他账号登录", 0.6),
            accountKey.Length > 0 ? FindBest(lines, accountKey) : null,
            server.Length > 0 ? FindBest(lines, server) : null,
            lines.Any(l => l.Text.Contains('@')));

    /// <summary>
    /// 按近似角色名找它的「点击登录」。
    ///
    /// 角色名是艺术字，认错一两个字是常态（「甜柚」→「甜桩。」）。所以不要求名字完全对上，
    /// 而是以每个「点击登录」为锚点、取它正上方那一行名字来比：相似度最高的那个按钮才算数。
    /// 好处是认错一个字也能选中，同时绝不会点到隔壁角色的按钮。
    /// </summary>
    public static (TextLine? Button, string? Name, double Score, string? Problem) FindRoleButtonFuzzy(
        IReadOnlyList<TextLine> lines,
        string character)
    {
        string target = OcrService.Normalize(character);
        if (target.Length == 0)
        {
            return (null, null, 0, null);
        }

        string[] words = ["点击登录"];
        List<TextLine> buttons = lines
            .Where(line => words.Any(w => OcrService.Similarity(OcrService.Normalize(line.Text), w) >= 0.68))
            .ToList();

        if (buttons.Count == 0)
        {
            return (null, null, 0, null);
        }

        List<(TextLine Button, TextLine? Name, double Score)> scored = buttons
            .Select(button =>
            {
                TextLine? name = FindNameAbove(lines, button);
                double score = name is null
                    ? 0
                    : OcrService.Similarity(OcrService.Normalize(name.Text), target);
                return (Button: button, Name: name, Score: score);
            })
            .OrderByDescending(x => x.Score)
            .ToList();

        (TextLine Button, TextLine? Name, double Score) top = scored[0];
        if (top.Score < FuzzyRoleThreshold)
        {
            return (null, top.Name?.Text, top.Score, null);
        }

        // 两个角色都像 → 不敢猜，让用户把名字填得更有区分度
        if (scored.Count > 1
            && scored[1].Score >= FuzzyRoleThreshold
            && top.Score - scored[1].Score < 0.15)
        {
            return (null, top.Name?.Text, top.Score,
                $"这一屏有两个角色都和「{character}」很像（「{top.Name?.Text}」「{scored[1].Name?.Text}」），"
                + "先把角色名填完整一点再试。");
        }

        return (top.Button, top.Name?.Text, top.Score, null);
    }

    /// <summary>找出按钮正上方那一行文字（角色卡片的名字就在「点击登录」上方）。</summary>
    private static TextLine? FindNameAbove(IReadOnlyList<TextLine> lines, TextLine button)
    {
        TextLine? best = null;

        foreach (TextLine line in lines)
        {
            if (line == button)
            {
                continue;
            }

            double gap = button.CenterY - line.CenterY;
            if (gap < 1.2 || gap > 9)
            {
                continue;
            }

            if (Math.Abs(line.CenterX - button.CenterX) > 10)
            {
                continue;
            }

            int length = OcrService.Normalize(line.Text).Length;
            if (length is < 1 or > 10)
            {
                continue;
            }

            // 别把另一个按钮当成名字
            if (OcrService.Similarity(OcrService.Normalize(line.Text), "点击登录") >= 0.6)
            {
                continue;
            }

            if (best is null || line.CenterY > best.CenterY)
            {
                best = line;
            }
        }

        return best;
    }

    /// <summary>两段文字像不像（忽略空格标点，0～1）。</summary>
    private static double Similar(string text, string target)
        => OcrService.Similarity(OcrService.Normalize(text), OcrService.Normalize(target));

    /// <summary>抢前台，实现见 WindowService（抓图和点击共用同一套逻辑）。</summary>
    public static bool ForceForeground(IntPtr hwnd) => WindowFocus.Force(hwnd);
}
