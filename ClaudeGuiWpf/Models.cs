using System.Text.Json.Serialization;

namespace ClaudeGui;

public class ProjectConfig
{
    [JsonPropertyName("projects")] public List<ProjectEntry> Projects { get; set; } = new();
    [JsonPropertyName("setupCompleted")] public bool SetupCompleted { get; set; }
    [JsonPropertyName("claudeDir")] public string? ClaudeDir { get; set; }
}

public class ProjectEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("lastAccessedAt")] public DateTime LastAccessedAt { get; set; }
}

// 聊天快照（MainWindow 历史加载用）
public class ChatSnapshotEntry
{
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    [JsonPropertyName("content")] public string Content { get; set; } = "";
    [JsonPropertyName("timestamp")] public DateTime Timestamp { get; set; }
}
