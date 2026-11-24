namespace Nightforge.Services;

/// <summary>
/// 把窗口抢到最前面。
///
/// 抓图和点击都依赖它：游戏是 DirectX 渲染，PrintWindow 抓不到画面，只能从屏幕拷；
/// 一块被别的窗口盖住的区域，拷回来的就是盖在上面的那个窗口。
/// 点击同理——坐标是按屏幕算的，窗口不在最前面就会点到别的程序。
/// </summary>
public static class WindowFocus
{
    /// <summary>
    /// Windows 只允许前台进程自己调用 SetForegroundWindow，
    /// 所以先用 AttachThreadInput 把输入队列挂到当前前台线程，还不够就补一次模拟 ALT 键，
    /// 让系统认为「用户刚刚有输入」从而放行。三招轮流试，最后返回是否真的抢到了。
    /// </summary>
    public static bool Force(IntPtr hwnd)
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
