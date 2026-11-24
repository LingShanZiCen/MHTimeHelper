namespace Nightforge.Models;

/// <summary>
/// 一个窗口要登录到哪：账号 / 服务器 / 角色，全部填文字即可。
/// 位置不用管，登录时由 OCR 在画面里自己找这几个字。
/// </summary>
public sealed class WindowLoginTarget
{
    /// <summary>账号（可留空）。填了会核对当前登录框里的账号，不一致时尝试切换。</summary>
    public string Account { get; set; } = "";

    /// <summary>服务器名，例如「红颜知己」。</summary>
    public string Server { get; set; } = "";

    /// <summary>角色名，例如「甜柚°」。留空则点第一个「点击登录」。</summary>
    public string Character { get; set; } = "";

    public bool IsEmpty
        => string.IsNullOrWhiteSpace(Account)
           && string.IsNullOrWhiteSpace(Server)
           && string.IsNullOrWhiteSpace(Character);

    public WindowLoginTarget Clone() => new()
    {
        Account = Account,
        Server = Server,
        Character = Character
    };

    /// <summary>日志里用的一句话描述。</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Account))
        {
            parts.Add($"账号 {Account}");
        }

        if (!string.IsNullOrWhiteSpace(Server))
        {
            parts.Add($"服务器 {Server}");
        }

        if (!string.IsNullOrWhiteSpace(Character))
        {
            parts.Add($"角色 {Character}");
        }

        return parts.Count == 0 ? "（未填写）" : string.Join(" → ", parts);
    }
}

/// <summary>自动登录设置，独立存 login.json。</summary>
public sealed class LoginSettings
{
    /// <summary>总开关。关掉时「一键启动」只启动与排列，不做任何登录动作。</summary>
    public bool Enabled { get; set; }

    /// <summary>窗口出现后先等几秒再开始认字（客户端加载界面期间认不出东西）。</summary>
    public int StartDelaySec { get; set; } = 8;

    /// <summary>每一步找文字最多等几秒。</summary>
    public int StepTimeoutSec { get; set; } = 15;

    /// <summary>是否自动点「登录游戏」「进入游戏」这类通用按钮。</summary>
    public bool AutoClickLogin { get; set; } = true;

    /// <summary>第 1–5 个窗口各自的登录目标。</summary>
    public List<WindowLoginTarget> Windows { get; set; } = [];

    public WindowLoginTarget ForWindow(int windowIndex)
    {
        Normalize();
        return windowIndex >= 1 && windowIndex <= Windows.Count
            ? Windows[windowIndex - 1]
            : new WindowLoginTarget();
    }

    public void Normalize()
    {
        Windows ??= [];

        while (Windows.Count < 5)
        {
            Windows.Add(new WindowLoginTarget());
        }

        if (Windows.Count > 5)
        {
            Windows.RemoveRange(5, Windows.Count - 5);
        }

        StartDelaySec = Math.Clamp(StartDelaySec, 0, 120);
        StepTimeoutSec = Math.Clamp(StepTimeoutSec, 3, 120);
    }
}
