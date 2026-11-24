namespace Nightforge.Services;

/// <summary>
/// 给游戏窗口标题加 ①~⑤ 序号。
/// <para>
/// 五开时五个窗口长得一模一样，Alt+Tab 和任务栏里都是一片「梦幻西游：时空」，
/// 分不清哪个是哪个号。标题前面挂一个序号，再配合 Ctrl+Alt+1~5 直达，
/// 就能「看得见 + 点得到」。
/// </para>
/// <para>
/// 序号按窗口在屏幕上的位置排（左上优先），所以 ① 永远是左上那个格子，
/// 不会因为窗口启动顺序不同而错位。
/// </para>
/// </summary>
public static class CaptionService
{
    private static readonly string[] Marks = ["①", "②", "③", "④", "⑤"];

    /// <summary>最多支持五开，序号只有 ①~⑤。</summary>
    public const int MaxIndex = 5;

    public static string MarkOf(int index)
        => index is >= 1 and <= MaxIndex ? Marks[index - 1] : index.ToString();

    /// <summary>
    /// 去掉标题开头可能残留的序号（程序异常退出时留下的）。
    /// 连续几个都去掉，避免出现「① ① 梦幻西游」。
    /// </summary>
    public static string Strip(string title)
    {
        string text = title.TrimStart();
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (string mark in Marks)
            {
                if (text.StartsWith(mark, StringComparison.Ordinal))
                {
                    text = text[mark.Length..].TrimStart();
                    changed = true;
                }
            }
        }

        return text;
    }

    public static string WithMark(string title, int index) => $"{MarkOf(index)} {Strip(title)}";

    /// <summary>标题里是不是已经有序号了（游戏自己重设标题后会变回没有）。</summary>
    public static bool HasMark(string title)
    {
        string trimmed = title.TrimStart();
        return Marks.Any(m => trimmed.StartsWith(m, StringComparison.Ordinal));
    }

    /// <summary>按给定顺序给窗口标号（顺序应来自 <see cref="WindowService.OrderByPosition"/>）。</summary>
    public static int Apply(IReadOnlyList<IntPtr> ordered)
    {
        int ok = 0;
        for (int i = 0; i < ordered.Count && i < MaxIndex; i++)
        {
            IntPtr hwnd = ordered[i];
            if (!NativeMethods.IsWindow(hwnd))
            {
                continue;
            }

            string current = WindowService.GetTitle(hwnd);
            if (current.Length == 0)
            {
                continue;
            }

            string target = WithMark(current, i + 1);
            if (string.Equals(target, current, StringComparison.Ordinal))
            {
                ok++;
                continue;
            }

            if (NativeMethods.SetWindowText(hwnd, target))
            {
                ok++;
            }
        }

        return ok;
    }

    /// <summary>
    /// 去掉所有窗口标题里的序号。程序退出时调用，不留下「① 梦幻西游」这种痕迹；
    /// 启动时也调一次，清理上次异常退出留下的前缀。
    /// </summary>
    public static int ClearAll(IReadOnlyList<IntPtr> windows)
    {
        int ok = 0;
        foreach (IntPtr hwnd in windows)
        {
            if (!NativeMethods.IsWindow(hwnd))
            {
                continue;
            }

            string current = WindowService.GetTitle(hwnd);
            if (!HasMark(current))
            {
                continue;
            }

            string clean = Strip(current);
            if (clean.Length == 0)
            {
                continue;
            }

            if (NativeMethods.SetWindowText(hwnd, clean))
            {
                ok++;
            }
        }

        return ok;
    }
}
