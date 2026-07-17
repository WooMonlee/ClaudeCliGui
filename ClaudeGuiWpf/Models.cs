using System.Text.Json.Serialization;

namespace ClaudeGui;

public class ProjectConfig
{
    [JsonPropertyName("projects")] public List<ProjectEntry> Projects { get; set; } = new();
    [JsonPropertyName("setupCompleted")] public bool SetupCompleted { get; set; }
    [JsonPropertyName("claudeDir")] public string? ClaudeDir { get; set; }
    [JsonPropertyName("apiProviders")] public List<ApiProviderConfig> ApiProviders { get; set; } = new();
    [JsonPropertyName("activeProvider")] public string? ActiveProviderName { get; set; }
    // 预载入策略: "current" | "lastN" | "all" | "smart"
    [JsonPropertyName("preloadStrategy")] public string PreloadStrategy { get; set; } = "lastN";
    [JsonPropertyName("preloadCount")] public int PreloadCount { get; set; } = 3;
    [JsonPropertyName("lastOpenDir")] public string? LastOpenDir { get; set; }
    [JsonPropertyName("lastActiveProject")] public string? LastActiveProject { get; set; }
}

public class ProjectEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("lastAccessedAt")] public DateTime LastAccessedAt { get; set; }
    [JsonPropertyName("accessCount")] public int AccessCount { get; set; }
    [JsonPropertyName("sortOrder")] public int SortOrder { get; set; }
    [JsonPropertyName("permissionMode")] public string PermissionMode { get; set; } = "bypassPermissions"; // bypassPermissions | plan | acceptEdits | manual
}

// API 提供商配置
public class ApiProviderConfig
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("baseUrl")] public string BaseUrl { get; set; } = "";
    [JsonPropertyName("apiKey")] public string ApiKey { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("smallFastModel")] public string SmallFastModel { get; set; } = "";
    [JsonPropertyName("defaultOpusModel")] public string DefaultOpusModel { get; set; } = "";
    [JsonPropertyName("defaultSonnetModel")] public string DefaultSonnetModel { get; set; } = "";
    [JsonPropertyName("defaultHaikuModel")] public string DefaultHaikuModel { get; set; } = "";
    [JsonPropertyName("defaultFableModel")] public string DefaultFableModel { get; set; } = "";
    [JsonPropertyName("priority")] public int Priority { get; set; } // 0=主, 1=备1, 2=备2, ... 99=禁用
    [JsonPropertyName("tags")] public string Tags { get; set; } = ""; // 逗号分隔: "多模态,长文本,便宜"
}

// 聊天快照（MainWindow 历史加载用）
public class ChatSnapshotEntry
{
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    [JsonPropertyName("content")] public string Content { get; set; } = "";
    [JsonPropertyName("timestamp")] public DateTime Timestamp { get; set; }
}
