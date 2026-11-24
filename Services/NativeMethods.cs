using System.Runtime.InteropServices;
using System.Text;

namespace Nightforge.Services;

/// <summary>集中的 Win32 互操作声明。</summary>
internal static class NativeMethods
{
    // ---------- 结构 ----------
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    /// <summary>
    /// 无边框窗口最大化时，系统默认按「整块屏幕」算尺寸，会盖住任务栏。
    /// 收到 WM_GETMINMAXINFO 时把最大化尺寸改成「显示器工作区」就能贴住任务栏上沿。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    // ---------- 常量 ----------
    public const int SW_SHOW = 5;
    public const int SW_MINIMIZE = 6;
    public const int SW_RESTORE = 9;

    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    public const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>GetWindow 的命令码：取 owner 窗口。</summary>
    public const uint GW_OWNER = 4;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    public const int WM_HOTKEY = 0x0312;

    /// <summary>窗口即将最大化/还原时询问尺寸限制。</summary>
    public const int WM_GETMINMAXINFO = 0x0024;

    // 退出流程用到的消息：点 × 等价于 WM_CLOSE，点按钮等价于 BM_CLICK
    public const uint WM_CLOSE = 0x0010;
    public const uint BM_CLICK = 0x00F5;

    public const uint WM_NULL = 0x0000;
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    public const byte VK_RETURN = 0x0D;
    public const byte VK_MENU = 0x12;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>把窗口提到 Z 序最上层（配合 SetForegroundWindow 提高抢前台成功率）。</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    // ---------- 窗口 ----------
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint Msg,
        IntPtr wParam,
        IntPtr lParam,
        uint fuFlags,
        uint uTimeout,
        out IntPtr lpdwResult);

    /// <summary>
    /// 窗口还在不在处理消息。
    /// 游戏卡死（无响应）时点它不会有任何反应，继续点只会让情况更糟，所以动手前先问一句。
    /// </summary>
    public static bool IsWindowResponsive(IntPtr hwnd, uint timeoutMs = 600)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            return false;
        }

        try
        {
            return SendMessageTimeout(
                hwnd,
                WM_NULL,
                IntPtr.Zero,
                IntPtr.Zero,
                SMTO_ABORTIFHUNG,
                timeoutMs,
                out _) != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>取 owner 窗口，用来区分「主窗口」和「附属小窗」（弹窗、提示条之类）。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>客户区尺寸（不含标题栏与边框），用来把「目标客户区尺寸」换算成窗口外框尺寸。</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    /// <summary>当前线程 ID，配合 AttachThreadInput 抢前台用。</summary>
    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    /// <summary>
    /// 把两个线程的输入队列临时挂在一起，让本进程也能把窗口设为前台。
    /// Windows 默认只允许「当前前台进程」调用 SetForegroundWindow，不这么做在后台时调用会被拒绝。
    /// </summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    /// <summary>异步投递消息，不阻塞调用线程；点 × 与点按钮都用它，避免目标窗口无响应时卡住界面。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    /// <summary>递归枚举子窗口，用来在确认框里定位「确定」按钮。</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    /// <summary>发送真实按键（走系统输入队列）。引擎自绘的弹框 PostMessage 键盘消息通常无效，只能靠它。</summary>
    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    // ---------- 显示器 ----------
    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    // ---------- 全局热键 ----------
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ---------- DPI ----------
    /// <summary>开启「每显示器 DPI 感知」。不开启时，系统会把窗口坐标按缩放比例虚拟化，
    /// 抓图和点击都会整体偏移（实测 125% 缩放下抓到的画面串到别的窗口上）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 的值。</summary>
    public static readonly IntPtr DPI_PER_MONITOR_AWARE_V2 = new(-4);

    /// <summary>某个窗口当前所在显示器的 DPI（96 = 100%，120 = 125%）。</summary>
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    // ---------- INI ----------
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetPrivateProfileString(string section, string key, string def,
        StringBuilder retVal, int size, string filePath);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WritePrivateProfileString(string section, string key, string value, string filePath);
}
