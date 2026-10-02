using System.Windows;
using System.Windows.Controls;

namespace Nightforge;

/// <summary>
/// 新版本已下载完成时的提示窗：可以立即重启换成新版，也可以稍后（下次启动自动生效）。
/// 纯代码构建，与更新提示窗风格保持一致。
/// </summary>
internal sealed class UpdateReadyWindow : Window
{
    /// <summary>用户选择了「立即重启更新」。</summary>
    public bool RestartNow { get; private set; }

    public UpdateReadyWindow(string version, string currentVersion)
    {
        Title = "更新已就绪 - 幻夜工坊";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 560;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;

        var panel = new StackPanel { Margin = new Thickness(22) };

        panel.Children.Add(new TextBlock
        {
            Text = "新版本已下载完成",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold
        });

        panel.Children.Add(new TextBlock
        {
            Text = $"当前版本 {currentVersion}　→　新版本 {version}",
            Margin = new Thickness(0, 8, 0, 0),
            FontSize = 13,
            Opacity = 0.8
        });

        panel.Children.Add(new TextBlock
        {
            Text = "点「立即重启更新」马上换成新版；点「下次启动更新」则在你下次打开软件时自动完成，中间不影响当前使用。",
            Margin = new Thickness(0, 14, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };

        var now = new Button { Content = "立即重启更新", Width = 132, Height = 34, IsDefault = true, Margin = new Thickness(0, 0, 10, 0) };
        var later = new Button { Content = "下次启动更新", Width = 132, Height = 34, IsCancel = true };
        now.Click += (_, _) => { RestartNow = true; Close(); };
        later.Click += (_, _) => Close();
        buttons.Children.Add(now);
        buttons.Children.Add(later);
        panel.Children.Add(buttons);

        Content = panel;
    }
}
