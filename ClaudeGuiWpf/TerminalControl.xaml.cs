using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClaudeGui;

public partial class TerminalControl : System.Windows.Controls.UserControl
{
    private Process? _process;
    private string? _sessionId;
    private string _currentDir = "";
    private string _permissionMode = "bypassPermissions"; // 当前项目的工作模式
    private string? _overrideProviderName; // 本会话临时覆盖的提供商（来自快捷指令选择）
    private string _lastPrompt = ""; // 本轮发送的提示词，result 时写入聊天记录
    private bool _isRunning;
    private Paragraph? _currentPara;
    private Run? _currentRun;
    private readonly StringBuilder _accumulated = new();   // 修复 P2：StringBuilder 避免高频拼接 GC
    private int _accumulatedLen;
    private const int MaxBlocks = 200;
    private List<(string role, string content)>? _fullSnapshot;
    private int _snapshotPos;
    private int _sessionInputTokens, _sessionOutputTokens;
    private decimal _sessionCost;
    private readonly StringBuilder _thinkingHistory = new(); // 全部思考/tool_use 历史（全屏回看用）
    private Stopwatch _stopwatch = new(); // 计时：进程启动 → 各阶段耗时
    private int _failoverRetry; // failover 重试计数
    private const double ThinkingNormalHeight = 200;
    private const double ThinkingExpandedHeight = 400;
    // 交互类工具：检测到就撑大 ThinkingBox 提醒用户
    private static readonly HashSet<string> InteractiveTools = new(StringComparer.OrdinalIgnoreCase)
        { "AskUserQuestion", "question", "confirm", "input", "prompt", "clarify" };
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
        ("⚡ 性能优化建议", "请分析代码性能瓶颈，给出优化方案"),
        ("📖 解释代码逻辑", "请解释以下代码的功能和逻辑"),
        ("🔧 重构建议", "请对以下代码给出重构建议"),
        ("📄 生成文档", "请为这个项目生成README文档"),
        ("💬 添加注释", "请为代码添加详细的中文注释"),
        ("🗜️ 压缩上下文", "/compact"),
        ("📋 聊天记录速览", "history:quickview"),
        // 以下为权限模式（Tag 前缀 mode:，不替换输入框文本）
        ("⚡ 自动执行", "mode:bypassPermissions"),
        ("📋 计划模式", "mode:plan"),
        ("🛡️ 半自动确认", "mode:acceptEdits"),
        ("👆 步步确认", "mode:manual"),
    };

    // 中文标签 → 英文原文
    private static readonly (string Label, string EnText)[] _tips =
    {
        ("逐步推理并解释", "think step by step and explain your reasoning"),
        ("自检纠错复核答案", "double check your answer for errors"),
        ("低推理深度（快速响应）", "effort: low"),
        ("中等推理深度", "effort: medium"),
        ("高推理深度", "effort: high"),
        ("超高推理深度", "effort: xhigh"),
        ("最大推理深度", "effort: max"),
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

        // 填充快捷指令（前 8 个填输入框，后 4 个为权限模式）
        CmbSkills.Items.Clear();
        CmbSkills.Items.Add(new ComboBoxItem { Content = "⚡ 快捷指令", Tag = "", IsSelected = true });
        for (int i = 0; i < _skills.Length; i++)
        {
            var s = _skills[i];
            if (i == 9) // 第 10 个起插入分隔线（前 9 个指令 + 聊天记录速览）
                CmbSkills.Items.Add(new ComboBoxItem { Content = "──────────", Tag = "", IsEnabled = false });
            CmbSkills.Items.Add(new ComboBoxItem { Content = s.Label, Tag = s.Prompt, ToolTip = s.Prompt });
        }

        CmbTips.Items.Clear();
        CmbTips.Items.Add(new ComboBoxItem { Content = "💡 提示技巧", Tag = "", IsSelected = true });
        foreach (var t in _tips)
            CmbTips.Items.Add(new ComboBoxItem { Content = t.Label, Tag = t.EnText, ToolTip = t.EnText });

        OutputBox.Loaded += (_, _) =>
        {
            var sv = GetVisualChild<ScrollViewer>(OutputBox);
            if (sv != null) sv.ScrollChanged += OnScrollChanged;
        };

        // ESC 关闭思考全屏覆盖层
        ThinkingFullBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                ThinkingOverlay.Visibility = Visibility.Collapsed;
        };
    }

    private void Skill_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (CmbSkills.SelectedItem is ComboBoxItem item && item.Tag is string tag && tag != "")
        {
            if (tag.StartsWith("mode:"))
            {
                // 权限模式切换
                _permissionMode = tag[5..];
                Config?.SetPermissionModeByPath(_currentDir, _permissionMode);
                Logger.Info($"权限模式切换: {item.Content}");
            }
            else if (tag.StartsWith("provider:"))
            {
                // 临时覆盖本条消息的 API 提供商
                _overrideProviderName = tag[9..];
                Logger.Info($"提供商切换: {_overrideProviderName}");
            }
            else if (tag == "history:quickview")
            {
                // 打开聊天记录速览窗口
                var win = Window.GetWindow(this);
                if (win is MainWindow mw)
                    mw.OpenChatHistoryViewer(_currentDir);
            }
            else if (tag == "reset") // 恢复默认提供商
            {
                _overrideProviderName = null;
                Logger.Info("提供商恢复默认");
            }
            else
            {
                InputBox.Text = tag;
                InputBox.Focus();
                InputBox.CaretIndex = InputBox.Text.Length;
            }
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

    // ===== 启动会话（持久进程模式）=====

    public void StartSession(string workDir, string prompt, string? claudeOverride = null)
    {
        StopProcess();
        _currentDir = workDir;
        _isRunning = true;
        _failoverRetry = 0;
        _thinkingHistory.Clear();
        BtnThinking.Visibility = Visibility.Collapsed;
        ThinkingPanel.Visibility = Visibility.Collapsed;
        ThinkingPanel.MaxHeight = ThinkingNormalHeight;
        ThinkingPanel.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x23, 0x35, 0x54));
        ThinkingBox.FontSize = 11;
        ThinkingOverlay.Visibility = Visibility.Collapsed;
        TxtPlaceholder.Visibility = Visibility.Collapsed;
        NewOutputParagraph();

        var userPara = new Paragraph(new Run($"> {prompt}\n"))
        {
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xcc, 0xcc, 0xcc)),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x25, 0x25, 0x30)),
            Margin = new Thickness(0, 2, 0, 6), LineHeight = 20, Padding = new Thickness(8, 4, 8, 4)
        };
        OutputBox.Document.Blocks.Add(userPara);
        NewOutputParagraph();

        var isFirstUse = !Directory.Exists(Path.Combine(workDir, ".claude"));
        var args = new StringBuilder();
        if (!isFirstUse)
        {
            args.Append($"--continue --permission-mode {_permissionMode}");
            if (!string.IsNullOrWhiteSpace(prompt)) args.Append($" -p \"{EscapeArg(prompt)}\"");
        }
        else
        {
            args.Append($"--permission-mode {_permissionMode} -p \"{EscapeArg(BuildInitPrompt(prompt, workDir))}\"");
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

        // 注入提供商环境变量
        var provider = ResolveProvider();
        if (provider != null) InjectProviderEnv(psi, provider);

        // 补充系统环境变量（修复 D1：提取公共方法）
        InjectSystemEnvVars(psi);

        // 修复隐私：不记录完整命令行（含用户 prompt），仅记录关键参数
        Logger.Info($"启动: claude --continue --permission-mode {_permissionMode} [提供商={provider?.Name ?? "默认"}]");
        _firstContentReceived = false;
        _stopwatch.Restart();
        var proc = Process.Start(psi);
        if (proc == null) { _isRunning = false; UpdateUIState(); throw new InvalidOperationException("无法启动 AI 助手，请检查环境配置"); }
        _process = proc;
        _process.StandardInput.Close();
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => Dispatcher.BeginInvoke(OnProcessExited);
        UpdateUIState();
        StartWaitingAnimation();
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
            // 修复 L1/P1：BeginInvoke 异步投递，不阻塞后台读取线程，防止管道缓冲区填满
            _ = Dispatcher.BeginInvoke(new Action(() => ProcessLine(captured, isStderr)));
        }
    }

    private bool _firstContentReceived; // 是否已收到第一条响应（用于记录首次响应延迟）

    private void ProcessLine(string line, bool isStderr)
    {
        if (isStderr) { StopWaitingAnimation(); AppendThinkingRaw($"  {ParseAnsiColors(line)}\n", false); return; }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (_sessionId == null && root.TryGetProperty("session_id", out var sid))
            {
                _sessionId = sid.GetString();
                Logger.Info($"会话ID: {_sessionId}");
            }

            // 首次收到有意义的内容 → 记录从进程启动到首响应的耗时
            if (!_firstContentReceived && type is "assistant" or "system" or "result")
            {
                _firstContentReceived = true;
                Logger.Info($"首次响应: +{_stopwatch.Elapsed.TotalSeconds:F1}秒 (type={type})");
            }
            if (type is "assistant" or "system" or "result" or "done") StopWaitingAnimation();

            switch (type)
            {
                case "assistant":
                {
                    var content = root.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var ct) ? ct : default;
                    if (content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var block in content.EnumerateArray())
                        {
                            var bt = block.TryGetProperty("type", out var btype) ? btype.GetString() : "";

                            if (bt == "thinking" && block.TryGetProperty("thinking", out var th))
                            {
                                if (_thinkingHistory.Length == 0)
                                    Logger.Info($"思考开始: +{_stopwatch.Elapsed.TotalSeconds:F1}秒");
                                AppendThinkingRaw(ThinkPrefix + (th.GetString() ?? ""), true);
                            }
                            else if (bt == "text" && block.TryGetProperty("text", out var txt))
                            {
                                if (_accumulatedLen == 0)
                                    Logger.Info($"回复开始: +{_stopwatch.Elapsed.TotalSeconds:F1}秒");
                                AppendStreamText(txt.GetString() ?? "");
                            }
                            else if (bt == "tool_use")
                            {
                                var name = block.TryGetProperty("name", out var bn) ? bn.GetString() ?? "tool" : "tool";
                                var input = block.TryGetProperty("input", out var inp) ? inp.ToString() : "";
                                if (input.Length > 300) input = input[..300] + "...";
                                Logger.Info($"工具调用: {name} +{_stopwatch.Elapsed.TotalSeconds:F1}秒");
                                AppendThinkingRaw(ToolPrefix + name + "\n" + ResultPrefix + input + "\n", false);

                                // 交互类工具 → 撑大 ThinkingBox 提醒用户
                                if (InteractiveTools.Contains(name))
                                {
                                    ThinkingPanel.MaxHeight = ThinkingExpandedHeight;
                                    ThinkingPanel.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x64, 0xff, 0xda));
                                    ThinkingBox.FontSize = 14;
                                }
                            }
                        }
                    }
                    break;
                }
                case "result":
                    Logger.Info($"思考结束: +{_stopwatch.Elapsed.TotalSeconds:F1}秒 思考字数={_thinkingHistory.Length}");
                    HideThinkingPanel();

                    var accText = _accumulated.ToString();
                    // 后台写入聊天记录
                    var promptSnapshot = _lastPrompt;
                    var dirSnapshot = _currentDir;
                    _ = Task.Run(() => SaveChatRecord(dirSnapshot, promptSnapshot, accText));
                    var oldPara = _currentPara;
                    var docRef = OutputBox.Document;
                    NewOutputParagraph();
                    if (oldPara != null && accText.Length > 0)
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                var blocks = docRef.Blocks;
                                if (blocks.Contains(oldPara))
                                {
                                    var rendered = MarkdownRenderer.Render(accText);
                                    blocks.Remove(oldPara);
                                    foreach (var b in rendered) blocks.Add(b);
                                    var sep = new Paragraph(new Run("─".PadRight(40, '─')))
                                    { Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x33, 0x44, 0x55)), Margin = new Thickness(0, 6, 0, 10) };
                                    blocks.Add(sep);
                                }
                            }
                            catch (Exception ex) { Logger.Error("Markdown 渲染失败", ex); }
                        }), System.Windows.Threading.DispatcherPriority.Background);
                    }
                    if (root.TryGetProperty("usage", out var usage))
                    {
                        _sessionInputTokens += usage.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0;
                        _sessionOutputTokens += usage.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0;
                        if (usage.TryGetProperty("total_cost_usd", out var tc) && tc.TryGetDecimal(out var cost))
                            _sessionCost += cost;
                        Logger.Info($"Token: 输入={_sessionInputTokens/1000}K 输出={_sessionOutputTokens/1000}K 费用=${_sessionCost:F4}");
                    }
                    UpdateStatsDisplay();
                    break;
                case "system":
                    if (root.TryGetProperty("subtype", out var st) && st.GetString() == "thinking_tokens") break;
                    var sc = root.TryGetProperty("content", out var c) ? c.GetString() : "";
                    if (!string.IsNullOrWhiteSpace(sc)) AppendThinkingRaw(sc + "\n", false);
                    break;
                case "done":
                    Logger.Info($"响应完成: +{_stopwatch.Elapsed.TotalSeconds:F1}秒");
                    NewOutputParagraph();
                    break;
            }
        }
        catch (JsonException) { AppendText(line + "\n", BrushNormal); }
    }

    // ===== 思考面板（ThinkingBox）=====

    // 思考内容的视觉标记
    private static readonly string ThinkPrefix = "💭 ";
    private static readonly string ToolPrefix = "🔧 ";
    private static readonly string ResultPrefix = "   ↓ ";

    /// <summary>向 ThinkingBox 追加原始内容（思考/tool_use/system）</summary>
    /// <param name="text">要追加的文本</param>
    /// <param name="isThinking">true=思考文字(灰蓝斜体), false=tool/system(保持原色)</param>
    private void AppendThinkingRaw(string text, bool isThinking)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (!text.EndsWith("\n")) text += "\n";

        // 首次：显示面板，启动呼吸灯；同时滚动 OutputBox，使最后内容可见
        if (ThinkingPanel.Visibility != Visibility.Visible)
        {
            ThinkingBox.Text = "";
            ThinkingPanel.Visibility = Visibility.Visible;
            Dispatcher.BeginInvoke(new Action(() => { OutputBox.ScrollToEnd(); }), System.Windows.Threading.DispatcherPriority.Background);
            if (ThinkingPanel.Resources["BreathingGlow"] is Storyboard sb)
                sb.Begin();
        }

        // 写入可见面板
        ThinkingBox.AppendText(text);
        ThinkingBox.ScrollToEnd();

        // 写入历史（全屏回看用）
        _thinkingHistory.Append(text);

        // 隐藏等待提示
        StopWaitingAnimation();
    }

    /// <summary>思考完成，隐藏面板，显示回看按钮</summary>
    private void HideThinkingPanel()
    {
        // 停止呼吸灯动画，恢复默认边框色
        if (ThinkingPanel.Resources["BreathingGlow"] is Storyboard sb)
            sb.Stop();
        ThinkingPanel.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x23, 0x35, 0x54));
        ThinkingPanel.Visibility = Visibility.Collapsed;
        if (_thinkingHistory.Length > 0)
            BtnThinking.Visibility = Visibility.Visible;
    }

    /// <summary>全屏查看思考历史</summary>
    private void ShowThinkingOverlay(object sender, RoutedEventArgs e)
    {
        ThinkingFullBox.Text = _thinkingHistory.ToString();
        ThinkingOverlay.Visibility = Visibility.Visible;
        ThinkingFullBox.ScrollToEnd();
        ThinkingFullBox.Focus();
    }

    private void CloseOverlay_Click(object sender, RoutedEventArgs e) => HideThinkingOverlay(sender, null!);

    /// <summary>关闭全屏覆盖层（点击背景或 ESC）</summary>
    private void HideThinkingOverlay(object sender, System.Windows.Input.MouseButtonEventArgs? e)
    {
        ThinkingOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>阻止覆盖层内部面板的点击冒泡到背景</summary>
    private void OverlayPanel_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void ThinkingOverlay_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            ThinkingOverlay.Visibility = Visibility.Collapsed;
    }

    // ===== ANSI 颜色解析 =====

    private static string ParseAnsiColors(string text)
    {
        if (!text.Contains('\x1b')) return text;
        return System.Text.RegularExpressions.Regex.Replace(text, @"\x1b\[\d+(;\d+)*m", "");
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

        _accumulated.Append(text); _accumulatedLen = _accumulated.Length;
        if (OutputBox.Document.Blocks.Count > MaxBlocks * 2) TrimOldBlocks();
        OutputBox.ScrollToEnd();
    }

    private static string RegexCompressNewlines(string input) => Regex.Replace(input, @"\n{3,}", "\n");

    private void NewOutputParagraph() { _currentPara = null; _currentRun = null; _accumulated.Clear(); _accumulatedLen = 0; }

    /// <summary>动画等待提示：⏳ 思考中... N秒（每秒递增）</summary>
    private System.Windows.Threading.DispatcherTimer? _waitingTimer;
    private int _waitingSeconds;
    private Paragraph? _waitingHint;

    private void StartWaitingAnimation()
    {
        StopWaitingAnimation();
        _waitingSeconds = 0;
        _waitingHint = new Paragraph(new Run("⏳ 思考中..."))
        {
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55, 0x66, 0x77)),
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 2)
        };
        OutputBox.Document.Blocks.Add(_waitingHint);
        OutputBox.ScrollToEnd();

        _waitingTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _waitingTimer.Tick += (_, _) =>
        {
            _waitingSeconds++;
            if (_waitingHint != null)
            {
                _waitingHint.Inlines.Clear();
                _waitingHint.Inlines.Add(new Run($"⏳ 思考中... {_waitingSeconds}秒"));
            }
        };
        _waitingTimer.Start();
    }

    private void StopWaitingAnimation()
    {
        _waitingTimer?.Stop();
        _waitingTimer = null;
        if (_waitingHint != null)
        {
            try { if (OutputBox.Document.Blocks.Contains(_waitingHint)) OutputBox.Document.Blocks.Remove(_waitingHint); }
            catch { }
            _waitingHint = null;
        }
    }

    private void AppendText(string text, SolidColorBrush color)
    {
        OutputBox.Document.Blocks.Add(new Paragraph(new Run(text)) { Foreground = color, Margin = new Thickness(0), LineHeight = 18 });
        NewOutputParagraph();
        OutputBox.ScrollToEnd();
    }

    /// <summary>系统消息（更新通知等），MainWindow 调用</summary>
    public void ShowSystemMessage(string text)
    {
        var para = new Paragraph(new Run(text))
        {
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x92, 0xb0)),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1a, 0x2a, 0x1a)),
            Margin = new Thickness(0, 4, 0, 4),
            LineHeight = 20,
            Padding = new Thickness(8, 4, 8, 4)
        };
        OutputBox.Document.Blocks.Add(para);
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
            if (msg.role == "user")
            {
                var para = new Paragraph(new Run($"> {msg.content}\n"))
                {
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xcc, 0xcc, 0xcc)),
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x25, 0x25, 0x30)),
                    Margin = new Thickness(0, 2, 0, 6), LineHeight = 20, Padding = new Thickness(8, 4, 8, 4)
                };
                if (OutputBox.Document.Blocks.FirstBlock != null)
                    OutputBox.Document.Blocks.InsertBefore(OutputBox.Document.Blocks.FirstBlock, para);
                else OutputBox.Document.Blocks.Add(para);
            }
            else
            {
                // AI 回复 → 走 Markdown 渲染
                var rendered = MarkdownRenderer.Render(msg.content);
                var sep = new Paragraph(new Run("─".PadRight(40, '─')))
                { Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x33, 0x44, 0x55)), Margin = new Thickness(0, 4, 0, 8) };
                rendered.Add(sep);
                for (int j = rendered.Count - 1; j >= 0; j--)
                {
                    if (OutputBox.Document.Blocks.FirstBlock != null)
                        OutputBox.Document.Blocks.InsertBefore(OutputBox.Document.Blocks.FirstBlock, rendered[j]);
                    else OutputBox.Document.Blocks.Add(rendered[j]);
                }
            }
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
        int exitCode = -1;
        try { exitCode = _process?.ExitCode ?? -1; }
        catch (InvalidOperationException) { exitCode = -1; } // 进程已释放后访问 ExitCode
        var p = _process; _process = null;
        _ = Task.Run(() => { try { p?.Dispose(); } catch { } });

        // Failover：非正常退出且不是用户主动停止 → 尝试备用提供商
        if (exitCode != 0 && _isRunning && Config != null && _failoverRetry < 3)
        {
            var chain = Config.GetFailoverChain();
            // 修复 F4：显式检查至少有两个提供商才做 failover
            if (chain.Count >= 2 && _failoverRetry < chain.Count - 1)
            {
                _failoverRetry++;
                var next = chain[_failoverRetry];
                Logger.Warn($"Failover: 切换到 {next.Name} (重试{_failoverRetry}/{chain.Count - 1})");
                // 直接重试当前 prompt（_currentDir 已有，但需要重建）
                Dispatcher.BeginInvoke(() =>
                {
                    StopWaitingAnimation();
                    StartSession(_currentDir, $"继续", ClaudePath);
                });
                return;
            }
        }

        _isRunning = false;
        _failoverRetry = 0;
        HideThinkingPanel();
        // 修复 UX-F9：异常退出时向用户显示错误提示
        if (exitCode != 0)
            AppendText($"⚠ 连接中断（退出码 {exitCode}）。请检查网络或切换 API 提供商。\n", BrushError);
        UpdateUIState();
        TrimOldBlocks();
    }

    private void StopProcess()
    {
        // 修复 T5：集中清理等待动画，避免各调用点遗漏
        StopWaitingAnimation();
        var proc = _process;
        _process = null;
        if (proc != null)
        {
            bool isAlive;
            try { isAlive = !proc.HasExited; }
            catch (InvalidOperationException) { isAlive = false; }
            if (isAlive)
            {
                try { proc.Kill(true); } catch { }
                // 延迟 Dispose，等 Exited 事件处理完毕
                var p = proc;
                _ = Task.Run(async () => { await Task.Delay(300); try { p?.Dispose(); } catch { } });
            }
            else
            {
                // 已退出的进程也需 Dispose
                try { proc.Dispose(); } catch { }
            }
        }
        _isRunning = false;
    }

    // ===== 聊天记录写入 =====

    private const long ChatRecordMaxSize = 3 * 1024 * 1024; // 3MB

    private static void SaveChatRecord(string projectDir, string prompt, string response)
    {
        if (string.IsNullOrWhiteSpace(projectDir) || string.IsNullOrWhiteSpace(prompt) || string.IsNullOrWhiteSpace(response))
            return;
        try
        {
            var chatPath = Path.Combine(projectDir, "聊天记录.md");

            // 超 3MB → 归档（使用 try-create 避免并发竞态）
            try
            {
                if (File.Exists(chatPath) && new FileInfo(chatPath).Length >= ChatRecordMaxSize)
                {
                    var memDir = Path.Combine(projectDir, ".claude", "memory");
                    Directory.CreateDirectory(memDir);
                    int seq = 1;
                    while (true)
                    {
                        var dest = Path.Combine(memDir, $"聊天记录{seq}.md");
                        if (!File.Exists(dest))
                        {
                            try { File.Move(chatPath, dest); break; }
                            catch (System.IO.IOException) { seq++; } // 并发冲突，试下一序号
                        }
                        else { seq++; }
                    }
                }
            }
            catch { /* 归档失败不阻塞写入 */ }

            var now = DateTime.Now;
            var record = new System.Text.StringBuilder();
            record.AppendLine($"=== {now:yyyy-MM-dd HH:mm:ss} ===");
            record.AppendLine($"👤 用户: {prompt}");
            record.AppendLine("---");
            record.AppendLine($"🤖 Claude:");
            record.AppendLine(response);
            record.AppendLine();

            File.AppendAllText(chatPath, record.ToString(), System.Text.Encoding.UTF8);
        }
        catch { /* 写入失败不崩溃 */ }
    }

    private void UpdateUIState()
    {
        InputBox.IsEnabled = true; // 思考期间也可输入，方便边等边写
        BtnSend.IsEnabled = !string.IsNullOrWhiteSpace(InputBox.Text);
        BtnSend.Content = _isRunning ? "打断发送" : "发送";
        BtnStop.Visibility = _isRunning ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(_currentDir)) BtnSend.Visibility = Visibility.Visible;
    }

    // ===== 用户输入 =====

    private void InputBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; SendMessage(); }
        else if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.Modifiers != ModifierKeys.Alt) { e.Handled = true; ClearOutput(); }
        else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.Modifiers != ModifierKeys.Alt) { e.Handled = true; ExportHtml(); }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.Modifiers != ModifierKeys.Alt) { e.Handled = true; SearchInOutput(); }
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
        StopProcess(); // 切换项目时杀持久进程
        _sessionId = null; // 修复 T2：避免旧 sessionId 传给新项目的 --resume
        OutputBox.Document.Blocks.Clear();
        _fullSnapshot = null; _snapshotPos = 0;
        _thinkingHistory.Clear();
        ThinkingPanel.Visibility = Visibility.Collapsed;
        ThinkingOverlay.Visibility = Visibility.Collapsed;
        BtnThinking.Visibility = Visibility.Collapsed;
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
    private void InputBox_TextChanged(object sender, TextChangedEventArgs e) { BtnSend.IsEnabled = !string.IsNullOrWhiteSpace(InputBox.Text); }

    private bool _sending;

    private void SendMessage()
    {
        if (_sending) return; // 修复 UX-F11：防止快速连点导致进程泄漏
        var prompt = InputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt)) return;
        if (string.IsNullOrWhiteSpace(_currentDir)) return; // 未选项目时忽略发送
        _sending = true;
        _lastPrompt = prompt; // 记下本轮提示词，result 时写入聊天记录
        if (_isRunning) { StopProcess(); HideThinkingPanel(); }
        InputBox.Text = "";
        ThinkingPanel.MaxHeight = ThinkingNormalHeight;
        ThinkingPanel.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x23, 0x35, 0x54));
        ThinkingBox.FontSize = 11;
        try { StartSession(_currentDir, prompt, ClaudePath); } catch (Exception ex) { AppendText($"启动失败: {ex.Message}\n", BrushError); }
        finally { _sending = false; _overrideProviderName = null; }
    }

    // ===== 公共 =====

    public string? ClaudePath { get; set; }
    public ConfigService? Config { get; set; }
    public bool IsProcessAlive => _process != null && !_process.HasExited;

    /// <summary>当前项目的工作模式（MainWindow 切换项目时同步）</summary>
    public string PermissionMode
    {
        get => _permissionMode;
        set => _permissionMode = value;
    }

    /// <summary>填充提供商到快捷指令下拉（动态追加在权限模式之后）</summary>
    public void RefreshSkillsProviders()
    {
        if (Config == null) return;
        // 移除旧的提供商项（1 标题 + 9 指令 + 1 分隔 + 4 模式 = 15 个固定项）
        while (CmbSkills.Items.Count > 15)
            CmbSkills.Items.RemoveAt(CmbSkills.Items.Count - 1);

        var allProviders = Config.GetProviders().ToList();
        if (allProviders.Count == 0) return;

        // 分隔线 + 标题
        CmbSkills.Items.Add(new ComboBoxItem { Content = "──────────", Tag = "", IsEnabled = false });
        CmbSkills.Items.Add(new ComboBoxItem { Content = "🔌 切换 AI", Tag = "", IsEnabled = false, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x66, 0x66, 0x66)) });

        foreach (var p in allProviders)
        {
            var hasKey = !string.IsNullOrWhiteSpace(p.ApiKey);
            var label = string.IsNullOrWhiteSpace(p.Tags) ? p.Name : $"{p.Name} ({p.Tags})";
            if (!hasKey) label += " (未设Key)";
            var item = new ComboBoxItem
            {
                Content = label,
                Tag = hasKey ? $"provider:{p.Name}" : "",
                ToolTip = hasKey ? p.BaseUrl : $"{p.Name}: 请在系统设置中填写 API Key",
            };
            if (hasKey)
                item.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x80, 0xb3, 0xff));
            else
                item.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x66, 0x66, 0x66));
            CmbSkills.Items.Add(item);
        }
    }

    /// <summary>解析当前应使用的提供商（可能被快捷指令临时覆盖）</summary>
    private ApiProviderConfig? ResolveProvider()
    {
        if (Config == null) return null;
        if (!string.IsNullOrWhiteSpace(_overrideProviderName))
        {
            var p = Config.GetProvider(_overrideProviderName);
            if (p != null && !string.IsNullOrWhiteSpace(p.ApiKey)) return p;
        }
        return Config.GetActiveProvider();
    }

    /// <summary>将提供商配置注入进程环境变量</summary>
    /// <summary>注入系统级环境变量（修复 D1：StartSession/PreloadSession 共用）</summary>
    private void InjectSystemEnvVars(ProcessStartInfo psi)
    {
        foreach (var name in _envNames)
        {
            if (psi.Environment.ContainsKey(name)) continue;
            if (!_envCache.TryGetValue(name, out var val))
            {
                val = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process)
                   ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
                   ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine) ?? "";
                _envCache[name] = val;
            }
            if (!string.IsNullOrWhiteSpace(val)) psi.Environment[name] = val;
        }
    }

    /// <summary>将提供商配置注入子进程环境（仅子进程，不扩散到父进程）</summary>
    private static void InjectProviderEnv(ProcessStartInfo psi, ApiProviderConfig provider)
    {
        if (!string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            psi.Environment["ANTHROPIC_API_KEY"] = provider.ApiKey;
            psi.Environment["ANTHROPIC_AUTH_TOKEN"] = provider.ApiKey;
            // 必须设到父进程级：claude CLI 读取 User 级注册表会覆盖 psi.Environment
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", provider.ApiKey, EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", provider.ApiKey, EnvironmentVariableTarget.Process);
        }
        if (!string.IsNullOrWhiteSpace(provider.BaseUrl))
        {
            psi.Environment["ANTHROPIC_BASE_URL"] = provider.BaseUrl;
            Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", provider.BaseUrl, EnvironmentVariableTarget.Process);
        }
        if (!string.IsNullOrWhiteSpace(provider.Model))
            psi.Environment["ANTHROPIC_MODEL"] = provider.Model;
        if (!string.IsNullOrWhiteSpace(provider.SmallFastModel))
            psi.Environment["ANTHROPIC_SMALL_FAST_MODEL"] = provider.SmallFastModel;
        if (!string.IsNullOrWhiteSpace(provider.DefaultOpusModel))
            psi.Environment["ANTHROPIC_DEFAULT_OPUS_MODEL"] = provider.DefaultOpusModel;
        if (!string.IsNullOrWhiteSpace(provider.DefaultSonnetModel))
            psi.Environment["ANTHROPIC_DEFAULT_SONNET_MODEL"] = provider.DefaultSonnetModel;
        if (!string.IsNullOrWhiteSpace(provider.DefaultHaikuModel))
            psi.Environment["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = provider.DefaultHaikuModel;
        if (!string.IsNullOrWhiteSpace(provider.DefaultFableModel))
            psi.Environment["ANTHROPIC_DEFAULT_FABLE_MODEL"] = provider.DefaultFableModel;
    }
    public void Activate(string workDir) { _currentDir = workDir; TxtPlaceholder.Visibility = Visibility.Collapsed; InputBox.IsEnabled = true; BtnSend.IsEnabled = !string.IsNullOrWhiteSpace(InputBox.Text); BtnSend.Visibility = Visibility.Visible; CmbSkills.IsEnabled = true; CmbSkills.Visibility = Visibility.Visible; CmbTips.IsEnabled = true; CmbTips.Visibility = Visibility.Visible; RefreshSkillsProviders(); InputBox.Focus(); }
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

    /// <summary>预载入：选项目时立即启动 claude 预热（加载上下文/认证/磁盘缓存）</summary>
    public void PreloadSession(string workDir, string? claudeOverride = null)
    {
        var isFirstUse = !Directory.Exists(Path.Combine(workDir, ".claude"));
        if (isFirstUse) return; // 首次使用不需要预热

        StopProcess();
        _sessionId = null;
        _firstContentReceived = false;
        _stopwatch.Restart();

        var claudeExe = claudeOverride ?? "claude";
        var psi = new ProcessStartInfo(claudeExe)
        {
            Arguments = "--continue --permission-mode {_permissionMode} --output-format stream-json --verbose",
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workDir,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        // 注入提供商环境变量
        var provider = ResolveProvider();
        if (provider != null) InjectProviderEnv(psi, provider);
        InjectSystemEnvVars(psi);

        Logger.Info($"预载入: {Path.GetFileName(workDir)} [提供商={provider?.Name ?? "默认"}]");
        _process = Process.Start(psi);
        if (_process == null) return;
        _process.StandardInput.Close(); // 立即关闭stdin让claude知道不交互
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            Logger.Info($"预载入进程退出 (项目={Path.GetFileName(workDir)})");
            var p = _process; _process = null;
            _ = Task.Run(() => { try { p?.Dispose(); } catch { } });
        });
        // 后台读取stdout捕获session_id（修复 R3：30秒超时防止永久挂起）
        _ = Task.Run(async () =>
        {
            try
            {
                var reader = _process?.StandardOutput;
                if (reader == null) return;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                string? line;
                while ((line = await reader.ReadLineAsync().WaitAsync(cts.Token)) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        if (doc.RootElement.TryGetProperty("session_id", out var sid))
                        {
                            _sessionId = sid.GetString();
                            Logger.Info($"预载入获取会话ID: {_sessionId}");
                            break;
                        }
                    }
                    catch { }
                }
            }
            catch (OperationCanceledException)
            {
                // 修复 T12：超时后杀进程防止僵尸
                var p = _process; _process = null;
                try { p?.Kill(true); } catch { }
                _ = Task.Run(async () => { await Task.Delay(300); try { p?.Dispose(); } catch { } });
                Logger.Warn("预载入超时，进程已终止");
            }
            catch { }
        });
    }

    public void Dispose()
    {
        // 修复 T13：清理计时器和动画资源
        StopWaitingAnimation();
        HideThinkingPanel();
        var proc = _process; _process = null;
        if (proc != null) { try { proc.Kill(true); } catch { } proc.Dispose(); }
    }

    // ===== 静态辅助 =====

    private static string EscapeArg(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return "";
        var sb = new StringBuilder();
        foreach (char c in arg) sb.Append(c is '\r' or '\n' or '\0' ? ' ' : c);
        var clean = sb.ToString();
        if (clean.Length > 8000) clean = clean[..8000];
        // 修复 S5：处理 % 防止 Windows Shell 环境变量展开
        return clean.Replace("%", "%%").Replace("\\", "\\\\").Replace("\"", "\\\"");
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
