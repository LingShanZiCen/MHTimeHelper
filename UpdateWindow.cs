using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Nightforge.Services;

namespace Nightforge;

/// <summary>
/// 更新提示弹窗：联网查到新版本时弹出，一键跳到 GitHub 下载页。
/// 纯代码构建，与热键录制窗口风格保持一致。
/// </summary>
internal sealed class UpdateWindow : Window
{
    private readonly CheckBox _cbSkip = new() { Content = "本版本不再提示", Margin = new Thickness(0, 12, 0, 0) };

    public string? SkippedVersion { get; private set; }

    public UpdateWindow(UpdateInfo info, string currentVersion)
    {
        Title = "发现新版本 - 幻夜工坊";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 640;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;

        var panel = new StackPanel { Margin = new Thickness(22) };

        panel.Children.Add(new TextBlock
        {
            Text = "幻夜工坊 有新版本可以更新",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold
        });

        panel.Children.Add(new TextBlock
        {
            Text = $"当前版本 {currentVersion}　→　最新版本 {info.Version}",
            Margin = new Thickness(0, 8, 0, 0),
            FontSize = 13,
            Opacity = 0.8
        });

        panel.Children.Add(new TextBlock
        {
            Text = "更新内容",
            Margin = new Thickness(0, 18, 0, 8),
            FontWeight = FontWeights.SemiBold
        });

        // 按「新增 / 优化 / 修复」分组展示：每组一个小标题 + 「· 」条目；条目多时在框内滚动
        panel.Children.Add(ChangeLogPresenter.Build(info.Changes, 260));

        panel.Children.Add(new TextBlock
        {
            Text = $"下载地址：{UpdateService.ReleasesUrl}",
            Margin = new Thickness(0, 12, 0, 0),
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap
        });

        panel.Children.Add(_cbSkip);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };

        var go = new Button { Content = "前往 GitHub 更新", Width = 150, Height = 34, IsDefault = true, Margin = new Thickness(0, 0, 10, 0) };
        var later = new Button { Content = "稍后再说", Width = 96, Height = 34, IsCancel = true };
        go.Click += (_, _) => { RememberSkip(info); OpenUrl(info.Url); Close(); };
        later.Click += (_, _) => { RememberSkip(info); Close(); };
        buttons.Children.Add(go);
        buttons.Children.Add(later);
        panel.Children.Add(buttons);

        Content = panel;
    }

    /// <summary>勾了「不再提示」才记录版本号，下次同版本不再打扰。</summary>
    private void RememberSkip(UpdateInfo info)
    {
        if (_cbSkip.IsChecked == true)
        {
            SkippedVersion = info.Version;
        }
    }

    /// <summary>用默认浏览器打开链接。</summary>
    public static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            url = UpdateService.ReleasesUrl;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // 浏览器打不开就退回主页，仍失败则静默忽略，不影响程序运行
            try
            {
                Process.Start(new ProcessStartInfo(UpdateService.RepoUrl) { UseShellExecute = true });
            }
            catch
            {
                // ignored
            }
        }
    }
}
