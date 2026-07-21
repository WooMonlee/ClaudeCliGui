using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ClaudeGui;

/// <summary>
/// 文件浏览器面板 — 可折叠树形目录 + 右键菜单。
/// 支持文件夹展开/折叠状态持久化、滚动位置保存、文件/文件夹操作。
/// </summary>
public partial class FileBrowserPanel : UserControl
{
    // 每项目的浏览器状态
    private readonly Dictionary<string, HashSet<string>> _fbExpanded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _fbScrollPos = new(StringComparer.OrdinalIgnoreCase);
    private string? _fbLoadingProject;
    private System.Windows.Controls.Primitives.Popup? _fbContextMenu;

    /// <summary>用户双击/左键点击文件时触发（MainWindow 将其插入输入框）</summary>
    public event Action<string>? FileClicked;

    public FileBrowserPanel()
    {
        InitializeComponent();
    }

    /// <summary>刷新指定路径的文件列表</summary>
    public void Refresh(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath)) return;

        // 保存当前状态
        if (_fbLoadingProject != null)
            SaveState(_fbLoadingProject);

        _fbLoadingProject = projectPath;
        TitleText.Text = $"\U0001f4c2 {new DirectoryInfo(projectPath).Name}";
        FileBrowserPanelInner.Children.Clear();

        if (!_fbExpanded.ContainsKey(projectPath))
            _fbExpanded[projectPath] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var expanded = _fbExpanded[projectPath];

        try
        {
            var rootDir = new DirectoryInfo(projectPath);
            foreach (var subDir in rootDir.GetDirectories()
                .Where(d => !d.Name.StartsWith('.') && d.Name != "bin" && d.Name != "obj" && d.Name != "node_modules")
                .OrderBy(d => d.Name))
            {
                FileBrowserPanelInner.Children.Add(
                    CreateFolderItem(subDir, expanded, 0));
            }

            foreach (var file in rootDir.GetFiles()
                .Where(f => !f.Name.StartsWith('.') && !f.Name.EndsWith(".exe") && !f.Name.EndsWith(".dll"))
                .OrderBy(f => f.Name))
            {
                FileBrowserPanelInner.Children.Add(
                    CreateFileItem(file, 0));
            }
        }
        catch { }

        // 恢复滚动位置
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_fbScrollPos.TryGetValue(projectPath, out var pos))
                FileBrowserScroll.ScrollToVerticalOffset(pos);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    // ============ 树构建 ============

    private UIElement CreateFolderItem(DirectoryInfo dir, HashSet<string> expandedSet, int depth)
    {
        if (depth > 6) return new TextBlock { Height = 0 };

        var isExpanded = expandedSet.Contains(dir.FullName);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(depth * 14, 0, 0, 0) };

        var toggle = new ToggleButton
        {
            Content = isExpanded ? "▼" : "▶",
            IsChecked = isExpanded,
            Width = 16, Height = 16,
            FontSize = 9,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x92, 0xb0)),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var label = new TextBlock
        {
            Text = $"\U0001f4c1 {dir.Name}",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)),
            Margin = new Thickness(2, 2, 0, 2),
            Cursor = Cursors.Hand,
        };
        label.MouseDown += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed) { toggle.IsChecked = !toggle.IsChecked; e.Handled = true; }
        };

        var container = new StackPanel();

        // 右键菜单
        label.MouseRightButtonDown += (_, e) => e.Handled = true;
        label.MouseRightButtonUp += (_, e) => { ShowFolderMenu(dir, container); e.Handled = true; };
        toggle.MouseRightButtonDown += (_, e) => e.Handled = true;
        toggle.MouseRightButtonUp += (_, e) => { ShowFolderMenu(dir, container); e.Handled = true; };

        var childrenPanel = new StackPanel { Margin = new Thickness(0), Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed };

        object? loadedLock = null;
        toggle.Checked += (_, _) =>
        {
            childrenPanel.Visibility = Visibility.Visible;
            if (loadedLock == null)
            {
                loadedLock = new();
                try
                {
                    foreach (var sd in dir.GetDirectories()
                        .Where(d => !d.Name.StartsWith('.') && d.Name != "bin" && d.Name != "obj" && d.Name != "node_modules")
                        .OrderBy(d => d.Name))
                        childrenPanel.Children.Add(CreateFolderItem(sd, expandedSet, depth + 1));
                    foreach (var f in dir.GetFiles()
                        .Where(fi => !fi.Name.StartsWith('.') && !fi.Name.EndsWith(".exe") && !fi.Name.EndsWith(".dll"))
                        .OrderBy(fi => fi.Name))
                        childrenPanel.Children.Add(CreateFileItem(f, depth + 1));
                }
                catch { }
            }
            expandedSet.Add(dir.FullName);
        };
        toggle.Unchecked += (_, _) =>
        {
            childrenPanel.Visibility = Visibility.Collapsed;
            expandedSet.Remove(dir.FullName);
        };

        row.Children.Add(toggle);
        row.Children.Add(label);
        label.MouseDown += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed && Keyboard.Modifiers == ModifierKeys.Control)
            {
                try { Process.Start("explorer.exe", dir.FullName); } catch { }
                e.Handled = true;
            }
        };
        container.Children.Add(row);
        container.Children.Add(childrenPanel);
        return container;
    }

    private UIElement CreateFileItem(FileInfo file, int depth)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(depth * 14 + 18, 0, 0, 0), Cursor = Cursors.Hand };
        row.MouseLeftButtonDown += (_, e) =>
        {
            FileClicked?.Invoke(file.Name);
            e.Handled = true;
        };
        row.MouseRightButtonDown += (_, e) => e.Handled = true;
        row.MouseRightButtonUp += (_, e) => { ShowFileMenu(file, row); e.Handled = true; };
        row.Children.Add(new TextBlock
        {
            Text = $"{GetFileIcon(file.Extension)} {file.Name}",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xbb, 0xbb, 0xbb)),
            Margin = new Thickness(2, 1, 0, 1),
        });
        return row;
    }

    // ============ 右键菜单 ============

    private void ShowFileMenu(FileInfo file, UIElement target)
    {
        var panel = new StackPanel { };
        AddMenuItem(panel, "\U0001f4c2 打开", () => { try { Process.Start(new ProcessStartInfo(file.FullName) { UseShellExecute = true }); } catch { } });
        AddMenuItem(panel, "✏️ 编辑", () => { try { Process.Start("notepad.exe", file.FullName); } catch { } });
        AddMenuSep(panel);
        AddMenuItem(panel, "\U0001f4dd 重命名", () => RenameFileOrFolder(file.FullName));
        AddMenuItem(panel, "\U0001f5d1️ 删除", () => DeleteFileOrFolder(file.FullName));
        AddMenuSep(panel);
        AddMenuItem(panel, "\U0001f50d 属性", () => { try { Process.Start("explorer.exe", $"/select,\"{file.FullName}\""); } catch { } });
        ShowPopupMenu(panel, target);
    }

    private void ShowFolderMenu(DirectoryInfo dir, UIElement target)
    {
        var panel = new StackPanel { };
        AddMenuItem(panel, "\U0001f4c2 打开", () => { try { Process.Start("explorer.exe", dir.FullName); } catch { } });
        AddMenuSep(panel);
        AddMenuItem(panel, "\U0001f4dd 重命名", () => RenameFileOrFolder(dir.FullName));
        AddMenuItem(panel, "\U0001f5d1️ 删除", () => DeleteFileOrFolder(dir.FullName));
        AddMenuSep(panel);
        AddMenuItem(panel, "\U0001f50d 属性", () => { try { Process.Start("explorer.exe", $"/select,\"{dir.FullName}\""); } catch { } });
        ShowPopupMenu(panel, target);
    }

    private void AddMenuItem(StackPanel panel, string header, Action onClick)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = header, FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(0xe0, 0xe0, 0xe0)) },
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Padding = new Thickness(8, 5, 16, 5),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            MinWidth = 140,
        };
        b.MouseEnter += (_, _) => b.Background = new SolidColorBrush(Color.FromRgb(0x23, 0x35, 0x54));
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.Click += (_, _) => { if (_fbContextMenu != null) { _fbContextMenu.IsOpen = false; _fbContextMenu = null; } onClick(); };
        panel.Children.Add(b);
    }

    private static void AddMenuSep(StackPanel panel)
    {
        panel.Children.Add(new Separator
        {
            Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            Height = 1, Margin = new Thickness(4, 1, 4, 1)
        });
    }

    private void ShowPopupMenu(StackPanel panel, UIElement target)
    {
        if (_fbContextMenu != null) { _fbContextMenu.IsOpen = false; _fbContextMenu = null; }

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x2e)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x23, 0x35, 0x54)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4),
            Child = panel,
        };
        _fbContextMenu = new Popup
        {
            Child = border,
            Placement = PlacementMode.MousePoint,
            StaysOpen = true,
            IsOpen = true,
        };
        _fbContextMenu.Closed += (_, _) =>
        {
            var win = Window.GetWindow(this);
            if (win != null) win.PreviewMouseDown -= OnWindowPreviewMouseDown;
            _fbContextMenu = null;
        };
        var window = Window.GetWindow(this);
        if (window != null) window.PreviewMouseDown += OnWindowPreviewMouseDown;
    }

    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_fbContextMenu != null)
        {
            _fbContextMenu.IsOpen = false;
            _fbContextMenu = null;
        }
    }

    // ============ 文件/文件夹操作 ============

    private void RenameFileOrFolder(string fullPath)
    {
        var oldName = Path.GetFileName(fullPath);
        var parent = Path.GetDirectoryName(fullPath);
        if (parent == null) return;

        var win = new Window
        {
            Title = "重命名", Width = 380, Height = 140,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x21, 0x3e)),
        };
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x21, 0x3e)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x23, 0x35, 0x54)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(20),
        };
        var panel = new StackPanel { };
        var title = new TextBlock
        {
            Text = "重命名", Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0xff, 0xda)),
            FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 14),
        };
        var tb = new TextBox
        {
            Text = oldName,
            Background = new SolidColorBrush(Color.FromRgb(0x0c, 0x0c, 0x0c)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xe0, 0xe0, 0xe0)),
            CaretBrush = new SolidColorBrush(Color.FromRgb(0x64, 0xff, 0xda)),
            FontSize = 14, Padding = new Thickness(8, 6, 8, 6),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x23, 0x35, 0x54)),
            Margin = new Thickness(0, 0, 0, 14),
        };
        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancelBtn = new Button
        {
            Content = "取消", Width = 70, Height = 30,
            Background = new SolidColorBrush(Color.FromRgb(0x23, 0x35, 0x54)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x92, 0xb0)),
            FontSize = 12, BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 8, 0),
        };
        var okBtn = new Button
        {
            Content = "确定", Width = 70, Height = 30,
            Background = new SolidColorBrush(Color.FromRgb(0x64, 0xff, 0xda)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x2e)),
            FontWeight = FontWeights.Bold, FontSize = 12,
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
        };
        tb.KeyDown += (s, ke) => { if (ke.Key == Key.Enter) okBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
        okBtn.Click += (_, _) =>
        {
            var newName = tb.Text.Trim();
            if (!string.IsNullOrWhiteSpace(newName) && newName != oldName)
            {
                var dest = Path.Combine(parent, newName);
                try { if (Directory.Exists(fullPath)) Directory.Move(fullPath, dest); else File.Move(fullPath, dest); }
                catch { }
            }
            win.Close();
            if (_fbLoadingProject != null) Refresh(_fbLoadingProject);
        };
        cancelBtn.Click += (_, _) => win.Close();
        btnPanel.Children.Add(okBtn); btnPanel.Children.Add(cancelBtn);
        panel.Children.Add(btnPanel);
        border.Child = panel;
        panel.Children.Add(title); panel.Children.Add(tb);
        // 调整顺序: title → tb → btnPanel
        panel.Children.Remove(btnPanel);
        panel.Children.Add(btnPanel);
        win.Content = border;
        tb.Focus(); tb.SelectAll();
        win.ShowDialog();
    }

    private void DeleteFileOrFolder(string fullPath)
    {
        var name = Path.GetFileName(fullPath);
        if (MessageBox.Show($"确定要删除 [{name}] 吗？\n此操作将不可撤销。", "确认删除",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            try
            {
                if (File.Exists(fullPath)) File.Delete(fullPath);
                else if (Directory.Exists(fullPath)) Directory.Delete(fullPath, true);
                if (_fbLoadingProject != null) Refresh(_fbLoadingProject);
            }
            catch (Exception ex) { MessageBox.Show($"删除失败: {ex.Message}", "错误"); }
        }
    }

    // ============ 状态持久化 ============

    private void SaveState(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return;
        _fbScrollPos[projectPath] = FileBrowserScroll.VerticalOffset;
    }

    // ============ 工具 ============

    private static string GetFileIcon(string ext) => ext.ToLowerInvariant() switch
    {
        ".cs" or ".xaml" or ".py" or ".js" or ".ts" or ".html" or ".css" => "\U0001f4c4",
        ".md" or ".txt" or ".log" => "\U0001f4dd",
        ".json" or ".xml" or ".yaml" or ".yml" or ".config" => "⚙️",
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" or ".ico" => "\U0001f5bc️",
        ".zip" or ".7z" or ".rar" => "\U0001f4e6",
        _ => "\U0001f4c4",
    };
}
