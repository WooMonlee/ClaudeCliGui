using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClaudeGui;

public partial class SetupPage : System.Windows.Controls.UserControl
{
    public event Action? SetupCompleted;
    public event Action? RequestShow;

    private string? _nodeDir;
    private bool _nodeOk, _claudeOk, _apiOk;
    private bool _autoInstalling;

    public string? NodeDir => _nodeDir;

    public SetupPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await CheckAllAsync();
    }

    // ============ 检测 ============

    private async Task CheckAllAsync()
    {
        Logger.Info("SetupPage: 开始检测环境");
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? Directory.GetCurrentDirectory();

        _nodeDir = FindPortableNodeDir(exeDir);

        // 便携版没有就试系统 node --version
        if (_nodeDir == null)
        {
            var sysVer = (await RunAndReadAsync("node", "--version", 5000))?.Trim();
            if (!string.IsNullOrWhiteSpace(sysVer))
            {
                // 找到 node.exe 路径以便后续找 npm
                var nodePath = (await RunAndReadAsync("where", "node", 3000))?.Split('\n', '\r').FirstOrDefault()?.Trim();
                _nodeDir = nodePath != null ? Path.GetDirectoryName(nodePath) : null;
                _nodeOk = true; SetGreen(NodeIcon, NodeStatus, "已就绪 (系统安装, " + sysVer + ")");
            }
        }
        else if (File.Exists(Path.Combine(_nodeDir, "node.exe")))
        {
            _nodeOk = true; SetGreen(NodeIcon, NodeStatus, "已就绪 (" + Path.GetFileName(_nodeDir) + ")");
        }

        if (_nodeOk)
        {
            await CheckClaudeAsync();
        }
        else
        {
            _nodeOk = false; NodeStatus.Text = "未安装";
            ClaudeStatus.Text = "等待 Node.js 就绪..."; ClaudeStatus.Foreground = new SolidColorBrush(Colors.Gray);
            ApiStatus.Text = "等待 Claude CLI 就绪..."; ApiStatus.Foreground = new SolidColorBrush(Colors.Gray);
            ApiIcon.Foreground = new SolidColorBrush(Colors.DarkGray); ClaudeIcon.Foreground = new SolidColorBrush(Colors.DarkGray);
        }

        await CheckApiAsync();
        UpdateReady();

        // 如果缺组件，显示一键安装按钮
        if (!_nodeOk || !_claudeOk)
        {
            RequestShow?.Invoke();
            BtnAutoSetup.Visibility = Visibility.Visible;
            TxtSubtitle.Text = "首次使用需安装运行环境，点击下方按钮自动完成";
            Logger.Info($"SetupPage: 缺组件 Node={_nodeOk} Claude={_claudeOk}");
        }
        else if (!_apiOk)
        {
            RequestShow?.Invoke();
            TxtSubtitle.Text = "请配置 API Key";
            Logger.Info("SetupPage: 仅缺 API Key");
        }
        else
        {
            Logger.Info("SetupPage: 环境完整，跳过");
            InstallContextMenu();
        }
    }

    private async Task CheckClaudeAsync()
    {
        var paths = new[] { Path.Combine(_nodeDir!, "claude.cmd"), Path.Combine(_nodeDir!, "node_modules", ".bin", "claude.cmd") };
        var found = paths.FirstOrDefault(File.Exists);
        if (found != null)
        {
            var ver = (await RunShellAsync($"\"{found}\" --version", 5000, Path.GetDirectoryName(found)!))?.Trim();
            if (!string.IsNullOrWhiteSpace(ver)) { _claudeOk = true; SetGreen(ClaudeIcon, ClaudeStatus, "已安装 (" + ver + ")"); return; }
        }
        var sysVer = (await RunAndReadAsync("claude", "--version", 5000))?.Trim();
        if (!string.IsNullOrWhiteSpace(sysVer)) { _claudeOk = true; SetGreen(ClaudeIcon, ClaudeStatus, "已安装 (" + sysVer + ")"); return; }
        _claudeOk = false; ClaudeStatus.Text = "未安装";
    }

    private Task CheckApiAsync()
    {
        var key = GetConfig("ANTHROPIC_API_KEY") ?? GetConfig("ANTHROPIC_AUTH_TOKEN");
        var url = GetConfig("ANTHROPIC_BASE_URL");
        if (!string.IsNullOrWhiteSpace(key))
        {
            // 修复 L2：Key 存在时同步到当前进程环境（子进程 claude 需要）
            Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", key, EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", key, EnvironmentVariableTarget.Process);
            if (!string.IsNullOrWhiteSpace(url))
                Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", url, EnvironmentVariableTarget.Process);

            _apiOk = true; SetGreen(ApiIcon, ApiStatus, "已配置");
            ApiKeyInput.Text = key; ApiKeyInput.IsEnabled = false;
            if (!string.IsNullOrWhiteSpace(url)) ApiUrlInput.Text = url;
            ApiUrlInput.IsEnabled = false; BtnSaveApi.IsEnabled = false;
        }
        else
        {
            _apiOk = false;
            if (_claudeOk) { ApiStatus.Text = "需要 DeepSeek API Key"; ApiUrlInput.IsEnabled = true; ApiKeyInput.IsEnabled = true; BtnSaveApi.IsEnabled = true; }
            else { ApiStatus.Text = "等待 Claude CLI 就绪..."; }
        }
        return Task.CompletedTask;
    }

    private void UpdateReady()
    {
        var allDone = _nodeOk && _claudeOk && _apiOk;
        TxtAllDone.Visibility = allDone ? Visibility.Visible : Visibility.Collapsed;
        BtnAutoSetup.Visibility = (!_nodeOk || !_claudeOk) && !_autoInstalling ? Visibility.Visible : Visibility.Collapsed;

        if (allDone)
        {
            TxtSubtitle.Text = "";
            // 瞬跳，不延迟
            Dispatcher.InvokeAsync(() => SetupCompleted?.Invoke());
        }
    }

    // ============ 一键安装 ============

    private async void AutoSetup_Click(object sender, RoutedEventArgs e)
    {
        _autoInstalling = true;
        BtnAutoSetup.IsEnabled = false; BtnAutoSetup.Content = "安装中...";
        TxtSubtitle.Text = "正在自动安装环境，请稍候...";
        Logger.Info("SetupPage: 一键安装开始");

        try
        {
            // Step 1: Node.js
            if (!_nodeOk)
            {
                NodeStatus.Text = "正在下载 Node.js...";
                await InstallNodeAsync();
            }

            // Step 2: Claude CLI
            if (_nodeOk && !_claudeOk)
            {
                ClaudeStatus.Text = "正在安装 Claude CLI...";
                await InstallClaudeAsync();
            }

            // 启用 API 输入
            if (_claudeOk)
            {
                ApiStatus.Text = "需要 DeepSeek API Key";
                ApiStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x92, 0xb0));
                ApiIcon.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x64, 0xff, 0xda));
                ApiUrlInput.IsEnabled = true; ApiKeyInput.IsEnabled = true; BtnSaveApi.IsEnabled = true;
            }

            TxtSubtitle.Text = "环境安装完成，请配置 API Key";
            Logger.Info("SetupPage: 一键安装完成");
        }
        catch (Exception ex)
        {
            TxtSubtitle.Text = $"安装失败: {ex.Message}";
            BtnAutoSetup.IsEnabled = true; BtnAutoSetup.Content = "重试安装";
            Logger.Error($"一键安装失败: {ex.Message}");
        }
        finally { _autoInstalling = false; }
    }

    private async Task InstallNodeAsync()
    {
        string json;
        using (var hc = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) })
            json = await hc.GetStringAsync("https://nodejs.org/dist/index.json");
        string? ver = null;
        using var doc = JsonDocument.Parse(json);
        foreach (var entry in doc.RootElement.EnumerateArray())
            if (entry.TryGetProperty("lts", out var lt) && lt.ValueKind != JsonValueKind.False)
            { ver = entry.GetProperty("version").GetString()!; break; }
        if (ver == null) throw new Exception("无法获取版本");

        var cleanVer = ver.StartsWith("v") ? ver[1..] : ver;
        var fileName = $"node-v{cleanVer}-win-x64.zip";
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? Directory.GetCurrentDirectory();
        var savePath = Path.Combine(exeDir, fileName);
        using (var dl = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        {
            var resp = await dl.GetAsync($"https://npmmirror.com/mirrors/node/{ver}/{fileName}", HttpCompletionOption.ResponseHeadersRead);
            var total = resp.Content.Headers.ContentLength ?? 0;
            using var s = await resp.Content.ReadAsStreamAsync();
            using var fs = File.Create(savePath);
            var buf = new byte[8192]; long got = 0; int n;
            while ((n = await s.ReadAsync(buf)) > 0) { fs.Write(buf, 0, n); got += n; if (total > 0) NodeStatus.Text = $"下载 Node.js... {got * 100 / total}%"; }
        }
        var nodeDir = Path.Combine(exeDir, "nodejs");
        try { if (Directory.Exists(nodeDir)) Directory.Delete(nodeDir, true); } catch { }
        ZipFile.ExtractToDirectory(savePath, nodeDir);
        try { File.Delete(savePath); } catch { }
        _nodeDir = Path.Combine(nodeDir, fileName.Replace(".zip", ""));
        _nodeOk = true; SetGreen(NodeIcon, NodeStatus, "安装完成 (" + ver + ")");
        ClaudeIcon.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x64, 0xff, 0xda));
        Logger.Info($"SetupPage: Node.js 安装完成 ({ver})");
    }

    private async Task InstallClaudeAsync()
    {
        var npmPath = Path.Combine(_nodeDir!, "npm.cmd");
        var psi = new ProcessStartInfo("cmd.exe",
            $"/c set PATH={_nodeDir};%PATH% && \"{npmPath}\" install -g @anthropic-ai/claude-code --registry=https://registry.npmmirror.com")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var proc = Process.Start(psi) ?? throw new Exception("无法启动 npm");
        var error = await proc.StandardError.ReadToEndAsync(); await proc.WaitForExitAsync();
        if (proc.ExitCode != 0) throw new Exception(error.Split('\n').FirstOrDefault(s => s.Contains("error")) ?? "npm 失败");

        var found = new[] { Path.Combine(_nodeDir!, "claude.cmd"), Path.Combine(_nodeDir!, "node_modules", ".bin", "claude.cmd") }.FirstOrDefault(File.Exists);
        if (found != null)
        {
            _nodeDir = Path.GetDirectoryName(found)!;
            var cp = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
            if (!cp.Contains(_nodeDir, StringComparison.OrdinalIgnoreCase))
                Environment.SetEnvironmentVariable("PATH", cp.TrimEnd(';') + ";" + _nodeDir, EnvironmentVariableTarget.User);
        }
        _claudeOk = true; SetGreen(ClaudeIcon, ClaudeStatus, "安装完成");
        ApiIcon.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x64, 0xff, 0xda));
        Logger.Info("SetupPage: Claude CLI 安装完成");
    }

    // ============ API Key 保存 ============

    private void SaveApi_Click(object sender, RoutedEventArgs e)
    {
        var key = ApiKeyInput.Text.Trim(); var url = ApiUrlInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(key)) return;
        if (string.IsNullOrWhiteSpace(url)) url = "https://api.deepseek.com/anthropic";

        try
        {
            var asm = Assembly.GetExecutingAssembly();
            string templateJson;
            using (var stream = asm.GetManifestResourceStream("ClaudeGui.settings-template.json")
                   ?? throw new Exception("模板文件未找到"))
            using (var reader = new StreamReader(stream))
                templateJson = reader.ReadToEnd();

            // 修复 S1：用 JsonNode 安全修改 JSON，避免正则注入
            var node = JsonNode.Parse(templateJson)!;
            node["env"]!["ANTHROPIC_AUTH_TOKEN"] = key;
            node["env"]!["ANTHROPIC_API_KEY"] = key;
            node["env"]!["ANTHROPIC_BASE_URL"] = url;

            var settingsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            Directory.CreateDirectory(settingsDir);
            var resultJson = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(settingsDir, "settings.json"), resultJson);

            if (node["env"] is JsonObject env)
                foreach (var p in env)
                {
                    var val = p.Value?.GetValue<string>() ?? "";
                    Environment.SetEnvironmentVariable(p.Key, val, EnvironmentVariableTarget.User);
                    Environment.SetEnvironmentVariable(p.Key, val, EnvironmentVariableTarget.Process);
                }

            _apiOk = true; SetGreen(ApiIcon, ApiStatus, "已配置");
            ApiSaveStatus.Text = "已保存"; ApiSaveStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4c, 0xaf, 0x50));
            ApiSaveStatus.Visibility = Visibility.Visible; ApiKeyInput.IsEnabled = false; BtnSaveApi.IsEnabled = false;
            Logger.Info("SetupPage: API Key 已保存");

            // 写入全局 CLAUDE.md 模板
            var globalMd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "CLAUDE.md");
            if (!File.Exists(globalMd))
            {
                try
                {
                    using var ms = asm.GetManifestResourceStream("ClaudeGui.claude-md-template.txt");
                    if (ms != null) { using var r = new StreamReader(ms); File.WriteAllText(globalMd, r.ReadToEnd()); }
                }
                catch { }
            }

            InstallContextMenu();
            UpdateReady();
        }
        catch (Exception ex)
        {
            ApiSaveStatus.Text = $"失败: {ex.Message}";
            ApiSaveStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xff, 0x6b, 0x6b));
            ApiSaveStatus.Visibility = Visibility.Visible;
        }
    }

    private void OpenDeepSeek_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://platform.deepseek.com/api_keys") { UseShellExecute = true }); } catch { }
    }

    // ============ 右键菜单注册 ============

    private void InstallContextMenu()
    {
        try
        {
            var exePath = Environment.ProcessPath ?? "";
            var menuName = "在当前文件夹使用ClaudeGui";

            Microsoft.Win32.Registry.SetValue(@"HKEY_CLASSES_ROOT\Directory\shell\ClaudeGui", "", menuName);
            Microsoft.Win32.Registry.SetValue(@"HKEY_CLASSES_ROOT\Directory\shell\ClaudeGui", "Icon", exePath);
            Microsoft.Win32.Registry.SetValue(@"HKEY_CLASSES_ROOT\Directory\shell\ClaudeGui\command", "",
                $"\"{exePath}\" --add-project \"%1\"");

            Microsoft.Win32.Registry.SetValue(@"HKEY_CLASSES_ROOT\Directory\Background\shell\ClaudeGui", "", menuName);
            Microsoft.Win32.Registry.SetValue(@"HKEY_CLASSES_ROOT\Directory\Background\shell\ClaudeGui", "Icon", exePath);
            Microsoft.Win32.Registry.SetValue(@"HKEY_CLASSES_ROOT\Directory\Background\shell\ClaudeGui\command", "",
                $"\"{exePath}\" --add-project \"%V\"");

            Logger.Info("右键菜单已安装");
        }
        catch (Exception ex)
        {
            Logger.Warn($"右键菜单安装失败（可能需要管理员权限）: {ex.Message}");
        }
    }

    // ============ 工具 ============

    private static void SetGreen(TextBlock icon, TextBlock status, string text)
    {
        icon.Text = "✓"; icon.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4c, 0xaf, 0x50));
        status.Text = text; status.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4c, 0xaf, 0x50));
    }

    private static readonly SolidColorBrush GrayBrush = new(System.Windows.Media.Color.FromRgb(0x66, 0x66, 0x66));
    private static readonly SolidColorBrush DarkGrayBrush = new(System.Windows.Media.Color.FromRgb(0x44, 0x44, 0x44));

    private static string? GetConfig(string name)
    {
        var val = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process)
               ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
        if (!string.IsNullOrWhiteSpace(val)) return val;
        var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
        if (!File.Exists(settingsPath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (doc.RootElement.TryGetProperty("env", out var env) && env.TryGetProperty(name, out var v))
                return v.GetString();
        }
        catch { }
        return null;
    }

    private static string? FindPortableNodeDir(string exeDir)
    {
        var d = Path.Combine(exeDir, "nodejs"); if (!Directory.Exists(d)) return null;
        try
        {
            foreach (var s in Directory.GetDirectories(d, "node-v*-win-x64"))
                if (File.Exists(Path.Combine(s, "node.exe"))) return s;
            if (File.Exists(Path.Combine(d, "node.exe"))) return d;
        }
        catch { }
        return null;
    }

    private static async Task<string> RunShellAsync(string cmd, int timeoutMs, string? extraPath = null)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c {cmd}")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            if (extraPath != null)
            {
                var sp = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "";
                var up = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
                psi.Environment["PATH"] = $"{extraPath};{up};{sp}";
            }
            using var proc = Process.Start(psi); if (proc == null) return "";
            var read = proc.StandardOutput.ReadToEndAsync();
            if (await Task.WhenAny(read, Task.Delay(timeoutMs)) == read) { await proc.WaitForExitAsync(); return read.Result; }
            try { proc.Kill(true); } catch { } return "";
        }
        catch { return ""; }
    }

    private static async Task<string> RunAndReadAsync(string file, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c {file} {args}")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var proc = Process.Start(psi); if (proc == null) return "";
            var read = proc.StandardOutput.ReadToEndAsync();
            if (await Task.WhenAny(read, Task.Delay(timeoutMs)) == read) { await proc.WaitForExitAsync(); return read.Result; }
            try { proc.Kill(true); } catch { } return "";
        }
        catch { return ""; }
    }
}
