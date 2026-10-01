using System.Diagnostics;
using System.Text;

namespace Nightforge.Services;

/// <summary>窗口枚举、多开捕获、排列、前置与最小化。</summary>
public sealed class WindowService
{
    private readonly LogService _log;

    public WindowService(LogService log) => _log = log;

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

    /// <summary>枚举当前所有「可见 + 标题含关键词 + 非本程序」的窗口。</summary>
    public List<IntPtr> Snapshot(string keyword)
    {
        var result = new List<IntPtr>();
        if (string.IsNullOrWhiteSpace(keyword))
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

            if (title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(hwnd);
            }

            return true;
        }, IntPtr.Zero);

        return result;
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
        var known = Snapshot(keyword);

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

                foreach (IntPtr hwnd in Snapshot(keyword))
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

        foreach (IntPtr hwnd in Snapshot(keyword))
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

    /// <summary>五个窗口固定布局：左上 / 上中 / 右上 / 左下 / 右下。</summary>
    public void ArrangeFive(IReadOnlyList<IntPtr> windows, int width, int height, bool resize)
    {
        if (windows.Count == 0)
        {
            return;
        }

        NativeMethods.RECT wa = GetPrimaryWorkArea();
        int screenW = wa.Right - wa.Left;
        int screenH = wa.Bottom - wa.Top;

        (int X, int Y)[] slots =
        [
            (wa.Left, wa.Top),
            (wa.Left + (screenW - width) / 2, wa.Top),
            (wa.Right - width, wa.Top),
            (wa.Left, wa.Bottom - height),
            (wa.Right - width, wa.Bottom - height)
        ];

        for (int i = 0; i < windows.Count && i < slots.Length; i++)
        {
            MoveWindow(windows[i], slots[i].X, slots[i].Y, width, height, resize);
        }
    }

    /// <summary>其余数量按网格自动排布。</summary>
    public void ArrangeGrid(IReadOnlyList<IntPtr> windows, int width, int height, bool resize)
    {
        if (windows.Count == 0)
        {
            return;
        }

        NativeMethods.RECT wa = GetPrimaryWorkArea();
        int screenW = wa.Right - wa.Left;
        int screenH = wa.Bottom - wa.Top;

        int maxCols = Math.Max(1, screenW / Math.Max(1, width));
        int cols = Math.Min(maxCols, windows.Count);
        int rows = (int)Math.Ceiling(windows.Count / (double)cols);

        while (rows > 1 && cols < windows.Count && (long)rows * height > screenH)
        {
            cols++;
            rows = (int)Math.Ceiling(windows.Count / (double)cols);
        }

        for (int i = 0; i < windows.Count; i++)
        {
            int row = i / cols;
            int col = i % cols;
            int x = wa.Left + col * width;
            int y = wa.Top + row * height;

            if (y + height > wa.Bottom)
            {
                y = Math.Max(wa.Top, wa.Bottom - height);
            }

            if (x + width > wa.Right)
            {
                x = Math.Max(wa.Left, wa.Right - width);
            }

            MoveWindow(windows[i], x, y, width, height, resize);
        }
    }

    public bool MoveWindow(IntPtr hwnd, int x, int y, int width, int height, bool resize)
    {
        if (!NativeMethods.IsWindow(hwnd))
        {
            return false;
        }

        if (NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            Thread.Sleep(30);
        }

        if (!resize)
        {
            if (NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rc))
            {
                width = rc.Right - rc.Left;
                height = rc.Bottom - rc.Top;
            }
        }

        uint flags = NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW;
        bool ok = NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, flags);
        Thread.Sleep(50);
        return ok;
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
}
