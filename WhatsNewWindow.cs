using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Nightforge.Models;
using Nightforge.Services;

namespace Nightforge;

/// <summary>
/// 更新完成后首次启动的提示窗：按「新增 / 优化 / 修复」列出这一版改了什么。
/// 纯代码构建，风格与 <see cref="UpdateWindow"/> 保持一致。
/// </summary>
internal sealed class WhatsNewWindow : Window
{
    public WhatsNewWindow(string version)
    {
        string ver = string.IsNullOrWhiteSpace(version) ? UpdateService.CurrentVersionText : version.Trim();

        Title = $"已更新到 v{ver} - 幻夜工坊";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 720;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(24) };

        panel.Children.Add(new TextBlock
        {
            Text = $"幻夜工坊 已更新到 {ver}",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });

        panel.Children.Add(new TextBlock
        {
            Text = "本次更新内容如下",
            Margin = new Thickness(0, 8, 0, 0),
            FontSize = 13,
            Opacity = 0.8
        });

        Border body = ChangeLogPresenter.Build(ChangeLog.Get(ver), 320);
        body.Margin = new Thickness(0, 16, 0, 0);
        panel.Children.Add(body);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
        };

        var go = new Button { Content = "前往 GitHub", Width = 130, Height = 34, IsDefault = true, Margin = new Thickness(0, 0, 10, 0) };
        var ok = new Button { Content = "知道了", Width = 96, Height = 34, IsCancel = true };
        go.Click += (_, _) => { UpdateWindow.OpenUrl(UpdateService.ReleasesUrl); Close(); };
        ok.Click += (_, _) => Close();
        buttons.Children.Add(go);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);

        Content = panel;
    }
}

/// <summary>
/// 更新条目分组渲染：按「新增 / 优化 / 修复」分组，每组一个小标题 + 「· 」条目列表。
/// UpdateWindow 与 WhatsNewWindow 共用同一套渲染，保证两处展示风格一致。
/// 内容高度随条目数量自适应，超过 maxHeight 时内部滚动。
/// </summary>
internal static class ChangeLogPresenter
{
    private static readonly Dictionary<string, Color> CategoryColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["新增"] = Color.FromRgb(0x1E, 0x8E, 0x3E),
        ["优化"] = Color.FromRgb(0x1A, 0x73, 0xE8),
        ["修复"] = Color.FromRgb(0xD9, 0x30, 0x25),
        ["更新"] = Color.FromRgb(0x5F, 0x63, 0x68)
    };

    private static readonly Color BodyColor = Color.FromRgb(0x20, 0x21, 0x24);
    private static readonly Color MutedColor = Color.FromRgb(0x5F, 0x63, 0x68);

    /// <summary>构建内容面板。maxHeight 为内部滚动阈值，超出后出现纵向滚动条。</summary>
    public static Border Build(IReadOnlyList<ChangeItem> items, double maxHeight)
    {
        var content = new StackPanel();
        IReadOnlyList<(string Category, List<ChangeItem> Items)> groups = ChangeLog.GroupedByCategory(items);

        if (groups.Count == 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = "（本版本暂无更新说明）",
                FontSize = 12.5,
                Foreground = new SolidColorBrush(MutedColor)
            });
        }
        else
        {
            bool first = true;
            foreach ((string category, List<ChangeItem> groupItems) in groups)
            {
                content.Children.Add(new TextBlock
                {
                    Text = category,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(CategoryColors.TryGetValue(category, out Color color) ? color : MutedColor),
                    Margin = new Thickness(0, first ? 0 : 12, 0, 6)
                });
                first = false;

                foreach (ChangeItem item in groupItems)
                {
                    content.Children.Add(new TextBlock
                    {
                        Text = "· " + item.Text,
                        FontSize = 12.5,
                        Foreground = new SolidColorBrush(BodyColor),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(2, 1, 0, 1)
                    });
                }
            }
        }

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = maxHeight,
            Content = content
        };

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xE1, 0xE6)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Child = scroll
        };
    }
}
