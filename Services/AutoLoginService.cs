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
    /// <summary>两次认字之间的间隔。一轮「抓图 + 识别」大约 0.2 秒，再加这点间隔不至于把 CPU 占满。</summary>
    private const int PollIntervalMs = 450;

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

        int timeout = settings.StepTimeoutSec;

        // 1) 通用按钮：账号界面的「登录游戏」→ 角色界面的「进入游戏」
        if (settings.AutoClickLogin)
        {
            // 账号界面的主按钮写的是「进入游戏」（不是「登录游戏」），两个都认
            TextLine? login = await WaitForAnyAsync(hwnd, ["登录游戏", "进入游戏"], 8, report, ct).ConfigureAwait(true);
            if (login is not null)
            {
                await ClickLineAsync(hwnd, login, "登录游戏", report, ct).ConfigureAwait(true);
                await Task.Delay(1200, ct).ConfigureAwait(true);
            }
            else
            {
                // 账号界面那个大按钮的字是图片字，OCR 认成「豆求腭又」这种，靠文字匹配不到。
                // 这个界面每次都得点一下，所以退一步：认准画面特征后，按「位置」点它。
                TextLine? fallback = await FindAccountScreenButtonAsync(hwnd, ct).ConfigureAwait(true);
                if (fallback is not null)
                {
                    report($"按钮文字认不出来（识别为「{fallback.Text}」），按位置点这个主按钮。");
                    await ClickLineAsync(hwnd, fallback, "主按钮（按位置）", report, ct).ConfigureAwait(true);
                    await Task.Delay(1500, ct).ConfigureAwait(true);
                }
                else
                {
                    report("画面上没有「登录游戏」按钮，可能已经在后面的界面，继续。");
                }
            }
        }

        // 2) 账号（只核对，不强切）：识别噪声大，切错账号比不切更麻烦
        if (!string.IsNullOrWhiteSpace(target.Account))
        {
            string accountKey = target.Account.Split('@')[0].Trim();
            TextLine? accountLine = await WaitForAnyAsync(hwnd, [accountKey], 3, null, ct).ConfigureAwait(true);
            report(accountLine is null
                ? $"没有在画面上找到账号「{target.Account}」，如果账号不对请手动切换。"
                : "账号已确认。");

            // 账号确认窗上的「进入游戏」
            TextLine? enter = await WaitForAnyAsync(hwnd, ["进入游戏"], 2, null, ct).ConfigureAwait(true);
            if (enter is not null)
            {
                await ClickLineAsync(hwnd, enter, "进入游戏", report, ct).ConfigureAwait(true);
                await Task.Delay(1500, ct).ConfigureAwait(true);
            }
        }

        // 3) 服务器：点开服务器行 → 若出现服务器列表，再点一次同名项
        if (!string.IsNullOrWhiteSpace(target.Server))
        {
            TextLine? server = await WaitForAnyAsync(hwnd, [target.Server], timeout, report, ct).ConfigureAwait(true);
            if (server is null)
            {
                return $"没找到服务器「{target.Server}」，可把名称缩短一点（比如只填「红颜」）";
            }

            await ClickLineAsync(hwnd, server, $"服务器「{target.Server}」", report, ct).ConfigureAwait(true);
            await Task.Delay(900, ct).ConfigureAwait(true);

            // 服务器列表页有「已有角色」「正式服」「轻享服」这些字样；出现说明刚打开列表，得再点一次名字
            TextLine? marker = await WaitForAnyAsync(hwnd, ["已有角色", "轻享服", "正式服"], 2, null, ct).ConfigureAwait(true);
            if (marker is not null)
            {
                TextLine? again = await WaitForAnyAsync(hwnd, [target.Server], 5, null, ct).ConfigureAwait(true);
                if (again is not null)
                {
                    await ClickLineAsync(hwnd, again, $"服务器列表里的「{target.Server}」", report, ct).ConfigureAwait(true);
                    await Task.Delay(900, ct).ConfigureAwait(true);
                }
            }
        }

        // 4) 角色：先认准角色名，再点它正下方那个「点击登录」
        //    一个服务器里可能有好几个角色，每个卡片下面都有一个「点击登录」，
        //    全局点第一个会进错号，所以必须按名字就近配对。
        //    走到这一步时可能还停在服务器页（那页的按钮叫「登录游戏」），先推进去。
        await AdvanceToRoleScreenAsync(hwnd, report, ct).ConfigureAwait(true);

        string character = target.Character.Trim();
        if (character.Length > 0)
        {
            (bool ok, string? failReason) = await ClickRoleAsync(hwnd, character, timeout, report, ct)
                .ConfigureAwait(true);

            if (!ok)
            {
                return failReason;
            }
        }
        else
        {
            TextLine? anyRole = await WaitForAnyAsync(hwnd, ["点击登录"], timeout, report, ct).ConfigureAwait(true);
            if (anyRole is null)
            {
                report("没有角色名可点，也没看到「点击登录」，可能已经在游戏里。");
                return null;
            }

            report("没有填角色名，点第一个「点击登录」（若进错角色，请把角色名填上）。");
            await ClickLineAsync(hwnd, anyRole, "点击登录", report, ct).ConfigureAwait(true);
        }

        // 5) 最后可能还有个「进入游戏」确认
        TextLine? confirm = await WaitForAnyAsync(hwnd, ["进入游戏"], 2, null, ct).ConfigureAwait(true);
        if (confirm is not null)
        {
            await ClickLineAsync(hwnd, confirm, "进入游戏", report, ct).ConfigureAwait(true);
        }

        return null;
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
    public static TextLine? FindBest(IReadOnlyList<TextLine> lines, string target)
    {
        string normalizedTarget = OcrService.Normalize(target);
        if (normalizedTarget.Length == 0)
        {
            return null;
        }

        // 短名字（两个字）容易被别的词蹭到，要求更严
        double threshold = normalizedTarget.Length <= 2 ? 0.9 : 0.68;

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

        return bestScore >= threshold ? best : null;
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
    /// 服务器页和角色页的按钮不一样：服务器页是「登录游戏」，角色页才有「点击登录」。
    /// 走到角色这一步时若还看不到「点击登录」，就先点「登录游戏」把页面推进去。
    /// 这样「服务器」「角色」都留空也能一路点到底。
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

        await ClickLineAsync(hwnd, forward, $"推进到角色页（{forward.Text}）", report, ct).ConfigureAwait(true);
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

        while (DateTime.Now <= deadline)
        {
            ct.ThrowIfCancellationRequested();

            ScreenCapturer.Frame? frame = ScreenCapturer.Capture(hwnd);
            if (frame is null)
            {
                await Task.Delay(PollIntervalMs, ct).ConfigureAwait(true);
                continue;
            }

            List<TextLine> lines = await OcrService.RecognizeAsync(frame.Value).ConfigureAwait(true);
            TextLine? role = FindBest(lines, character);
            if (role is null)
            {
                // 角色名偶尔是浅底深字排在角色卡片里，慢通道能多认一些
                lines = await OcrService.RecognizeAsync(frame.Value, thorough: true).ConfigureAwait(true);
                role = FindBest(lines, character);
            }

            if (role is null)
            {
                if (!saidWaiting)
                {
                    saidWaiting = true;
                    report($"等角色「{character}」出现…");
                }

                await Task.Delay(PollIntervalMs, ct).ConfigureAwait(true);
                continue;
            }

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

        return (false, $"没找到角色「{character}」，可把名字缩短一点（比如只填两个字）");
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

    /// <summary>
    /// 抢前台。Windows 只允许前台进程自己调用 SetForegroundWindow，
    /// 所以先用 AttachThreadInput 把输入队列挂到当前前台线程，还不够就补一次模拟 ALT 键，
    /// 让系统认为「用户刚刚有输入」从而放行。三招轮流试，最后返回是否真的抢到了。
    /// </summary>
    public static bool ForceForeground(IntPtr hwnd)
    {
        if (NativeMethods.GetForegroundWindow() == hwnd)
        {
            return true;
        }

        if (NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            Thread.Sleep(120);
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            uint currentThread = NativeMethods.GetCurrentThreadId();
            uint foregroundThread = foreground == IntPtr.Zero
                ? 0
                : NativeMethods.GetWindowThreadProcessId(foreground, out _);

            bool attached = false;
            if (foregroundThread != 0 && foregroundThread != currentThread)
            {
                attached = NativeMethods.AttachThreadInput(foregroundThread, currentThread, true);
            }

            try
            {
                NativeMethods.BringWindowToTop(hwnd);
                if (!NativeMethods.SetForegroundWindow(hwnd) || NativeMethods.GetForegroundWindow() != hwnd)
                {
                    // 兜底：系统把「模拟 ALT」当作用户输入，随后的 SetForegroundWindow 就不再被拒
                    NativeMethods.keybd_event(NativeMethods.VK_MENU, 0, 0, UIntPtr.Zero);
                    NativeMethods.keybd_event(NativeMethods.VK_MENU, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
                    NativeMethods.SetForegroundWindow(hwnd);
                }
            }
            finally
            {
                if (attached)
                {
                    NativeMethods.AttachThreadInput(foregroundThread, currentThread, false);
                }
            }

            if (NativeMethods.GetForegroundWindow() == hwnd)
            {
                return true;
            }

            Thread.Sleep(120);
        }

        return NativeMethods.GetForegroundWindow() == hwnd;
    }
}
