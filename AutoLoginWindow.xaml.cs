using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Nightforge.Models;
using Nightforge.Services;

namespace Nightforge;

/// <summary>
/// 自动登录设置窗：每个窗口填「账号 / 服务器 / 角色」，登录时由 OCR 在画面里找这几个字并点击。
/// 不涉及任何坐标，游戏改版后一般不用重配。
/// </summary>
public sealed partial class AutoLoginWindow : Window
{
    private readonly LoginSettings _settings;
    private readonly AutoLoginService _service;
    private readonly Func<IReadOnlyList<IntPtr>> _windowsProvider;

    private readonly List<TextBox> _accountBoxes = [];
    private readonly List<TextBox> _serverBoxes = [];
    private readonly List<TextBox> _characterBoxes = [];

    private CancellationTokenSource? _ocrCts;

    public AutoLoginWindow(
        LoginSettings settings,
        AutoLoginService service,
        Func<IReadOnlyList<IntPtr>> windowsProvider)
    {
        InitializeComponent();

        _settings = settings;
        _service = service;
        _windowsProvider = windowsProvider;

        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ChkEnabled.IsChecked = _settings.Enabled;
        ChkAutoClick.IsChecked = _settings.AutoClickLogin;
        TxtStartDelay.Text = _settings.StartDelaySec.ToString();
        TxtStepTimeout.Text = _settings.StepTimeoutSec.ToString();

        BuildTargetRows();
        RefreshTestWindows();

        SetStatus(OcrService.IsAvailable
            ? "就绪。可先点「识别测试」，确认画面里的字能认出来。"
            : $"系统 OCR 不可用：{OcrService.UnavailableReason}");
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        CommitToSettings();
        LoginSettingsService.Save(_settings);
    }

    private void OnClosed(object? sender, EventArgs e) => _ocrCts?.Cancel();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // ================= 填表 =================

    private void BuildTargetRows()
    {
        TargetGrid.ColumnDefinitions.Clear();
        TargetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        TargetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        TargetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        TargetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (int i = 0; i < LoginSettingsService.MaxWindows; i++)
        {
            TargetGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
            {
                Text = (i + 1).ToString(),
                Style = (Style)FindResource("FieldLabel"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6)
            };
            Grid.SetRow(label, i);
            Grid.SetColumn(label, 0);
            TargetGrid.Children.Add(label);

            WindowLoginTarget target = _settings.ForWindow(i + 1);

            TextBox account = AddBox(i, 1, target.Account, "例如 liaozicen041203@163.com（可空）");
            TextBox server = AddBox(i, 2, target.Server, "例如 红颜知己");
            TextBox character = AddBox(i, 3, target.Character, "例如 甜柚°（可空）");

            _accountBoxes.Add(account);
            _serverBoxes.Add(server);
            _characterBoxes.Add(character);
        }
    }

    private TextBox AddBox(int row, int column, string value, string hint)
    {
        var box = new TextBox
        {
            Text = value ?? "",
            Style = (Style)FindResource("InputBox"),
            Height = 32,
            Margin = new Thickness(0, 0, column == 3 ? 0 : 10, 6),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = hint
        };

        Grid.SetRow(box, row);
        Grid.SetColumn(box, column);
        TargetGrid.Children.Add(box);
        return box;
    }

    private void CommitToSettings()
    {
        _settings.Enabled = ChkEnabled.IsChecked == true;
        _settings.AutoClickLogin = ChkAutoClick.IsChecked == true;
        _settings.StartDelaySec = ParseInt(TxtStartDelay.Text, _settings.StartDelaySec);
        _settings.StepTimeoutSec = ParseInt(TxtStepTimeout.Text, _settings.StepTimeoutSec);

        for (int i = 0; i < LoginSettingsService.MaxWindows; i++)
        {
            WindowLoginTarget target = _settings.ForWindow(i + 1);
            target.Account = _accountBoxes[i].Text.Trim();
            target.Server = _serverBoxes[i].Text.Trim();
            target.Character = _characterBoxes[i].Text.Trim();
        }

        _settings.Normalize();
    }

    private static int ParseInt(string text, int fallback)
        => int.TryParse(text.Trim(), out int value) ? value : fallback;

    private void OnCopyFirstClick(object sender, RoutedEventArgs e)
    {
        for (int i = 1; i < LoginSettingsService.MaxWindows; i++)
        {
            _accountBoxes[i].Text = _accountBoxes[0].Text;
            _serverBoxes[i].Text = _serverBoxes[0].Text;
            _characterBoxes[i].Text = _characterBoxes[0].Text;
        }

        SetStatus("已把窗口 1 的内容复制到其余窗口，各自改角色名即可。");
    }

    // ================= 识别测试 =================

    private void RefreshTestWindows()
    {
        int count = _windowsProvider().Count;
        var items = new List<string>();
        for (int i = 1; i <= count; i++)
        {
            items.Add($"窗口 {i}");
        }

        if (items.Count == 0)
        {
            items.Add("未捕获");
        }

        CmbTestWindow.ItemsSource = items;
        CmbTestWindow.SelectedIndex = 0;
        CmbTestWindow.IsEnabled = count > 0;
    }

    private async void OnRecognizeClick(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<IntPtr> windows = _windowsProvider();
        if (windows.Count == 0)
        {
            RefreshTestWindows();
            SetStatus("还没有捕获到游戏窗口。先回主界面点「一键启动」或「前置全部」。");
            return;
        }

        int index = Math.Clamp(CmbTestWindow.SelectedIndex, 0, windows.Count - 1);
        IntPtr hwnd = windows[index];

        _ocrCts?.Cancel();
        _ocrCts = new CancellationTokenSource();

        SetStatus("正在识别，请稍候…");
        TxtOcrResult.Text = "";

        try
        {
            List<TextLine> lines = await _service.RecognizeAsync(hwnd, _ocrCts.Token);
            if (lines.Count == 0)
            {
                SetStatus("没认出任何文字：可能窗口还在加载，或者抓不到画面（可先把窗口点一下再试）。");
                return;
            }

            CommitToSettings();

            // 顺手核对一遍：填的字有没有出现在识别结果里
            var found = new List<string>();
            var missing = new List<string>();
            for (int i = 0; i < LoginSettingsService.MaxWindows; i++)
            {
                if (i >= windows.Count)
                {
                    break;
                }

                WindowLoginTarget target = _settings.ForWindow(i + 1);
                if (target.IsEmpty)
                {
                    continue;
                }

                foreach (string text in new[] { target.Server, target.Character })
                {
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    bool hit = lines.Any(l => OcrService.Similarity(
                        OcrService.Normalize(l.Text), OcrService.Normalize(text)) >= 0.68);

                    (hit ? found : missing).Add(text);
                }
            }

            TxtOcrResult.Text = "识别到：" + string.Join(" | ", lines.Take(18).Select(l => l.Text));

            string summary = $"识别到 {lines.Count} 行文字。";
            if (found.Count > 0)
            {
                summary += $" 能认出：{string.Join("、", found.Distinct())}。";
            }

            if (missing.Count > 0)
            {
                summary += $" 当前画面没看到：{string.Join("、", missing.Distinct())}（可能还不在这个界面）。";
            }

            SetStatus(summary);
        }
        catch (OperationCanceledException)
        {
            SetStatus("已取消。");
        }
        catch (Exception ex)
        {
            SetStatus($"识别失败：{ex.Message}");
        }
    }

    // ================= 保存 =================

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        CommitToSettings();
        LoginSettingsService.Save(_settings);
        SetStatus("已保存。");
        Close();
    }

    private void SetStatus(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetStatus(message));
            return;
        }

        TxtStatus.Text = message;
    }
}
