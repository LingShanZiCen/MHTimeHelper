using System.Text;

namespace Nightforge.Services;

/// <summary>
/// 账号登录悬浮窗。
///
/// 「邮箱 + 进入游戏 + 使用其他账号登录」这一屏并不在游戏画面里，而是一个独立的小窗：
/// 类名 MPAY_LOGIN、标题「登录」、约 450×500、归属主窗口并浮在它上面。
/// 这带来两件事：
///   1. 直播 / 录屏只抓游戏画面时根本看不到它，所以认屏不能只看游戏窗口；
///   2. 单独截它反而更可靠——尺寸小、认字快、不受游戏窗口被裁切或缩放影响。
/// </summary>
public static class LoginOverlay
{
    /// <summary>已知的登录窗类名。版本更新后类名若变了，还有标题和尺寸兜底。</summary>
    private static readonly string[] KnownClasses = ["MPAY_LOGIN", "MPAY_LOGIN_WND"];

    private const string LoginTitle = "登录";

    /// <summary>
    /// 找某个游戏窗口的登录悬浮窗。找不到返回 IntPtr.Zero。
    /// </summary>
    public static IntPtr Find(IntPtr gameHwnd)
    {
        if (!NativeMethods.IsWindow(gameHwnd))
        {
            return IntPtr.Zero;
        }

        NativeMethods.GetWindowThreadProcessId(gameHwnd, out uint gamePid);

        IntPtr best = IntPtr.Zero;
        int bestScore = 0;
        long bestArea = 0;

        NativeMethods.EnumWindows((candidate, _) =>
        {
            if (candidate == gameHwnd || !NativeMethods.IsWindowVisible(candidate))
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(candidate, out uint pid);
            if (pid != gamePid)
            {
                return true;
            }

            if (!NativeMethods.GetClientRect(candidate, out NativeMethods.RECT rc))
            {
                return true;
            }

            int width = rc.Right - rc.Left;
            int height = rc.Bottom - rc.Top;

            // 登录窗比这大或比这小都不对：太小是输入法/阴影壳，太大是主窗口
            if (width is < 200 or > 900 || height is < 200 or > 1000)
            {
                return true;
            }

            string className = GetClassName(candidate);
            string title = GetTitle(candidate);

            int score = 0;
            if (KnownClasses.Any(c => string.Equals(c, className, StringComparison.OrdinalIgnoreCase)))
            {
                score = 6;
            }
            else if (title == LoginTitle)
            {
                score = 4;
            }
            else
            {
                return true;
            }

            // 归属这个游戏窗口的优先（五开时能分清是谁的登录窗）
            if (NativeMethods.GetWindow(candidate, NativeMethods.GW_OWNER) == gameHwnd)
            {
                score += 3;
            }

            long area = (long)width * height;
            if (score > bestScore || (score == bestScore && area > bestArea))
            {
                best = candidate;
                bestScore = score;
                bestArea = area;
            }

            return true;
        }, IntPtr.Zero);

        return best;
    }

    /// <summary>描述一下这个窗口，方便写日志。</summary>
    public static string Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            return "没有账号悬浮窗";
        }

        NativeMethods.GetClientRect(hwnd, out NativeMethods.RECT rc);
        return $"账号悬浮窗 0x{hwnd.ToInt64():X}（{GetClassName(hwnd)} {rc.Right - rc.Left}×{rc.Bottom - rc.Top}）";
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetTitle(IntPtr hwnd)
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
}
