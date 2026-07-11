using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace ClaudeGui;

public partial class TerminalControl : System.Windows.Controls.UserControl
{
    private Process? _process;
    private string? _sessionId;
    private string _currentDir = "";
    private bool _isRunning;
    private Paragraph? _currentPara;
    private Run? _currentRun;
    private string _accumulated = "";
    private int _accumulatedLen;                           // 修复 P2：O(1) 去重
    private const int MaxBlocks = 200;
    private List<(string role, string content)>? _fullSnapshot;
    private int _snapshotPos;
    private int _sessionInputTokens, _sessionOutputTokens;
    private decimal _sessionCost;
    private const int InitialShow = 40;
    private const int LoadMoreCount = 20;

    private static readonly SolidColorBrush BrushSystem = new(System.Windows.Media.Color.FromRgb(0x88, 0x92, 0xb0));
    private static readonly SolidColorBrush BrushError = new(System.Windows.Media.Color.FromRgb(0xff, 0x6b, 0x6b));
    private static readonly SolidColorBrush BrushAccent = new(System.Windows.Media.Color.FromRgb(0xff, 0xf0, 0x60));
    private static readonly SolidColorBrush BrushUserBg = new(System.Windows.Media.Color.FromRgb(0x1e, 0x1e, 0x1e));
    private static readonly SolidColorBrush BrushNormal = new(System.Windows.Media.Color.FromRgb(0xcc, 0xcc, 0xcc));

    // 修复 P1：环境变量只查一次，缓存
    private static readonly Dictionary<string, string> _envCache = new();
    private static readonly string[] _envNames = { "ANTHROPIC_API_KEY","ANTHROPIC_AUTH_TOKEN","ANTHROPIC_BASE_URL",
        "ANTHROPIC_MODEL","ANTHROPIC_DEFAULT_OPUS_MODEL","ANTHROPIC_DEFAULT_SONNET_MODEL",
        "ANTHROPIC_DEFAULT_HAIKU_MODEL","ANTHROPIC_DEFAULT_FABLE_MODEL" };

    // ===== 技能/提示模板 =====

    private static readonly (string Label, string Prompt)[] _skills =
    {
        ("📊 分析项目结构", "请分析当前项目的目录结构和技术栈"),
        ("🔍 全面代码审计", "分析程序漏洞、内存泄漏、逻辑问题。第一步只提问题列出风险等级，第二步沟通后修复。覆盖致命错误/安全/泄漏/逻辑/性能/冗余六个层级。"),
        ("📝 代码审查", "请审查以下代码，指出改进点"),
        ("⚡ 性能优化建议", "请分析代码性能瓶颈，给出优化方案"),
        ("🔒 安全检查", "请检查代码中的安全漏洞：注入风险、敏感泄露、权限越界"),
        ("📖 解释代码逻辑", "请解释以下代码的功能和逻辑"),
        ("🔧 重构建议", "请对以下代码给出重构建议"),
        ("📄 生成文档", "请为这个项目生成README文档"),
        ("💬 添加注释", "请为代码添加详细的中文注释"),
        ("🧪 编写单元测试", "请为以下代码编写单元测试"),
    };

    // 中文标签 → 英文原文
    private static readonly (string Label, string EnText)[] _tips =
    {
        ("逐步推理并解释", "think step by step and explain your reasoning"),
        ("自检纠错复核答案", "double check your answer for errors"),
        ("最大推理努力", "effort: max"),
        ("组建专家团队协作", "create a team of 3 experts to solve this problem"),
        ("主动提问不盲猜", "ask me clarifying questions if anything is unclear"),
        ("简洁回答去废话", "be extremely concise and to the point"),
        ("列出隐含假设", "list all the assumptions you are making"),
        ("记入长期记忆", "save this to long-term memory"),
        ("忽略无关上下文", "forget everything except the code I'm about to share"),
        ("并行子代理执行", "run this task in parallel with 3 subagents"),
    };

    public TerminalControl()
    {
        InitializeComponent();

        // 填充下拉框
        CmbSkills.Items.Clear();
        CmbSkills.Items.Add(new ComboBoxItem { Content = "⚡ 快捷指令", Tag = "", IsSelected = true });
        foreach (var s in _skills)
            CmbSkills.Items.Add(new ComboBoxItem { Content = s.Label, Tag = s.Prompt, ToolTip = s.Prompt });

        CmbTips.Items.Clear();
        CmbTips.Items.Add(new ComboBoxItem { Content = "💡 提示技巧", Tag = "", IsSelected = true });
        foreach (var t in _tips)
            CmbTips.Items.Add(new ComboBoxItem { Content = t.Label, Tag = t.EnText, ToolTip = t.EnText });

        OutputBox.Loaded += (_, _) =>
        {
            var sv = GetVisualChild<ScrollViewer>(OutputBox);
            if (sv != null) sv.ScrollChanged += OnScrollChanged;
        };
    }

    private void Skill_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (CmbSkills.SelectedItem is ComboBoxItem item && item.Tag is string prompt && prompt != "")
        {
            InputBox.Text = prompt;
            InputBox.Focus();
            InputBox.CaretIndex = InputBox.Text.Length;
        }
        CmbSkills.SelectedIndex = 0; // 重置回标题
    }

    private void Tip_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (CmbTips.SelectedItem is ComboBoxItem item && item.Tag is string tip && tip != "")
        {
            var pos = InputBox.CaretIndex;
            var prefix = string.IsNullOrEmpty(InputBox.Text) || InputBox.Text.EndsWith("\n") ? "" : "\n";
            InputBox.Text = InputBox.Text.Insert(pos, prefix + tip + "\n");
            InputBox.CaretIndex = pos + prefix.Length + tip.Length + 1;
            InputBox.Focus();
        }
        CmbTips.SelectedIndex = 0;
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange < 0 && e.VerticalOffset < 50 && _snapshotPos > 0)
            ShowMoreSnapshot(LoadMoreCount);
    }

    // ===== 启动会话 =====

    public void StartSession(string workDir, string prompt, string? claudeOverride = null)
    {
        StopProcess();
        _currentDir = workDir;
        _isRunning = true;
        TxtPlaceholder.Visibility = Visibility.Collapsed;
        NewOutputParagraph();

        var userPara = new Paragraph(new Run($"> {prompt}\n"))
        {
            Foreground = BrushAccent, Background = BrushUserBg,
            Margin = new Thickness(0, 2, 0, 6), LineHeight = 20, Padding = new Thickness(8, 4, 8, 4)
        };
        OutputBox.Document.Blocks.Add(userPara);
        NewOutputParagraph();

        var isFirstUse = !Directory.Exists(Path.Combine(workDir, ".claude"));
        var args = new StringBuilder();
        if (!isFirstUse)
        {
            args.Append("--continue --permission-mode bypassPermissions");
            if (!string.IsNullOrWhiteSpace(prompt)) args.Append($" -p \"{EscapeArg(prompt)}\"");
        }
        else
        {
            args.Append($"--permission-mode bypassPermissions -p \"{EscapeArg(BuildInitPrompt(prompt, workDir))}\"");
        }
        args.Append(" --output-format stream-json --verbose");
        if (!string.IsNullOrEmpty(_sessionId)) args.Append($" --resume {_sessionId}");

        var claudeExe = claudeOverride ?? "claude";
        var psi = new ProcessStartInfo(claudeExe)
        {
            Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workDir,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };

        // 修复 P1：从缓存读取环境变量
        foreach (var name in _envNames)
        {
            if (!_envCache.TryGetValue(name, out var val))
            {
                val = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process)
                   ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
                   ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine) ?? "";
                _envCache[name] = val;
            }
            if (!string.IsNullOrWhiteSpace(val)) psi.Environment[name] = val;
        }

        Logger.Info($"启动: claude {psi.Arguments}");
        _process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 claude");
        _process.StandardInput.Close();
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => Dispatcher.BeginInvoke(OnProcessExited);
        UpdateUIState();
        _ = Task.Run(ReadOutputAsync);
    }

    // ===== 读取输出 =====

    private async Task ReadOutputAsync()
    {
        if (_process == null) return;
        try { await Task.WhenAll(ReadStreamAsync(_process.StandardOutput, false), ReadStreamAsync(_process.StandardError, true)); }
        catch (Exception ex) { Logger.Error("ReadOutput异常", ex); }
    }

    private async Task ReadStreamAsync(StreamReader reader, bool isStderr)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!isStderr && line.Contains("thinking_tokens")) continue;
            var captured = line;
            Dispatcher.Invoke(() => ProcessLine(captured, isStderr));
        }
    }

    private void ProcessLine(string line, bool isStderr)
    {
        if (isStderr) { AppendText($"  {line}\n", BrushSystem); return; }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (_sessionId == null && root.TryGetProperty("session_id", out var sid)) _sessionId = sid.GetString();

            switch (type)
            {
                case "assistant":
                    var content = root.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var ct) ? ct : default;
                    AppendStreamText(ExtractContentText(content));
                    break;
                case "result":
                    NewOutputParagraph();
                    // 提取 Token/费用统计（B: Token 追踪）
                    if (root.TryGetProperty("usage", out var usage))
                    {
                        _sessionInputTokens += usage.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0;
                        _sessionOutputTokens += usage.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0;
                        if (usage.TryGetProperty("total_cost_usd", out var tc) && tc.TryGetDecimal(out var cost))
                            _sessionCost += cost;
                    }
                    UpdateStatsDisplay();
                    break;
                case "system":
                    if (root.TryGetProperty("subtype", out var st) && st.GetString() == "thinking_tokens") break;
                    var sc = root.TryGetProperty("content", out var c) ? c.GetString() : "";
                    if (!string.IsNullOrWhiteSpace(sc)) AppendText($"  {sc}", BrushSystem);
                    break;
                case "done":
                    NewOutputParagraph();
                    break;
            }
        }
        catch (JsonException) { AppendText(line + "\n", BrushNormal); }
    }

    // ===== 流式文本 =====

    private void AppendStreamText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        text = RegexCompressNewlines(text);

        // 修复 P2：O(1) 长度切片去重
        if (_accumulatedLen > 0 && text.Length > _accumulatedLen)
            text = text[_accumulatedLen..];
        else if (_accumulatedLen > 0) return;

        if (_currentRun == null || _currentPara == null)
        {
            _currentPara = new Paragraph { Margin = new Thickness(0), LineHeight = 18 };
            _currentRun = new Run(text); _currentPara.Inlines.Add(_currentRun);
            OutputBox.Document.Blocks.Add(_currentPara);
        }
        else _currentRun.Text += text;

        _accumulated += text; _accumulatedLen = _accumulated.Length;
        if (OutputBox.Document.Blocks.Count > MaxBlocks * 2) TrimOldBlocks();
        OutputBox.ScrollToEnd();
    }

    private static string RegexCompressNewlines(string input) => Regex.Replace(input, @"\n{3,}", "\n");

    private void NewOutputParagraph() { _currentPara = null; _currentRun = null; _accumulated = ""; _accumulatedLen = 0; }

    private void AppendText(string text, SolidColorBrush color)
    {
        OutputBox.Document.Blocks.Add(new Paragraph(new Run(text)) { Foreground = color, Margin = new Thickness(0), LineHeight = 18 });
        NewOutputParagraph();
        OutputBox.ScrollToEnd();
    }

    // ===== 历史快照 =====

    public void LoadFullSnapshot(List<(string role, string content)> allMessages)
    {
        OutputBox.Document.Blocks.Clear();
        _fullSnapshot = allMessages;
        _snapshotPos = allMessages.Count;
        ShowMoreSnapshot(InitialShow);
    }

    private void ShowMoreSnapshot(int count)
    {
        if (_fullSnapshot == null || _snapshotPos <= 0) return;
        var start = Math.Max(0, _snapshotPos - count);
        var batch = _fullSnapshot.Skip(start).Take(_snapshotPos - start).ToList();
        _snapshotPos = start;
        var scrollPos = OutputBox.VerticalOffset;

        for (int i = batch.Count - 1; i >= 0; i--)
        {
            var msg = batch[i];
            var prefix = msg.role == "user" ? "> " : "";
            var para = new Paragraph(new Run($"{prefix}{msg.content}\n"))
            {
                Foreground = msg.role == "user" ? BrushAccent : BrushNormal,
                Margin = new Thickness(0, 0, 0, 4), LineHeight = 20
            };
            if (msg.role == "user") { para.Background = BrushUserBg; para.Padding = new Thickness(8, 4, 8, 4); para.Margin = new Thickness(0, 2, 0, 6); }
            if (OutputBox.Document.Blocks.FirstBlock != null)
                OutputBox.Document.Blocks.InsertBefore(OutputBox.Document.Blocks.FirstBlock, para);
            else OutputBox.Document.Blocks.Add(para);
        }

        Dispatcher.BeginInvoke(new Action(() =>
        { OutputBox.UpdateLayout(); OutputBox.ScrollToVerticalOffset(scrollPos + 100); }),
            System.Windows.Threading.DispatcherPriority.Background);

        if (_snapshotPos > 0)
        {
            var hint = new Paragraph(new Run($"▲ 向上滚动加载更多（剩余 {_snapshotPos} 条）"))
            { Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55, 0x55, 0x55)), FontSize = 11 };
            if (OutputBox.Document.Blocks.FirstBlock != null)
                OutputBox.Document.Blocks.InsertBefore(OutputBox.Document.Blocks.FirstBlock, hint);
            else OutputBox.Document.Blocks.Add(hint);
        }
    }

    // ===== 裁剪 =====

    private void TrimOldBlocks()
    {
        var blocks = OutputBox.Document.Blocks;
        while (blocks.Count > MaxBlocks) blocks.Remove(blocks.FirstBlock);
    }

    // ===== 进程生命周期 =====

    private void OnProcessExited()
    {
        _isRunning = false;
        UpdateUIState();
        TrimOldBlocks();
        // 修复 F1/L3：只在 OnProcessExited 中 Dispose
        var p = _process; _process = null;
        _ = Task.Run(() => { try { p?.Dispose(); } catch { } });
    }

    private void StopProcess()
    {
        // 修复 F1/L3：只 Kill，不 Dispose（Dispose 统一在 OnProcessExited 中）
        if (_process != null && !_process.HasExited)
        {
            try { _process.Kill(true); } catch { }
            _process = null; // 不 Dispose，由 Exited 事件 → OnProcessExited 处理
        }
        _isRunning = false;
    }

    private void UpdateUIState()
    {
        BtnSend.IsEnabled = !_isRunning; InputBox.IsEnabled = !_isRunning;
        BtnStop.Visibility = _isRunning ? Visibility.Visible : Visibility.Collapsed;
        BtnSend.Content = _isRunning ? "处理中..." : "发送";
        if (!_isRunning && !string.IsNullOrWhiteSpace(_currentDir)) BtnSend.Visibility = Visibility.Visible;
    }

    // ===== 用户输入 =====

    private void InputBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; SendMessage(); }
        else if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; ClearOutput(); }
        else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; ExportHtml(); }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; SearchInOutput(); }
    }

    private void UpdateStatsDisplay()
    {
        if (_sessionCost == 0 && _sessionInputTokens == 0) { TxtStats.Text = ""; return; }
        var costStr = _sessionCost > 0 ? $"$ {_sessionCost:F4}" : "";
        var tokenStr = $"{_sessionInputTokens / 1000}K↑ {_sessionOutputTokens / 1000}K↓";
        TxtStats.Text = _sessionCost > 0 ? $"{costStr} · {tokenStr}" : tokenStr;
    }

    public void ClearOutput()
    {
        OutputBox.Document.Blocks.Clear();
        _fullSnapshot = null; _snapshotPos = 0;
        TxtPlaceholder.Visibility = Visibility.Visible;
    }

    private async void ExportHtml()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "HTML|*.html", FileName = $"对话导出_{DateTime.Now:yyyyMMddHHmm}.html" };
        if (dlg.ShowDialog() != true) return;

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>");
        sb.AppendLine("body{font-family:'Segoe UI',sans-serif;background:#1a1a2e;color:#ccc;max-width:900px;margin:auto;padding:20px}");
        sb.AppendLine(".user{color:#fff060;background:#1e1e1e;padding:8px 12px;border-radius:8px;margin:8px 0}");
        sb.AppendLine(".ai{color:#ccc;margin:8px 0;padding:4px 0}");
        sb.AppendLine("pre{background:#0c0c0c;padding:12px;border-radius:6px;overflow-x:auto}");
        sb.AppendLine("</style></head><body><h2>Claude 对话导出</h2>");

        foreach (var block in OutputBox.Document.Blocks)
        {
            if (block is Paragraph p)
            {
                var text = new TextRange(p.ContentStart, p.ContentEnd).Text;
                if (text.StartsWith("> ")) sb.AppendLine($"<div class=\"user\">{System.Net.WebUtility.HtmlEncode(text[2..])}</div>");
                else if (!text.StartsWith("─")) sb.AppendLine($"<div class=\"ai\">{System.Net.WebUtility.HtmlEncode(text)}</div>");
            }
        }
        sb.AppendLine("</body></html>");
        await File.WriteAllTextAsync(dlg.FileName, sb.ToString());
    }

    private void SearchInOutput()
    {
        // 简化搜索：焦点移到输出区，用户 Ctrl+F 触发浏览器式查找
        OutputBox.Focus();
    }

    private void Send_Click(object sender, RoutedEventArgs e) => SendMessage();
    private void Stop_Click(object sender, RoutedEventArgs e) { StopProcess(); UpdateUIState(); }
    private void InputBox_TextChanged(object sender, TextChangedEventArgs e) { BtnSend.IsEnabled = !_isRunning && !string.IsNullOrWhiteSpace(InputBox.Text); }

    private void SendMessage()
    {
        var prompt = InputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt) || _isRunning) return;
        InputBox.Text = "";
        try { StartSession(_currentDir, prompt, ClaudePath); } catch (Exception ex) { AppendText($"启动失败: {ex.Message}\n", BrushError); }
    }

    // ===== 公共 =====

    public string? ClaudePath { get; set; }
    public void Activate(string workDir) { _currentDir = workDir; TxtPlaceholder.Visibility = Visibility.Collapsed; if (!_isRunning) { InputBox.IsEnabled = true; BtnSend.IsEnabled = !string.IsNullOrWhiteSpace(InputBox.Text); BtnSend.Visibility = Visibility.Visible; CmbSkills.IsEnabled = true; CmbSkills.Visibility = Visibility.Visible; CmbTips.IsEnabled = true; CmbTips.Visibility = Visibility.Visible; } InputBox.Focus(); }
    public void ScrollToEnd() { Dispatcher.BeginInvoke(new Action(() => { OutputBox.UpdateLayout(); OutputBox.ScrollToEnd(); }), System.Windows.Threading.DispatcherPriority.Background); }
    // ===== 拖拽文件 =====
    private void OnDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) && e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files)
        {
            foreach (var f in files)
            {
                InputBox.Text += (InputBox.Text.Length > 0 && !InputBox.Text.EndsWith("\n") ? "\n" : "") + f + "\n";
            }
            InputBox.Focus();
            InputBox.CaretIndex = InputBox.Text.Length;
        }
    }

    public void Dispose() { if (_process != null) { try { _process.Kill(true); } catch { } _process.Dispose(); } }

    // ===== 静态辅助 =====

    private static string ExtractContentText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var block in content.EnumerateArray())
            {
                var bt = block.TryGetProperty("type", out var t) ? t.GetString() : "";
                if (bt == "text" && block.TryGetProperty("text", out var txt)) sb.Append(txt.GetString());
                else if (bt == "thinking" && block.TryGetProperty("thinking", out var th)) sb.Append($"\n[思考] {th.GetString()}\n");
                else if (bt == "tool_use") { var n = block.TryGetProperty("name", out var bn) ? bn.GetString() ?? "tool" : "tool"; sb.Append($"\n[{n}]\n"); }
            }
            return sb.ToString();
        }
        return "";
    }

    private static string EscapeArg(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return "";
        var sb = new StringBuilder();
        foreach (char c in arg) sb.Append(c is '\r' or '\n' or '\0' ? ' ' : c);
        var clean = sb.ToString();
        if (clean.Length > 8000) clean = clean[..8000];
        return clean.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static string BuildInitPrompt(string userPrompt, string workDir) =>
        $"""## 项目初始化 这是你第一次在当前项目目录下工作。请浏览项目结构并分析代码，在 .claude/CLAUDE.md 中记录项目信息。今后的所有记忆保存在 .claude/ 目录下。---用户的需求：{userPrompt}""";

    private static T? GetVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var result = GetVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }
}
