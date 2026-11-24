using System.Diagnostics;
using System.Text;

namespace Nightforge.Services;

/// <summary>窗口枚举、多开捕获、排列、前置与最小化。</summary>
public sealed class WindowService
{
    private readonly LogService _log;

    public WindowService(LogService log) => _log = log;

    /// <summary>窗口边框占位：客户区相对窗口左上角的偏移，以及左右 / 上下边框合计。</summary>
    public readonly record struct WindowFrame(int OffsetX, int OffsetY, int ExtraWidth, int ExtraHeight)
    {
        public static readonly WindowFrame None = new(0, 0, 0, 0);
    }

    /// <summary>主显示器工作区（物理像素，已排除任务栏）。</summary>
    public readonly record struct WorkArea(int Left, int Top, int Width, int Height);

    public static string GetTitle(IntPtr hwnd)
    {
        int len = NativeMethods.GetWindowTextLength(hwnd);
        if (len <= 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(len + 2);
        NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>
    /// 枚举游戏窗口。
    ///
    /// 默认只按标题关键词匹配；includeGameProcesses 打开后会额外按「游戏进程名 + 是主窗口」兜底，
    /// 这样某个窗口标题变了（或暂时为空）也不会被整个漏掉。
    /// </summary>
    public List<IntPtr> Snapshot(string keyword, bool includeGameProcesses = false)
    {
        var result = new List<IntPtr>();
        if (string.IsNullOrWhiteSpace(keyword) && !includeGameProcesses)
        {
            return result;
        }

        uint selfPid = (uint)Environment.ProcessId;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd))
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == selfPid)
            {
                return true;
            }

            string title = GetTitle(hwnd);
            if (title.Length == 0)
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(keyword)
                && title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(hwnd);
                return true;
            }

            if (includeGameProcesses && IsGameMainWindow(hwnd, pid))
            {
                result.Add(hwnd);
            }

            return true;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>游戏客户端进程名（登录器与游戏本体）。</summary>
    private static readonly string[] GameProcessNames =
    [
        "MyLauncher_x64r", "MyPCLauncher_x64r", "MyGame_x64r"
    ];

    /// <summary>
    /// 判断是不是游戏主窗口：进程名对得上、没有 owner（有 owner 的通常是提示条/进度条这类附属小窗）、尺寸像正常窗口。
    /// </summary>
    private static bool IsGameMainWindow(IntPtr hwnd, uint pid)
    {
        string name;
        try
        {
            using Process process = Process.GetProcessById((int)pid);
            name = process.ProcessName;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        if (!GameProcessNames.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero)
        {
            return false;
        }

        if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rc))
        {
            return false;
        }

        int w = rc.Right - rc.Left;
        int h = rc.Bottom - rc.Top;
        return w >= 320 && h >= 240;
    }

    /// <summary>依次启动游戏并捕获新出现的窗口句柄。</summary>
    public async Task<List<IntPtr>> LaunchMultipleAsync(
        string gamePath,
        string keyword,
        int count,
        int waitSeconds,
        Action<string> report,
        CancellationToken token)
    {
        var captured = new List<IntPtr>();
        var known = Snapshot(keyword, includeGameProcesses: true);

        for (int i = 1; i <= count; i++)
        {
            token.ThrowIfCancellationRequested();
            report($"正在启动第 {i} / {count} 个窗口...");

            try
            {
                ProcessHelper.StartExternal(gamePath);
            }
            catch (Exception ex)
            {
                report($"第 {i} 个窗口启动失败：{ex.Message}");
                continue;
            }

            IntPtr found = IntPtr.Zero;
            DateTime deadline = DateTime.Now.AddSeconds(10);
            while (DateTime.Now < deadline)
            {
                await Task.Delay(200, token).ConfigureAwait(true);

                foreach (IntPtr hwnd in Snapshot(keyword, includeGameProcesses: true))
                {
                    if (!known.Contains(hwnd))
                    {
                        found = hwnd;
                        break;
                    }
                }

                if (found != IntPtr.Zero)
                {
                    break;
                }
            }

            if (found != IntPtr.Zero)
            {
                known.Add(found);
                captured.Add(found);
                report($"第 {i} 个窗口已捕获：{GetTitle(found)}");
            }
            else
            {
                report($"第 {i} 个窗口 10 秒内未出现，稍后全量扫描时补捕。");
            }
        }

        if (waitSeconds > 0)
        {
            report($"等待 {waitSeconds} 秒，让窗口稳定...");
            for (int s = waitSeconds; s > 0; s--)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(1000, token).ConfigureAwait(true);
            }
        }

        foreach (IntPtr hwnd in Snapshot(keyword, includeGameProcesses: true))
        {
            if (!captured.Contains(hwnd))
            {
                captured.Add(hwnd);
                report($"补捕到窗口：{GetTitle(hwnd)}");
            }
        }

        return captured;
    }

    /// <summary>主显示器工作区（物理像素）。</summary>
    public WorkArea GetWorkArea()
    {
        NativeMethods.RECT work = GetPrimaryWorkArea();
        return new WorkArea(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top);
    }

    /// <summary>主显示器工作区原始结构（物理像素）。</summary>
    internal NativeMethods.RECT GetPrimaryWorkArea()
    {
        var pt = new NativeMethods.POINT { X = 0, Y = 0 };
        IntPtr monitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };

        if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return info.rcWork;
        }

        return new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }

    /// <summary>
    /// 量出窗口边框的占位。排列的目标是「客户区」正好落在槽位上，
    /// 所以真正传给 SetWindowPos 的位置和尺寸都要把这段边框补偿掉，
    /// 否则带标题栏的窗口会整体偏右下、内容区也比预期小一截。
    /// </summary>
    public WindowFrame MeasureFrame(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT windowRect)
            || !NativeMethods.GetClientRect(hwnd, out NativeMethods.RECT clientRect))
        {
            return WindowFrame.None;
        }

        var origin = new NativeMethods.POINT { X = 0, Y = 0 };
        if (!NativeMethods.ClientToScreen(hwnd, ref origin))
        {
            return WindowFrame.None;
        }

        int extraW = (windowRect.Right - windowRect.Left) - clientRect.Right;
        int extraH = (windowRect.Bottom - windowRect.Top) - clientRect.Bottom;

        // 最小化或动画中的窗口矩形是负坐标、数值很怪，这种读数不可信，按「没有边框」处理
        if (extraW is < 0 or > 160 || extraH is < 0 or > 160)
        {
            return WindowFrame.None;
        }

        return new WindowFrame(origin.X - windowRect.Left, origin.Y - windowRect.Top, extraW, extraH);
    }

    /// <summary>
    /// 量一批窗口里最大的边框占位。取最大值而不是第一个，
    /// 是为了让「给标题栏预留多少空间」在所有窗口上一次性算够，不会有某个窗口压线。
    /// </summary>
    public WindowFrame MeasureFrame(IReadOnlyList<IntPtr> windows)
    {
        WindowFrame max = WindowFrame.None;
        foreach (IntPtr hwnd in windows)
        {
            if (!NativeMethods.IsWindow(hwnd))
            {
                continue;
            }

            WindowFrame frame = MeasureFrame(hwnd);
            max = new WindowFrame(
                Math.Max(max.OffsetX, frame.OffsetX),
                Math.Max(max.OffsetY, frame.OffsetY),
                Math.Max(max.ExtraWidth, frame.ExtraWidth),
                Math.Max(max.ExtraHeight, frame.ExtraHeight));
        }

        return max;
    }

    /// <summary>按排列方案摆放窗口，返回成功摆好的数量。</summary>
    public int ApplyLayout(IReadOnlyList<IntPtr> windows, LayoutPlan plan, bool resize, WorkArea keepInside)
    {
        if (plan.IsEmpty || windows.Count == 0)
        {
            return 0;
        }

        int ok = 0;
        int total = Math.Min(windows.Count, plan.Slots.Count);
        for (int i = 0; i < total; i++)
        {
            IntPtr hwnd = windows[i];
            if (!NativeMethods.IsWindow(hwnd))
            {
                continue;
            }

            if (ApplySlot(hwnd, plan.Slots[i], resize, keepInside))
            {
                ok++;
            }
        }

        return ok;
    }

    private bool ApplySlot(IntPtr hwnd, WindowSlot slot, bool resize, WorkArea keepInside)
    {
        if (NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            Thread.Sleep(30);
        }

        WindowFrame frame = MeasureFrame(hwnd);
        int x = slot.X - frame.OffsetX;
        int y = slot.Y - frame.OffsetY;
        int w = slot.Width + frame.ExtraWidth;
        int h = slot.Height + frame.ExtraHeight;

        if (!resize)
        {
            // 不改尺寸时保留窗口当前大小，只把它挪到槽位对应的客户区原点
            if (NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT current))
            {
                w = current.Right - current.Left;
                h = current.Bottom - current.Top;
            }
        }

        uint flags = NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW;
        bool ok = NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, flags);
        Thread.Sleep(30);

        // 兜底：万一算出来的位置让标题栏露到屏幕外（比如窗口边框比预估的厚），
        // 整体把窗口推回工作区内。只挪不缩，保证最小化 / 关闭按钮永远够得着。
        ClampIntoWorkArea(hwnd, keepInside);
        return ok;
    }

    /// <summary>把窗口整块挪进工作区（尺寸不动）。已经在里面就什么都不做。</summary>
    private static void ClampIntoWorkArea(IntPtr hwnd, WorkArea area)
    {
        if (area.Width <= 0 || area.Height <= 0)
        {
            return;
        }

        if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rc))
        {
            return;
        }

        int w = rc.Right - rc.Left;
        int h = rc.Bottom - rc.Top;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        int x = w <= area.Width ? Math.Clamp(rc.Left, area.Left, area.Left + area.Width - w) : area.Left;
        int y = h <= area.Height ? Math.Clamp(rc.Top, area.Top, area.Top + area.Height - h) : area.Top;

        if (x == rc.Left && y == rc.Top)
        {
            return;
        }

        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        Thread.Sleep(20);
    }

    /// <summary>把全部游戏窗口恢复并逐一前置。</summary>
    public int BringAllToFront(IReadOnlyList<IntPtr> windows)
    {
        int ok = 0;
        foreach (IntPtr hwnd in windows)
        {
            if (!NativeMethods.IsWindow(hwnd))
            {
                continue;
            }

            if (NativeMethods.IsIconic(hwnd))
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            }

            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);
            if (NativeMethods.SetForegroundWindow(hwnd))
            {
                ok++;
            }

            Thread.Sleep(40);
        }

        return ok;
    }

    /// <summary>老板键：最小化全部游戏窗口。</summary>
    public int MinimizeAll(IReadOnlyList<IntPtr> windows)
    {
        int ok = 0;
        foreach (IntPtr hwnd in windows)
        {
            if (!NativeMethods.IsWindow(hwnd))
            {
                continue;
            }

            if (NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MINIMIZE))
            {
                ok++;
            }
        }

        return ok;
    }

    /// <summary>窗口外框尺寸。</summary>
    public (int Width, int Height) GetWindowSize(IntPtr hwnd)
    {
        if (NativeMethods.IsWindow(hwnd) && NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rc))
        {
            int w = rc.Right - rc.Left;
            int h = rc.Bottom - rc.Top;
            if (w > 0 && h > 0)
            {
                return (w, h);
            }
        }

        return (0, 0);
    }

    /// <summary>窗口客户区尺寸（不含标题栏与边框）。</summary>
    public (int Width, int Height) GetClientSize(IntPtr hwnd)
    {
        if (NativeMethods.IsWindow(hwnd) && NativeMethods.GetClientRect(hwnd, out NativeMethods.RECT cr))
        {
            if (cr.Right > 0 && cr.Bottom > 0)
            {
                return (cr.Right, cr.Bottom);
            }
        }

        return (0, 0);
    }
}
