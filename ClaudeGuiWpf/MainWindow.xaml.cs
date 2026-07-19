using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace ClaudeGui;

public partial class MainWindow : Window
{
    private ConfigService _config = null!;
    private ProjectEntry? _currentProject;
    private readonly Dictionary<string, TerminalControl> _terminals = new(StringComparer.OrdinalIgnoreCase);
    private TerminalControl? _activeTerminal;
    private const string CancelExitEventName = "ClaudeCliGui_CancelExit";
    private EventWaitHandle? _cancelExitEvent;

    public MainWindow()
    {
        Logger.Info("MainWindow 构造开始");
        try
        {
            InitializeComponent();
            Logger.Info("InitializeComponent 完成");
            _config = new ConfigService();
            Logger.Info($"ConfigService 初始化完成，项目数: {_config.GetProjects().Count}");
            Loaded += OnLoaded;
        }
        catch (Exception ex) { Logger.Error("MainWindow 构造失败", ex); throw; }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Logger.Info("MainWindow Loaded");

        // 创建跨进程退出取消信号（新实例恢复隐藏窗口时触发）
        try { _cancelExitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, CancelExitEventName); }
        catch { }

        SetupPage.SetupCompleted += OnSetupCompleted;
        SetupPage.RequestShow += () =>
        {
            SetupPage.Visibility = Visibility.Visible;
            if (_activeTerminal != null)
                _activeTerminal.Visibility = Visibility.Collapsed;
        };
        UpdateMenuStatusText();
        RefreshProjectList();

        // 先处理 --add-project 参数（来自右键菜单），必须在 return 之前
        var args = Environment.GetCommandLineArgs();
        ProjectEntry? addedProject = null;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--add-project" && i + 1 < args.Length && Directory.Exists(args[i + 1]))
            {
                var existing = _config.GetProjects().FirstOrDefault(p => NormalizePath(p.Path) == NormalizePath(args[i + 1]));
                if (existing == null) try { existing = _config.AddExistingProject(args[i + 1]); } catch { }
                addedProject = existing;
                break;
            }
        }
        if (addedProject != null) { RefreshProjectList(); }

        // 恢复上次关闭时的项目（--add-project 优先）
        var lastProjectName = _config.GetConfigValue("lastActiveProject", "");
        if (addedProject != null)
        {
            // 右键菜单启动：切换到添加的项目
            Logger.Info($"右键添加并切换项目: {addedProject.Name}");
            SelectProject(addedProject);
        }
        else if (!string.IsNullOrWhiteSpace(lastProjectName))
        {
            var lastProj = _config.GetProject(lastProjectName);
            if (lastProj != null && Directory.Exists(lastProj.Path))
            {
                Logger.Info($"恢复上次项目: {lastProj.Name}");
                SelectProject(lastProj);
                _ = CheckForUpdateAsync();
                RunStartupPreload();
                StartAddProjectWatcher();
                return;
            }
        }

        _ = CheckForUpdateAsync();

        // 按策略预载入项目
        RunStartupPreload();

        // 启动 IPC 轮询（后续右键菜单窗口激活后添加项目）
        StartAddProjectWatcher();
    }

    // ============ 项目列表 ============

    private void RefreshProjectList()
    {
        var projects = _config.GetProjects()
            .Select(p => new ProjectItem
            {
                Name = p.Name, Path = p.Path, LastAccessedAt = p.LastAccessedAt,
                IsActive = _currentProject?.Name == p.Name, Original = p
            }).ToList();
        ProjectListBox.ItemsSource = projects;
    }

    // ============ 切换右边：设置向导 / 终端 ============

    private void OnSetupCompleted()
    {
        SetupPage.Visibility = Visibility.Collapsed;
        if (_activeTerminal != null)
            _activeTerminal.Visibility = Visibility.Visible;
    }

    private TerminalControl GetOrCreateTerminal(string projectPath)
    {
        if (_terminals.TryGetValue(projectPath, out var tc) && tc != null)
            return tc;

        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? Directory.GetCurrentDirectory();
        var tcNew = new TerminalControl { Config = _config };
        var nodeDir = FindPortableNodeDir(exeDir);
        if (nodeDir != null)
        {
            var portableClaude = Path.Combine(nodeDir, "claude.cmd");
            if (File.Exists(portableClaude)) tcNew.ClaudePath = portableClaude;
        }
        tcNew.RefreshSkillsProviders();
        _terminals[projectPath] = tcNew;
        return tcNew;
    }

    private void ShowTerminal(TerminalControl tc)
    {
        if (_activeTerminal == tc) return;
        // 隐藏当前
        if (_activeTerminal != null)
            _activeTerminal.Visibility = Visibility.Collapsed;
        // 显示新的
        tc.Visibility = Visibility.Visible;
        if (!TerminalHost.Children.Contains(tc))
            TerminalHost.Children.Add(tc);
        _activeTerminal = tc;
    }

    private void SelectProject(ProjectEntry project)
    {
        Logger.Info($"选择项目: {project.Name} ({project.Path})");
        _currentProject = project;
        _config.UpdateAccessTime(project.Name);
        _config.SetConfigValue("lastActiveProject", project.Name); // 保存最后打开的项目
        RefreshProjectList();

        var tc = GetOrCreateTerminal(project.Path);

        // 如果是新建的终端，加载历史快照
        if (_activeTerminal != tc)
            LoadSnapshot(tc, project.Path);

        ShowTerminal(tc);
        tc.PermissionMode = project.PermissionMode; // 恢复该项目的权限模式（默认 bypassPermissions）
        tc.Activate(project.Path);

        // 预载入 claude（仅新建时触发）
        PreloadIfNeeded(tc, project.Path);

        // 刷新文件浏览器
        RefreshFileBrowser(project.Path);
    }

    private void PreloadIfNeeded(TerminalControl tc, string projectPath)
    {
        if (!tc.IsProcessAlive)
            tc.PreloadSession(projectPath, tc.ClaudePath);
    }

    /// <summary>启动时按配置策略分批预载入所有项目</summary>
    private async void RunStartupPreload()
    {
        var list = _config.GetPreloadList();
        if (list.Count == 0) return;

        Logger.Info($"启动预载入: 策略={_config.GetConfigValue("preloadStrategy")} 数量={list.Count}");

        // 逐个预载入，每个间隔 3 秒，避免同时启动太多进程
        for (int i = 0; i < list.Count; i++)
        {
            var project = list[i];
            // 如果用户已经点击了某个项目，跳过它（已经被 SelectProject 预载过）
            var tc = GetOrCreateTerminal(project.Path);
            if (!tc.IsProcessAlive)
            {
                Logger.Info($"  预载入 [{i + 1}/{list.Count}]: {project.Name}");
                tc.PreloadSession(project.Path, tc.ClaudePath);
            }

            // 间距，同时给用户留出点击第一个项目的时间
            if (i < list.Count - 1)
                await Task.Delay(3000);
        }

        Logger.Info("启动预载入完成");
    }

    // ============ 拖拽排序 ============

    private Point _dragStartPoint;
    private Border? _dragSource;
    private bool _isDragging; // 防 DoDragDrop 重入

    private void ProjectCard_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border)
        {
            // 如果点的是按钮（重命名/删除），不捕获鼠标，让 Button 正常收到 Click
            if (e.OriginalSource is DependencyObject src)
            {
                var p = src;
                while (p != null)
                {
                    if (p is Button) return;
                    // Run/ContentElement 不在可视树中，走逻辑树
                    p = (p is System.Windows.Media.Visual)
                        ? VisualTreeHelper.GetParent(p) as DependencyObject
                        : (p as FrameworkContentElement)?.Parent as DependencyObject;
                }
            }
            _dragStartPoint = e.GetPosition(null);
            _dragSource = border;
            border.CaptureMouse();
        }
    }

    private void ProjectCard_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragSource == null || _isDragging || e.LeftButton != MouseButtonState.Pressed) return;
        var diff = _dragStartPoint - e.GetPosition(null);
        if (Math.Abs(diff.X) < 5 && Math.Abs(diff.Y) < 5) return;

        if (_dragSource.Tag is ProjectItem item)
        {
            _isDragging = true;
            _dragSource.Opacity = 0.4;
            var source = _dragSource; // 本地快照，防止 DoDragDrop 模态期间被置 null
            source.ReleaseMouseCapture();
            DragDrop.DoDragDrop(source, item, DragDropEffects.Move);
            source.Opacity = 1.0;
            _isDragging = false;
        }
        _dragSource = null;
    }

    private void ProjectCard_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // 只有当 _dragSource 实际捕获过鼠标时才判定为卡片点击（防止按钮单击误触发 SelectProject）
        if (_dragSource != null && _dragSource.Tag is ProjectItem item && item.Original != null)
        {
            if (sender is Border && sender == (object?)_dragSource)
            {
                var diff = _dragStartPoint - e.GetPosition(null);
                if (Math.Abs(diff.X) < 5 && Math.Abs(diff.Y) < 5)
                    SelectProject(item.Original);
            }
        }
        _dragSource?.ReleaseMouseCapture();
        _dragSource = null;
    }

    private void ProjectCard_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(ProjectItem)) is ProjectItem dragged
            && sender is Border targetBorder
            && targetBorder.Tag is ProjectItem target
            && dragged.Original != null && target.Original != null)
        {
            _config.SwapSortOrder(dragged.Original.Name, target.Original.Name);
            RefreshProjectList();
        }
    }

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new NewProjectDialog(_config) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result != null) { RefreshProjectList(); SelectProject(dlg.Result); }
    }

    private void AddExisting_Click(object sender, RoutedEventArgs e)
    {
        // 修复 3：从配置读取上次打开的目录（保存的是被选项目本身的目录，下次打开其父目录即可）
        var lastDir = _config.GetConfigValue("lastOpenDir", "");
        var parentDir = Directory.Exists(lastDir) ? Path.GetDirectoryName(lastDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? "" : "";
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择项目文件夹",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(parentDir) ? parentDir : Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            // 保存本次实际选择的项目路径
            _config.SetConfigValue("lastOpenDir", dlg.SelectedPath);
            try
            {
                var proj = _config.AddExistingProject(dlg.SelectedPath);
                var claudeDir = Path.Combine(proj.Path, ".claude");
                if (!Directory.Exists(claudeDir)) try { Directory.CreateDirectory(claudeDir); } catch { }
                RefreshProjectList();
                SelectProject(proj);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
    }

    private void RenameProject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ProjectItem item)
        {
            // 自定义重命名窗口，替代原始 InputBox
            var win = new System.Windows.Window
            {
                Title = "重命名项目",
                Width = 380, Height = 160,
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x16, 0x21, 0x3e)),
                ResizeMode = System.Windows.ResizeMode.NoResize,
                WindowStyle = System.Windows.WindowStyle.None,
                AllowsTransparency = true,
                ShowInTaskbar = false
            };
            var outerBorder = new System.Windows.Controls.Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x16, 0x21, 0x3e)),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x23, 0x35, 0x54)),
                BorderThickness = new System.Windows.Thickness(1),
                CornerRadius = new System.Windows.CornerRadius(8),
                Padding = new System.Windows.Thickness(20)
            };
            var panel = new System.Windows.Controls.StackPanel { };
            var title = new System.Windows.Controls.TextBlock
            {
                Text = "重命名项目",
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x64, 0xff, 0xda)),
                FontSize = 16, FontWeight = System.Windows.FontWeights.Bold,
                Margin = new System.Windows.Thickness(0, 0, 0, 14)
            };
            var txt = new System.Windows.Controls.TextBox
            {
                Text = item.Name,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0c, 0x0c, 0x0c)),
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xe0, 0xe0, 0xe0)),
                CaretBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x64, 0xff, 0xda)),
                FontSize = 14,
                Padding = new System.Windows.Thickness(8, 6, 8, 6),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x23, 0x35, 0x54)),
                Margin = new System.Windows.Thickness(0, 0, 0, 14)
            };
            var btnRow = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };
            var btnCancel = new System.Windows.Controls.Button
            {
                Content = "取消", Width = 70, Height = 30,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x23, 0x35, 0x54)),
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x92, 0xb0)),
                FontSize = 12, BorderThickness = new System.Windows.Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new System.Windows.Thickness(0, 0, 8, 0)
            };
            var btnOk = new System.Windows.Controls.Button
            {
                Content = "确定", Width = 70, Height = 30,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x64, 0xff, 0xda)),
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1a, 0x1a, 0x2e)),
                FontWeight = System.Windows.FontWeights.Bold, FontSize = 12,
                BorderThickness = new System.Windows.Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            txt.KeyDown += (s, ke) => { if (ke.Key == System.Windows.Input.Key.Enter) btnOk.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); };
            btnCancel.Click += (s, ce) => win.Close();
            btnOk.Click += (s, ce) =>
            {
                var newName = txt.Text.Trim();
                if (string.IsNullOrWhiteSpace(newName)) { txt.Focus(); return; }
                try { var u = _config.RenameProject(item.Name, newName); if (u != null) { if (_currentProject?.Name == item.Name) _currentProject = u; RefreshProjectList(); } win.Close(); }
                catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "错误", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error); }
            };
            btnRow.Children.Add(btnCancel); btnRow.Children.Add(btnOk);
            panel.Children.Add(title); panel.Children.Add(txt); panel.Children.Add(btnRow);
            outerBorder.Child = panel;
            win.Content = outerBorder;
            txt.Focus(); txt.SelectAll();
            win.ShowDialog();
        }
    }

    private void MoreProject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ProjectItem item) return;

	        // Popup 自定义下拉菜单（避开 MenuItem 默认图标列白色底问题）
        System.Windows.Controls.Primitives.Popup? popup = null;
        var border = new System.Windows.Controls.Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x2e)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x23, 0x35, 0x54)),
            BorderThickness = new Thickness(1),
            CornerRadius = new System.Windows.CornerRadius(6),
            Padding = new Thickness(4),
        };
        var panel = new System.Windows.Controls.StackPanel { };

        void AddItem(string emoji, string text, Action onClick)
        {
            var b = new System.Windows.Controls.Button
            {
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = $"{emoji}  {text}",
                    FontSize = 13,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xe0, 0xe0, 0xe0)),
                },
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Padding = new Thickness(8, 6, 16, 6),
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
                MinWidth = 160,
            };
            b.MouseEnter += (_, _) => b.Background = new SolidColorBrush(Color.FromRgb(0x23, 0x35, 0x54));
            b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
            b.Click += (_, _) => { popup!.IsOpen = false; onClick(); };
            panel.Children.Add(b);
        }

        AddItem("📂", "打开项目文件夹", () =>
        {
            try { System.Diagnostics.Process.Start("explorer.exe", item.Original?.Path ?? item.Path); }
            catch { }
        });
        panel.Children.Add(new System.Windows.Controls.Separator
        {
            Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            Height = 1, Margin = new Thickness(4, 2, 4, 2)
        });
        AddItem("✏️", "重命名", () =>
        {
            var fakeBtn = new System.Windows.Controls.Button { Tag = item };
            RenameProject_Click(fakeBtn, new RoutedEventArgs());
        });
        panel.Children.Add(new System.Windows.Controls.Separator
        {
            Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            Height = 1, Margin = new Thickness(4, 2, 4, 2)
        });
        AddItem("🗑️", "删除项目", () =>
        {
            var fakeBtn = new System.Windows.Controls.Button { Tag = item };
            DeleteProject_Click(fakeBtn, new RoutedEventArgs());
        });

        border.Child = panel;
        popup = new System.Windows.Controls.Primitives.Popup
        {
            Child = border,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            PlacementTarget = btn,
        };
        popup.Closed += (_, _) => btn.Background = Brushes.Transparent;
        popup.StaysOpen = false;
        popup.IsOpen = true;
    }

    private void DeleteProject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ProjectItem item)
        {
            if (MessageBox.Show($"确定要删除项目 [{item.Name}] 吗？\n（不会删除实际文件）", "确认删除",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                _config.DeleteProject(item.Name);
                if (_currentProject?.Name == item.Name)
                {
                    _currentProject = null;
                    if (_terminals.TryGetValue(item.Original?.Path ?? "", out var t))
                    {
                        t.Dispose();
                        TerminalHost.Children.Remove(t);
                        _terminals.Remove(item.Original?.Path ?? "");
                        if (_activeTerminal == t) _activeTerminal = null;
                    }
                }
                RefreshProjectList();
            }
        }
    }

    private void SystemSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SystemSettingsWindow(_config) { Owner = this };
        dlg.ShowDialog();
        // 刷新当前活跃终端的提供商下拉
        if (_activeTerminal != null)
            _activeTerminal.RefreshSkillsProviders();
    }

    private async void WipeAll_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "此功能将清除本软件所有痕迹，包括：\n\n" +
            "  · 删除所有项目记录（claudeCliGui.json）\n" +
            "  · 清除 API Key 和模型配置（环境变量）\n" +
            "  · 删除全局 settings.json 和 CLAUDE.md\n" +
            "  · 删除 Node.js 便携环境（nodejs 目录）\n" +
            "  · 删除 Claude CLI（npm 全局包）\n" +
            "  · 清除右键菜单注册表项\n\n" +
            "⚠ 此操作不可撤销，仅用于卸载或在他人电脑上退出时使用。\n\n" +
            "确定继续？",
            "⚠ 清零确认 - 慎用！", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            // 1. 清除所有 ANTHROPIC / CLAUDE_CODE / API_TIMEOUT 环境变量
            var targets = new[]
            {
                "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL",
                "ANTHROPIC_MODEL", "ANTHROPIC_SMALL_FAST_MODEL",
                "ANTHROPIC_DEFAULT_OPUS_MODEL", "ANTHROPIC_DEFAULT_SONNET_MODEL",
                "ANTHROPIC_DEFAULT_HAIKU_MODEL", "ANTHROPIC_DEFAULT_FABLE_MODEL",
                "CLAUDE_CODE_SUBAGENT_MODEL", "CLAUDE_CODE_MAX_OUTPUT_TOKENS",
                "API_TIMEOUT_MS", "ANTHROPIC_DISABLE_TELEMETRY",
            };
            foreach (var t in targets)
            {
                Environment.SetEnvironmentVariable(t, null, EnvironmentVariableTarget.User);
                Environment.SetEnvironmentVariable(t, null, EnvironmentVariableTarget.Process);
            }

            // 2. 删除 settings.json（Claude 全局配置）
            var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
            if (File.Exists(settingsPath)) try { File.Delete(settingsPath); } catch { }

            // 3. 删除 claudeCliGui.json
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? Directory.GetCurrentDirectory();
            var configPath = Path.Combine(exeDir, "claudeCliGui.json");
            if (File.Exists(configPath)) try { File.Delete(configPath); } catch { }

            // 3. 删除 nodejs 便携目录
            var nodejsDir = Path.Combine(exeDir, "nodejs");
            if (Directory.Exists(nodejsDir)) try { Directory.Delete(nodejsDir, true); } catch { }

            // 4. 删除 node-*.zip 下载残留
            foreach (var f in Directory.GetFiles(exeDir, "node-*.zip"))
                try { File.Delete(f); } catch { }

            // 5. 删除全局 CLAUDE.md
            var claudeMdPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "CLAUDE.md");
            if (File.Exists(claudeMdPath)) try { File.Delete(claudeMdPath); } catch { }

            // 6. 清除右键菜单注册表
            try { Microsoft.Win32.Registry.ClassesRoot.DeleteSubKeyTree(@"Directory\shell\ClaudeGui", false); } catch { }
            try { Microsoft.Win32.Registry.ClassesRoot.DeleteSubKeyTree(@"Directory\Background\shell\ClaudeGui", false); } catch { }

            Logger.Info("清零完成，即将退出");

            MessageBox.Show("已清除所有配置。程序即将退出。", "清零完成", MessageBoxButton.OK, MessageBoxImage.Information);

            // 退出程序
            DisposeAllTerminals();
            await Task.Delay(300);
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Logger.Error("清零失败", ex);
            MessageBox.Show($"清零失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool _exitingGracefully;
    private System.Windows.Threading.DispatcherTimer? _exitWatchTimer;

    protected override void OnClosing(CancelEventArgs e)
    {
        // 有后台进程在跑 → 隐藏窗口，等跑完再退出
        if (!_exitingGracefully && _terminals.Values.Any(t => t.IsProcessAlive))
        {
            e.Cancel = true;
            Hide();
            Logger.Info("窗口关闭，后台进程继续运行，等待完成...");

            _exitWatchTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            var deadline = DateTime.UtcNow.AddMinutes(10);
            _exitWatchTimer.Tick += (_, _) =>
            {
                // 新实例恢复了窗口 → 取消退出，恢复正常
                if (_cancelExitEvent != null && _cancelExitEvent.WaitOne(0))
                {
                    _cancelExitEvent.Reset();
                    _exitWatchTimer?.Stop();
                    _exitWatchTimer = null;
                    Show();
                    WindowState = WindowState.Normal;
                    Activate();
                    Logger.Info("新实例触发恢复，取消后台退出");

                    // 立即处理 IPC 文件（新实例可能写了 --add-project 路径）
                    ProcessAddProjectFile();
                    return;
                }

                var anyAlive = _terminals.Values.Any(t => t.IsProcessAlive);
                if (!anyAlive || DateTime.UtcNow > deadline)
                {
                    _exitWatchTimer?.Stop();
                    Logger.Info(anyAlive ? "后台超时，强制退出" : "后台任务完成，退出");
                    _exitingGracefully = true;
                    DisposeAllTerminals();
                    try { _config.Save(); } catch { }
                    try { _cancelExitEvent?.Dispose(); } catch { }
                    _ipcWatcher?.Dispose();
                    Environment.Exit(0);
                }
            };
            _exitWatchTimer.Start();
            return;
        }

        DisposeAllTerminals();
        // 修复 R2：正常退出时释放 EventWaitHandle
        try { _cancelExitEvent?.Dispose(); } catch { }
        _cancelExitEvent = null;
        _ipcWatcher?.Dispose();
        try { _config.Save(); } catch (Exception ex) { Logger.Error("Config.Save 失败", ex); }
        base.OnClosing(e);
    }

    private void DisposeAllTerminals()
    {
        foreach (var (_, tc) in _terminals)
        {
            try { TerminalHost.Children.Remove(tc); tc.Dispose(); } catch { }
        }
        _terminals.Clear();
        _activeTerminal = null;
    }

    // ============ 历史快照 ============

    private static void LoadSnapshot(TerminalControl tc, string workDir)
    {
        var all = LoadClaudeJsonlHistory(workDir, 1000);
        if (all.Count == 0) all = LoadClaudegSnapshot(workDir);
        if (all.Count == 0) return;

        var messages = all.Select(m => (m.Role, m.Content)).ToList();
        tc.LoadFullSnapshot(messages);
        tc.ScrollToEnd();
        Logger.Info($"加载历史: {all.Count} 条");
    }

    private static List<SnapshotMsg> LoadClaudegSnapshot(string workDir)
    {
        var result = new List<SnapshotMsg>();
        var p = Path.Combine(workDir, ".claude", "claudeg-snapshot.json");
        if (!File.Exists(p)) return result;
        try
        {
            var msgs = JsonSerializer.Deserialize<List<ChatSnapshotEntry>>(File.ReadAllText(p));
            if (msgs != null) result.AddRange(msgs.Select(m => new SnapshotMsg { Role = m.Role, Content = m.Content }));
        }
        catch { }
        return result;
    }

    private static List<SnapshotMsg> LoadClaudeJsonlHistory(string workDir, int maxCount = 500)
    {
        var result = new List<SnapshotMsg>();
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        if (!Directory.Exists(root)) return result;
        var norm = workDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var encoded = EncodeProjectPath(norm);
        var pDir = Path.Combine(root, encoded);
        if (!Directory.Exists(pDir))
            pDir = Directory.GetDirectories(root).FirstOrDefault(d =>
                string.Equals(Path.GetFileName(d), encoded, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(d), EncodeProjectPath(norm + Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
        if (pDir == null) return result;

        // 修复 L9：用内容字符串做去重键，避免 GetHashCode 碰撞丢消息
        var seen = new HashSet<string>();
        foreach (var f in Directory.GetFiles(pDir, "*.jsonl").OrderBy(f => new FileInfo(f).LastWriteTimeUtc))
        {
            try
            {
                foreach (var line in File.ReadLines(f))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var msg = ParseJsonlLine(line);
                    if (msg != null && seen.Add(msg.Content)) result.Add(msg);
                }
            }
            catch { }
        }
        if (result.Count > maxCount) result = result.Skip(result.Count - maxCount).ToList();
        return result;
    }

    private static SnapshotMsg? ParseJsonlLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "user" && root.TryGetProperty("message", out var um) && um.TryGetProperty("content", out var uc))
            {
                var c = uc.GetString();
                if (!string.IsNullOrWhiteSpace(c)) return new SnapshotMsg { Role = "user", Content = c };
            }
            else if (type == "assistant" && root.TryGetProperty("message", out var am))
            {
                var ct = am.TryGetProperty("content", out var ac) ? ac : default;
                var text = ExtractJsonlText(ct);
                if (!string.IsNullOrWhiteSpace(text)) return new SnapshotMsg { Role = "assistant", Content = text };
            }
        }
        catch { }
        return null;
    }

    private static string ExtractJsonlText(JsonElement c)
    {
        if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
        if (c.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var b in c.EnumerateArray())
            {
                var bt = b.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (bt == "text" && b.TryGetProperty("text", out var txt)) sb.AppendLine(txt.GetString());
            }
            return sb.ToString().TrimEnd();
        }
        return "";
    }

    private static string EncodeProjectPath(string path)
    {
        var sb = new StringBuilder(path.Length);
        foreach (char c in path) sb.Append((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' ? c : '-');
        return sb.ToString();
    }

    private class SnapshotMsg { public string Role { get; set; } = ""; public string Content { get; set; } = ""; }

    // ============ 工具 ============

    public static string? FindPortableNodeDirStatic(string exeDir) => FindPortableNodeDir(exeDir);
    private static string? FindPortableNodeDir(string exeDir)
    {
        var d = Path.Combine(exeDir, "nodejs");
        if (!Directory.Exists(d)) return null;
        try
        {
            foreach (var s in Directory.GetDirectories(d, "node-v*-win-x64"))
                if (File.Exists(Path.Combine(s, "node.exe"))) return s;
            if (File.Exists(Path.Combine(d, "node.exe"))) return d;
        }
        catch { }
        return null;
    }

    // ============ 自动更新 ============

    private bool _selfUpdateReady;   // 自己已下载 .new.exe
    private string? _selfUpdateTag;   // 新版本号
    private string? _selfUpdatePath;  // 下载路径
    private bool _cliUpdateReady;    // Claude CLI 已更新完成
    private string? _cliNewVersion;

    private async Task CheckForUpdateAsync()
    {
        // 双路并行检测
        await Task.WhenAll(CheckSelfUpdateAsync(), CheckCliUpdateAsync());
        Dispatcher.Invoke(UpdateDotState);
    }

    /// <summary>自己：查 GitHub release → 下载 claudeCliGui.new.exe</summary>
    private async Task CheckSelfUpdateAsync()
    {
        try
        {
            using var hc = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            hc.DefaultRequestHeaders.Add("User-Agent", "ClaudeGui");
            var json = await hc.GetStringAsync("https://api.github.com/repos/WooMonlee/ClaudeCliGui/releases?per_page=3");
            using var doc = JsonDocument.Parse(json);
            // 取最新发布（含预发布），排除 draft
            var latest = doc.RootElement.EnumerateArray()
                .FirstOrDefault(r => !r.TryGetProperty("draft", out var d) || !d.GetBoolean());
            if (latest.ValueKind != JsonValueKind.Object) return;
            var tag = latest.GetProperty("tag_name").GetString();
            var assets = latest.GetProperty("assets");
            string? downloadUrl = null;
            foreach (var a in assets.EnumerateArray())
                if (a.GetProperty("name").GetString() == "claudeCliGui.exe")
                { downloadUrl = a.GetProperty("browser_download_url").GetString(); break; }

            if (tag == null || downloadUrl == null) return;

            // 版本比较：提取数字部分（兼容 v1.0.0-beta → 1.0.0）
            var currentVer = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            var currentStr = currentVer != null ? $"{currentVer.Major}.{currentVer.Minor}.{currentVer.Build}" : "0.0";
            var tagNum = ExtractSemver(tag.StartsWith("v") ? tag[1..] : tag);
            var curNum = ExtractSemver(currentStr);
            if (tagNum == curNum) return;

            var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? Directory.GetCurrentDirectory();
            var newPath = Path.Combine(exeDir, "claudeCliGui.new.exe");
            if (File.Exists(newPath)) { _selfUpdateReady = true; _selfUpdateTag = tag; _selfUpdatePath = exeDir; return; } // 已下载

            Logger.Info($"发现新版本: {tag} (当前 {currentStr})，正在下载...");
            var data = await hc.GetByteArrayAsync(downloadUrl);
            await File.WriteAllBytesAsync(newPath, data);
            _selfUpdateReady = true;
            _selfUpdateTag = tag;
            _selfUpdatePath = exeDir;
            Logger.Info($"新版本已下载: {newPath}，下次启动时更新");
            Dispatcher.Invoke(() => _activeTerminal?.ShowSystemMessage(
                $"📦 已下载 ClaudeCliGui {tag} 到 {exeDir}，将在下次启动时启用。（{DateTime.Now:yyyy年M月d日}）"));
        }
        catch (Exception ex) { Logger.Info($"自身更新检测跳过: {ex.Message}"); }
    }

    /// <summary>Claude CLI：npm registry 查最新版 → 对比本地 → npm update</summary>
    private async Task CheckCliUpdateAsync()
    {
        try
        {
            // 查本地版本
            var localVer = (await RunCliVersionAsync("claude", "--version")).Trim();
            if (string.IsNullOrWhiteSpace(localVer)) return;

            // 查 npm 最新版
            var npmVer = (await RunCliVersionAsync("npm", "view @anthropic-ai/claude-code version")).Trim();
            if (string.IsNullOrWhiteSpace(npmVer)) return;

            if (localVer == npmVer) return;

            Logger.Info($"Claude CLI 更新: {localVer} → {npmVer}，正在更新...");
            var psi = new ProcessStartInfo("npm", "install -g @anthropic-ai/claude-code --registry=https://registry.npmmirror.com")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return;
            await proc.WaitForExitAsync();
            if (proc.ExitCode == 0)
            {
                _cliUpdateReady = true;
                _cliNewVersion = npmVer;
                Logger.Info($"Claude CLI 已更新到 {npmVer}");
                Dispatcher.Invoke(() => _activeTerminal?.ShowSystemMessage(
                    $"✅ 已更新 Claude Code CLI 到 {npmVer}，无需重启。（{DateTime.Now:yyyy年M月d日}）"));
            }
        }
        catch (Exception ex) { Logger.Info($"CLI 更新检测跳过: {ex.Message}"); }
    }

    private static async Task<string> RunCliVersionAsync(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var proc = Process.Start(psi);
            if (proc == null) return "";
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return output;
        }
        catch { return ""; }
    }

    /// <summary>提取 x.y.z 数字前缀用于版本比较</summary>
    private static string ExtractSemver(string tag)
    {
        var match = System.Text.RegularExpressions.Regex.Match(tag, @"^(\d+\.\d+\.\d+)");
        return match.Success ? match.Value : tag;
    }

    private void UpdateDotState()
    {
        if (_selfUpdateReady)
        {
            BtnUpdateDot.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x64, 0xff, 0xda));
            BtnUpdateDot.ToolTip = $"ClaudeCliGui 有更新 {_selfUpdateTag}，已下载，点击重启更新";
            // 呼吸动画
            if (BtnUpdateDot.Resources["UpdateGlow"] is System.Windows.Media.Animation.Storyboard sb)
                sb.Begin();
        }
        else if (_cliUpdateReady)
        {
            BtnUpdateDot.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x80, 0xb3, 0xff));
            BtnUpdateDot.ToolTip = $"Claude CLI 已更新到 {_cliNewVersion}，点击确认";
        }
        else
        {
            if (BtnUpdateDot.Resources["UpdateGlow"] is System.Windows.Media.Animation.Storyboard sb)
                sb.Stop();
            BtnUpdateDot.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x16, 0x21, 0x3e));
            BtnUpdateDot.ToolTip = "";
        }
    }

    private void UpdateDot_Click(object sender, RoutedEventArgs e)
    {
        if (_selfUpdateReady)
        {
            var exePath = Environment.ProcessPath!;
            Process.Start(exePath);
            Application.Current.Shutdown();
        }
        else if (_cliUpdateReady)
        {
            _activeTerminal?.ShowSystemMessage(
                $"✅ Claude Code CLI 已更新到 {_cliNewVersion}，无需重启，下次对话生效。");
            _cliUpdateReady = false;
            _cliNewVersion = null;
            UpdateDotState();
        }
    }

    // ============ 右键菜单切换 ============

    private void ToggleContextMenu_Click(object sender, MouseButtonEventArgs e)
    {
        if (IsContextMenuInstalled())
        {
            try { Microsoft.Win32.Registry.ClassesRoot.DeleteSubKeyTree(@"Directory\shell\ClaudeGui", false); } catch { }
            try { Microsoft.Win32.Registry.ClassesRoot.DeleteSubKeyTree(@"Directory\Background\shell\ClaudeGui", false); } catch { }
        }
        else
        {
            var exePath = Environment.ProcessPath ?? "";
            Microsoft.Win32.Registry.SetValue(@"HKEY_CLASSES_ROOT\Directory\shell\ClaudeGui", "", "在当前文件夹使用ClaudeGui");
            Microsoft.Win32.Registry.SetValue(@"HKEY_CLASSES_ROOT\Directory\shell\ClaudeGui", "Icon", exePath);
            Microsoft.Win32.Registry.SetValue(@"HKEY_CLASSES_ROOT\Directory\shell\ClaudeGui\command", "", $"\"{exePath}\" --add-project \"%1\"");
            Microsoft.Win32.Registry.SetValue(@"HKEY_CLASSES_ROOT\Directory\Background\shell\ClaudeGui", "", "在当前文件夹使用ClaudeGui");
            Microsoft.Win32.Registry.SetValue(@"HKEY_CLASSES_ROOT\Directory\Background\shell\ClaudeGui", "Icon", exePath);
            Microsoft.Win32.Registry.SetValue(@"HKEY_CLASSES_ROOT\Directory\Background\shell\ClaudeGui\command", "", $"\"{exePath}\" --add-project \"%V\"");
        }
        UpdateMenuStatusText();
    }

    private void UpdateMenuStatusText()
    {
        var ok = IsContextMenuInstalled();
        TxtMenuStatus.Text = ok ? "右键菜单 ✓" : "右键菜单 ✗";
        TxtMenuStatus.Foreground = ok
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4c, 0xaf, 0x50))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x66, 0x66, 0x66));
    }

    private static bool IsContextMenuInstalled()
    {
        try { return Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"Directory\shell\ClaudeGui\command") != null; }
        catch { return false; }
    }

    private static string NormalizePath(string p)
        => p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace("/", "\\").ToLowerInvariant();

    /// <summary>打开聊天记录速览窗口（从 TerminalControl 快捷指令调用）</summary>
    public void OpenChatHistoryViewer(string projectDir)
    {
        if (string.IsNullOrWhiteSpace(projectDir) || !Directory.Exists(projectDir)) return;
        var win = new ChatHistoryWindow(projectDir, _config) { Owner = this };
        win.Show();
    }

    // ============ 文件浏览器（可折叠 TreeView + 状态持久化） ============

    private bool _fileBrowserOpen;
    // 每项目的浏览器状态：展开的文件夹路径集合
    private readonly Dictionary<string, HashSet<string>> _fbExpanded = new(StringComparer.OrdinalIgnoreCase);
    // 每项目的滚动偏移
    private readonly Dictionary<string, double> _fbScrollPos = new(StringComparer.OrdinalIgnoreCase);
    // 当前正在构建的路径 （防重复加载）
    private string? _fbLoadingProject;
    // 右键弹出菜单引用（防止 GC 回收）
    private System.Windows.Controls.Primitives.Popup? _fbContextMenu;

    private void ToggleFileBrowser_Click(object sender, RoutedEventArgs e)
    {
        _fileBrowserOpen = !_fileBrowserOpen;
        FileBrowserCol.Width = _fileBrowserOpen ? new GridLength(260) : new GridLength(0);
        FileBrowserPanel.Visibility = _fileBrowserOpen ? Visibility.Visible : Visibility.Collapsed;

        if (_fileBrowserOpen && _currentProject != null)
            RefreshFileBrowser(_currentProject.Path);
    }

    /// <summary>切换项目时由 SelectProject 调用</summary>
    private void RefreshFileBrowser(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath)) return;
        if (!_fileBrowserOpen) return;

        // 保存当前状态到上一个项目
        if (_fbLoadingProject != null)
            SaveFileBrowserState(_fbLoadingProject);

        _fbLoadingProject = projectPath;
        FileBrowserTitle.Text = $"📂 {new DirectoryInfo(projectPath).Name}";
        FileBrowserPanelInner.Children.Clear();

        // 恢复该项目之前展开的文件夹集合
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
            {
                var sv = FileBrowserScroll;
                sv.ScrollToVerticalOffset(pos);
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>创建文件夹行（ToggleButton ▶/▼ + 递归子项容器）</summary>
    private UIElement CreateFolderItem(DirectoryInfo dir, HashSet<string> expandedSet, int depth)
    {
        if (depth > 6) return new TextBlock { Height = 0 };

        var isExpanded = expandedSet.Contains(dir.FullName);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(depth * 14, 0, 0, 0) };

        var toggle = new System.Windows.Controls.Primitives.ToggleButton
        {
            Content = isExpanded ? "▼" : "▶",
            IsChecked = isExpanded,
            Width = 16, Height = 16,
            FontSize = 9,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x92, 0xb0)),
            Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var label = new TextBlock
        {
            Text = $"📁 {dir.Name}",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)),
            Margin = new Thickness(2, 2, 0, 2),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        // 限制鼠标左键才展开/收缩（右键走独立菜单）
        label.MouseDown += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                toggle.IsChecked = !toggle.IsChecked;
                e.Handled = true;
            }
        };
        // 容器需在此声明，后面右键菜单要用
        var container = new StackPanel();

        // 右键文件夹 → 弹出操作菜单（右键抬起时弹出）
        label.MouseRightButtonDown += (_, e) => e.Handled = true;
        label.MouseRightButtonUp += (_, e) => { ShowFolderMenu(dir, container); e.Handled = true; };
        toggle.MouseRightButtonDown += (_, e) => e.Handled = true;
        toggle.MouseRightButtonUp += (_, e) => { ShowFolderMenu(dir, container); e.Handled = true; };

        // 子项容器（折叠/展开由 ToggleButton 控制）
        var childrenPanel = new StackPanel { Margin = new Thickness(0), Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed };

        // 只展开时加载子项（懒加载）
        object? loadedLock = null;
        toggle.Checked += (_, _) =>
        {
            childrenPanel.Visibility = Visibility.Visible;
            if (loadedLock == null)
            {
                loadedLock = new(); // 仅加载一次
                try
                {
                    foreach (var sd in dir.GetDirectories()
                        .Where(d => !d.Name.StartsWith('.') && d.Name != "bin" && d.Name != "obj" && d.Name != "node_modules")
                        .OrderBy(d => d.Name))
                    {
                        childrenPanel.Children.Add(CreateFolderItem(sd, expandedSet, depth + 1));
                    }
                    foreach (var f in dir.GetFiles()
                        .Where(fi => !fi.Name.StartsWith('.') && !fi.Name.EndsWith(".exe") && !fi.Name.EndsWith(".dll"))
                        .OrderBy(fi => fi.Name))
                    {
                        childrenPanel.Children.Add(CreateFileItem(f, depth + 1));
                    }
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
        // 文件夹行 Ctrl+点击打开资源管理器
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

    /// <summary>创建文件行（左键复制文件名到输入框，右键弹出操作菜单）</summary>
    private UIElement CreateFileItem(FileInfo file, int depth)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(depth * 14 + 18, 0, 0, 0), Cursor = System.Windows.Input.Cursors.Hand };
        // 左键：复制文件名到输入框光标处
        row.MouseLeftButtonDown += (_, e) =>
        {
            _activeTerminal?.InsertTextAtCursor(file.Name);
            e.Handled = true;
        };
        // 右键：弹出操作菜单（右键抬起时弹出）
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

    // ============ 文件/文件夹右键菜单 ============

    /// <summary>文件右键菜单：打开 / 编辑 / 重命名 / 删除 / 属性</summary>
    private void ShowFileMenu(FileInfo file, UIElement target)
    {
        var panel = new StackPanel { };
        AddMenuItem(panel, "📂 打开", () => { try { Process.Start(new ProcessStartInfo(file.FullName) { UseShellExecute = true }); } catch { } });
        AddMenuItem(panel, "✏️ 编辑", () => { try { Process.Start("notepad.exe", file.FullName); } catch { } });
        AddMenuSep(panel);
        AddMenuItem(panel, "📝 重命名", () => RenameFileOrFolder(file.FullName));
        AddMenuItem(panel, "🗑️ 删除", () => DeleteFileOrFolder(file.FullName));
        AddMenuSep(panel);
        AddMenuItem(panel, "🔍 属性", () => { try { Process.Start("explorer.exe", $"/select,\"{file.FullName}\""); } catch { } });
        ShowPopupMenu(panel, target);
    }

    /// <summary>文件夹右键菜单：打开 / 重命名 / 删除 / 属性</summary>
    private void ShowFolderMenu(DirectoryInfo dir, UIElement target)
    {
        var panel = new StackPanel { };
        AddMenuItem(panel, "📂 打开", () => { try { Process.Start("explorer.exe", dir.FullName); } catch { } });
        AddMenuSep(panel);
        AddMenuItem(panel, "📝 重命名", () => RenameFileOrFolder(dir.FullName));
        AddMenuItem(panel, "🗑️ 删除", () => DeleteFileOrFolder(dir.FullName));
        AddMenuSep(panel);
        AddMenuItem(panel, "🔍 属性", () => { try { Process.Start("explorer.exe", $"/select,\"{dir.FullName}\""); } catch { } });
        ShowPopupMenu(panel, target);
    }

    // 右键菜单构建工具

    private void AddMenuItem(StackPanel panel, string header, Action onClick)
    {
        var b = new System.Windows.Controls.Button
        {
            Content = new TextBlock { Text = header, FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(0xe0, 0xe0, 0xe0)) },
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Padding = new Thickness(8, 5, 16, 5),
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
            MinWidth = 140,
        };
        b.MouseEnter += (_, _) => b.Background = new SolidColorBrush(Color.FromRgb(0x23, 0x35, 0x54));
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.Click += (_, _) => { if (_fbContextMenu != null) { _fbContextMenu.IsOpen = false; _fbContextMenu = null; } onClick(); };
        panel.Children.Add(b);
    }

    private static void AddMenuSep(StackPanel panel)
    {
        panel.Children.Add(new System.Windows.Controls.Separator
        {
            Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            Height = 1, Margin = new Thickness(4, 1, 4, 1)
        });
    }

    private void ShowPopupMenu(StackPanel panel, UIElement target)
    {
        // 关闭上一个右键菜单
        if (_fbContextMenu != null) { _fbContextMenu.IsOpen = false; _fbContextMenu = null; }

        var border = new System.Windows.Controls.Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x2e)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x23, 0x35, 0x54)),
            BorderThickness = new Thickness(1),
            CornerRadius = new System.Windows.CornerRadius(6),
            Padding = new Thickness(4),
            Child = panel,
        };
        _fbContextMenu = new System.Windows.Controls.Primitives.Popup
        {
            Child = border,
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
            StaysOpen = true,   // 右键释放后不自动关闭
            IsOpen = true,
        };
        _fbContextMenu.Closed += (_, _) =>
        {
            this.PreviewMouseDown -= OnWindowPreviewMouseDown;
            _fbContextMenu = null;
        };
        this.PreviewMouseDown += OnWindowPreviewMouseDown;
    }

    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 在主窗口任意位置点击 → 关闭右键菜单
        if (_fbContextMenu != null)
        {
            _fbContextMenu.IsOpen = false;
            _fbContextMenu = null;
        }
    }

    private void RenameFileOrFolder(string fullPath)
    {
        var oldName = Path.GetFileName(fullPath);
        var parent = Path.GetDirectoryName(fullPath);
        if (parent == null) return;

        var win = new Window
        {
            Title = "重命名", Width = 380, Height = 140,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this, ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None, AllowsTransparency = true,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x21, 0x3e)),
        };
        var border = new System.Windows.Controls.Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x21, 0x3e)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x23, 0x35, 0x54)),
            BorderThickness = new Thickness(1),
            CornerRadius = new System.Windows.CornerRadius(8), Padding = new Thickness(20),
        };
        var panel = new StackPanel { };
        panel.Children.Add(new TextBlock { Text = "新名称:", Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0xff, 0xda)), FontSize = 13, Margin = new Thickness(0, 0, 0, 8) });
        var tb = new System.Windows.Controls.TextBox
        {
            Text = oldName, FontSize = 13, Padding = new Thickness(6, 4, 6, 4),
            Background = new SolidColorBrush(Color.FromRgb(0x0c, 0x0c, 0x0c)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            BorderThickness = new Thickness(1),
            CaretBrush = new SolidColorBrush(Color.FromRgb(0x64, 0xff, 0xda)),
        };
        panel.Children.Add(tb);
        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var okBtn = new System.Windows.Controls.Button
        {
            Content = "确定", Width = 70, Height = 28, Margin = new Thickness(0, 0, 8, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x64, 0xff, 0xda)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x2e)),
            FontWeight = FontWeights.Bold, BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        var cancelBtn = new System.Windows.Controls.Button
        {
            Content = "取消", Width = 70, Height = 28,
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x92, 0xb0)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x23, 0x35, 0x54)),
            BorderThickness = new Thickness(1), Cursor = System.Windows.Input.Cursors.Hand,
        };
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
            if (_currentProject != null) RefreshFileBrowser(_currentProject.Path);
        };
        cancelBtn.Click += (_, _) => win.Close();
        btnPanel.Children.Add(okBtn); btnPanel.Children.Add(cancelBtn);
        panel.Children.Add(btnPanel);
        border.Child = panel;
        win.Content = border;
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
                if (_currentProject != null) RefreshFileBrowser(_currentProject.Path);
            }
            catch (Exception ex) { MessageBox.Show($"删除失败: {ex.Message}", "错误"); }
        }
    }

    /// <summary>保存当前项目的浏览器状态（展开路径 + 滚动位置）</summary>
    private void SaveFileBrowserState(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return;
        var sv = FileBrowserScroll;
        _fbScrollPos[projectPath] = sv.VerticalOffset;
    }

    private static string GetFileIcon(string ext) => ext.ToLowerInvariant() switch
    {
        ".cs" or ".xaml" or ".py" or ".js" or ".ts" or ".html" or ".css" => "📄",
        ".md" or ".txt" or ".log" => "📝",
        ".json" or ".xml" or ".yaml" or ".yml" or ".config" => "⚙️",
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" or ".ico" => "🖼️",
        ".zip" or ".7z" or ".rar" => "📦",
        _ => "📄",
    };

    private static string IpcFilePath => Path.Combine(Path.GetTempPath(), "claudeCliGui-add-project.txt");

    /// <summary>启动轮询定时器，监听临时文件中的 --add-project 路径</summary>
    private void StartAddProjectWatcher()
    {
        try
        {
            // 启动时处理残留文件
            ProcessAddProjectFile();

            // System.Threading.Timer 比 DispatcherTimer 更可靠
            var t = new System.Threading.Timer(
                _ => Dispatcher.Invoke(ProcessAddProjectFile),
                null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
            // 改为字段存储以阻止 GC
            _ipcWatcher = t;
            Logger.Info("IPC 监听已启动");
        }
        catch { }
    }
    private System.Threading.Timer? _ipcWatcher;

    /// <summary>读取临时文件中的项目路径，添加并切换到该项目</summary>
    private void ProcessAddProjectFile()
    {
        var filePath = IpcFilePath;
        if (!File.Exists(filePath)) return;

        string? path;
        try
        {
            path = File.ReadAllText(filePath).Trim();
            File.Delete(filePath);
            Logger.Info($"IPC 读取到路径: {path}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"IPC 文件读取失败: {ex.Message}");
            return;
        }

        if (!Directory.Exists(path))
        {
            Logger.Warn($"IPC 路径不存在: {path}");
            return;
        }

        try
        {
            var existing = _config.GetProjects().FirstOrDefault(p => NormalizePath(p.Path) == NormalizePath(path));
            if (existing == null)
                existing = _config.AddExistingProject(path);
            if (existing != null)
            {
                RefreshProjectList();
                SelectProject(existing);
                Logger.Info($"IPC 添加并切换到项目: {existing.Name}");
            }
        }
        catch { }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) yield return t;
            foreach (var c in FindVisualChildren<T>(child)) yield return c;
        }
    }
}

public class ProjectItem : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public DateTime LastAccessedAt { get; set; }
    public bool IsActive { get; set; }
    public ProjectEntry? Original { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
