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
    /// 账号框右边「历史记录」那个下三角不是文字，OCR 看不到，只能按这个高度横向试几个位置。
    /// 从常见位置往两边排，点到弹出来为止（不是写死坐标，是「试到对为止」）。
    /// </summary>
    private static readonly double[] DropdownCandidatePercents = [87, 84, 90, 81, 93, 78];

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
        string accountFull = target.Account.Trim();
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

        // 换账号只做一次：悬浮窗记着的是上次的号，五个窗口常常全进同一个号。
        bool switchTried = false;
        bool warnedAfterSwitch = false;

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
            // 「登录表单」是点过「使用其他账号登录」之后那一屏（账号 + 密码 + 登录按钮），
            // 它和快速登录页都长在悬浮窗里，所以得分开认：有「登录」按钮就是表单，别在那儿乱点。
            bool onLoginForm = framedFromOverlay && found.IsLoginForm;
            bool onAccountScreen = framedFromOverlay && found.IsAccountScreen && !onLoginForm;

            TextLine? roleButton = found.Role;
            TextLine? enterButton = found.Enter;
            TextLine? serverButton = found.ServerButton;
            TextLine? serverLine = found.Server;

            string screen = onLoginForm ? "登录表单"
                : onAccountScreen ? "账号页"
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

            // 0) 换账号：悬浮窗上记着的是「上次登录的号」。五开时每个窗口要的号不一样，
            //    只点「进入游戏」就会全部进同一个号——所以先核对，不一致就走账号历史换号。
            bool accountDiffers = accountFull.Length > 0
                                  && (onAccountScreen || onLoginForm)
                                  && !(found.Account is not null && AccountMatches(found.Account.Text, accountFull));

            if (!switchTried && accountDiffers)
            {
                report(found.Account is not null
                    ? $"这一屏记着的是「{found.Account.Text}」，要登录的是「{accountFull}」，先换账号"
                    : $"认不出这一屏记着的账号（要登录「{accountFull}」），先打开账号历史核对一下");

                switchTried = true;

                // 账号页上还没打开表单：先点「使用其他账号登录」把表单叫出来
                if (onAccountScreen && found.SwitchAccount is not null
                    && !await ClickOnceAsync(found.SwitchAccount, "使用其他账号登录", "switch-account", 2200)
                        .ConfigureAwait(true))
                {
                    return fatalReason ?? "「使用其他账号登录」点了好几次都没有反应";
                }

                if (onLoginForm || found.SwitchAccount is not null)
                {
                    (bool switched, string? switchReason) = await SwitchAccountAsync(clickTarget, accountFull, report, ct)
                        .ConfigureAwait(true);
                    if (!switched)
                    {
                        return switchReason;
                    }

                    continue;
                }

                report("这一屏没看到「使用其他账号登录」，只能按当前记着的账号进。"
                       + "要换号，请先手动点一下「使用其他账号登录」再重试。");
                warnedAfterSwitch = true;
            }
            else if (switchTried && accountDiffers && !warnedAfterSwitch)
            {
                warnedAfterSwitch = true;
                report($"换完账号后显示的仍是「{found.Account?.Text}」，和要登录的「{accountFull}」对不上，先按当前账号进入。");
            }

            // 1) 登录表单（换号那一屏）：有「登录」就提交，别在这儿干等
            if (onLoginForm)
            {
                if (found.LoginButton is not null
                    && await ClickOnceAsync(found.LoginButton, "登录", "form-login", 3000).ConfigureAwait(true))
                {
                    continue;
                }

                await Task.Delay(PollIntervalMs, ct).ConfigureAwait(true);
                continue;
            }

            // 2) 账号页（只在悬浮窗上判）：点「进入游戏」
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
                    // 认出来的是「登录/登陆」说明这其实是换号表单，按位置点它正好也是提交，不算乱点
                    bool isSubmit = Similar(fallback.Text, "登录") >= 0.6 || Similar(fallback.Text, "登陆") >= 0.6;
                    string desc = isSubmit ? "登录" : "主按钮（按位置）";

                    report($"按钮文字认不出来（识别为「{fallback.Text}」），按位置点这个{desc}。");
                    if (!await ClickOnceAsync(fallback, desc, isSubmit ? "form-login-fallback" : "enter-fallback")
                            .ConfigureAwait(true))
                    {
                        return fatalReason ?? $"{desc}点了好几次都没有反应";
                    }

                    enterClickedAt = DateTime.UtcNow;
                    continue;
                }

                await Task.Delay(PollIntervalMs, ct).ConfigureAwait(true);
                continue;
            }

            // 3) 角色列表：一屏可能有好几个「点击登录」，必须按角色名就近配对，绝不点别人的
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

    /// <summary>
    /// 换成指定账号。
    ///
    /// 「使用其他账号登录」之后的表单里，账号框右边有个下三角，点开就是客户端记住的登录历史，
    /// 从里面挑目标账号再点「登录」——历史是客户端自己存的，所以换号不用密码、也不用敲键盘。
    /// 历史里没有这个号时如实说明，不猜密码、不乱点。
    /// </summary>
    private async Task<(bool Ok, string? Reason)> SwitchAccountAsync(
        IntPtr gameHwnd,
        string account,
        Action<string> report,
        CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);

        TextLine? field = null;
        TextLine? submit = null;
        List<TextLine> lines = [];
        ScreenCapturer.Frame? frame = null;
        LoginArea area = default;

        // 1) 等表单出来：账号框里有字（邮箱 / 手机号），并且有「登录」按钮。
        //    换号页有时是新开的一个窗口（不再是原来那个悬浮窗），所以先认悬浮窗，
        //    几秒还没出来就把同进程的其它小窗口也试一遍。
        DateTime started = DateTime.UtcNow;
        bool ready = false;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            bool includeOthers = (DateTime.UtcNow - started).TotalSeconds >= 5;

            foreach (IntPtr window in SwitchWindows(gameHwnd, includeOthers))
            {
                if (!TryCaptureArea(window, out LoginArea candidateArea, out ScreenCapturer.Frame? captured)
                    || captured is null)
                {
                    continue;
                }

                frame ??= captured;

                List<TextLine> scanned = await OcrService.RecognizeAsync(captured.Value, thorough: true)
                    .ConfigureAwait(true);

                TextLine? login = FindLoginButton(scanned);
                if (login is null)
                {
                    continue;
                }

                TextLine? textField = FindAccountField(scanned, login);
                if (textField is null)
                {
                    continue;
                }

                area = candidateArea;
                frame = captured;
                lines = scanned;
                field = textField;
                submit = login;
                ready = true;
                break;
            }

            if (ready)
            {
                break;
            }

            await Task.Delay(400, ct).ConfigureAwait(true);
        }

        if (!ready || field is null)
        {
            string dump = SaveDebug(frame, "nightforge_switch_form.bmp");
            return (false, "点了「使用其他账号登录」之后没看到账号输入框（这一屏可能不是账号密码登录）。"
                           + (dump.Length > 0 ? $"画面已存到 {dump}，发我看一眼就能对上。" : ""));
        }

        report($"账号框里现在是「{field.Text}」，点它右边的下三角，打开账号历史…");

        // 表单里本来就是目标账号：不必点下三角，直接提交（多一次保险，省掉一堆点击）
        if (submit is not null && AccountMatches(field.Text, account))
        {
            report($"账号框里已经是「{field.Text}」，不用换号，直接点「登录」");
            await ClickPointAsync(area, submit, "登录", report, ct).ConfigureAwait(true);
            await Task.Delay(3000, ct).ConfigureAwait(true);
            return (true, null);
        }

        // 2) 下三角是图不是字，OCR 看不到。按账号框这一行横向试几个位置，弹出来为止。
        List<TextLine> history = [];
        bool opened = false;

        foreach (double x in DropdownCandidates(field))
        {
            var triangle = new TextLine("▾", x, field.CenterY, 0, 0);
            await ClickPointAsync(area, triangle, $"账号框右边的下三角（{x:F0}%）", report, ct)
                .ConfigureAwait(true);
            await Task.Delay(800, ct).ConfigureAwait(true);

            if (!TryCaptureArea(area.Window, out area, out frame) || frame is null)
            {
                continue;
            }

            lines = await OcrService.RecognizeAsync(frame.Value, thorough: true).ConfigureAwait(true);
            history = AccountsBelow(lines, field);
            if (history.Count > 0)
            {
                opened = true;
                break;
            }
        }

        if (!opened)
        {
            string dump = SaveDebug(frame, "nightforge_switch_form.bmp");
            return (false, "没能点开账号框右边的历史记录（下三角）。"
                           + (dump.Length > 0 ? $"这一屏已存到 {dump}，发我一下我按实际位置对。" : ""));
        }

        // 3) 在历史里挑目标账号
        TextLine? pick = null;
        double bestScore = 0;
        foreach (TextLine item in history)
        {
            double score = AccountSimilarity(item.Text, account);
            if (score > bestScore)
            {
                bestScore = score;
                pick = item;
            }
        }

        if (pick is null || bestScore < 0.6)
        {
            string seen = string.Join("」「", history.Take(6).Select(l => l.Text));
            return (false, $"账号历史里没看到「{account}」" + (seen.Length > 0 ? $"（这一列是「{seen}」）" : "")
                           + "。先用这个账号手动登录一次，让客户端记住它，再点开始登录。");
        }

        report($"在账号历史里点「{pick.Text}」（要登录的是「{account}」）");
        await ClickPointAsync(area, pick, $"账号历史里的「{pick.Text}」", report, ct).ConfigureAwait(true);
        await Task.Delay(1500, ct).ConfigureAwait(true);

        // 4) 有的客户端选完直接进，有的还要再点一下「登录」
        if (TryCaptureArea(area.Window, out area, out frame) && frame is not null)
        {
            List<TextLine> after = await OcrService.RecognizeAsync(frame.Value, thorough: true).ConfigureAwait(true);
            TextLine? button = FindLoginButton(after);
            bool alreadyIn = FindBest(after, "进入游戏") is not null;

            if (button is not null && !alreadyIn)
            {
                report("选完账号还要点一下「登录」");
                await ClickPointAsync(area, button, "登录", report, ct).ConfigureAwait(true);
                await Task.Delay(3000, ct).ConfigureAwait(true);
            }
        }

        return (true, null);
    }

    /// <summary>
    /// 换号用的抓图区域：账号悬浮窗整块，下面再多留一截。
    /// 账号历史有时不是画在悬浮窗里，而是单独的小弹窗贴在它下面——只截悬浮窗就永远看不到列表。
    /// </summary>
    private readonly record struct LoginArea(int X, int Y, int Width, int Height, IntPtr Window);

    private static bool TryCaptureLoginArea(IntPtr gameHwnd, out LoginArea area, out ScreenCapturer.Frame? frame)
    {
        area = default;
        frame = null;

        IntPtr overlay = LoginOverlay.Find(gameHwnd);
        return TryCaptureArea(overlay != IntPtr.Zero ? overlay : gameHwnd, out area, out frame);
    }

    private static bool TryCaptureArea(IntPtr window, out LoginArea area, out ScreenCapturer.Frame? frame)
    {
        area = default;
        frame = null;

        if (!NativeMethods.GetClientRect(window, out NativeMethods.RECT rc))
        {
            return false;
        }

        int width = rc.Right - rc.Left;
        int height = rc.Bottom - rc.Top;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var origin = new NativeMethods.POINT { X = 0, Y = 0 };
        if (!NativeMethods.ClientToScreen(window, ref origin))
        {
            return false;
        }

        // 账号历史列表可能画在窗户外，下面多留半屏；主窗口太大就不留了
        int extra = height <= 800 ? height / 2 : 0;
        frame = ScreenCapturer.CaptureScreenRect(window, origin.X, origin.Y, width, height + extra);
        if (frame is null)
        {
            return false;
        }

        area = new LoginArea(origin.X, origin.Y, width, height + extra, window);
        return true;
    }

    /// <summary>换号流程要看的窗口：先账号悬浮窗，需要时再补同进程的其它小窗口。</summary>
    private static List<IntPtr> SwitchWindows(IntPtr gameHwnd, bool includeOthers)
    {
        var list = new List<IntPtr>();

        IntPtr overlay = LoginOverlay.Find(gameHwnd);
        list.Add(overlay != IntPtr.Zero ? overlay : gameHwnd);

        if (!includeOthers)
        {
            return list;
        }

        NativeMethods.GetWindowThreadProcessId(gameHwnd, out uint pid);

        NativeMethods.EnumWindows((candidate, _) =>
        {
            if (candidate == gameHwnd || list.Contains(candidate) || !NativeMethods.IsWindowVisible(candidate))
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(candidate, out uint candidatePid);
            if (candidatePid != pid)
            {
                return true;
            }

            if (!NativeMethods.GetClientRect(candidate, out NativeMethods.RECT rc))
            {
                return true;
            }

            int width = rc.Right - rc.Left;
            int height = rc.Bottom - rc.Top;
            if (width is < 200 or > 1100 || height is < 200 or > 1200)
            {
                return true;
            }

            list.Add(candidate);
            return list.Count < 5;   // 最多看 5 个窗口，别把几十个辅助窗口全抓一遍
        }, IntPtr.Zero);

        return list;
    }

    /// <summary>
    /// 找账号输入框那一行。优先带 @ 的（已经填着账号）；否则找「请输入账号」这类占位字，
    /// 并且必须在「登录」按钮上方——否则会把标题栏的「账号登录」当成输入框。
    /// </summary>
    private static TextLine? FindAccountField(List<TextLine> lines, TextLine submit)
    {
        TextLine? email = lines
            .Where(l => l.Text.Contains('@'))
            .Where(l => l.CenterY < submit.CenterY)
            .OrderBy(l => l.CenterY)
            .FirstOrDefault();

        if (email is not null)
        {
            return email;
        }

        return lines
            .Where(l => l.CenterY < submit.CenterY - 4)
            .Where(l => l.Text.Contains("请输入") || l.Text.Contains("帐号") || l.Text.Contains("账号")
                        || l.Text.Contains("邮箱") || l.Text.Contains("手机"))
            .OrderByDescending(l => l.CenterY)
            .FirstOrDefault();
    }

    /// <summary>按区域内的百分比坐标点一下（换号流程用：坐标是屏幕坐标，不受窗口客户区限制）。</summary>
    private async Task ClickPointAsync(
        LoginArea area,
        TextLine line,
        string description,
        Action<string> report,
        CancellationToken ct)
    {
        if (area.Width <= 0 || area.Height <= 0)
        {
            return;
        }

        // 点击前把账号窗提到前台：不在前台时系统会把模拟输入丢掉
        if (!ForceForeground(area.Window))
        {
            report($"点击{description}失败：账号窗抢不到前台（被其他程序挡着？先点一下游戏窗口再试）");
            return;
        }

        await Task.Delay(150, ct).ConfigureAwait(true);

        int x = area.X + (int)Math.Round(area.Width * line.CenterX / 100.0);
        int y = area.Y + (int)Math.Round(area.Height * line.CenterY / 100.0);

        report($"点击{description}（识别为「{line.Text}」，屏幕 {x},{y}）");
        Click(x, y);
    }

    /// <summary>下三角的横向候选：先试账号文字右边缘，再退到画面右侧几个常见位置。</summary>
    private static IEnumerable<double> DropdownCandidates(TextLine field)
    {
        var list = new List<double> { field.Left + field.Width + 1.5, field.Left + field.Width + 4 };
        list.AddRange(DropdownCandidatePercents);
        return list.Where(x => x is > 5 and < 96).Distinct();
    }

    /// <summary>账号框下面那些像账号的行（历史记录列表）。</summary>
    private static List<TextLine> AccountsBelow(List<TextLine> lines, TextLine field)
        => lines
            .Where(l => l.CenterY > field.CenterY + 2 && LooksLikeAccountText(l.Text))
            .OrderBy(l => l.CenterY)
            .ToList();

    /// <summary>这行像不像账号（邮箱 / 手机号 / 一串字母数字）。</summary>
    private static bool LooksLikeAccountText(string text)
    {
        string s = OcrService.Normalize(text);
        if (s.Length < 4)
        {
            return false;
        }

        if (s.Contains('@'))
        {
            return true;
        }

        // 历史列表里可能只显示「liaozicen041203」这样的前缀
        return s.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-');
    }

    /// <summary>
    /// 显示的账号是不是要登录的那个。
    /// 邮箱必须比域名——两个号常常前缀一模一样、只有 @126.com / @163.com 不同，
    /// 只比整体相似度会当成同一个号（实测 0.92）。
    /// </summary>
    public static bool AccountMatches(string shown, string target) => AccountSimilarity(shown, target) >= 0.72;

    /// <summary>两个账号的相似度；域名不同直接算 0。</summary>
    public static double AccountSimilarity(string shown, string target)
    {
        string a = CleanAccount(shown);
        string b = CleanAccount(target);
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        int ai = a.LastIndexOf('@');
        int bi = b.LastIndexOf('@');

        if (ai > 0 && bi > 0)
        {
            if (!string.Equals(a[(ai + 1)..], b[(bi + 1)..], StringComparison.Ordinal))
            {
                return 0;
            }

            string la = a[..ai];
            string lb = b[..bi];

            // 客户端常把账号打码成 lia***@163.com：星号当通配符比，别把同一个号当成别人
            if (la.Contains('*') || lb.Contains('*'))
            {
                return MaskedPrefixMatches(la, lb) ? 0.9 : 0;
            }

            return OcrService.Similarity(la, lb);
        }

        // 有一边没认出域名：只比前缀，认不全时按包含关系给个保底分
        string pa = ai > 0 ? a[..ai] : a;
        string pb = bi > 0 ? b[..bi] : b;

        if (pa.Contains(pb, StringComparison.Ordinal) || pb.Contains(pa, StringComparison.Ordinal))
        {
            return 0.9;
        }

        return OcrService.Similarity(pa, pb);
    }

    /// <summary>
    /// 打码账号（lia***）和完整账号比：星号当通配符，把认得到的几段按顺序在完整账号里找到就算同一个。
    /// </summary>
    private static bool MaskedPrefixMatches(string first, string second)
    {
        string masked = first.Contains('*') ? first : second;
        string plain = ReferenceEquals(masked, first) ? second : first;

        string[] segments = masked.Split('*', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return false;   // 整段都是星号，认不出是谁
        }

        int index = 0;
        foreach (string segment in segments)
        {
            int hit = plain.IndexOf(segment, index, StringComparison.Ordinal);
            if (hit < 0)
            {
                return false;
            }

            index = hit + segment.Length;
        }

        return true;
    }

    /// <summary>把账号行归一化：去空格、转小写、把认错的中文标点换回 . 和 @。</summary>
    private static string CleanAccount(string text)
    {
        var sb = new StringBuilder(text.Length);

        foreach (char raw in text)
        {
            char ch = char.ToLowerInvariant(raw);
            if (char.IsWhiteSpace(ch))
            {
                continue;
            }

            sb.Append(ch switch
            {
                '＠' => '@',
                '。' or '，' or '、' or '·' or '•' => '.',
                _ => ch
            });
        }

        return sb.ToString();
    }

    /// <summary>把画面存下来，对不上时好排查。</summary>
    private static string SaveDebug(ScreenCapturer.Frame? frame, string fileName)
        => frame is null
            ? ""
            : ScreenCapturer.SaveBmp(frame.Value, Path.Combine(Path.GetTempPath(), fileName)) ?? "";

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
        bool HasEmail,
        TextLine? LoginButton)
    {
        public bool Anything => Role is not null || Enter is not null || ServerButton is not null
                                || SwitchAccount is not null || Account is not null || HasEmail
                                || LoginButton is not null;

        /// <summary>
        /// 快速登录页（记住账号那一屏：邮箱 + 进入游戏 + 使用其他账号登录）。
        /// 有「登录」按钮的是换号表单，不算这一屏——两张脸都长在悬浮窗里，必须分开。
        /// </summary>
        public bool IsAccountScreen
            => LoginButton is null && (Account is not null || SwitchAccount is not null || Enter is not null);

        /// <summary>换号表单（账号输入框 + 密码 + 登录按钮）。</summary>
        public bool IsLoginForm => LoginButton is not null && Enter is null;
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

        // 账号页的「进入游戏」、换号表单的「登录」都是白字深底，快通道经常认不出来（只认得出邮箱）。
        // 既然已经确定停在这一屏，就换加强通道把它找准——总比按位置瞎点稳妥。
        if (found.IsAccountScreen && found.Enter is null)
        {
            List<TextLine> strong = await OcrService.RecognizeAsync(frame, thorough: true).ConfigureAwait(true);
            Found retry = FindAll(strong, accountKey, server);
            if (retry.Enter is not null || retry.LoginButton is not null)
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
        string screen = fromOverlay && found.IsLoginForm ? "登录表单"
            : fromOverlay && found.IsAccountScreen ? "账号页"
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
        sb.AppendLine($"  登录（换号表单）：{Where(found.LoginButton)}");
        sb.AppendLine($"  使用其他账号登录：{Where(found.SwitchAccount)}");
        sb.AppendLine($"  账号行：{Where(found.Account)}");
        sb.AppendLine($"  服务器行：{Where(found.Server)}");

        if (accountKey.Length > 0)
        {
            bool matches = found.Account is not null && AccountMatches(found.Account.Text, accountKey);
            sb.AppendLine($"  与目标账号「{accountKey}」："
                          + (found.Account is null ? "认不出显示的账号，会先打开账号历史核对"
                              : matches ? "一致，直接用这个号进" : "不一致，会点「使用其他账号登录」换号"));
        }

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
            FindShownAccount(lines, accountKey),
            server.Length > 0 ? FindBest(lines, server) : null,
            lines.Any(l => l.Text.Contains('@')),
            FindLoginButton(lines));

    /// <summary>
    /// 认「这一屏显示的账号」，用来判断要不要换号。
    /// 优先取最上面那行带 @ 的（表单里账号框在上、历史记录列表在下），
    /// 没有邮箱才退回到和目标前缀最像的那一行。
    /// </summary>
    private static TextLine? FindShownAccount(List<TextLine> lines, string accountKey)
    {
        TextLine? email = lines
            .Where(l => l.Text.Contains('@'))
            .OrderBy(l => l.CenterY)
            .FirstOrDefault();

        if (email is not null)
        {
            return email;
        }

        return accountKey.Length > 0 ? FindBest(lines, accountKey, 0.62) : null;
    }

    /// <summary>找「登录」按钮（快通道认不出深底白字时，调用方会换加强通道再算一遍）。</summary>
    private static TextLine? FindLoginButton(List<TextLine> lines)
    {
        TextLine? best = null;
        double bestScore = 0;

        foreach (TextLine line in lines)
        {
            string text = OcrService.Normalize(line.Text);

            // 「登录」两个字，最多容忍认成四个字（「登 录」「登录※」这种）
            if (text.Length is < 2 or > 4)
            {
                continue;
            }

            double score = Math.Max(
                OcrService.Similarity(text, "登录"),
                OcrService.Similarity(text, "登陆"));

            if (score > bestScore)
            {
                bestScore = score;
                best = line;
            }
        }

        return bestScore >= 0.85 ? best : null;
    }

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
