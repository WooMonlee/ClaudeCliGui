using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace ClaudeGui;

/// <summary>
/// 将 Markdown 文本转为 WPF FlowDocument 的 Block 列表
/// 支持：标题、粗斜体、代码块、表格、列表、引用、分割线
/// </summary>
public static class MarkdownRenderer
{
    private static readonly SolidColorBrush BrText = new(System.Windows.Media.Color.FromRgb(0xcc, 0xcc, 0xcc));
    private static readonly SolidColorBrush BrCode = new(System.Windows.Media.Color.FromRgb(0x64, 0xff, 0xda));
    private static readonly SolidColorBrush BrCodeBg = new(System.Windows.Media.Color.FromRgb(0x1a, 0x1a, 0x2e));
    private static readonly SolidColorBrush BrHeading = new(System.Windows.Media.Color.FromRgb(0x64, 0xff, 0xda));
    private static readonly SolidColorBrush BrQuote = new(System.Windows.Media.Color.FromRgb(0x88, 0x92, 0xb0));
    private static readonly SolidColorBrush BrTableHeader = new(System.Windows.Media.Color.FromRgb(0x1e, 0x3a, 0x5f));
    private static readonly SolidColorBrush BrTableBorder = new(System.Windows.Media.Color.FromRgb(0x23, 0x35, 0x54));
    private static readonly SolidColorBrush BrTableCell = new(System.Windows.Media.Color.FromRgb(0x0c, 0x0c, 0x0c));

    public static List<Block> Render(string markdown)
    {
        var blocks = new List<Block>();
        if (string.IsNullOrWhiteSpace(markdown)) return blocks;

        var lines = markdown.Split('\n');
        var i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];

            // 空行跳过
            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }

            // 代码块 ```
            if (line.TrimStart().StartsWith("```"))
            {
                blocks.Add(RenderCodeBlock(lines, ref i));
                continue;
            }

            // 表格 |
            if (line.TrimStart().StartsWith("|") && IsTable(lines, i))
            {
                blocks.Add(RenderTable(lines, ref i));
                continue;
            }

            // 标题 #
            var headingMatch = Regex.Match(line, @"^(#{1,6})\s+(.+)$");
            if (headingMatch.Success)
            {
                var level = headingMatch.Groups[1].Length;
                var text = headingMatch.Groups[2].Value;
                blocks.Add(CreateHeading(text, level));
                i++;
                continue;
            }

            // 分割线 ---
            if (Regex.IsMatch(line.Trim(), @"^[-*_]{3,}$"))
            {
                blocks.Add(new Paragraph(new Run("─".PadRight(60, '─')))
                { Foreground = BrTableBorder, Margin = new Thickness(0, 8, 0, 8) });
                i++;
                continue;
            }

            // 引用 >
            if (line.TrimStart().StartsWith(">"))
            {
                blocks.Add(RenderBlockquote(lines, ref i));
                continue;
            }

            // 无序列表 - /*
            var ulMatch = Regex.Match(line, @"^(\s*)[-*]\s+(.+)$");
            if (ulMatch.Success)
            {
                blocks.Add(RenderList(lines, ref i, false));
                continue;
            }

            // 有序列表 1.
            var olMatch = Regex.Match(line, @"^(\s*)\d+\.\s+(.+)$");
            if (olMatch.Success)
            {
                blocks.Add(RenderList(lines, ref i, true));
                continue;
            }

            // 普通段落（可能多行）
            blocks.Add(RenderParagraph(lines, ref i));
        }

        return blocks;
    }

    // ===== 段落 =====

    private static Paragraph RenderParagraph(string[] lines, ref int i)
    {
        var para = new Paragraph { Margin = new Thickness(0, 2, 0, 6), LineHeight = 20 };
        var text = lines[i].Trim();
        i++;

        // 合并后续非空非特殊行
        while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i])
            && !lines[i].TrimStart().StartsWith("```")
            && !lines[i].TrimStart().StartsWith("|")
            && !Regex.IsMatch(lines[i], @"^#{1,6}\s")
            && !Regex.IsMatch(lines[i], @"^[-*_]{3,}$")
            && !lines[i].TrimStart().StartsWith(">")
            && !Regex.IsMatch(lines[i], @"^\s*[-*]\s+")
            && !Regex.IsMatch(lines[i], @"^\s*\d+\.\s+"))
        {
            text += "\n" + lines[i].Trim();
            i++;
        }

        RenderInline(para, text);
        return para;
    }

    // ===== 内联格式（粗体、斜体、行内代码、链接） =====

    private static void RenderInline(Paragraph para, string text)
    {
        // 正则匹配：**粗体**、*斜体*、`代码`、[链接](url)
        var pattern = @"(\*\*(.+?)\*\*)|(\*(.+?)\*)|(`(.+?)`)|(\[(.+?)\]\((.+?)\))";
        var lastIndex = 0;

        foreach (Match m in Regex.Matches(text, pattern))
        {
            // 前面的普通文本
            if (m.Index > lastIndex)
            {
                para.Inlines.Add(new Run(text[lastIndex..m.Index]) { Foreground = BrText });
            }

            if (m.Groups[1].Success) // **粗体**
            {
                para.Inlines.Add(new Bold(new Run(m.Groups[2].Value) { Foreground = BrText }));
            }
            else if (m.Groups[3].Success) // *斜体*
            {
                para.Inlines.Add(new Italic(new Run(m.Groups[4].Value) { Foreground = BrText }));
            }
            else if (m.Groups[5].Success) // `代码`
            {
                para.Inlines.Add(new Run(m.Groups[6].Value)
                {
                    Foreground = BrCode,
                    // Inline code background not directly supported; skip
                });
            }
            else if (m.Groups[7].Success) // [链接](url)
            {
                var linkText = m.Groups[8].Value;
                var linkUrl = m.Groups[9].Value;
                var hyperlink = new Hyperlink(new Run(linkText) { Foreground = BrCode })
                {
                    NavigateUri = new Uri(linkUrl),
                    ToolTip = linkUrl
                };
                hyperlink.RequestNavigate += (_, e) =>
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.ToString()) { UseShellExecute = true }); }
                    catch { }
                };
                para.Inlines.Add(hyperlink);
            }

            lastIndex = m.Index + m.Length;
        }

        // 剩余文本
        if (lastIndex < text.Length)
        {
            para.Inlines.Add(new Run(text[lastIndex..]) { Foreground = BrText });
        }

        // 如果没有任何匹配，直接添加全文
        if (lastIndex == 0 && para.Inlines.Count == 0)
        {
            para.Inlines.Add(new Run(text) { Foreground = BrText });
        }
    }

    // ===== 代码块 =====

    private static Block RenderCodeBlock(string[] lines, ref int i)
    {
        i++; // 跳过开头的 ```
        var sb = new System.Text.StringBuilder();
        while (i < lines.Length && !lines[i].TrimStart().StartsWith("```"))
        {
            sb.AppendLine(lines[i]);
            i++;
        }
        i++; // 跳过结尾的 ```

        var para = new Paragraph(new Run(sb.ToString().TrimEnd()))
        {
            Background = BrCodeBg,
            Foreground = BrCode,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 8),
            Padding = new Thickness(12, 8, 12, 8),
            LineHeight = 18
        };
        return para;
    }

    // ===== 表格 =====

    private static bool IsTable(string[] lines, int start)
    {
        if (start + 1 >= lines.Length) return false;
        return lines[start].TrimStart().StartsWith("|")
            && Regex.IsMatch(lines[start + 1].Trim(), @"^\|[\s\-:|]+\|$");
    }

    private static Block RenderTable(string[] lines, ref int i)
    {
        // 收集所有表格行直到非表格行
        var tableLines = new List<string>();
        while (i < lines.Length && lines[i].TrimStart().StartsWith("|"))
        {
            tableLines.Add(lines[i].Trim());
            i++;
        }

        if (tableLines.Count < 2) return new Paragraph(new Run("(空表格)"));

        // 解析表头
        var headers = ParseTableRow(tableLines[0]);
        var aligns = ParseTableAligns(tableLines[1]);

        var table = new Table
        {
            BorderBrush = BrTableBorder,
            BorderThickness = new Thickness(1),
            CellSpacing = 0,
            Margin = new Thickness(0, 6, 0, 10)
        };

        // 列定义
        foreach (var h in headers)
            table.Columns.Add(new TableColumn { Width = GridLength.Auto });

        // 表头行组
        var headerGroup = new TableRowGroup();
        var headerRow = new TableRow();
        for (int c = 0; c < headers.Count; c++)
        {
            var cell = new TableCell(new Paragraph(new Run(headers[c].Trim()) { Foreground = BrHeading, FontWeight = System.Windows.FontWeights.Bold })
            {
                Margin = new Thickness(0),
                Padding = new Thickness(8, 4, 8, 4)
            })
            {
                Background = BrTableHeader,
                BorderBrush = BrTableBorder,
                BorderThickness = new Thickness(1),
                TextAlignment = GetAlignment(aligns, c)
            };
            headerRow.Cells.Add(cell);
        }
        headerGroup.Rows.Add(headerRow);
        table.RowGroups.Add(headerGroup);

        // 数据行组
        var dataGroup = new TableRowGroup();
        for (int r = 2; r < tableLines.Count; r++)
        {
            var cols = ParseTableRow(tableLines[r]);
            var row = new TableRow();
            for (int c = 0; c < Math.Min(cols.Count, headers.Count); c++)
            {
                var cell = new TableCell(new Paragraph(new Run(cols[c].Trim()) { Foreground = BrText })
                {
                    Margin = new Thickness(0),
                    Padding = new Thickness(8, 4, 8, 4)
                })
                {
                    Background = BrTableCell,
                    BorderBrush = BrTableBorder,
                    BorderThickness = new Thickness(1),
                    TextAlignment = GetAlignment(aligns, c)
                };
                row.Cells.Add(cell);
            }
            // 补全列数不足的行
            for (int c = cols.Count; c < headers.Count; c++)
            {
                row.Cells.Add(new TableCell(new Paragraph()) { BorderBrush = BrTableBorder, BorderThickness = new Thickness(1) });
            }
            dataGroup.Rows.Add(row);
        }
        table.RowGroups.Add(dataGroup);

        return table;
    }

    private static List<string> ParseTableRow(string line)
    {
        var trimmed = line.Trim('|');
        return trimmed.Split('|').Select(c => c.Trim()).ToList();
    }

    private static List<string> ParseTableAligns(string line)
    {
        var trimmed = line.Trim('|');
        return trimmed.Split('|').Select(c => c.Trim()).ToList();
    }

    private static TextAlignment GetAlignment(List<string> aligns, int col)
    {
        if (col >= aligns.Count) return TextAlignment.Left;
        var a = aligns[col].Trim();
        if (a.StartsWith(":") && a.EndsWith(":")) return TextAlignment.Center;
        if (a.EndsWith(":")) return TextAlignment.Right;
        return TextAlignment.Left;
    }

    // ===== 标题 =====

    private static Paragraph CreateHeading(string text, int level)
    {
        double size = level switch { 1 => 22, 2 => 18, 3 => 16, _ => 14 };
        var para = new Paragraph
        {
            Margin = new Thickness(0, 8, 0, 4),
            LineHeight = size + 6
        };
        var run = new Run(text) { Foreground = BrHeading, FontWeight = System.Windows.FontWeights.Bold, FontSize = size };
        para.Inlines.Add(run);
        return para;
    }

    // ===== 引用 =====

    private static Paragraph RenderBlockquote(string[] lines, ref int i)
    {
        var sb = new System.Text.StringBuilder();
        while (i < lines.Length && lines[i].TrimStart().StartsWith(">"))
        {
            var content = lines[i].TrimStart().TrimStart('>').TrimStart();
            sb.AppendLine(content);
            i++;
        }
        var para = new Paragraph(new Run(sb.ToString().TrimEnd()))
        {
            Foreground = BrQuote,
            BorderBrush = BrCode,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 0, 0, 0),
            Margin = new Thickness(0, 4, 0, 8),
            FontStyle = FontStyles.Italic
        };
        return para;
    }

    // ===== 列表 =====

    private static Block RenderList(string[] lines, ref int i, bool ordered)
    {
        var list = new System.Windows.Documents.List
        {
            MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(20, 2, 0, 6),
            Foreground = BrText
        };

        while (i < lines.Length)
        {
            var line = lines[i];
            var match = ordered
                ? Regex.Match(line, @"^(\s*)\d+\.\s+(.+)$")
                : Regex.Match(line, @"^(\s*)[-*]\s+(.+)$");

            if (!match.Success) break;

            var item = new ListItem();
            var para = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
            RenderInline(para, match.Groups[2].Value);
            item.Blocks.Add(para);
            list.ListItems.Add(item);
            i++;
        }

        return list;
    }
}
