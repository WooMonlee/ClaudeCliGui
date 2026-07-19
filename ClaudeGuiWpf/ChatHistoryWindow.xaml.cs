using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClaudeGui;

public partial class ChatHistoryWindow : Window
{
    private readonly string _projectDir;
    private readonly ConfigService? _config;

    // 全部已加载的条目（解析后保留在内存）
    private List<ChatRecordEntry> _allEntries = new();
    // 经筛选/搜索后的可见条目
    private List<ChatRecordEntry> _visibleEntries = new();
    // 普通模式下当前渲染窗口的起始下标
    private int _renderStart;
    private const int BatchSize = 80;
    private const int MaxBlocks = 2000; // 文档最大块数（约 330 条记录）
    private string _filterMode = "all"; // "all" | "user"
    private string _searchText = "";
    private int _totalFiles;
    private bool _isLoadingMore;
    private int _currentMatchIndex; // 搜索当前高亮项下标

    // 搜索防抖
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };

    // 颜色刷
    private static readonly SolidColorBrush BrUserBg = new(Color.FromRgb(0x1a, 0x1a, 0x28));
    private static readonly SolidColorBrush BrUserBorder = new(Color.FromRgb(0xff, 0xf0, 0x60));
    private static readonly SolidColorBrush BrTimestamp = new(Color.FromRgb(0x33, 0x55, 0x77));
    private static readonly SolidColorBrush BrText = new(Color.FromRgb(0xcc, 0xcc, 0xcc));
    private static readonly SolidColorBrush BrHeading = new(Color.FromRgb(0x64, 0xff, 0xda));
    private static readonly SolidColorBrush BrSep = new(Color.FromRgb(0x23, 0x35, 0x54));
    private static readonly SolidColorBrush BrHighlight = new(Color.FromRgb(0x64, 0xff, 0xda));
    private static readonly SolidColorBrush BrHighlightBg = new(Color.FromRgb(0x2a, 0x2a, 0x0a));
    private static readonly SolidColorBrush BrStatus = new(Color.FromRgb(0x66, 0x66, 0x66));

    public ChatHistoryWindow(string projectDir, ConfigService? config)
    {
        InitializeComponent();
        _projectDir = projectDir;
        _config = config;
        _searchTimer.Tick += SearchTimer_Tick;

        // 窗口标题
        var projName = Path.GetFileName(projectDir.TrimEnd('\\', '/'));
        TxtTitle.Text = $"📋 聊天记录速览 — {projName}";

        // 加载数据
        Loaded += async (_, _) =>
        {
            var sv = FindVisualChild<ScrollViewer>(OutputBox);
            if (sv != null) sv.ScrollChanged += OnScrollChanged;

            await Task.Run(() => LoadAllEntries());
            Dispatcher.Invoke(RefreshView);
        };
    }

    // ============ 数据加载 ============

    private void LoadAllEntries()
    {
        try
        {
            var files = ChatRecordParser.EnumerateChatFiles(_projectDir);
            _totalFiles = files.Count;

            var all = new List<ChatRecordEntry>();
            foreach (var (path, label) in files)
            {
                foreach (var entry in ChatRecordParser.ParseFile(path, label))
                    all.Add(entry);
            }
            _allEntries = all;
        }
        catch (Exception ex)
        {
            Logger.Error("聊天记录加载失败", ex);
        }
    }

    // ============ 统一渲染入口 ============

    /// <summary>刷新视图：重新应用筛选并重渲染</summary>
    private void RefreshView()
    {
        try
        {
            LoadingOverlay.Visibility = Visibility.Visible;

            ApplyFilter();

            OutputBox.Document.Blocks.Clear();

            if (_visibleEntries.Count == 0)
            {
                // 空状态提示
                var emptyMsg = _allEntries.Count == 0 ? "📭 暂无聊天记录" : "🔍 无匹配结果";
                OutputBox.Document.Blocks.Add(new Paragraph(new Run(emptyMsg))
                {
                    Foreground = BrStatus,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 40, 0, 0),
                    FontSize = 14,
                });
                if (_filterMode == "user")
                {
                    OutputBox.Document.Blocks.Add(new Paragraph(new Run("（已开启「只看用户」筛选）")
                    { Foreground = BrStatus, FontSize = 11 })
                    { TextAlignment = TextAlignment.Center });
                }
            }
            else if (!string.IsNullOrWhiteSpace(_searchText))
            {
                // 搜索模式：只渲染当前匹配
                _currentMatchIndex = 0;
                ShowCurrentMatch();
            }
            else
            {
                // 普通浏览模式：从最后 BatchSize 条开始渲染，滚到底部
                _renderStart = Math.Max(0, _visibleEntries.Count - BatchSize);
                RenderWindow();
                OutputBox.ScrollToEnd();
            }

            UpdateStatus();
        }
        finally
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>渲染当前窗口 [_renderStart, _renderStart + BatchSize)</summary>
    private void RenderWindow()
    {
        var end = Math.Min(_renderStart + BatchSize, _visibleEntries.Count);
        for (int i = _renderStart; i < end; i++)
        {
            foreach (var block in CreateEntryBlocks(_visibleEntries[i], _searchText))
                OutputBox.Document.Blocks.Add(block);
        }

        TrimHeadBlocks(); // 安全修剪（控制内存上限）
    }

    /// <summary>
    /// 创建单条条目的所有 Block。
    /// 返回列表：时间戳 → 用户/AI 内容 → 分隔线（可选）
    /// </summary>
    private List<Block> CreateEntryBlocks(ChatRecordEntry entry, string searchText, bool includeSeparator = true)
    {
        var blocks = new List<Block>();

        // 时间戳
        blocks.Add(new Paragraph(new Run($"📅 {entry.Timestamp:yyyy-MM-dd HH:mm}"))
        {
            Foreground = BrTimestamp,
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 2),
            FontWeight = FontWeights.SemiBold,
        });

        if (entry.Role == "user")
        {
            // 用户消息：左框高亮
            var userPara = new Paragraph
            {
                Background = BrUserBg,
                BorderBrush = BrUserBorder,
                BorderThickness = new Thickness(2, 0, 0, 0),
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(10, 4, 10, 4),
                LineHeight = 20,
            };
            AddTextWithHighlight(userPara.Inlines, entry.Content, searchText, BrText);
            blocks.Add(userPara);
        }
        else
        {
            // AI 回复：MarkdownRenderer 渲染后逐块高亮
            try
            {
                var rendered = MarkdownRenderer.Render(entry.Content);
                foreach (var b in rendered)
                {
                    ApplyHighlightToBlock(b, searchText);
                    blocks.Add(b);
                }
            }
            catch
            {
                var fb = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
                AddTextWithHighlight(fb.Inlines, entry.Content, searchText, BrText);
                blocks.Add(fb);
            }
        }

        if (includeSeparator)
        {
            blocks.Add(new Paragraph(new Run("─".PadRight(30, '─'))
            { Foreground = BrSep, FontSize = 10 })
            { Margin = new Thickness(0, 2, 0, 4) });
        }

        return blocks;
    }

    // ============ 搜索模式 ============

    /// <summary>搜索模式：只渲染第 _currentMatchIndex 条匹配</summary>
    private void ShowCurrentMatch()
    {
        if (_visibleEntries.Count == 0) return;
        if (_currentMatchIndex >= _visibleEntries.Count) _currentMatchIndex = 0;
        if (_currentMatchIndex < 0) _currentMatchIndex = _visibleEntries.Count - 1;

        OutputBox.Document.Blocks.Clear();
        var entry = _visibleEntries[_currentMatchIndex];

        // 匹配计数器
        OutputBox.Document.Blocks.Add(new Paragraph(
            new Run($"🔍 {_currentMatchIndex + 1} / {_visibleEntries.Count} 个匹配"))
        { Foreground = BrHeading, FontSize = 13, Margin = new Thickness(0, 0, 0, 8) });

        // 渲染条目（不含尾部分隔线）
        foreach (var block in CreateEntryBlocks(entry, _searchText, includeSeparator: false))
            OutputBox.Document.Blocks.Add(block);

        OutputBox.ScrollToHome();
    }

    private void NextMatch()
    {
        if (_visibleEntries.Count == 0) return;
        _currentMatchIndex++;
        if (_currentMatchIndex >= _visibleEntries.Count) _currentMatchIndex = 0;
        ShowCurrentMatch();
    }

    private void PrevMatch()
    {
        if (_visibleEntries.Count == 0) return;
        _currentMatchIndex--;
        if (_currentMatchIndex < 0) _currentMatchIndex = _visibleEntries.Count - 1;
        ShowCurrentMatch();
    }

    // ============ 筛选 ============

    private void ApplyFilter()
    {
        var query = _allEntries.AsEnumerable();

        if (_filterMode == "user")
            query = query.Where(e => e.Role == "user");

        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            var st = _searchText.Trim();
            query = query.Where(e => e.Content.Contains(st, StringComparison.OrdinalIgnoreCase));
        }

        _visibleEntries = query.ToList();
    }

    // ============ 滚动加载更多（插入到开头，不振荡） ============

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_isLoadingMore) return;
        if (!string.IsNullOrWhiteSpace(_searchText)) return; // 搜索模式不滚动加载
        if (_visibleEntries.Count == 0) return;

        var sv = (ScrollViewer)sender;

        // 滚到顶部 → 在文档开头插入更早的记录
        if (e.VerticalOffset < 50 && _renderStart > 0)
        {
            _isLoadingMore = true;

            var oldStart = _renderStart;
            var oldExtent = sv.ExtentHeight;
            _renderStart = Math.Max(0, _renderStart - BatchSize);

            // 逆序插入到文档开头以保证正序排列
            for (int i = oldStart - 1; i >= _renderStart; i--)
            {
                var blocks = CreateEntryBlocks(_visibleEntries[i], _searchText);
                for (int b = blocks.Count - 1; b >= 0; b--)
                {
                    OutputBox.Document.Blocks.InsertBefore(
                        OutputBox.Document.Blocks.FirstBlock!, blocks[b]);
                }
            }

            TrimTailBlocks(); // 内存控制：从尾部移除最旧的内容
            UpdateStatus();

            // 滚动补偿：插入新内容后视口下移，避免可见内容跳变
            sv.UpdateLayout();
            sv.ScrollToVerticalOffset(e.VerticalOffset + (sv.ExtentHeight - oldExtent));

            _isLoadingMore = false;
        }
    }

    // ============ 文档块修剪 ============

    /// <summary>从头部移除旧块</summary>
    private void TrimHeadBlocks()
    {
        var blocks = OutputBox.Document.Blocks;
        while (blocks.Count > MaxBlocks && blocks.FirstBlock != null)
            blocks.Remove(blocks.FirstBlock);
    }

    /// <summary>从尾部移除最旧块（用于插入到开头后的内存控制）</summary>
    private void TrimTailBlocks()
    {
        var blocks = OutputBox.Document.Blocks;
        while (blocks.Count > MaxBlocks && blocks.LastBlock != null)
            blocks.Remove(blocks.LastBlock);
    }

    // ============ 高亮工具方法 ============

    /// <summary>将文本拆分为普通 Run + 高亮 Run 添加到 InlineCollection</summary>
    private static void AddTextWithHighlight(InlineCollection inlines, string text, string search, Brush defaultBrush)
    {
        if (string.IsNullOrWhiteSpace(search) || !text.Contains(search, StringComparison.OrdinalIgnoreCase))
        {
            inlines.Add(new Run(text) { Foreground = defaultBrush });
            return;
        }

        int last = 0;
        var st = search.Trim();
        for (int i = 0; i <= text.Length - st.Length;)
        {
            var idx = text.IndexOf(st, i, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) break;

            if (idx > last)
                inlines.Add(new Run(text[last..idx]) { Foreground = defaultBrush });

            inlines.Add(new Run(text[idx..(idx + st.Length)])
            {
                Foreground = BrHighlight,
                Background = BrHighlightBg,
                FontWeight = FontWeights.Bold,
            });

            i = idx + st.Length;
            last = i;
        }
        if (last < text.Length)
            inlines.Add(new Run(text[last..]) { Foreground = defaultBrush });
    }

    /// <summary>遍历 Paragaph 中所有 Inline，对有匹配的 Run 进行高亮替换</summary>
    private static void ApplyHighlightToBlock(Block block, string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText)) return;
        if (block is not Paragraph p) return;
        if (p.Inlines.Count == 0) return;

        // 快照所有 Inline，清空后重新添加（InlineCollection 无索引器）
        var snapshot = new List<Inline>(p.Inlines.Count);
        foreach (var inline in p.Inlines) snapshot.Add(inline);
        p.Inlines.Clear();

        foreach (var inline in snapshot)
        {
            if (inline is Run run)
            {
                var text = run.Text;
                if (text.Contains(searchText, StringComparison.OrdinalIgnoreCase))
                {
                    var fg = run.Foreground;
                    var bg = run.Background;
                    var fw = run.FontWeight;
                    var fs = run.FontStyle;
                    AddTextWithHighlightEx(p.Inlines, text, searchText, fg ?? BrText, bg, fw, fs);
                }
                else
                {
                    p.Inlines.Add(inline);
                }
            }
            else
            {
                p.Inlines.Add(inline);
            }
        }
    }

    /// <summary>高亮版本：保留原 Run 样式属性</summary>
    private static void AddTextWithHighlightEx(InlineCollection inlines, string text, string search,
        Brush fg, Brush? bg, FontWeight fw, FontStyle fs)
    {
        if (!text.Contains(search, StringComparison.OrdinalIgnoreCase))
        {
            inlines.Add(new Run(text) { Foreground = fg, Background = bg, FontWeight = fw, FontStyle = fs });
            return;
        }

        int last = 0;
        var st = search.Trim();
        for (int i = 0; i <= text.Length - st.Length;)
        {
            var idx = text.IndexOf(st, i, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) break;

            if (idx > last)
                inlines.Add(new Run(text[last..idx]) { Foreground = fg, Background = bg, FontWeight = fw, FontStyle = fs });

            inlines.Add(new Run(text[idx..(idx + st.Length)])
            {
                Foreground = BrHighlight,
                Background = BrHighlightBg,
                FontWeight = FontWeights.Bold,
            });

            i = idx + st.Length;
            last = i;
        }
        if (last < text.Length)
            inlines.Add(new Run(text[last..]) { Foreground = fg, Background = bg, FontWeight = fw, FontStyle = fs });
    }

    // ============ 状态栏 ============

    private void UpdateStatus()
    {
        TxtStats.Text = $"{_allEntries.Count} 条 / {_totalFiles} 个文件";
        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            TxtStatus.Text = $"匹配 {_visibleEntries.Count} 条  搜索: \"{_searchText}\"  ← 上一个 / → 下一个" +
                (_filterMode == "user" ? "  [只看用户]" : "");
        }
        else if (_filterMode == "user")
        {
            TxtStatus.Text = $"已加载 {_visibleEntries.Count} 条  [只看用户]";
        }
        else
        {
            TxtStatus.Text = $"已加载 {_visibleEntries.Count} 条";
        }
    }

    // ============ 事件处理 ============

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        // 立即搜索，绕过 300ms 防抖延迟
        _searchTimer.Stop();
        var newText = SearchBox.Text.Trim();

        if (newText == _searchText && !string.IsNullOrWhiteSpace(_searchText))
        {
            // 文本未变，仅导航
            if (Keyboard.Modifiers == ModifierKeys.Shift) PrevMatch();
            else NextMatch();
        }
        else
        {
            // 文本变了，立即触发搜索
            _searchText = newText;
            RefreshView();

            // Shift+Enter 跳到最后一个匹配
            if (!string.IsNullOrWhiteSpace(_searchText) &&
                Keyboard.Modifiers == ModifierKeys.Shift &&
                _visibleEntries.Count > 0)
            {
                _currentMatchIndex = _visibleEntries.Count - 1;
                ShowCurrentMatch();
                UpdateStatus();
            }
        }
        e.Handled = true;
    }

    private void SearchTimer_Tick(object? sender, EventArgs e)
    {
        _searchTimer.Stop();
        _searchText = SearchBox.Text.Trim();
        RefreshView();
    }

    private void FilterUser_Checked(object sender, RoutedEventArgs e)
    {
        _filterMode = "user";
        RefreshView();
    }

    private void FilterUser_Unchecked(object sender, RoutedEventArgs e)
    {
        _filterMode = "all";
        RefreshView();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _allEntries.Clear();
        _visibleEntries.Clear();
        OutputBox.Document.Blocks.Clear();
        base.OnClosed(e);
    }

    // ============ 工具方法 ============

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var found = FindVisualChild<T>(child);
            if (found != null) return found;
        }
        return null;
    }
}
