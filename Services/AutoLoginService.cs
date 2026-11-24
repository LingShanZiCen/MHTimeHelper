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
    /// 三角算「暗」的灰度上限。实测三角平均亮度 105~130、边缘 174，
    /// 面板底色 235 以上——卡 165 能把三角整块圈进来，又不碰边框和底色。
    /// </summary>
    private const int TriangleDarkThreshold = 165;

    /// <summary>
    /// 连续多久认不出任何登录字样，就认定已经进了游戏。
    /// 点完按钮后要加载、切场景，中途画面是空的，所以这个值要够宽（20 秒）。
    /// </summary>
    private const double UnknownScreenToleranceSeconds = 20;

    /// <summary>
    /// 服务器列表里没有目标服时，停下来等用户手动点一下的时间（秒）。
    /// 游戏只认输入法，程序往里打中文只会得到空心方块，所以这一步交给用户；
    /// 点一次游戏就记住了，之后这个窗口不必再选。
    /// </summary>
    private const int ManualServerWaitSeconds = 45;

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

        // 服务器列表里没有目标服时，等你手动点一下的截止时间
        DateTime? manualServerWaitUntil = null;
        int serverCardTries = 0;

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

            // 搜索框里也可能显示着服务器名，那一行不是服务器按钮：
            // 点它只会把光标点回输入框。所以与搜索框同一高度的行一律不算。
            TextLine? searchBox = found.Search;
            if (serverLine is not null && searchBox is not null
                && Math.Abs(serverLine.CenterY - searchBox.CenterY) < 3)
            {
                serverLine = null;
            }

            string screen = onLoginForm ? "登录表单"
                : onAccountScreen ? "账号页"
                : roleButton is not null ? "角色页"
                : serverLine is not null || serverButton is not null ? "服务器页"
                : found.IsServerList ? "服务器列表"
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

                // 换号入口就是账号框右边那个三角，直接把账号列表拉出来挑号。
                // （子流程里三角点不开时，会自己退回点「使用其他账号登录」再试一次）
                (bool switched, string? switchReason) = await SwitchAccountAsync(clickTarget, accountFull, report, ct)
                    .ConfigureAwait(true);
                if (!switched)
                {
                    return switchReason;
                }

                continue;
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

            // 2.5) 服务器列表页上没看到目标服：这一屏默认只列当前分区的那几个服。
            //      之前试过自动用左上角的搜索框搜，但这个客户端只认输入法：
            //      程序送进去的字（Unicode、GBK 字节、粘贴都试过）一律变成空心方块，
            //      所以不再折腾——直接请你手动点一次。点一次游戏就记住了，
            //      下次这张卡片直接就是它，这个窗口也就不用再选。
            if (server.Length > 0 && serverLine is null && found.IsServerList)
            {
                if (manualServerWaitUntil is null)
                {
                    manualServerWaitUntil = DateTime.UtcNow.AddSeconds(ManualServerWaitSeconds);
                    SaveDebug(frame, "nightforge_server_page.bmp");
                    report($"服务器列表里没看到「{server}」。请手动点一下这个服务器——"
                           + $"只点这一次，游戏会记住，以后这个窗口就不用再选。我在这儿等着继续（最多等 {ManualServerWaitSeconds} 秒）…");
                }
                else if (DateTime.UtcNow > manualServerWaitUntil)
                {
                    string dump = SaveDebug(frame, "nightforge_server_page.bmp");
                    return $"服务器列表里没有「{server}」，等了 {ManualServerWaitSeconds} 秒没看到你点它。"
                           + "可以手动点一下这个服务器，或者把这个窗口的服务器名改成列表里已有的那个。"
                           + "我没有去点「登录游戏」——那是进上次玩过的角色，会跳过选服选角。"
                           + (dump.Length > 0 ? $"（画面已存到 {dump}）" : "");
                }

                await Task.Delay(PollIntervalMs, ct).ConfigureAwait(true);
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
                // 下面的「登录游戏」是「进上次玩过的角色」：点它就跳过选服选角。
                // 那一屏通常是「上次登录的服务器卡片 + 登录游戏」，卡片上写着别的服名，
                // 点卡片才会展开完整服务器列表（有搜索框那一屏）。
                // 实测窗口 3 点的就是卡片（42%,57%），点完列表才出来；窗口 1、2 点的是「登录游戏」，
                // 结果直接进了上次的角色。
                if (server.Length > 0 && serverLine is null)
                {
                    TextLine card = FindServerCardAbove(lines, serverButton);

                    // 卡片要连点两次才肯开的情况是有的（界面动画没走完，第一下点空了），
                    // 所以这里允许试两次：第一次按认到的位置，第二次按几何位置。
                    if (serverCardTries < 2)
                    {
                        serverCardTries++;

                        if (serverCardTries == 1)
                        {
                            SaveDebug(frame, "nightforge_server_page.bmp");
                        }

                        report($"这一屏是「上次登录的服务器」，先点卡片「{card.Text}」进完整服务器列表");

                        if (!await ClickOnceAsync(card, $"服务器卡片「{card.Text}」", "server-card", 2500)
                                .ConfigureAwait(true))
                        {
                            return fatalReason ?? "点服务器卡片没有反应";
                        }

                        continue;
                    }

                    string dump = SaveDebug(frame, "nightforge_server_page.bmp");
                    return "点了服务器卡片之后还是没出现完整的服务器列表，"
                           + $"找不到「{server}」。没有点「登录游戏」——那是进上次玩过的角色，会跳过选服选角。"
                           + (dump.Length > 0 ? $"（画面已存到 {dump}，发我一眼就能对上。）" : "");
                }

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
    /// 实测（用户截图）：账号框右边那个三角就是换号入口——点开之后，客户端记住的账号
    /// 直接在窗口里一条条列出来，点哪个就用哪个号登，既不用密码、也不用敲键盘。
    /// 所以这里先点三角；三角点不开，再退回「使用其他账号登录」重试一次。
    /// 列表里没有这个号时如实说明，不猜密码、不乱点。
    /// </summary>
    private async Task<(bool Ok, string? Reason)> SwitchAccountAsync(
        IntPtr gameHwnd,
        string account,
        Action<string> report,
        CancellationToken ct)
    {
        string lastProblem = "";

        // 第 1 轮：直接点三角（常态）；第 2 轮：三角没反应，点「使用其他账号登录」再点三角
        for (int attempt = 0; attempt < 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (!TryCaptureLoginArea(gameHwnd, out LoginArea area, out ScreenCapturer.Frame? frame)
                || frame is null)
            {
                await Task.Delay(700, ct).ConfigureAwait(true);
                continue;
            }

            List<TextLine> lines = await OcrService.RecognizeAsync(frame.Value, thorough: true).ConfigureAwait(true);
            TextLine? field = FindShownField(lines);

            if (field is null)
            {
                lastProblem = "这一屏认不出账号输入框";
                await Task.Delay(1000, ct).ConfigureAwait(true);
                continue;
            }

            // 账号框里已经是目标号，不用换
            if (AccountMatches(field.Text, account))
            {
                return (true, null);
            }

            if (attempt == 1)
            {
                TextLine? switchLink = FindBest(lines, "使用其他账号登录", 0.6);
                if (switchLink is null)
                {
                    lastProblem = "这一屏也没有「使用其他账号登录」";
                    continue;
                }

                report("三角点不开账号列表，改成点「使用其他账号登录」再试一次");
                await ClickPointAsync(area, switchLink, "使用其他账号登录", report, ct).ConfigureAwait(true);
                await Task.Delay(1800, ct).ConfigureAwait(true);

                if (!TryCaptureLoginArea(gameHwnd, out area, out frame) || frame is null)
                {
                    continue;
                }

                lines = await OcrService.RecognizeAsync(frame.Value, thorough: true).ConfigureAwait(true);
                field = FindShownField(lines);
                if (field is null)
                {
                    lastProblem = "点了「使用其他账号登录」之后认不出账号输入框";
                    continue;
                }
            }

            report($"账号框里现在是「{field.Text}」，点它右边的三角，把账号列表拉出来…");

            // 列表可能已经开着（上一次点开了），先看一眼，别又点一下把它收回去
            List<TextLine> history = AccountsBelow(lines, field, frame.Value.Height, area.ClientHeight);

            bool listOpened = false;

            if (history.Count == 0)
            {
                // 三角是图形，OCR 看不到：先从像素里把它找出来，找不到再按常见位置试
                double? fromPixels = FindTriangleX(frame.Value, field);
                report(fromPixels is null
                    ? "没在画面里认出那个三角，按几个常见位置逐个试"
                    : $"三角在 {fromPixels.Value:F1}% 的位置");

                var candidates = new List<double>();
                if (fromPixels is not null)
                {
                    candidates.Add(fromPixels.Value);
                }

                candidates.AddRange(DropdownCandidates(field));

                foreach (double x in candidates)
                {
                    int beforeHeight = area.ClientHeight;

                    var triangle = new TextLine("▾", x, field.CenterY, 0, 0);
                    await ClickPointAsync(area, triangle, $"账号框右边的三角（{x:F0}%）", report, ct)
                        .ConfigureAwait(true);
                    await Task.Delay(800, ct).ConfigureAwait(true);

                    if (!TryCaptureLoginArea(gameHwnd, out area, out frame) || frame is null)
                    {
                        continue;
                    }

                    lines = await OcrService.RecognizeAsync(frame.Value, thorough: true).ConfigureAwait(true);
                    field = FindShownField(lines) ?? field;
                    history = AccountsBelow(lines, field, frame.Value.Height, area.ClientHeight);
                    if (history.Count > 0)
                    {
                        listOpened = true;
                        break;
                    }

                    // 列表是往下展开的，展开时窗口会被撑高。哪怕一个字都没认出来，
                    // 也不能继续点下一个候选——那一下正好把刚展开的列表点回去。
                    if (area.ClientHeight > beforeHeight + 12)
                    {
                        report($"点了三角之后窗口从 {beforeHeight} 高变成 {area.ClientHeight} 高，"
                               + "账号列表确实展开了，只是里面的账号行没认出来");
                        listOpened = true;
                        break;
                    }
                }
            }

            if (history.Count == 0)
            {
                if (listOpened)
                {
                    string dump = SaveDebug(frame, "nightforge_switch_list.bmp");
                    return (false, "账号列表展开了，但认不出里面的账号行"
                                   + (dump.Length > 0 ? $"（这一屏已存到 {dump}，发我看一眼就能对上）" : "")
                                   + "。");
                }

                lastProblem = "点了三角也没把账号列表拉出来";
                continue;
            }

            // 在列表里挑目标账号：比域名，126 和 163 是两个号
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
                string dump = SaveDebug(frame, "nightforge_switch_list.bmp");
                return (false, $"账号列表里没有「{account}」" + (seen.Length > 0 ? $"（看到的是「{seen}」）" : "")
                               + "。先用这个号手动登录一次，让客户端记住它，再点开始登录。"
                               + (dump.Length > 0 ? $"（列表已存到 {dump}）" : ""));
            }

            report($"在账号列表里点「{pick.Text}」（要登录的是「{account}」）");
            await ClickPointAsync(area, pick, $"账号列表里的「{pick.Text}」", report, ct).ConfigureAwait(true);
            await Task.Delay(2000, ct).ConfigureAwait(true);
            return (true, null);
        }

        return (false, $"换账号没成功：{lastProblem}。"
                       + "可以手动点一下账号框右边的三角，看看能不能拉出账号列表；"
                       + "能拉出来却还是换不过去，把日志发我。");
    }

    /// <summary>
    /// 换号用的抓图区域：账号悬浮窗整块，下面再多留一截。
    /// 账号历史有时不是画在悬浮窗里，而是单独的小弹窗贴在它下面——只截悬浮窗就永远看不到列表。
    /// </summary>
    /// <summary>
    /// 截图区域：屏幕坐标 + 尺寸。ClientHeight 是窗口客户区自己的高度，
    /// 比 Height 小（Height 还含下面多留的那半屏），用来把「窗外的东西」排除掉。
    /// </summary>
    private readonly record struct LoginArea(int X, int Y, int Width, int Height, IntPtr Window, int ClientHeight);

    private static bool TryCaptureLoginArea(IntPtr gameHwnd, out LoginArea area, out ScreenCapturer.Frame? frame)
    {
        area = default;
        frame = null;

        IntPtr overlay = LoginOverlay.Find(gameHwnd);
        return TryCaptureArea(overlay != IntPtr.Zero ? overlay : gameHwnd, out area, out frame);
    }

    /// <summary>
    /// 抓「窗口客户区」这一块，并记下它的屏幕位置，供按百分比点击用。
    ///
    /// captureBelow=true 时下面再多留半屏：账号历史列表是贴在悬浮窗下面的小弹窗，
    /// 只截窗口本身看不到它。**但这时帧高不再是客户区高度**，百分比含义也跟着变——
    /// 把这种帧算出来的百分比拿去乘客户区高度，点击就会整体往下掉。
    /// 服务器列表页曾经因此点「搜索框」点成了「新服推荐」。所以只有换号流程才要这一段。
    /// </summary>
    private static bool TryCaptureArea(
        IntPtr window,
        out LoginArea area,
        out ScreenCapturer.Frame? frame,
        bool captureBelow = true)
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
        int extra = captureBelow && height <= 800 ? height / 2 : 0;
        frame = ScreenCapturer.CaptureScreenRect(window, origin.X, origin.Y, width, height + extra);
        if (frame is null)
        {
            return false;
        }

        area = new LoginArea(origin.X, origin.Y, width, height + extra, window, height);
        return true;
    }

    /// <summary>
    /// 找账号输入框那一行：取最上面那行带 @ 的（账号框在列表上方）。
    /// 认不出邮箱时退回「请输入账号」这类占位字，且别把底部的「使用其他账号登录」当输入框。
    /// </summary>
    private static TextLine? FindShownField(List<TextLine> lines)
    {
        TextLine? email = lines
            .Where(l => l.Text.Contains('@'))
            .OrderBy(l => l.CenterY)
            .FirstOrDefault();

        if (email is not null)
        {
            return email;
        }

        return lines
            .Where(l => !l.Text.Contains("使用其他账号"))
            .Where(l => l.Text.Contains("请输入") || l.Text.Contains("帐号") || l.Text.Contains("账号")
                        || l.Text.Contains("邮箱") || l.Text.Contains("手机"))
            .OrderBy(l => l.CenterY)
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

    /// <summary>
    /// 从画面里找出账号框右边那个三角的横向位置（百分比）。找不到返回 null。
    ///
    /// 三角是图形，OCR 完全看不到；按百分比猜差几个像素就点空（三角只有 9 像素宽）。
    /// 所以直接看像素：在账号文字那一行的高度带里、从文字右边缘往右扫，
    /// 找一团连续的中等灰度像素（实测三角平均亮度 105~130，面板底色 235 以上）。
    /// 还得像三角：上宽下窄、有几行高——否则输入框边框、分隔线会被误认。
    /// </summary>
    public static double? FindTriangleX(ScreenCapturer.Frame frame, TextLine field)
    {
        if (frame.Width <= 8 || frame.Height <= 8)
        {
            return null;
        }

        // 从文字右边缘往右留一点，别把最后一个字的笔画算进来
        int x0 = (int)Math.Round(frame.Width * (field.Left + field.Width) / 100.0) + 5;
        int y0 = (int)Math.Round(frame.Height * field.Top / 100.0) - 4;
        int y1 = (int)Math.Round(frame.Height * (field.Top + field.Height) / 100.0) + 4;

        // 三角是「▾」：行框常常只框到字的主体，上下都留点余量
        int pad = Math.Max(3, (y1 - y0) / 3);
        y0 -= pad;
        y1 += pad;

        x0 = Math.Clamp(x0, 1, frame.Width - 2);
        y0 = Math.Clamp(y0, 0, frame.Height - 2);
        y1 = Math.Clamp(y1, y0 + 1, frame.Height - 1);

        int stride = frame.Width;
        int runStart = -1;

        for (int x = x0; x < frame.Width; x++)
        {
            // 这一列在高度带里有几像素是「中等偏暗」——三角是浅灰蓝，阈值 110 太严会全漏
            int dark = 0;
            for (int y = y0; y <= y1; y++)
            {
                int offset = (y * stride + x) * 4;
                if ((frame.Pixels[offset] + frame.Pixels[offset + 1] + frame.Pixels[offset + 2]) / 3
                    < TriangleDarkThreshold)
                {
                    dark++;
                }
            }

            bool isDark = dark >= 2;

            if (isDark)
            {
                if (runStart < 0)
                {
                    runStart = x;
                }

                continue;
            }

            if (runStart < 0)
            {
                continue;
            }

            int width = x - runStart;
            if (width is >= 3 and <= 26 && LooksLikeTriangle(frame, runStart, x - 1, y0, y1))
            {
                return (runStart + width / 2.0) * 100.0 / stride;
            }

            runStart = -1;
        }

        // 一直暗到画面右边（比如碰到窗口边框）：不认
        return null;
    }

    /// <summary>
    /// 这一团像素像不像「▾」：有多行高、最宽的那行不在最底下（上宽下窄），
    /// 宽度要够（实测 9 像素），但不能宽到像个方块。
    /// </summary>
    private static bool LooksLikeTriangle(
        ScreenCapturer.Frame frame, int startX, int endX, int y0, int y1)
    {
        int stride = frame.Width;
        var widths = new List<int>();

        for (int y = y0; y <= y1; y++)
        {
            int count = 0;
            for (int x = startX; x <= endX; x++)
            {
                int offset = (y * stride + x) * 4;
                if ((frame.Pixels[offset] + frame.Pixels[offset + 1] + frame.Pixels[offset + 2]) / 3
                    < TriangleDarkThreshold)
                {
                    count++;
                }
            }

            if (count > 0)
            {
                widths.Add(count);
            }
        }

        if (widths.Count < 3)
        {
            return false;
        }

        int max = widths.Max();
        if (max < 4 || max > endX - startX + 1)
        {
            return false;
        }

        // 最宽的一行要落在上半段（下宽上窄的实心块、竖线都不满足）
        int maxAt = widths.IndexOf(max);
        return maxAt <= widths.Count / 2 + 1;
    }

    /// <summary>
    /// 三角的横向候选：先按实测的几个常见位置试（窗口右侧那一带），
    /// 再退回「账号文字右边缘」——窗口大小不同时也能兜住。
    /// </summary>
    private static IEnumerable<double> DropdownCandidates(TextLine field)
    {
        var list = new List<double>(DropdownCandidatePercents)
        {
            field.Left + field.Width + 1.5,
            field.Left + field.Width + 4,
            field.Left + field.Width + 8
        };

        return list.Where(x => x is > 5 and < 96).Distinct();
    }

    /// <summary>
    /// 账号框下面那些像账号的行（历史记录列表）。
    ///
    /// 必须限定在窗口客户区内：抓图时往下多留了半屏看贴在窗外的弹窗，
    /// 但那半屏其实是屏幕上的游戏窗口和桌面——实测那里的「健康游戏忠告」含 @，
    /// 被当成账号列表，程序一次三角都没点就报「列表里没有这个号」。
    /// </summary>
    private static List<TextLine> AccountsBelow(
        List<TextLine> lines, TextLine field, int frameHeight, int clientHeight)
    {
        double clientPercent = clientHeight > 0 && frameHeight > 0
            ? clientHeight * 100.0 / frameHeight
            : 100;

        return lines
            .Where(l => l.CenterY > field.CenterY + 2 && l.CenterY < clientPercent - 1)
            .Where(l => LooksLikeAccountText(l.Text))
            .OrderBy(l => l.CenterY)
            .ToList();
    }

    /// <summary>
    /// 这行像不像账号（邮箱 / 手机号 / 只显示前缀的账号）。
    ///
    /// 光看「含 @ 且够长」不行：忠告文字里有「自我保圹@ 防到上当。」，
    /// CleanAccount 之后是「自我保圹@防到上当。适戏。...」——含 @、够长、全中。
    /// 所以 @ 两边都得是真正像账号的东西：前面得有拉丁字母，后面也得有字母（域名）。
    /// </summary>
    public static bool LooksLikeAccountText(string text)
    {
        string s = CleanAccount(text);
        if (s.Length is < 4 or > 48)
        {
            return false;
        }

        int at = s.IndexOf('@');
        if (at >= 0)
        {
            string local = s[..at];
            string domain = s[(at + 1)..];

            return local.Length >= 3
                   && local.Any(char.IsAsciiLetter)
                   && domain.Length >= 3
                   && domain.Any(char.IsAsciiLetter);
        }

        // 没 @ 的只可能是「历史里只显示前缀」那种行：纯字母数字，且字母得够多
        return s.Length >= 6
               && s.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-')
               && s.Count(char.IsAsciiLetter) >= 4;
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
            // 只比域名主段（163.com → 163）：OCR 常把「.com」认成「℃ om」或丢字，
            // 直接比整段会全部对不上；126 和 163 的差别也正好在主段上
            if (!string.Equals(DomainKey(a[(ai + 1)..]), DomainKey(b[(bi + 1)..]), StringComparison.Ordinal))
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
    /// 域名主段：163.com → 163、126om → 126。
    /// OCR 把「.com」认成「℃ om」「. om」、或者干脆丢字都很常见，
    /// 而不同邮箱服务商（126 / 163 / qq）的区别就在主段上，所以只比主段最稳。
    /// </summary>
    private static string DomainKey(string domain)
    {
        int dot = domain.IndexOf('.');
        if (dot > 0)
        {
            return domain[..dot];
        }

        foreach (string suffix in new[] { "comcn", "com", "con", "corn", "cn", "net", "org", "om" })
        {
            if (domain.Length > suffix.Length && domain.EndsWith(suffix, StringComparison.Ordinal))
            {
                return domain[..^suffix.Length];
            }
        }

        return domain;
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

    /// <summary>
    /// 把账号行归一化：去空格、转小写、把认错的中文标点换回 . 和 @，
    /// 并且只留账号里可能出现的字符——OCR 常把右边的「×」、头像字、中文标签黏进同一行，
    /// 带着它们比域名会全都对不上（实测「liaozicen041203@163.com ×」会被当成 163.com×）。
    /// </summary>
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

            ch = ch switch
            {
                '＠' => '@',
                '。' or '，' or '、' or '·' or '•' => '.',
                _ => ch
            };

            if (char.IsAsciiLetterOrDigit(ch) || ch is '@' or '.' or '_' or '-' or '*')
            {
                sb.Append(ch);
            }
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
        TextLine? LoginButton,
        TextLine? Search,
        int ZoneTabs)
    {
        public bool Anything => Role is not null || Enter is not null || ServerButton is not null
                                || SwitchAccount is not null || Account is not null || HasEmail
                                || LoginButton is not null || Search is not null;

        /// <summary>
        /// 服务器列表页（左边有「正式服 / 轻享服 / 已有角色 / 时空区 / 七区…」那一列分区按钮）。
        /// 这一屏默认只列当前分区的几个服；目标服不在里面时，程序会停在这里等你手动点一次。
        ///
        /// 不能只凭搜索框判：实测搜索框是灰色小字，快通道根本认不出来（19 行里没有它），
        /// 而「登录游戏」认得出——只认搜索框的话就会去点「登录游戏」，直接进上次的角色。
        /// </summary>
        public bool IsServerList => Search is not null || ZoneTabs >= 2;

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
        // 判据是「有没有能点的东西」，不是 Anything：Anything 里含「有 @ 就算」，
        // 而服务器列表页那堆分区名里根本没有可点的登录目标，快通道又认不出灰色的
        // 「搜索」两个字（实测快通道 19 行里没有它，加强通道 25 行才有）——
        // 用 Anything 会一直不补，最后把这一屏当成「已进游戏」。
        if (found.Role is null && found.Enter is null && found.ServerButton is null
            && found.Server is null && found.SwitchAccount is null && found.LoginButton is null)
        {
            List<TextLine> strong = await OcrService.RecognizeAsync(frame, thorough: true).ConfigureAwait(true);
            Found retry = FindAll(strong, accountKey, server);

            if (retry.Role is not null || retry.Enter is not null || retry.ServerButton is not null
                || retry.Server is not null || retry.SwitchAccount is not null
                || retry.LoginButton is not null || retry.Search is not null)
            {
                lines = strong;
                found = retry;
            }
        }

        // 只认出「登录游戏」、没认出目标服务器：这一屏八成就是服务器列表（左上角那个灰色的
        // 「搜索」只有加强通道认得出）。不补这一次，就会去点「登录游戏」——
        // 那是「进上次的角色」，直接跳过选服选角（实测窗口 1、2 就这么进错的）。
        if (found.ServerButton is not null && found.Server is null && found.Role is null
            && found.Search is null && found.ZoneTabs == 0 && server.Length > 0)
        {
            List<TextLine> strong = await OcrService.RecognizeAsync(frame, thorough: true).ConfigureAwait(true);
            Found retry = FindAll(strong, accountKey, server);

            if (retry.Search is not null || retry.Server is not null || retry.ZoneTabs >= 2)
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
            : found.IsServerList ? "服务器列表"
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
        sb.AppendLine($"  服务器搜索框：{Where(found.Search)}");

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
    {
        // 先认搜索框：搜完之后框里就显示着服务器名，那一行绝不能被当成「服务器按钮」
        TextLine? search = FindSearchBox(lines);

        return new Found(
            FindBest(lines, "点击登录"),
            FindBest(lines, "进入游戏"),
            FindBest(lines, "登录游戏"),
            FindBest(lines, "使用其他账号登录", 0.6),
            FindShownAccount(lines, accountKey),
            server.Length > 0 ? FindServerRow(lines, server, search) : null,
            lines.Any(l => l.Text.Contains('@')),
            FindLoginButton(lines),
            search,
            lines.Count(l => IsZoneTab(l.Text)));
    }

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
    /// <summary>这一行是不是服务器列表页左边的分区按钮（正式服 / 轻享服 / 已有角色 / 七区…）。</summary>
    private static bool IsZoneTab(string text)
    {
        string n = OcrService.Normalize(text);
        if (n.Length is 0 or > 6)
        {
            return false;
        }

        if (n.Contains("正式服", StringComparison.Ordinal) || n.Contains("轻享服", StringComparison.Ordinal)
            || n.Contains("已有角色", StringComparison.Ordinal) || n.Contains("新服推荐", StringComparison.Ordinal)
            || n.Contains("时空区", StringComparison.Ordinal))
        {
            return true;
        }

        // 「七区」「六区」这种：两个字，且以区结尾（OCR 常把「七」认成「乜」，所以不认第一个字）
        return n.Length == 2 && n.EndsWith('区');
    }

    /// <summary>
    /// 服务器页上「登录游戏」按钮正上方的那张服务器卡片（卡片上写着上次登录的服名）。
    /// 点它才会展开完整的服务器列表——也就是有搜索框的那一屏。
    /// </summary>
    /// <summary>给命令行探针用：看这一屏会把哪一行当成「上次登录的服务器卡片」。</summary>
    public static TextLine? FindServerCardForProbe(List<TextLine> lines, TextLine serverButton)
        => FindServerCardAbove(lines, serverButton);

    private static TextLine FindServerCardAbove(List<TextLine> lines, TextLine serverButton)
    {
        // 先按文字找：卡片上是「⊙ 服务器名」，两到六个字。
        // 光靠「在登录游戏上方、x 差不多」不够——实测窗口 1 把卡片下面的装饰认成了
        // 「一 ： 0 ， ． 毒 嚹 ， 产 生 0」（55.0%, 61.8%），比真卡片更靠下，
        // 按 CenterY 取最靠下就正好点到它，卡片一点没动。
        TextLine? byText = lines
            .Where(l => l.CenterY < serverButton.CenterY - 6 && l.CenterY > serverButton.CenterY - 24)
            .Where(l => Math.Abs(l.CenterX - serverButton.CenterX) < 24)
            .Where(l => LooksLikeServerCard(l.Text))
            .OrderByDescending(l => l.CenterY)
            .FirstOrDefault();

        if (byText is not null)
        {
            return byText;
        }

        // 一个像样的候选都没有（卡片是动画飞进来的、刚好糊成一团）就按位置点：
        // 实测卡片中心就在「登录游戏」正上方 12% 处，x 与「登录游戏」基本对齐
        // （卡片是条长条，左右各差几个百分点也还在里面）。点空最多是没反应，不会误进游戏。
        return new TextLine("（服务器卡片·按位置）", serverButton.CenterX, serverButton.CenterY - 12.1, 4, 3);
    }

    /// <summary>
    /// 这一行像不像服务器卡片上的名字：纯汉字（允许开头一个圈/数字当图标），
    /// 两到八个字，且不像乱码那样夹着一堆标点。
    /// </summary>
    private static bool LooksLikeServerCard(string text)
    {
        string n = OcrService.Normalize(text);
        if (n.Length is < 2 or > 8 || n.Contains("登录", StringComparison.Ordinal))
        {
            return false;
        }

        const string junk = "，。．、；：！？,./;:!?·…—-_~@#%&*()（）【】「」《》<>+=";
        if (n.Any(c => junk.Contains(c)))
        {
            return false;
        }

        int han = n.Count(c => c >= 0x4E00 && c <= 0x9FFF);
        return han >= 2 && han >= n.Length - 1;
    }

    /// <summary>
    /// 认目标服务器这一行。
    ///
    /// 必须排除「红颜知己的搜索结果：」这种标题——它含完整服务器名，相似度满分，
    /// 点下去只是点到一行字。真正的结果卡片在它下面。
    /// </summary>
    private static TextLine? FindServerRow(List<TextLine> lines, string server, TextLine? searchBox)
    {
        var candidates = lines
            .Where(l => !OcrService.Normalize(l.Text).Contains("搜索结果", StringComparison.Ordinal))
            // 搜索框里的字和服务器名一模一样，但它不是按钮
            .Where(l => searchBox is null || Math.Abs(l.CenterY - searchBox.CenterY) >= 3)
            .ToList();

        return FindBest(candidates, server);
    }

    /// <summary>
    /// 找服务器列表页左上角那个搜索框。
    ///
    /// 空的时候框里写着灰色的「搜索」占位字，直接认它最省事；
    /// 框里已经有上次搜的字时占位字就不见了，这时退回到「已有角色」按钮上方那一行。
    /// </summary>
    private static TextLine? FindSearchBox(List<TextLine> lines)
    {
        TextLine? placeholder = FindBest(lines, "搜索", 0.78);
        if (placeholder is not null)
        {
            return placeholder;
        }

        TextLine? anchor = FindBest(lines, "已有角色", 0.68);
        if (anchor is null)
        {
            return null;
        }

        // 搜索框在「已有角色」正上方、左侧那一栏。
        // 必须限制在左栏（中心 x < 28%）：右边是服务器名字（35%、68%），
        // 不管这个就会把「再续前缘」当成搜索框，然后往服务器按钮上打字。
        return lines
            .Where(l => l.CenterX < 28 && l.CenterY < anchor.CenterY - 2)
            .Where(l => l.CenterY > anchor.CenterY - 14)
            .OrderByDescending(l => l.CenterY)
            .FirstOrDefault();
    }

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
