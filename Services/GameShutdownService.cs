using System.Diagnostics;
using System.Linq;
using System.Text;

namespace Nightforge.Services;

/// <summary>
/// 一键退出游戏：等价于逐个点掉游戏窗口右上角的 ×，再把随之弹出的「确定」确认框点一下。
///
/// 关键是「并行」，不是逐个串行处理：
///   1. 一次性给所有窗口投递 WM_CLOSE，5 个窗口的确认框是同时弹出来的，不用排队等
///   2. 等确认框的这段时间所有窗口共享，不是每个窗口各等一遍
///   3. 独立确认框（有真实按钮的）直接投递点击，不需要切前台，可以一口气点完
///   4. 游戏内自绘的弹层只能靠回车键确认，必须切前台，所以只有这一步才逐个来
///   5. 最后统一等一轮退出，中途全部退完就提前收工
///
/// 这样 5 个窗口的总耗时和 1 个窗口差不多，而不是原来的 5 倍。
/// </summary>
public sealed class GameShutdownService
{
    /// <summary>
    /// 点 × 之后等确认框出现的时间（所有窗口共享这一段）。
    /// 给够是因为这段是并行的：多等 0.5 秒，5 个窗口总共也只多 0.5 秒，
    /// 但等太短的话回车可能落在还没弹框的游戏界面上。
    /// </summary>
    private const int DialogDetectMs = 1500;

    /// <summary>逐个发回车之间的间隔，够系统把前台切过去就行。</summary>
    private const int ConfirmGapMs = 220;

    /// <summary>
    /// 确认之后统一等退出的时间。正常退出一般 1～2 秒内就全退完，会提前收工；
    /// 只有真卡住才会等满这一段，所以宁可给宽裕一点，避免把正在退出的游戏误判成没退而强杀。
    /// </summary>
    private const int ExitWaitMs = 4000;

    private static readonly string[] ConfirmKeywords = ["确定", "确认", "是", "退出", "OK", "Yes"];

    /// <summary>退出结果：总数 / 正常退出 / 强制结束 / 失败 / 耗时。</summary>
    public sealed record Summary(int Total, int Graceful, int Forced, int Failed, double Seconds)
    {
        public bool AllClosed => Failed == 0 && Total > 0;
    }

    /// <summary>单个窗口在这次退出里的状态。</summary>
    private sealed class Target
    {
        public IntPtr Hwnd { get; init; }
        public uint Pid { get; init; }
        public string Title { get; init; } = string.Empty;

        /// <summary>找到的独立确认框句柄，0 表示没找到（多半是游戏内自绘弹层）。</summary>
        public IntPtr Dialog { get; set; }

        public bool Gone { get; set; }
        public bool Blocked { get; set; }
        public bool Confirmed { get; set; }
        public bool Forced { get; set; }
        public bool Failed { get; set; }
    }

    public async Task<Summary> ShutdownAsync(
        IReadOnlyList<IntPtr> windows,
        Action<string> report,
        CancellationToken token)
    {
        var watch = Stopwatch.StartNew();

        var targets = new List<Target>();
        foreach (IntPtr hwnd in windows)
        {
            if (!NativeMethods.IsWindow(hwnd))
            {
                continue;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            targets.Add(new Target { Hwnd = hwnd, Pid = pid, Title = WindowService.GetTitle(hwnd) });
        }

        if (targets.Count == 0)
        {
            return new Summary(0, 0, 0, 0, watch.Elapsed.TotalSeconds);
        }

        // 1. 所有窗口同时点 ×
        report($"同时向 {targets.Count} 个窗口发送退出请求…");
        foreach (Target target in targets)
        {
            if (!NativeMethods.PostMessage(target.Hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero))
            {
                target.Blocked = true;
            }
        }

        // 2. 共用一段等待：一边等确认框弹出来，一边盯着有没有窗口直接就关掉了
        DateTime deadline = DateTime.Now.AddMilliseconds(DialogDetectMs);
        while (DateTime.Now < deadline)
        {
            await Task.Delay(80, token).ConfigureAwait(true);
            RefreshState(targets);
            if (targets.All(t => t.Gone || t.Dialog != IntPtr.Zero))
            {
                break;
            }
        }

        // 3. 有真实按钮的确认框最省事：不抢前台，一次性全点掉
        var clickable = targets.Where(t => !t.Gone && t.Dialog != IntPtr.Zero).ToList();
        if (clickable.Count > 0)
        {
            report($"检测到 {clickable.Count} 个独立确认框，直接点「确定」。");
            foreach (Target target in clickable)
            {
                if (ClickConfirmButton(target.Dialog))
                {
                    target.Confirmed = true;
                }
            }
        }

        // 4. 剩下的（游戏内自绘弹层）只能逐窗发回车，这一步绕不开串行
        var needEnter = targets.Where(t => !t.Gone && !t.Confirmed).ToList();
        if (needEnter.Count > 0)
        {
            report($"其余 {needEnter.Count} 个是游戏内自绘弹层，逐个按回车确认…");
        }

        foreach (Target target in needEnter)
        {
            token.ThrowIfCancellationRequested();

            if (!NativeMethods.IsWindow(target.Hwnd))
            {
                target.Gone = true;
                continue;
            }

            if (await SendEnterAsync(target.Hwnd, token).ConfigureAwait(true))
            {
                target.Confirmed = true;
            }

            await Task.Delay(ConfirmGapMs, token).ConfigureAwait(true);
        }

        // 5. 统一等一轮退出，全部退完就提前收工，不等满
        deadline = DateTime.Now.AddMilliseconds(ExitWaitMs);
        while (DateTime.Now < deadline)
        {
            await Task.Delay(100, token).ConfigureAwait(true);
            RefreshState(targets);
            if (targets.All(t => t.Gone))
            {
                break;
            }
        }

        // 6. 还没退掉的才强杀
        foreach (Target target in targets.Where(t => !t.Gone))
        {
            if (target.Pid == 0)
            {
                target.Failed = true;
                report($"  {target.Title}：窗口没有关闭，且拿不到进程号，请手动关闭。");
                continue;
            }

            try
            {
                using Process process = Process.GetProcessById((int)target.Pid);
                process.Kill();
                target.Forced = true;
                target.Gone = true;
                report($"  {target.Title}：正常退出没有生效，已强制结束进程。");
            }
            catch (Exception ex)
            {
                target.Failed = true;
                report($"  {target.Title}：强制结束失败（{ex.Message}）。");
            }
        }

        foreach (Target target in targets.Where(t => t.Blocked))
        {
            report($"  {target.Title}：点 × 的消息被系统拦截，建议以管理员身份重新运行本程序。");
        }

        watch.Stop();

        int graceful = targets.Count(t => t.Gone && !t.Forced);
        int forced = targets.Count(t => t.Forced);
        int failed = targets.Count(t => t.Failed);
        return new Summary(targets.Count, graceful, forced, failed, watch.Elapsed.TotalSeconds);
    }

    /// <summary>刷新每个窗口的状态：已消失 / 出现确认框。</summary>
    private static void RefreshState(List<Target> targets)
    {
        foreach (Target target in targets)
        {
            if (target.Gone)
            {
                continue;
            }

            if (!NativeMethods.IsWindow(target.Hwnd))
            {
                target.Gone = true;
                continue;
            }

            if (target.Dialog == IntPtr.Zero)
            {
                target.Dialog = FindConfirmWindow(target.Pid, target.Hwnd);
            }
        }
    }

    /// <summary>
    /// 找确认框：先按「标准对话框类名 / 标题关键词」精确找，再退化为同进程的其它可见窗口。
    /// 只认同进程、非游戏主窗口、尺寸合理的窗口，避免误点到悬浮窗、输入法候选框之类的东西。
    /// </summary>
    private static IntPtr FindConfirmWindow(uint pid, IntPtr exclude)
    {
        IntPtr exact = IntPtr.Zero;
        IntPtr fallback = IntPtr.Zero;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (hwnd == exclude || !NativeMethods.IsWindowVisible(hwnd))
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint wpid);
            if (wpid != pid)
            {
                return true;
            }

            if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rc))
            {
                return true;
            }

            int w = rc.Right - rc.Left;
            int h = rc.Bottom - rc.Top;
            if (w < 120 || h < 60 || w > 2600 || h > 1600)
            {
                return true;
            }

            string cls = GetClassName(hwnd);
            string title = WindowService.GetTitle(hwnd);

            bool looksLikeDialog =
                cls.Equals("#32770", StringComparison.OrdinalIgnoreCase)
                || title.Contains("退出", StringComparison.OrdinalIgnoreCase)
                || title.Contains("确认", StringComparison.OrdinalIgnoreCase)
                || title.Contains("提示", StringComparison.OrdinalIgnoreCase)
                || title.Contains("确定", StringComparison.OrdinalIgnoreCase);

            if (looksLikeDialog)
            {
                exact = hwnd;
                return false;
            }

            if (fallback == IntPtr.Zero)
            {
                fallback = hwnd;
            }

            return true;
        }, IntPtr.Zero);

        return exact != IntPtr.Zero ? exact : fallback;
    }

    /// <summary>在确认框（含子窗口）里找「确定」按钮并点一下。</summary>
    private static bool ClickConfirmButton(IntPtr dialog)
    {
        IntPtr button = FindButton(dialog, ConfirmKeywords);
        return button != IntPtr.Zero
            && NativeMethods.PostMessage(button, NativeMethods.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
    }

    private static IntPtr FindButton(IntPtr root, string[] keywords)
    {
        IntPtr hit = IntPtr.Zero;
        NativeMethods.EnumChildWindows(root, (hwnd, _) =>
        {
            string cls = GetClassName(hwnd);
            if (!cls.Equals("Button", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string text = WindowService.GetTitle(hwnd);
            if (text.Length == 0)
            {
                return true;
            }

            foreach (string keyword in keywords)
            {
                if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    hit = hwnd;
                    return false;
                }
            }

            return true;
        }, IntPtr.Zero);

        return hit;
    }

    /// <summary>
    /// 把窗口拉到前台后发一次真实回车，用来确认引擎自绘的弹框。
    ///
    /// 必须先确认「确实抢到了前台」再发按键：抢失败时按键会打在当时最前面的窗口上，
    /// 严重的话会变成在别的程序里乱按。抢不到就返回 false，交给后面的强杀兜底。
    /// </summary>
    private static async Task<bool> SendEnterAsync(IntPtr hwnd, CancellationToken token)
    {
        if (!NativeMethods.IsWindow(hwnd))
        {
            return false;
        }

        if (NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            await Task.Delay(80, token).ConfigureAwait(true);
        }

        ForceForeground(hwnd);
        await Task.Delay(120, token).ConfigureAwait(true);

        if (NativeMethods.GetForegroundWindow() != hwnd)
        {
            return false;
        }

        NativeMethods.keybd_event(NativeMethods.VK_RETURN, 0, 0, UIntPtr.Zero);
        await Task.Delay(40, token).ConfigureAwait(true);
        NativeMethods.keybd_event(NativeMethods.VK_RETURN, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
        return true;
    }

    /// <summary>
    /// 硬抢前台。Windows 有前台锁，普通 SetForegroundWindow 在后台调用往往只闪一下任务栏，
    /// 用 AttachThreadInput 临时把当前前台线程的输入队列挂到一起就能绕过去。
    /// </summary>
    private static void ForceForeground(IntPtr hwnd)
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground == hwnd)
        {
            return;
        }

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
            NativeMethods.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                NativeMethods.AttachThreadInput(foregroundThread, currentThread, false);
            }
        }
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        int len = NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString() : string.Empty;
    }
}
