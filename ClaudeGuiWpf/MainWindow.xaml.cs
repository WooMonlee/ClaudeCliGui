using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
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
        SetupPage.SetupCompleted += OnSetupCompleted;
        SetupPage.RequestShow += () =>
        {
            SetupPage.Visibility = Visibility.Visible;
            Terminal.Visibility = Visibility.Collapsed;
        };
        UpdateMenuStatusText();
        RefreshProjectList();
        // 后台检测更新
        _ = CheckForUpdateAsync();

        // SetupPage 也会自身检测，缺组件才显示

        var args = Environment.GetCommandLineArgs();
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--add-project" && i + 1 < args.Length && Directory.Exists(args[i + 1]))
            {
                var existing = _config.GetProjects().FirstOrDefault(p => NormalizePath(p.Path) == NormalizePath(args[i + 1]));
                if (existing == null) try { existing = _config.AddExistingProject(args[i + 1]); } catch { }
                if (existing != null) { RefreshProjectList(); SelectProject(existing); }
                break;
            }
        }
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

    private void UpdateProjectCardSelection()
    {
        foreach (var card in FindVisualChildren<Border>(ProjectListBox))
            if (card.Tag is ProjectItem item)
                card.Background = item.IsActive
                    ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1a, 0x3a, 0x4a))
                    : System.Windows.Media.Brushes.Transparent;
    }

    // ============ 切换右边：设置向导 / 终端 ============

    private void OnSetupCompleted()
    {
        SetupPage.Visibility = Visibility.Collapsed;
        Terminal.Visibility = Visibility.Visible;
    }

    private void SelectProject(ProjectEntry project)
    {
        Logger.Info($"选择项目: {project.Name} ({project.Path})");
        _currentProject = project;
        _config.UpdateAccessTime(project.Name);
        RefreshProjectList();

        // 切换项目时清空旧内容
        Terminal.ClearOutput();
        LoadSnapshot(project.Path);

        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? Directory.GetCurrentDirectory();
        var nodeDir = FindPortableNodeDir(exeDir);
        if (nodeDir != null)
        {
            var portableClaude = Path.Combine(nodeDir, "claude.cmd");
            if (File.Exists(portableClaude)) Terminal.ClaudePath = portableClaude;
        }

        Terminal.Activate(project.Path);
    }

    // ============ 项目操作 ============

    private void ProjectCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is ProjectItem item && item.Original != null)
            SelectProject(item.Original);
    }

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new NewProjectDialog(_config) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result != null) { RefreshProjectList(); SelectProject(dlg.Result); }
    }

    private void AddExisting_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "选择项目文件夹", UseDescriptionForTitle = true };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
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
            var newName = Microsoft.VisualBasic.Interaction.InputBox("新名称：", "重命名项目", item.Name);
            if (!string.IsNullOrWhiteSpace(newName) && newName != item.Name)
            {
                try
                {
                    var u = _config.RenameProject(item.Name, newName);
                    if (u != null) { if (_currentProject?.Name == item.Name) _currentProject = u; RefreshProjectList(); }
                }
                catch (Exception ex) { MessageBox.Show(ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error); }
            }
        }
    }

    private void DeleteProject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ProjectItem item)
        {
            if (MessageBox.Show($"确定要删除项目 [{item.Name}] 吗？\n（不会删除实际文件）", "确认删除",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                _config.DeleteProject(item.Name);
                if (_currentProject?.Name == item.Name) { _currentProject = null; Terminal.Dispose(); }
                RefreshProjectList();
            }
        }
    }

    private async void WipeAll_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "此功能将清除本软件所有痕迹，包括：\n\n" +
            "  · 删除所有项目记录（claudeg.json）\n" +
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

            // 3. 删除 claudeg.json
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? Directory.GetCurrentDirectory();
            var configPath = Path.Combine(exeDir, "claudeg.json");
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
            Terminal.Dispose();
            await Task.Delay(300);
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Logger.Error("清零失败", ex);
            MessageBox.Show($"清零失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        try { Terminal.Dispose(); } catch (Exception ex) { Logger.Error("Terminal.Dispose 失败", ex); }
        try { _config.Save(); } catch (Exception ex) { Logger.Error("Config.Save 失败", ex); }
        base.OnClosed(e);
    }

    // ============ 历史快照 ============

    private void LoadSnapshot(string workDir)
    {
        var all = LoadClaudeJsonlHistory(workDir, 1000);
        if (all.Count == 0) all = LoadClaudegSnapshot(workDir);
        if (all.Count == 0) return;

        var messages = all.Select(m => (m.Role, m.Content)).ToList();
        Terminal.LoadFullSnapshot(messages);
        Terminal.ScrollToEnd();
        Logger.Info($"加载历史: {all.Count} 条");
    }

    private List<SnapshotMsg> LoadClaudeJsonlHistory(string workDir, int maxCount = 500)
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

        var seen = new HashSet<int>();
        foreach (var f in Directory.GetFiles(pDir, "*.jsonl").OrderBy(f => new FileInfo(f).LastWriteTimeUtc))
        {
            try
            {
                foreach (var line in File.ReadLines(f))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var msg = ParseJsonlLine(line);
                    if (msg != null && seen.Add(msg.Content.GetHashCode())) result.Add(msg);
                }
            }
            catch { }
        }
        if (result.Count > maxCount) result = result.Skip(result.Count - maxCount).ToList();
        return result;
    }

    private List<SnapshotMsg> LoadClaudegSnapshot(string workDir)
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

    private static async Task CheckForUpdateAsync()
    {
        try
        {
            using var hc = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            hc.DefaultRequestHeaders.Add("User-Agent", "ClaudeG");
            var json = await hc.GetStringAsync("https://api.github.com/repos/WooMonlee/ClaudeG/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var tag = doc.RootElement.GetProperty("tag_name").GetString();
            var assets = doc.RootElement.GetProperty("assets");
            string? downloadUrl = null;
            foreach (var a in assets.EnumerateArray())
                if (a.GetProperty("name").GetString() == "claudeg.exe")
                { downloadUrl = a.GetProperty("browser_download_url").GetString(); break; }

            if (tag == null || downloadUrl == null) return;

            // 修复 L7：用 SemanticVersion 比较
            var currentVer = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            var currentStr = currentVer != null ? $"{currentVer.Major}.{currentVer.Minor}.{currentVer.Build}" : "0.0";
            var tagVer = tag.StartsWith("v") ? tag[1..] : tag;
            if (tagVer == currentStr) return;

            var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? Directory.GetCurrentDirectory();
            var newPath = Path.Combine(exeDir, "claudeg.new.exe");
            if (File.Exists(newPath)) return; // 已在下载

            Logger.Info($"发现新版本: {tag}，正在下载...");
            var data = await hc.GetByteArrayAsync(downloadUrl);
            File.WriteAllBytes(newPath, data);
            Logger.Info($"新版本已下载: {newPath}，下次启动时更新");
        }
        catch (Exception ex) { Logger.Info($"更新检测跳过: {ex.Message}"); }
    }

    // ============ 右键菜单切换 ============

    private void ToggleContextMenu_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
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
