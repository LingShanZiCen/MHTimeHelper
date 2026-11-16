namespace Nightforge.Models;

/// <summary>
/// 游戏画面比例。
/// <para>
/// <see cref="Key"/> 与游戏客户端写在 <c>LocalData\UserDefault.xml</c> 里的
/// <c>DeviceScreenResolution</c> 取值完全一致（从启动器程序里提取到的就是这几个），
/// 写入时直接用这个字符串，不需要再换算。
/// </para>
/// </summary>
public sealed record AspectRatioOption(string Key, string Name, int RatioW, int RatioH)
{
    /// <summary>宽 / 高，用于排列时按比例算窗口尺寸。</summary>
    public double Ratio => (double)RatioW / RatioH;

    /// <summary>竖屏时宽比高小，排列算法要据此换算出合理的列数。</summary>
    public bool IsPortrait => RatioW < RatioH;

    public override string ToString() => Name;

    /// <summary>可选比例。顺序即界面下拉框顺序。</summary>
    public static IReadOnlyList<AspectRatioOption> All { get; } =
    [
        new("Ratio16to9", "16:9 宽屏", 16, 9),
        new("Ratio4to3", "4:3 经典", 4, 3),
        new("RatioPortrait", "竖屏 9:16", 9, 16),
        new("Ratio16to9_4K", "16:9 宽屏（4K）", 16, 9),
        new("Ratio4to3_4K", "4:3 经典（4K）", 4, 3),
    ];

    public static AspectRatioOption Default => All[0];

    /// <summary>按 Configuration 里的 Key 取回选项；找不到返回 null。</summary>
    public static AspectRatioOption? FromKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        string trimmed = key.Trim();
        return All.FirstOrDefault(o => string.Equals(o.Key, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    public static AspectRatioOption FromKeyOrDefault(string? key) => FromKey(key) ?? Default;
}
