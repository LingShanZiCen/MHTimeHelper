using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Nightforge.Models;
using Nightforge.Services;
using WinForms = System.Windows.Forms;

namespace Nightforge;

public partial class MainWindow : Window
{
    private readonly LogService _log = new();
    private readonly WindowService _windows;
    private HotkeyService? _hotkeys;
    private WinForms.NotifyIcon? _tray;
    private AppConfig _config = new();
    private List<IntPtr> _gameWindows = [];
    private bool _reallyExit;
    private bool _busy;
    private bool _checkingUpdate;
    private DispatcherTimer? _updateTimer;

    public MainWindow()
    {
        InitializeComponent();
        _windows = new WindowService(_log);
        _log.LineAdded += AppendLog;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    // ================= 初始化 =================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CmbCount.ItemsSource = Enumerable.Range(1, 5).Select(i => i.ToString()).ToList();
        CmbResMode.ItemsSource = AppConfig.ModeOptions;
        CmbResPreset.ItemsSource = AppConfig.PresetOptions;

        // 必须先读配置再注册：SourceInitialized 阶段文本框还是空的，会把空手势当成快捷键
        LoadConfigToUi();
        InitHotkeys();
        InitTray();
        UpdateAdminBanner();

        _log.Add($"幻夜工坊已启动（版本 {UpdateService.CurrentVersionText}）。");
        _log.Add($"配置文件：{ConfigService.ConfigPath}");
        if (!IsAdministrator())
        {
            _log.Add("提示：当前不是管理员权限，建议以管理员身份运行。");
        }

        InitUpdateCheck();
    }

    /// <summary>启动时联网检查更新：界面先显示，稍后再发起请求，失败只写日志不打扰。</summary>
    private void InitUpdateCheck()
    {
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _updateTimer.Tick += async (_, _) => await CheckUpdateAsync(silent: true);

        if (!_config.CheckUpdateOnStart)
        {
            return;
        }

        _updateTimer.Start();
        _ = AutoCheckAfterStartupAsync();
    }

    private async Task AutoCheckAfterStartupAsync()
    {
        // 让主界面先渲染出来，避免启动瞬间弹窗打断用户
        await Task.Delay(TimeSpan.FromSeconds(3));
        await CheckUpdateAsync(silent: true);
    }

    private void InitHotkeys()
    {
        _hotkeys ??= new HotkeyService(this);
        RegisterHotkeys(_config);
    }

    private void InitTray()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("显示主界面", null, (_, _) => RestoreFromTray());
        menu.Items.Add("退出", null, (_, _) => ExitApplication());

        _tray = new WinForms.NotifyIcon
        {
            Text = "幻夜工坊",
            Icon = LoadApplicationIcon(),
            ContextMenuStrip = menu,
            Visible = true
        };
        _tray.DoubleClick += (_, _) => RestoreFromTray();
    }

    /// <summary>读取随程序打包的 app.ico 作为托盘图标；资源缺失时退回系统默认图标。</summary>
    private static System.Drawing.Icon LoadApplicationIcon()
    {
        try
        {
            Stream? stream = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"))?.Stream;
            if (stream is not null)
            {
                using (stream)
                {
                    return new System.Drawing.Icon(stream, new System.Drawing.Size(32, 32));
                }
            }
        }
        catch
        {
            // 图标读取失败不影响主流程
        }

        return System.Drawing.SystemIcons.Application;
    }

    private void UpdateAdminBanner()
    {
        AdminBanner.Visibility = IsAdministrator() ? Visibility.Collapsed : Visibility.Visible;
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    // ================= 配置 =================

    private void LoadConfigToUi()
    {
        _config = ConfigService.Load();

        // 不再支持 Win 组合：旧配置里的 Win 热键会导致启动即注册失败，一次性迁移为新默认值并落盘
        if (HasWinModifier(_config.HotkeyFront) || HasWinModifier(_config.HotkeyMinimize) || HasWinModifier(_config.HotkeyGetSize))
        {
            var defaults = new AppConfig();
            if (HasWinModifier(_config.HotkeyFront)) _config.HotkeyFront = defaults.HotkeyFront;
            if (HasWinModifier(_config.HotkeyMinimize)) _config.HotkeyMinimize = defaults.HotkeyMinimize;
            if (HasWinModifier(_config.HotkeyGetSize)) _config.HotkeyGetSize = defaults.HotkeyGetSize;
            ConfigService.Save(_config);
            _log.Add($"旧配置中的 Win 组合已不再支持，已自动改为 {_config.HotkeyFront} / {_config.HotkeyMinimize} / {_config.HotkeyGetSize}。");
        }

        TxtGamePath.Text = _config.GamePath;
        TxtWinTitle.Text = _config.WinTitle;
        CmbCount.SelectedIndex = Math.Clamp(_config.LastCount, 1, 5) - 1;
        TxtWinWidth.Text = _config.WinWidth.ToString();
        TxtWinHeight.Text = _config.WinHeight.ToString();
        TxtWaitSec.Text = _config.WaitSec.ToString();
        CmbResMode.SelectedItem = AppConfig.ModeOptions.Contains(_config.ResMode) ? _config.ResMode : AppConfig.ModeOptions[0];
        CmbResPreset.SelectedItem = AppConfig.PresetOptions.Contains(_config.ResPreset) ? _config.ResPreset : AppConfig.PresetOptions[0];
        TxtSyncPath.Text = _config.SyncPath;
        TxtHotkeyFront.Text = _config.HotkeyFront;
        TxtHotkeyMinimize.Text = _config.HotkeyMinimize;
        TxtHotkeyGetSize.Text = _config.HotkeyGetSize;
        ChkAutoUpdate.IsChecked = _config.CheckUpdateOnStart;

        UpdateResControls();
    }

    private AppConfig CollectConfig(bool persist = true)
    {
        var cfg = new AppConfig
        {
            GamePath = TxtGamePath.Text.Trim().Trim('"'),
            WinTitle = string.IsNullOrWhiteSpace(TxtWinTitle.Text) ? "梦幻西游" : TxtWinTitle.Text.Trim(),
            LastCount = Math.Clamp(CmbCount.SelectedIndex + 1, 1, 5),
            WinWidth = ParseInt(TxtWinWidth.Text, 800),
            WinHeight = ParseInt(TxtWinHeight.Text, 600),
            WaitSec = Math.Clamp(ParseInt(TxtWaitSec.Text, 7), 1, 120),
            ResMode = CmbResMode.SelectedItem as string ?? AppConfig.ModeOptions[0],
            ResPreset = CmbResPreset.SelectedItem as string ?? AppConfig.PresetOptions[0],
            SyncPath = TxtSyncPath.Text.Trim().Trim('"'),
            HotkeyFront = TxtHotkeyFront.Text.Trim(),
            HotkeyMinimize = TxtHotkeyMinimize.Text.Trim(),
            HotkeyGetSize = TxtHotkeyGetSize.Text.Trim(),
            // 更新检查开关与「不再提示的版本」不属于界面收集项，按当前值透传，避免保存时被重置
            CheckUpdateOnStart = ChkAutoUpdate.IsChecked == true,
            SkipVersion = _config.SkipVersion
        };

        if (persist)
        {
            ConfigService.Save(cfg);
        }

        return cfg;
    }

    private static int ParseInt(string text, int fallback)
        => int.TryParse(text.Trim(), out int value) ? value : fallback;

    /// <summary>判断手势里是否含 Win 修饰键。</summary>
    private static bool HasWinModifier(string gesture)
        => gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Any(p => p.Equals("Win", StringComparison.OrdinalIgnoreCase)
                        || p.Equals("Windows", StringComparison.OrdinalIgnoreCase));

    // ================= 热键 =================

    /// <summary>重新注册全部热键，返回三条是否全部注册成功。</summary>
    private bool RegisterHotkeys(AppConfig cfg)
    {
        if (_hotkeys is null)
        {
            return false;
        }

        _hotkeys.UnregisterAll();
        bool allOk = true;

        if (!_hotkeys.Register(cfg.HotkeyFront, () => BringAllWindows("热键"), out string err1))
        {
            _log.Add(err1);
            allOk = false;
        }

        if (!_hotkeys.Register(cfg.HotkeyMinimize, () => MinimizeAllWindows("热键"), out string err2))
        {
            _log.Add(err2);
            allOk = false;
        }

        if (!_hotkeys.Register(cfg.HotkeyGetSize, () => ReadWindowSize("热键"), out string err3))
        {
            _log.Add(err3);
            allOk = false;
        }

        return allOk;
    }

    private void OnSetHotkeyClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string which)
        {
            return;
        }

        string current = which switch
        {
            "Front" => TxtHotkeyFront.Text,
            "Minimize" => TxtHotkeyMinimize.Text,
            _ => TxtHotkeyGetSize.Text
        };

        // 录制期间先注销本程序的全部全局热键：
        // 否则用户按下当前已注册的组合（如 Ctrl+Alt+T）时会被热键抢先响应，
        // 按键根本送不到录制窗口，表现就是「按了没反应、设置不了」。
        _hotkeys?.UnregisterAll();

        string? captured = null;
        try
        {
            var dialog = new HotkeyCaptureWindow(current) { Owner = this };
            if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.Result))
            {
                captured = dialog.Result;
            }
        }
        finally
        {
            // 无论确定还是取消，都必须恢复热键注册，避免取消一次后热键全部失效。
            if (captured is not null)
            {
                switch (which)
                {
                    case "Front":
                        TxtHotkeyFront.Text = captured;
                        break;
                    case "Minimize":
                        TxtHotkeyMinimize.Text = captured;
                        break;
                    default:
                        TxtHotkeyGetSize.Text = captured;
                        break;
                }
            }

            AppConfig cfg = CollectConfig();
            _config = cfg;
            bool allOk = RegisterHotkeys(cfg);

            if (captured is not null)
            {
                _log.Add(allOk
                    ? $"快捷键已更新：{captured}"
                    : $"快捷键 {captured} 已保存，但未能全部生效（可能被系统或其他程序占用），详见上方日志");
            }
        }
    }

    // ================= 主要动作 =================

    private async void OnLaunchClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        AppConfig cfg = CollectConfig();
        _config = cfg;

        if (cfg.GamePath.Length == 0 || !File.Exists(cfg.GamePath))
        {
            MessageBox.Show("请先填写正确的游戏路径（必须包含 .exe 文件名，且文件真实存在）。",
                "路径无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _busy = true;
        try
        {
            _log.AddSeparator();
            _log.Add($"一键启动：{cfg.LastCount} 个窗口，关键词「{cfg.WinTitle}」");

            _gameWindows = await _windows.LaunchMultipleAsync(
                cfg.GamePath, cfg.WinTitle, cfg.LastCount, cfg.WaitSec, _log.Add, CancellationToken.None);

            if (_gameWindows.Count == 0)
            {
                _log.Add("未捕获到任何游戏窗口，请检查游戏路径与标题关键词。");
                return;
            }

            _log.Add($"共捕获 {_gameWindows.Count} 个窗口，开始排列...");
            ArrangeWindows(cfg, _gameWindows);
        }
        catch (Exception ex)
        {
            _log.Add($"启动过程出错：{ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    private void ArrangeWindows(AppConfig cfg, IReadOnlyList<IntPtr> windows)
    {
        bool resize = cfg.ResMode != "不改变窗口大小";
        int width = cfg.WinWidth;
        int height = cfg.WinHeight;

        if (cfg.ResMode == "使用预设")
        {
            string[] parts = cfg.ResPreset.Split('x', 'X');
            if (parts.Length == 2 && int.TryParse(parts[0], out int pw) && int.TryParse(parts[1], out int ph))
            {
                width = pw;
                height = ph;
            }
        }

        if (windows.Count == 5)
        {
            _windows.ArrangeFive(windows, width, height, resize);
        }
        else
        {
            _windows.ArrangeGrid(windows, width, height, resize);
        }

        _log.Add($"排列完成（{windows.Count} 个窗口，{(resize ? "含调整大小" : "仅移动位置")}）。");
    }

    private void OnBringAllClick(object sender, RoutedEventArgs e) => BringAllWindows("按钮");

    private void BringAllWindows(string source)
    {
        List<IntPtr> windows = RefreshWindows();
        if (windows.Count == 0)
        {
            _log.Add($"前置全部（{source}）：没有找到匹配的窗口。");
            return;
        }

        int ok = _windows.BringAllToFront(windows);
        _log.Add($"前置全部（{source}）：{ok} / {windows.Count} 个窗口已置顶。");
    }

    private void OnBossKeyClick(object sender, RoutedEventArgs e) => MinimizeAllWindows("按钮");

    private void MinimizeAllWindows(string source)
    {
        List<IntPtr> windows = RefreshWindows();
        if (windows.Count == 0)
        {
            _log.Add($"老板键（{source}）：没有找到匹配的窗口。");
            return;
        }

        int ok = _windows.MinimizeAll(windows);
        _log.Add($"老板键（{source}）：已最小化 {ok} 个窗口。");
    }

    private void OnGetSizeClick(object sender, RoutedEventArgs e) => ReadWindowSize("按钮");

    private void ReadWindowSize(string source)
    {
        List<IntPtr> windows = RefreshWindows();
        if (windows.Count == 0)
        {
            _log.Add($"获取窗口大小（{source}）：没有找到匹配的窗口，请先启动游戏。");
            return;
        }

        (int Width, int Height) size = _windows.GetWindowSize(windows[0]);
        if (size.Width <= 0 || size.Height <= 0)
        {
            _log.Add($"获取窗口大小（{source}）：读取失败。");
            return;
        }

        TxtWinWidth.Text = size.Width.ToString();
        TxtWinHeight.Text = size.Height.ToString();
        CmbResMode.SelectedItem = "自定义宽高";
        UpdateResControls();
        _log.Add($"获取窗口大小（{source}）：{size.Width} × {size.Height}，已回填到界面。");
    }

    private List<IntPtr> RefreshWindows()
    {
        _gameWindows = _gameWindows.Where(NativeMethods.IsWindow).ToList();
        if (_gameWindows.Count == 0)
        {
            string keyword = string.IsNullOrWhiteSpace(TxtWinTitle.Text) ? "梦幻西游" : TxtWinTitle.Text.Trim();
            _gameWindows = _windows.Snapshot(keyword);
        }

        return _gameWindows;
    }

    private void OnSaveConfigClick(object sender, RoutedEventArgs e)
    {
        // 手输或录制得到的热键先校验格式，避免把无效写法存进配置
        var problems = new List<string>();
        CheckHotkey("前置全部窗口", TxtHotkeyFront.Text, problems);
        CheckHotkey("老板键（最小化）", TxtHotkeyMinimize.Text, problems);
        CheckHotkey("获取窗口大小", TxtHotkeyGetSize.Text, problems);

        if (problems.Count > 0)
        {
            MessageBox.Show(
                "以下快捷键写得不合法，请修正后再保存：\n\n"
                + string.Join("\n", problems)
                + "\n\n正确示例：Ctrl+Alt+T、Ctrl+Shift+F5、F8（修饰键可省略，但字母/数字单键会吞掉全局输入，不建议）。",
                "快捷键格式有误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _config = CollectConfig();
        bool allOk = RegisterHotkeys(_config);
        _log.Add(allOk
            ? "配置已保存。"
            : "配置已保存，但部分快捷键未能注册（可能被系统或其他程序占用），详见上方日志。");
    }

    private static void CheckHotkey(string label, string gesture, List<string> problems)
    {
        string text = gesture.Trim();
        var (_, virtualKey) = HotkeyService.Parse(text);

        if (virtualKey == 0)
        {
            problems.Add($"· {label}：「{text}」无法识别");
        }
    }

    // ================= 浏览与外部程序 =================

    private void OnBrowseGameClick(object sender, RoutedEventArgs e)
    {
        string? path = PickExe("选择游戏启动程序");
        if (path is not null)
        {
            TxtGamePath.Text = path;
        }
    }

    private void OnBrowseSyncClick(object sender, RoutedEventArgs e)
    {
        string? path = PickExe("选择同步器程序");
        if (path is not null)
        {
            TxtSyncPath.Text = path;
        }
    }

    private static string? PickExe(string title)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private void OnOpenSyncClick(object sender, RoutedEventArgs e)
    {
        string path = TxtSyncPath.Text.Trim().Trim('"');
        if (path.Length == 0 || !File.Exists(path))
        {
            MessageBox.Show("请先填写正确的同步器路径。", "路径无效",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            ProcessHelper.StartExternal(path);
            _log.Add($"已启动同步器：{path}");
        }
        catch (Exception ex)
        {
            _log.Add($"同步器启动失败：{ex.Message}");
        }
    }

    private void OnRestartAdminClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                return;
            }

            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            _reallyExit = true;
            Close();
        }
        catch (Exception ex)
        {
            _log.Add($"提权重启失败：{ex.Message}");
        }
    }

    // ================= 更新检查 =================

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        _log.Add("正在联网检查更新...");
        await CheckUpdateAsync(silent: false);
    }

    private void OnOpenRepoClick(object sender, RoutedEventArgs e)
        => UpdateWindow.OpenUrl(UpdateService.RepoUrl);

    private void OnAutoUpdateChanged(object sender, RoutedEventArgs e)
    {
        _config.CheckUpdateOnStart = ChkAutoUpdate.IsChecked == true;
        ConfigService.Save(_config);

        if (_config.CheckUpdateOnStart)
        {
            _updateTimer?.Start();
            _log.Add("已开启启动时自动检查更新。");
            _ = CheckUpdateAsync(silent: true);
        }
        else
        {
            _updateTimer?.Stop();
            _log.Add("已关闭启动时自动检查更新（仍可点「检查更新」手动查询）。");
        }
    }

    /// <summary>
    /// 联网查询 GitHub 最新版本。silent=true 时只在发现新版本才弹窗，其余情况只写日志。
    /// 全程不抛异常，网络不通也只是提示一下，绝不拖垮主程序。
    /// </summary>
    private async Task CheckUpdateAsync(bool silent)
    {
        if (_checkingUpdate)
        {
            return;
        }

        _checkingUpdate = true;
        try
        {
            UpdateCheckResult result = await UpdateService.CheckAsync();

            if (!result.Succeeded || result.Info is null)
            {
                _log.Add($"检查更新失败：{result.Error}");
                if (!silent)
                {
                    MessageBox.Show(
                        $"暂时查不到最新版本，请稍后再试，或直接前往 GitHub 查看。\n\n原因：{result.Error}\n{UpdateService.ReleasesUrl}",
                        "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                return;
            }

            UpdateInfo info = result.Info;
            string current = UpdateService.CurrentVersionText;

            if (!UpdateService.IsNewer(info.Version))
            {
                _log.Add($"已是最新版本（{current}）。");
                if (!silent)
                {
                    MessageBox.Show($"当前已是最新版本（{current}）。", "检查更新",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }

                return;
            }

            // 用户点过「本版本不再提示」就按配置静默跳过（手动检查时仍然展示）
            if (silent && IsSkipped(info.Version))
            {
                _log.Add($"发现新版本 {info.Version}，但已设置不再提示该版本。");
                return;
            }

            _log.Add($"发现新版本：{info.Version}（当前 {current}），已弹出更新提示。");

            var dialog = new UpdateWindow(info, current);
            if (IsVisible)
            {
                dialog.Owner = this;
            }

            dialog.ShowDialog();

            if (dialog.SkippedVersion is not null)
            {
                _config.SkipVersion = dialog.SkippedVersion;
                ConfigService.Save(_config);
                _log.Add($"已设置不再提示版本 {dialog.SkippedVersion}。");
            }
        }
        catch (Exception ex)
        {
            _log.Add($"检查更新出错：{ex.Message}");
        }
        finally
        {
            _checkingUpdate = false;
        }
    }

    private bool IsSkipped(string version)
        => string.Equals(Normalize(_config.SkipVersion), Normalize(version), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string version) => version.Trim().TrimStart('v', 'V');

    // ================= 日志 =================

    private void AppendLog(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AppendLog(line));
            return;
        }

        TxtLog.AppendText(line + Environment.NewLine);
        TxtLog.ScrollToEnd();
    }

    private void OnClearLogClick(object sender, RoutedEventArgs e)
    {
        TxtLog.Clear();
        _log.Clear();
    }

    private void OnExportLogClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出运行日志",
            Filter = "文本文件 (*.txt)|*.txt",
            FileName = $"梦幻时空助手_日志_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            _log.Export(dialog.FileName);
            _log.Add($"日志已导出：{dialog.FileName}");
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{dialog.FileName}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导出失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ================= 其它 =================

    private void OnResModeChanged(object sender, SelectionChangedEventArgs e) => UpdateResControls();

    private void UpdateResControls()
    {
        if (TxtWinWidth is null || CmbResPreset is null)
        {
            return;
        }

        bool preset = (CmbResMode.SelectedItem as string) == "使用预设";
        CmbResPreset.IsEnabled = preset;
        TxtWinWidth.IsEnabled = !preset;
        TxtWinHeight.IsEnabled = !preset;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;

        if (_reallyExit)
        {
            ShutdownAll();
            return;
        }

        MessageBoxResult result = MessageBox.Show(
            "是：彻底退出程序\n否：最小化到托盘继续运行\n取消：返回",
            "关闭幻夜工坊", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        switch (result)
        {
            case MessageBoxResult.Yes:
                ShutdownAll();
                break;
            case MessageBoxResult.No:
                Hide();
                break;
        }
    }

    private void ExitApplication()
    {
        _reallyExit = true;
        Close();
    }

    private void ShutdownAll()
    {
        _hotkeys?.Dispose();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }

        _reallyExit = true;
        Application.Current.Shutdown();
    }
}

/// <summary>快捷键录制窗口：勾选修饰键 + 按下主键，避免占用系统级键盘钩子。</summary>
internal sealed class HotkeyCaptureWindow : Window
{
    private readonly CheckBox _cbCtrl = new() { Content = "Ctrl", Margin = new Thickness(0, 0, 16, 0) };
    private readonly CheckBox _cbAlt = new() { Content = "Alt", Margin = new Thickness(0, 0, 16, 0) };
    private readonly CheckBox _cbShift = new() { Content = "Shift" };
    private readonly TextBox _keyBox = new()
    {
        Height = 32,
        IsReadOnly = true,
        VerticalContentAlignment = VerticalAlignment.Center,
        Text = "点击此处后按下主键"
    };
    private readonly TextBlock _preview = new()
    {
        Margin = new Thickness(0, 14, 0, 0),
        FontSize = 14,
        FontWeight = FontWeights.SemiBold
    };
    private string _keyName = string.Empty;
    private bool _hadWin;

    public string? Result { get; private set; }

    public HotkeyCaptureWindow(string current)
    {
        Title = "设置快捷键";
        Width = 440;
        Height = 360;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(20) };

        panel.Children.Add(new TextBlock { Text = "修饰键", Margin = new Thickness(0, 0, 0, 8) });

        var mods = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
        mods.Children.Add(_cbCtrl);
        mods.Children.Add(_cbAlt);
        mods.Children.Add(_cbShift);
        panel.Children.Add(mods);

        panel.Children.Add(new TextBlock { Text = "主键", Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(_keyBox);
        panel.Children.Add(_preview);
        panel.Children.Add(new TextBlock
        {
            Text = "打开本窗口后直接按组合键即可（如 Ctrl+Alt+T），修饰键会自动勾选，什么都不按时按 Esc 取消。\n"
                 + "字母、数字、F1-F24、回车、方向键等都能录进来；字母/数字单独使用会全局吞掉该键，建议搭配 Ctrl / Alt / Shift。已移除 Win 键支持。",
            Opacity = 0.65,
            FontSize = 11,
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        var ok = new Button { Content = "确定", Width = 84, Height = 32, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "取消", Width = 84, Height = 32, IsCancel = true };
        ok.Click += (_, _) => Confirm();
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        Content = panel;

        _cbCtrl.Checked += (_, _) => UpdatePreview();
        _cbCtrl.Unchecked += (_, _) => UpdatePreview();
        _cbAlt.Checked += (_, _) => UpdatePreview();
        _cbAlt.Unchecked += (_, _) => UpdatePreview();
        _cbShift.Checked += (_, _) => UpdatePreview();
        _cbShift.Unchecked += (_, _) => UpdatePreview();
        // 捕获挂在窗口级：对话框一打开按键盘就能录，不需要用户先点一下文本框
        PreviewKeyDown += OnKeyDownCapture;
        // 兜底：中文输入法激活时字母键会被 IME 先吃掉（WPF 只收到 Key.ImeProcessed），
        // 单靠 KeyDown 会漏字，这里再挂一层文本输入事件把字母补回来。
        PreviewTextInput += OnTextInputCapture;
        // 从根上关掉本窗口的输入法：录制的是物理按键，不该被中文输入法改写。
        InputMethod.SetIsInputMethodEnabled(this, false);
        InputMethod.SetIsInputMethodEnabled(_keyBox, false);
        _keyBox.GotFocus += (_, _) => _keyBox.Text = string.IsNullOrEmpty(_keyName) ? "请按下主键" : _keyBox.Text;
        Loaded += (_, _) => _keyBox.Focus();

        ApplyCurrent(current);
    }

    private void ApplyCurrent(string current)
    {
        if (string.IsNullOrWhiteSpace(current))
        {
            UpdatePreview();
            return;
        }

        foreach (string part in current.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    _cbCtrl.IsChecked = true;
                    break;
                case "ALT":
                    _cbAlt.IsChecked = true;
                    break;
                case "SHIFT":
                    _cbShift.IsChecked = true;
                    break;
                case "WIN":
                case "WINDOWS":
                    // 新版不再支持 Win 组合：Win 系组合多为系统保留，注册容易失败
                    _hadWin = true;
                    break;
                default:
                    _keyName = part.ToUpperInvariant();
                    break;
            }
        }

        _keyBox.Text = string.IsNullOrEmpty(_keyName) ? "点击此处后按下主键" : _keyName;

        if (_hadWin)
        {
            _preview.Text = $"原快捷键「{current}」含 Win 键，已不再支持，请重新按一次组合键";
            return;
        }

        UpdatePreview();
    }

    /// <summary>取真实按键：中文输入法下字母键会以 ImeProcessed 形式送达，真实键在 ImeProcessedKey 里。</summary>
    private static Key ResolveKey(KeyEventArgs e)
    {
        Key key = e.Key;

        if (key == Key.ImeProcessed)
        {
            key = e.ImeProcessedKey;
        }
        else if (key == Key.DeadCharProcessed)
        {
            key = e.DeadCharProcessedKey;
        }

        if (key == Key.System)
        {
            key = e.SystemKey;
        }

        return key;
    }

    private void OnKeyDownCapture(object sender, KeyEventArgs e)
    {
        Key key = ResolveKey(e);

        // 按下修饰键本身：自动勾选对应复选框，用户能直观看到当前组合
        switch (key)
        {
            case Key.LeftCtrl or Key.RightCtrl:
                _cbCtrl.IsChecked = true;
                e.Handled = true;
                return;
            case Key.LeftAlt or Key.RightAlt:
                _cbAlt.IsChecked = true;
                e.Handled = true;
                return;
            case Key.LeftShift or Key.RightShift:
                _cbShift.IsChecked = true;
                e.Handled = true;
                return;
            case Key.LWin or Key.RWin:
                // 不再支持 Win 组合，同时吞掉按键，避免弹出开始菜单打断录制
                e.Handled = true;
                return;
        }

        // 一个修饰键都没勾选时按 Esc，视为取消设置
        if (key == Key.Escape
            && _cbCtrl.IsChecked != true && _cbAlt.IsChecked != true
            && _cbShift.IsChecked != true)
        {
            DialogResult = false;
            return;
        }

        string name = KeyToName(key);
        if (name.Length == 0)
        {
            e.Handled = true;
            return;
        }

        _keyName = name;
        _keyBox.Text = name;
        e.Handled = true;
        UpdatePreview();
    }

    /// <summary>输入法吞掉字母键时的兜底：用产生的字符反推主键。</summary>
    private void OnTextInputCapture(object sender, TextCompositionEventArgs e)
    {
        foreach (char ch in e.Text)
        {
            string? name = CharToKeyName(ch);
            if (name is null)
            {
                continue;
            }

            _keyName = name;
            _keyBox.Text = name;
            e.Handled = true;
            UpdatePreview();
            return;
        }
    }

    private static string? CharToKeyName(char ch)
    {
        if (ch is >= 'a' and <= 'z')
        {
            return char.ToUpperInvariant(ch).ToString();
        }

        if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            return ch.ToString();
        }

        return ch switch
        {
            '`' or '~' => "`",
            '-' or '_' => "-",
            '=' or '+' => "=",
            '[' or '{' => "[",
            ']' or '}' => "]",
            '\\' or '|' => "\\",
            ';' or ':' => ";",
            '\'' or '"' => "'",
            ',' or '<' => ",",
            '.' or '>' => ".",
            '/' or '?' => "/",
            ' ' => "Space",
            _ => null
        };
    }

    /// <summary>是否属于“打字键”（字母、数字、符号、空格）：单独当全局热键会污染整个系统的输入。</summary>
    private static bool IsTypingKey(string name) => name.Length == 1 || name == "Space";

    private void UpdatePreview()
    {
        var parts = new List<string>();
        if (_cbCtrl.IsChecked == true) parts.Add("Ctrl");
        if (_cbAlt.IsChecked == true) parts.Add("Alt");
        if (_cbShift.IsChecked == true) parts.Add("Shift");
        if (!string.IsNullOrEmpty(_keyName)) parts.Add(_keyName);

        if (parts.Count == 0)
        {
            _preview.Text = "（未设置）";
            return;
        }

        _preview.Text = string.Join(" + ", parts);

        if (parts.Count == 1 && IsTypingKey(_keyName))
        {
            _preview.Text += "　（字母/数字单键会吞掉全局输入，建议加 Ctrl / Alt / Shift）";
        }
    }

    private void Confirm()
    {
        var parts = new List<string>();
        if (_cbCtrl.IsChecked == true) parts.Add("Ctrl");
        if (_cbAlt.IsChecked == true) parts.Add("Alt");
        if (_cbShift.IsChecked == true) parts.Add("Shift");

        if (string.IsNullOrEmpty(_keyName))
        {
            MessageBox.Show("请先按下主键。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 字母 / 数字 / 符号这类“打字键”单独做全局热键，会把整个电脑上这个键吞掉，
        // 这里明确提醒一次，最终由用户自己决定。
        if (parts.Count == 0 && IsTypingKey(_keyName))
        {
            MessageBoxResult answer = MessageBox.Show(
                $"「{_keyName}」没有搭配任何修饰键。\n\n"
                + "这样设置后，只要本程序在运行，整个电脑上按这个键都会被热键接管（在别的程序里打字会没反应）。\n"
                + "建议改成 Ctrl / Alt / Shift 组合。确定仍要这样设置吗？",
                "确认单独使用该键", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        parts.Add(_keyName);
        Result = string.Join("+", parts);
        DialogResult = true;
    }

    private static string KeyToName(Key key)
    {
        if (key is >= Key.A and <= Key.Z)
        {
            return key.ToString();
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            return ((int)(key - Key.D0)).ToString();
        }

        if (key is >= Key.F1 and <= Key.F24)
        {
            return key.ToString();
        }

        return key switch
        {
            Key.OemTilde => "`",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemPipe => "\\",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            Key.Space => "Space",
            Key.Tab => "Tab",
            Key.Enter => "Enter",
            Key.Escape => "Esc",
            Key.Back => "Backspace",
            Key.Delete => "Delete",
            Key.Insert => "Insert",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Left => "Left",
            Key.Right => "Right",
            _ => string.Empty
        };
    }
}
