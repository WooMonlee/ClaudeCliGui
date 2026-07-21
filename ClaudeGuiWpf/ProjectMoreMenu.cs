using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ClaudeGui;

/// <summary>
/// 项目卡片 ⋮ 更多菜单（Popup 下拉）。
/// 从 MainWindow 提取的静态辅助。
/// </summary>
public static class ProjectMoreMenu
{
    /// <summary>弹出项目更多操作菜单</summary>
    public static void Show(Button btn, ProjectItem item, Action onRename, Action onDelete)
    {
        // Popup 自定义下拉菜单（避开 MenuItem 默认图标列白色底问题）
        Popup? popup = null;
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x2e)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x23, 0x35, 0x54)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4),
        };
        var panel = new StackPanel { };

        AddItem(panel, popup, "📂", "打开项目文件夹", () =>
        {
            try { Process.Start("explorer.exe", item.Original?.Path ?? item.Path); }
            catch { }
        });
        panel.Children.Add(new Separator
        {
            Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            Height = 1, Margin = new Thickness(4, 2, 4, 2)
        });
        AddItem(panel, popup, "✏️", "重命名", onRename);
        panel.Children.Add(new Separator
        {
            Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            Height = 1, Margin = new Thickness(4, 2, 4, 2)
        });
        AddItem(panel, popup, "🗑️", "删除项目", onDelete);

        border.Child = panel;
        popup = new Popup
        {
            Child = border,
            Placement = PlacementMode.Bottom,
            PlacementTarget = btn,
        };
        popup.Closed += (_, _) => btn.Background = Brushes.Transparent;
        popup.StaysOpen = false;
        popup.IsOpen = true;
    }

    private static void AddItem(StackPanel panel, Popup? popup, string emoji, string text, Action onClick)
    {
        var b = new Button
        {
            Content = new TextBlock
            {
                Text = $"{emoji}  {text}",
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(0xe0, 0xe0, 0xe0)),
            },
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Padding = new Thickness(8, 6, 16, 6),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            MinWidth = 160,
        };
        b.MouseEnter += (_, _) => b.Background = new SolidColorBrush(Color.FromRgb(0x23, 0x35, 0x54));
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.Click += (_, _) => { if (popup != null) popup.IsOpen = false; onClick(); };
        panel.Children.Add(b);
    }
}
