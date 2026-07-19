using System.Text;

namespace ClaudeGui;

/// <summary>单条聊天记录（用户或 AI）</summary>
public class ChatRecordEntry
{
    public DateTime Timestamp { get; set; }
    public string Role { get; set; } = "";  // "user" | "assistant"
    public string Content { get; set; } = "";
    public string FileLabel { get; set; } = "";  // 来源说明
}

/// <summary>聊天记录 .md 文件解析器</summary>
public static class ChatRecordParser
{
    /// <summary>枚举当前项目所有聊天记录文件（当前 + 归档），按从旧到新排序</summary>
    public static List<(string Path, string Label)> EnumerateChatFiles(string projectDir)
    {
        var files = new List<(string, string)>();

        // 1. 归档文件 .claude/memory/聊天记录N.md
        var memDir = Path.Combine(projectDir, ".claude", "memory");
        if (Directory.Exists(memDir))
        {
            foreach (var f in Directory.GetFiles(memDir, "聊天记录*.md")
                         .OrderBy(f => ParseArchiveSeq(f)))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                files.Add((f, $"归档 {name}"));
            }
        }

        // 2. 当前文件
        var current = Path.Combine(projectDir, "聊天记录.md");
        if (File.Exists(current))
            files.Add((current, "当前记录"));

        return files;
    }

    /// <summary>从归档文件名解析序号（聊天记录N.md → N）</summary>
    private static int ParseArchiveSeq(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path); // "聊天记录3"
        var digits = new string(name.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : 0;
    }

    /// <summary>惰性解析单个 .md 文件，yield return 每条记录</summary>
    public static IEnumerable<ChatRecordEntry> ParseFile(string filePath, string fileLabel)
    {
        // 状态
        const int StateInit = 0;
        const int StateUser = 1;
        const int StateSep = 2;
        const int StateAssistant = 3;

        int state = StateInit;
        DateTime currentTs = default;
        var userContent = new StringBuilder();
        var assistantContent = new StringBuilder();
        bool hasAssistant = false;

        foreach (var rawLine in File.ReadLines(filePath))
        {
            var line = rawLine.TrimEnd();

            // 检测时间戳头 === yyyy-MM-dd HH:mm:ss ===（任意状态都生效，开启新会话）
            if (line.StartsWith("===") && line.EndsWith("==="))
            {
                // 结算上一轮
                FlushEntry(currentTs, userContent, assistantContent, hasAssistant, fileLabel);
                if (currentTs != default)
                {
                    if (userContent.Length > 0)
                        yield return new ChatRecordEntry { Timestamp = currentTs, Role = "user", Content = userContent.ToString().Trim(), FileLabel = fileLabel };
                    if (hasAssistant && assistantContent.Length > 0)
                        yield return new ChatRecordEntry { Timestamp = currentTs, Role = "assistant", Content = assistantContent.ToString().Trim(), FileLabel = fileLabel };
                }

                // 解析时间
                var tsStr = line.TrimStart('=', ' ').TrimEnd('=', ' ');
                if (!DateTime.TryParseExact(tsStr, "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out currentTs))
                    DateTime.TryParse(tsStr, out currentTs); // fallback
                userContent.Clear();
                assistantContent.Clear();
                hasAssistant = false;
                state = StateInit;
                continue;
            }

            // 👤 用户: 开头 — 只在 StateInit 下生效（AI 回复中的 👤 不会误触发）
            if (state == StateInit && line.StartsWith("👤 ") && line.Contains(": "))
            {
                var colonIdx = line.IndexOf(": ");
                if (colonIdx > 0)
                {
                    userContent.Append(line.AsSpan((colonIdx + 2)..));
                    state = StateUser;
                    continue;
                }
            }

            // --- 分隔行 — 只在 StateUser 下生效（用户多行 prompt 中的 --- 不会误触发）
            if (line.Trim() == "---" && state == StateUser)
            {
                state = StateSep;
                continue;
            }

            // 🤖 Claude: 开头 — 只在 StateSep 下生效（AI 回复中的 🤖 不会误触发）
            if (state == StateSep && line.StartsWith("🤖 ") && line.Contains(":"))
            {
                hasAssistant = true;
                var colonIdx = line.IndexOf(": ");
                if (colonIdx > 0) assistantContent.Append(line.AsSpan((colonIdx + 2)..));
                state = StateAssistant;
                continue;
            }

            // 内容行 — 仅在明确状态下累加
            if (state == StateUser && !string.IsNullOrWhiteSpace(line))
            {
                userContent.Append('\n');
                userContent.Append(line);
            }
            else if (state == StateAssistant)
            {
                assistantContent.Append('\n');
                assistantContent.Append(line);
            }
        }

        // 文件尾结算
        if (currentTs != default)
        {
            if (userContent.Length > 0)
                yield return new ChatRecordEntry { Timestamp = currentTs, Role = "user", Content = userContent.ToString().Trim(), FileLabel = fileLabel };
            if (hasAssistant && assistantContent.Length > 0)
                yield return new ChatRecordEntry { Timestamp = currentTs, Role = "assistant", Content = assistantContent.ToString().Trim(), FileLabel = fileLabel };
        }
    }

    /// <summary>清空 StringBuilder（复用对象减少分配）</summary>
    private static void FlushEntry(DateTime ts, StringBuilder userSb, StringBuilder assistantSb, bool hasAssistant, string label)
    {
        // 方法级注释：此处在未来可改为写入缓存，当前仅占位保持语义对齐
    }
}
