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
    private Paragraph? _currentPara; // 当前累积文本的段落
    private Run? _currentRun;        // 累积文本的 Run
    private string _accumulated = "";
    private const int MaxBlocks = 200;
    private List<(string role, string content)>? _fullSnapshot; // 完整快照
    private int _snapshotPos; // 已显示到的位置（从末尾倒数） // 用于去重判断

    private static readonly SolidColorBrush BrushSystem = new(System.Windows.Media.Color.FromRgb(0x88, 0x92, 0xb0));
    private static readonly SolidColorBrush BrushError = new(System.Windows.Media.Color.FromRgb(0xff, 0x6b, 0x6b));
    private static readonly SolidColorBrush BrushAccent = new(System.Windows.Media.Color.FromRgb(0xff, 0xf0, 0x60)); // 亮黄
    private static readonly SolidColorBrush BrushUserBg = new(System.Windows.Media.Color.FromRgb(0x1a, 0x3d, 0x1a)); // 深绿底
    private static readonly SolidColorBrush BrushNormal = new(System.Windows.Media.Color.FromRgb(0xcc, 0xcc, 0xcc));

    public TerminalControl()
    {
        InitializeComponent();
        OutputBox.Loaded += (_, _) =>
        {
            var sv = GetVisualChild<ScrollViewer>(OutputBox);
            if (sv != null) sv.ScrollChanged += OnScrollChanged;
        };
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange < 0 && e.VerticalOffset < 50 && _snapshotPos > 0)
        {
            ShowMoreSnapshot(LoadMoreCount);
        }
    }

    public void StartSession(string workDir, string prompt, string? claudeOverride = null)
    {
        StopProcess();
        _currentDir = workDir;
        _isRunning = true;

        // 隐藏占位文字
        TxtPlaceholder.Visibility = Visibility.Collapsed;

        // 启动新段落（不清理旧内容，保留历史）
        NewOutputParagraph();
        var userPara = new Paragraph(new Run($"> {prompt}\n"))
        {
            Foreground = BrushAccent,
            Background = BrushUserBg,
            Margin = new Thickness(0, 2, 0, 6),
            LineHeight = 20,
            Padding = new Thickness(8, 4, 8, 4)
        };
        OutputBox.Document.Blocks.Add(userPara);
        NewOutputParagraph();

        var isFirstUse = !Directory.Exists(Path.Combine(workDir, ".claude"));
        var args = new StringBuilder();

        if (!isFirstUse)
        {
            args.Append("--continue --permission-mode bypassPermissions");
            if (!string.IsNullOrWhiteSpace(prompt))
                args.Append($" -p \"{EscapeArg(prompt)}\"");
        }
        else
        {
            // 修复：首次使用也要绕过权限确认
            var initPrompt = BuildInitPrompt(prompt, workDir);
            args.Append($"--permission-mode bypassPermissions -p \"{EscapeArg(initPrompt)}\"");
        }

        args.Append(" --output-format stream-json --verbose");

        if (!string.IsNullOrEmpty(_sessionId))
        {
            args.Append($" --resume {_sessionId}");
        }

        var claudeExe = claudeOverride ?? "claude";
        var psi = new ProcessStartInfo(claudeExe)
        {
            Arguments = args.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workDir,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // 显式传递 API 环境变量（解决子进程继承问题）
        CopyEnv(psi, "ANTHROPIC_API_KEY");
        CopyEnv(psi, "ANTHROPIC_AUTH_TOKEN");
        CopyEnv(psi, "ANTHROPIC_BASE_URL");
        CopyEnv(psi, "ANTHROPIC_MODEL");
        CopyEnv(psi, "ANTHROPIC_DEFAULT_OPUS_MODEL");
        CopyEnv(psi, "ANTHROPIC_DEFAULT_SONNET_MODEL");
        CopyEnv(psi, "ANTHROPIC_DEFAULT_HAIKU_MODEL");
        CopyEnv(psi, "ANTHROPIC_DEFAULT_FABLE_MODEL");

        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY", EnvironmentVariableTarget.Process)
                  ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY", EnvironmentVariableTarget.User);
        var apiUrl = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL", EnvironmentVariableTarget.Process)
                  ?? Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL", EnvironmentVariableTarget.User);
        Logger.Info($"启动: claude {psi.Arguments}");
        Logger.Info($"API URL: {apiUrl ?? "(未设置，默认 api.anthropic.com)"}");
        Logger.Info($"API Key: {(string.IsNullOrEmpty(apiKey) ? "(未设置)" : apiKey[..Math.Min(8, apiKey.Length)] + "...")}");
        _process = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 claude");

        _process.StandardInput.Close();
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            Logger.Info("TRACE: Exited event fired");
            // 不在 UI 线程等待，改用 BeginInvoke
            Dispatcher.BeginInvoke(() =>
            {
                Logger.Info("TRACE: BeginInvoke OnProcessExited START");
                OnProcessExited();
                Logger.Info("TRACE: BeginInvoke OnProcessExited END");
            });
        };

        UpdateUIState();
        _ = Task.Run(() => ReadOutputAsync());
    }

    private async Task ReadOutputAsync()
    {
        if (_process == null) return;
        try
        {
            var stdoutTask = ReadStreamAsync(_process.StandardOutput, false);
            var stderrTask = ReadStreamAsync(_process.StandardError, true);
            await Task.WhenAll(stdoutTask, stderrTask);
        }
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
        Logger.Info("TRACE: ReadStreamAsync END (stream closed)");
    }

    private int _rawCount;
    private void ProcessLine(string line, bool isStderr)
    {
        // thinking_tokens 不记日志（量太大，每秒数百条）
        if (!line.Contains("thinking_tokens"))
        {
            var isError = line.Contains("error") || line.Contains("api_retry") || line.Contains("authentication");
            var logLine = isError ? line : line[..Math.Min(line.Length, 200)];
            Logger.Info(isError ? $"RAW: {logLine}" : $"raw: {logLine}");
        }
        else if (++_rawCount % 100 == 0)
        {
            Logger.Info($"thinking_tokens: {_rawCount} lines skipped");
        }

        if (isStderr)
        {
            AppendText($"  {line}\n", BrushSystem);
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            if (_sessionId == null && root.TryGetProperty("session_id", out var sid))
                _sessionId = sid.GetString();

            switch (type)
            {
                case "assistant":
                    var content = root.TryGetProperty("message", out var msg)
                        && msg.TryGetProperty("content", out var ct) ? ct : default;
                    AppendStreamText(ExtractContentText(content));
                    break;

                case "result":
                    Logger.Info("TRACE: result START");
                    FinalizeStreamingOutput();
                    Logger.Info("TRACE: result FinalizeStreamingOutput done");
                    if (root.TryGetProperty("result", out var res))
                    {
                        var rt = res.GetString();
                        Logger.Info($"TRACE: result text len={rt?.Length ?? 0}");
                        if (!string.IsNullOrWhiteSpace(rt) && rt.Length < 200)
                            AppendText(rt, BrushNormal);
                    }
                    Logger.Info("TRACE: result END, waiting for exit");
                    break;

                case "system":
                    var subtype = root.TryGetProperty("subtype", out var st) ? st.GetString() : "";
                    if (subtype == "thinking_tokens") break;
                    var sc = root.TryGetProperty("content", out var c) ? c.GetString() : "";
                    if (!string.IsNullOrWhiteSpace(sc))
                        AppendText($"  {sc}", BrushSystem);
                    break;

                case "done":
                    FinalizeStreamingOutput();
                    break;
            }
        }
        catch (JsonException)
        {
            AppendText(line + "\n", BrushNormal);
        }
    }

    /// <summary>
    /// 追加流式文本到当前段落
    /// </summary>
    private void AppendStreamText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        // 压缩连续空行：3+ 个连续换行 → 1 个换行
        text = RegexCompressNewlines(text);

        // 去重：新文本以累积文本开头时只取增量
        if (!string.IsNullOrEmpty(_accumulated) && text.StartsWith(_accumulated))
        {
            var delta = text[_accumulated.Length..];
            if (string.IsNullOrEmpty(delta)) return;
            text = delta;
        }

        if (_currentRun == null || _currentPara == null)
        {
            _currentPara = new Paragraph { Margin = new Thickness(0), LineHeight = 18 };
            _currentRun = new Run(text);
            _currentPara.Inlines.Add(_currentRun);
            OutputBox.Document.Blocks.Add(_currentPara);
        }
        else
        {
            _currentRun.Text += text;
        }

        _accumulated += text;
        if (OutputBox.Document.Blocks.Count > MaxBlocks * 2) TrimOldBlocks();
        OutputBox.ScrollToEnd();
    }

    private static string RegexCompressNewlines(string input)
    {
        // 3+ 连续换行 → 1 换行；首尾空白保留（增量追加需要）
        return Regex.Replace(input, @"\n{3,}", "\n");
    }

    /// <summary>
    /// 替换当前段落内容（用于 result 类型覆盖流式内容）
    /// </summary>
    private void ReplaceCurrentPara(string text)
    {
        text = RegexCompressNewlines(text);

        if (_currentPara != null)
        {
            _currentPara.Inlines.Clear();
            _currentRun = new Run(text);
            _currentPara.Inlines.Add(_currentRun);
        }
        else
        {
            _currentRun = new Run(text);
            _currentPara = new Paragraph { Margin = new Thickness(0), LineHeight = 18 };
            _currentPara.Inlines.Add(_currentRun);
            OutputBox.Document.Blocks.Add(_currentPara);
        }
        _accumulated = text;
        OutputBox.ScrollToEnd();
    }

    /// <summary>
    /// 开始新输出段落（消息切换时）
    /// </summary>
    private void NewOutputParagraph()
    {
        _currentPara = null;
        _currentRun = null;
        _accumulated = "";
    }

    /// <summary>
    /// 流式输出完成后，用 markdown 渲染替换纯文本段落
    /// </summary>
    private void FinalizeStreamingOutput()
    {
        if (_currentPara == null || string.IsNullOrWhiteSpace(_accumulated))
        {
            NewOutputParagraph();
            return;
        }

        var markdown = _accumulated;
        var oldPara = _currentPara;

        // 移除旧的纯文本段落
        if (OutputBox.Document.Blocks.Contains(oldPara))
            OutputBox.Document.Blocks.Remove(oldPara);

        // 用 markdown 渲染追加到末尾
        var renderedBlocks = MarkdownRenderer.Render(markdown);
        foreach (var block in renderedBlocks)
            OutputBox.Document.Blocks.Add(block);

        // 分隔线
        var sep = new Paragraph(new Run("─".PadRight(40, '─')))
        {
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x33, 0x44, 0x55)),
            Margin = new Thickness(0, 6, 0, 10)
        };
        OutputBox.Document.Blocks.Add(sep);

        NewOutputParagraph();
        OutputBox.ScrollToEnd();
    }

    private void AppendText(string text, SolidColorBrush color)
    {
        var para = new Paragraph(new Run(text))
        {
            Foreground = color,
            Margin = new Thickness(0),
            LineHeight = 18
        };
        OutputBox.Document.Blocks.Add(para);
        NewOutputParagraph(); // 后续流式内容用新段落
        OutputBox.ScrollToEnd();
    }

    private const int InitialShow = 40; // 首次显示 40 条
    private const int LoadMoreCount = 20; // 每次上滚加载 20 条

    /// <summary>
    /// 加载完整历史快照，只显示尾部，上滚时动态加载
    /// </summary>
    public void LoadFullSnapshot(List<(string role, string content)> allMessages)
    {
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

        // 保存当前滚动位置
        var scrollPos = OutputBox.VerticalOffset;

        // 在文档开头插入旧消息
        for (int i = batch.Count - 1; i >= 0; i--)
        {
            var msg = batch[i];
            var color = msg.role == "user"
                ? System.Windows.Media.Color.FromRgb(0xff, 0xf0, 0x60)
                : System.Windows.Media.Color.FromRgb(0xcc, 0xcc, 0xcc);
            var prefix = msg.role == "user" ? "> " : "";
            var para = new Paragraph(new Run($"{prefix}{msg.content}\n"))
            {
                Foreground = new SolidColorBrush(color),
                Margin = new Thickness(0, 0, 0, 4),
                LineHeight = 20
            };
            if (msg.role == "user")
            {
                para.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1a, 0x3d, 0x1a));
                para.Padding = new Thickness(8, 4, 8, 4);
                para.Margin = new Thickness(0, 2, 0, 6);
            }
            OutputBox.Document.Blocks.InsertBefore(OutputBox.Document.Blocks.FirstBlock, para);
        }

        // 恢复滚动位置（补偿新增内容的高度）
        Dispatcher.BeginInvoke(new Action(() =>
        {
            OutputBox.UpdateLayout();
            OutputBox.ScrollToVerticalOffset(scrollPos + 100); // 补偿新插入的偏移
        }), System.Windows.Threading.DispatcherPriority.Background);

        // 如果还有更多
        if (_snapshotPos > 0)
        {
            var hint = new Paragraph(new Run($"▲ 向上滚动加载更多（剩余 {_snapshotPos} 条）"))
            {
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55, 0x55, 0x55)),
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 2)
            };
            OutputBox.Document.Blocks.InsertBefore(OutputBox.Document.Blocks.FirstBlock, hint);
        }
    }

    public void AppendSnapshot(string role, string content, System.Windows.Media.Color color)
    {
        TxtPlaceholder.Visibility = Visibility.Collapsed;

        if (role == "user")
        {
            var para = new Paragraph(new Run($"> {content}\n"))
            {
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xff, 0xf0, 0x60)),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1a, 0x3d, 0x1a)),
                Margin = new Thickness(0, 2, 0, 6),
                LineHeight = 20,
                Padding = new Thickness(8, 4, 8, 4)
            };
            OutputBox.Document.Blocks.Add(para);
        }
        else if (role == "assistant")
        {
            // AI 消息：完整 markdown 渲染
            var blocks = MarkdownRenderer.Render(content);
            foreach (var block in blocks)
                OutputBox.Document.Blocks.Add(block);

            // 分隔线
            OutputBox.Document.Blocks.Add(new Paragraph(new Run("─".PadRight(40, '─')))
            {
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x33, 0x44, 0x55)),
                Margin = new Thickness(0, 6, 0, 10)
            });
        }
        else
        {
            // 系统消息：灰色
            var para = new Paragraph(new Run($"  {content}"))
            {
                Foreground = new SolidColorBrush(color),
                Margin = new Thickness(0, 0, 0, 2)
            };
            OutputBox.Document.Blocks.Add(para);
        }
    }

    private void TrimOldBlocks()
    {
        var blocks = OutputBox.Document.Blocks;
        while (blocks.Count > MaxBlocks)
            blocks.Remove(blocks.FirstBlock);
        if (blocks.Count > MaxBlocks / 2)
            Logger.Info($"Trimmed blocks: {blocks.Count} remaining");
    }

    private void OnProcessExited()
    {
        Logger.Info("TRACE: OnProcessExited 1/5 _isRunning=false");
        _isRunning = false;
        Logger.Info("TRACE: OnProcessExited 2/5 UpdateUIState");
        UpdateUIState();
        Logger.Info("TRACE: OnProcessExited 3/5 check exitCode");
        TrimOldBlocks();
        if (_process?.ExitCode != 0)
            AppendText($"\n[进程退出, 代码: {_process?.ExitCode}]\n", BrushError);
        Logger.Info("TRACE: OnProcessExited 4/5 Dispose (background)");
        var p = _process;
        _process = null;
        _ = Task.Run(() => { try { p?.Dispose(); } catch { } Logger.Info("TRACE: Dispose done in background"); });
        Logger.Info("TRACE: OnProcessExited 5/5 done");
    }

    private void InputBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Ctrl+Enter = 发送
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            SendMessage();
        }
    }

    private void Send_Click(object sender, RoutedEventArgs e) => SendMessage();
    private void Stop_Click(object sender, RoutedEventArgs e) { StopProcess(); UpdateUIState(); }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        BtnSend.IsEnabled = !_isRunning && !string.IsNullOrWhiteSpace(InputBox.Text);
    }

    private void SendMessage()
    {
        var prompt = InputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt) || _isRunning) return;

        InputBox.Text = "";
        try { StartSession(_currentDir, prompt, ClaudePath); }
        catch (Exception ex) { AppendText($"启动失败: {ex.Message}\n", BrushError); }
    }

    private void StopProcess()
    {
        if (_process != null && !_process.HasExited)
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
            _process.Dispose();
        }
        _process = null;
        _isRunning = false;
    }

    private void UpdateUIState()
    {
        BtnSend.IsEnabled = !_isRunning;
        InputBox.IsEnabled = !_isRunning;
        BtnStop.Visibility = _isRunning ? Visibility.Visible : Visibility.Collapsed;
        BtnSend.Content = _isRunning ? "处理中..." : "发送";
    }

    public string? ClaudePath { get; set; }

    public void Activate(string workDir)
    {
        _currentDir = workDir;
        if (!_isRunning)
        {
            InputBox.IsEnabled = true;
            BtnSend.IsEnabled = !string.IsNullOrWhiteSpace(InputBox.Text);
        }
        InputBox.Focus();
    }

    /// <summary>
    /// 滚动输出区到最底部
    /// </summary>
    public void ScrollToEnd()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            OutputBox.UpdateLayout();
            OutputBox.ScrollToEnd();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    // === 静态辅助 ===

    private static string ExtractContentText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? "";

        if (content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var block in content.EnumerateArray())
            {
                var bt = block.TryGetProperty("type", out var t) ? t.GetString() : "";
                if (bt == "text" && block.TryGetProperty("text", out var txt))
                    sb.Append(txt.GetString());
                else if (bt == "thinking" && block.TryGetProperty("thinking", out var th))
                    sb.Append($"\n[思考] {th.GetString()}\n");
                else if (bt == "tool_use")
                {
                    var name = block.TryGetProperty("name", out var n) ? n.GetString() ?? "tool" : "tool";
                    sb.Append($"\n[{name}]\n");
                }
            }
            return sb.ToString();
        }
        return "";
    }

    private static string EscapeArg(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return "";
        var sb = new StringBuilder();
        foreach (char c in arg)
            sb.Append(c is '\r' or '\n' or '\0' ? ' ' : c);
        var clean = sb.ToString();
        if (clean.Length > 8000) clean = clean[..8000];
        return clean.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static string BuildInitPrompt(string userPrompt, string workDir)
    {
        var dirName = Path.GetFileName(workDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return $"""
            ## 项目初始化
            这是你第一次在当前项目目录下工作。请浏览项目结构并分析代码，在 .claude/CLAUDE.md 中记录项目信息。
            今后的所有记忆保存在 .claude/ 目录下。
            ---
            用户的需求：{userPrompt}
            """;
    }

    public void Dispose() => StopProcess();

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

    private static void CopyEnv(ProcessStartInfo psi, string name)
    {
        // 三个级别全查：进程 > 用户注册表 > 系统注册表
        var val = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process)
               ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
               ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
        if (!string.IsNullOrWhiteSpace(val))
        {
            psi.Environment[name] = val;
            Logger.Info($"env {name}: {val[..Math.Min(val.Length, 30)]}...");
        }
        else
        {
            Logger.Info($"env {name}: (未设置)");
        }
    }
}
