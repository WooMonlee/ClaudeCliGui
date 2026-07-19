using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace ClaudeGui;

/// <summary>
/// Markdown → WPF FlowDocument Block 列表
/// 支持：标题、粗斜体、代码块、表格、列表、引用、分割线、行内代码
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
            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }
            if (line.TrimStart().StartsWith("```")) { blocks.Add(RenderCodeBlock(lines, ref i)); continue; }
            if (line.Contains('|') && IsTable(lines, i)) { blocks.Add(RenderTable(lines, ref i)); continue; }
            var hm = Regex.Match(line, @"^(#{1,6})\s+(.+)$");
            if (hm.Success) { blocks.Add(CreateHeading(hm.Groups[2].Value, hm.Groups[1].Length)); i++; continue; }
            if (Regex.IsMatch(line.Trim(), @"^[-*_]{3,}$")) { blocks.Add(new Paragraph(new Run("─".PadRight(60, '─'))) { Foreground = BrTableBorder, Margin = new Thickness(0, 8, 0, 8) }); i++; continue; }
            if (line.TrimStart().StartsWith(">")) { blocks.Add(RenderBlockquote(lines, ref i)); continue; }
            var um = Regex.Match(line, @"^(\s*)[-*]\s+(.+)$");
            if (um.Success) { blocks.Add(RenderList(lines, ref i, false)); continue; }
            var om = Regex.Match(line, @"^(\s*)\d+\.\s+(.+)$");
            if (om.Success) { blocks.Add(RenderList(lines, ref i, true)); continue; }
            blocks.Add(RenderParagraph(lines, ref i));
        }
        return blocks;
    }

    private static Paragraph RenderParagraph(string[] lines, ref int i)
    {
        var para = new Paragraph { Margin = new Thickness(0, 2, 0, 6), LineHeight = 20 };
        var text = lines[i].Trim(); i++;
        while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) && !IsSpecialLine(lines[i])) { text += "\n" + lines[i].Trim(); i++; }
        RenderInline(para, text);
        return para;
    }

    private static bool IsSpecialLine(string l) => l.TrimStart().StartsWith("```") || l.Contains('|') || Regex.IsMatch(l, @"^#{1,6}\s") || Regex.IsMatch(l, @"^[-*_]{3,}$") || l.TrimStart().StartsWith(">") || Regex.IsMatch(l, @"^\s*[-*]\s+") || Regex.IsMatch(l, @"^\s*\d+\.\s+");

    private static void RenderInline(Paragraph para, string text)
    {
        var pattern = @"(\*\*(.+?)\*\*)|(`(.+?)`)|(\[(.+?)\]\((.+?)\))|(\*(.+?)\*)";
        int last = 0;
        foreach (Match m in Regex.Matches(text, pattern))
        {
            if (m.Index > last) para.Inlines.Add(new Run(text[last..m.Index]) { Foreground = BrText });
            if (m.Groups[1].Success) para.Inlines.Add(new Bold(new Run(m.Groups[2].Value) { Foreground = BrText }));
            else if (m.Groups[3].Success) para.Inlines.Add(new Run(m.Groups[4].Value) { Foreground = BrCode });
            else if (m.Groups[5].Success)
            {
                var hl = new Hyperlink(new Run(m.Groups[6].Value) { Foreground = BrCode }) { NavigateUri = new Uri(m.Groups[7].Value), ToolTip = m.Groups[7].Value };
                hl.RequestNavigate += (_, e) =>
                {
                    try
                    {
                        if (e.Uri.Scheme is "http" or "https")
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.ToString()) { UseShellExecute = true });
                    }
                    catch { }
                };
                para.Inlines.Add(hl);
            }
            else if (m.Groups[8].Success) para.Inlines.Add(new Italic(new Run(m.Groups[9].Value) { Foreground = BrText }));
            last = m.Index + m.Length;
        }
        if (last < text.Length) para.Inlines.Add(new Run(text[last..]) { Foreground = BrText });
        if (last == 0 && para.Inlines.Count == 0) para.Inlines.Add(new Run(text) { Foreground = BrText });
    }

    private static Block RenderCodeBlock(string[] lines, ref int i)
    {
        i++;
        var sb = new System.Text.StringBuilder();
        while (i < lines.Length && !lines[i].TrimStart().StartsWith("```")) { sb.AppendLine(lines[i]); i++; }
        i++;
        return new Paragraph(new Run(sb.ToString().TrimEnd())) { Background = BrCodeBg, Foreground = BrCode, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12, Margin = new Thickness(0, 4, 0, 8), Padding = new Thickness(12, 8, 12, 8), LineHeight = 18 };
    }

    private static bool IsTable(string[] lines, int start)
    {
        if (start + 1 >= lines.Length) return false;
        var l1 = lines[start];
        var l2 = lines[start + 1];
        if (!l1.Contains('|') || !l2.Contains('|')) return false;
        // 分隔行：仅由 |、-、:、空格组成（含对齐冒号 |:---|:---:|）
        return Regex.IsMatch(l2.Trim(), @"^[\s\-:|:]+$");
    }

    private static bool IsTableLine(string l) => l.Contains('|') && Regex.IsMatch(l.Trim(), @"^[\s\-:|:]+$") || (l.Contains('|') && !l.TrimStart().StartsWith("```"));

    private static Block RenderTable(string[] lines, ref int i)
    {
        var tableLines = new List<string>();
        while (i < lines.Length && lines[i].Contains('|')) { tableLines.Add(lines[i].Trim()); i++; }
        // 跳过纯分隔行
        var dataLines = tableLines.Where(l => !Regex.IsMatch(l, @"^[\s\-:|:]+$")).ToList();
        if (dataLines.Count < 1) return new Paragraph(new Run("(空表格)"));
        var headers = ParseRow(dataLines[0]);
        var aligns = tableLines.Count > 1 ? ParseRow(tableLines[1]) : new List<string>();
        var table = new Table { BorderBrush = BrTableBorder, BorderThickness = new Thickness(1), CellSpacing = 0, Margin = new Thickness(0, 6, 0, 10) };
        foreach (var h in headers) table.Columns.Add(new TableColumn { Width = GridLength.Auto });
        var hg = new TableRowGroup(); var hr = new TableRow();
        for (int c = 0; c < headers.Count; c++) hr.Cells.Add(new TableCell(new Paragraph(new Run(headers[c].Trim()) { Foreground = BrHeading, FontWeight = System.Windows.FontWeights.Bold }) { Margin = new Thickness(0), Padding = new Thickness(8, 4, 8, 4) }) { Background = BrTableHeader, BorderBrush = BrTableBorder, BorderThickness = new Thickness(1), TextAlignment = GetAlign(aligns, c) });
        hg.Rows.Add(hr); table.RowGroups.Add(hg);
        var dg = new TableRowGroup();
        for (int r = 1; r < dataLines.Count; r++) { var cols = ParseRow(dataLines[r]); var row = new TableRow(); for (int c = 0; c < Math.Min(cols.Count, headers.Count); c++) row.Cells.Add(new TableCell(new Paragraph(new Run(cols[c].Trim()) { Foreground = BrText }) { Margin = new Thickness(0), Padding = new Thickness(8, 4, 8, 4) }) { Background = BrTableCell, BorderBrush = BrTableBorder, BorderThickness = new Thickness(1), TextAlignment = GetAlign(aligns, c) }); for (int c = cols.Count; c < headers.Count; c++) row.Cells.Add(new TableCell(new Paragraph()) { BorderBrush = BrTableBorder, BorderThickness = new Thickness(1) }); dg.Rows.Add(row); }
        table.RowGroups.Add(dg); return table;
    }
    private static List<string> ParseRow(string l) => l.Trim('|').Split('|').Select(c => c.Trim()).ToList();
    private static TextAlignment GetAlign(List<string> a, int c) => c >= a.Count ? TextAlignment.Left : a[c].Trim().EndsWith(":") && a[c].Trim().StartsWith(":") ? TextAlignment.Center : a[c].Trim().EndsWith(":") ? TextAlignment.Right : TextAlignment.Left;

    private static Paragraph CreateHeading(string text, int level) { double s = level switch { 1 => 22, 2 => 18, 3 => 16, _ => 14 }; var p = new Paragraph { Margin = new Thickness(0, 8, 0, 4), LineHeight = s + 6 }; p.Inlines.Add(new Run(text) { Foreground = BrHeading, FontWeight = System.Windows.FontWeights.Bold, FontSize = s }); return p; }

    private static Paragraph RenderBlockquote(string[] lines, ref int i) { var sb = new System.Text.StringBuilder(); while (i < lines.Length && lines[i].TrimStart().StartsWith(">")) { sb.AppendLine(lines[i].TrimStart().TrimStart('>').TrimStart()); i++; } return new Paragraph(new Run(sb.ToString().TrimEnd())) { Foreground = BrQuote, BorderBrush = BrCode, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 0, 0, 0), Margin = new Thickness(0, 4, 0, 8), FontStyle = FontStyles.Italic }; }

    private static Block RenderList(string[] lines, ref int i, bool ordered) { var l = new System.Windows.Documents.List { MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Margin = new Thickness(20, 2, 0, 6), Foreground = BrText }; while (i < lines.Length) { var m = ordered ? Regex.Match(lines[i], @"^(\s*)\d+\.\s+(.+)$") : Regex.Match(lines[i], @"^(\s*)[-*]\s+(.+)$"); if (!m.Success) break; var item = new ListItem(); var p = new Paragraph { Margin = new Thickness(0, 1, 0, 1) }; RenderInline(p, m.Groups[2].Value); item.Blocks.Add(p); l.ListItems.Add(item); i++; } return l; }
}
