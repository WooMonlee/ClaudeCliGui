using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeGui;

/// <summary>
/// Claude CLI stream-json 输出的每一行消息
/// </summary>
public class ClaudeStreamMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("subtype")]
    public string? Subtype { get; set; }

    // assistant 类型的消息
    [JsonPropertyName("message")]
    public ClaudeMessage? Message { get; set; }

    // result 类型的消息
    [JsonPropertyName("result")]
    public string? Result { get; set; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }

    // system 类型
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    // cost/usage 信息
    [JsonPropertyName("usage")]
    public ClaudeUsage? Usage { get; set; }

    // 原始 JSON，用于转发未知字段
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class ClaudeMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("content")]
    public JsonElement Content { get; set; }
}

public class ClaudeContentBlock
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("input")]
    public JsonElement? Input { get; set; }

    // tool_result
    [JsonPropertyName("tool_use_id")]
    public string? ToolUseId { get; set; }

    [JsonPropertyName("is_error")]
    public bool? IsError { get; set; }

    // thinking
    [JsonPropertyName("thinking")]
    public string? Thinking { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class ClaudeUsage
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }

    [JsonPropertyName("cache_read_input_tokens")]
    public int CacheReadInputTokens { get; set; }

    [JsonPropertyName("cache_creation_input_tokens")]
    public int CacheCreationInputTokens { get; set; }
}

/// <summary>
/// 会话状态
/// </summary>
public class SessionInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string WorkDir { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string? ClaudeSessionId { get; set; }
    public bool IsRunning { get; set; }
    public int TotalInputTokens { get; set; }
    public int TotalOutputTokens { get; set; }
}

/// <summary>
/// 客户端发送的消息
/// </summary>
public class ClientMessage
{
    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";

    [JsonPropertyName("workDir")]
    public string? WorkDir { get; set; }
}

/// <summary>
/// 推送到客户端的包装消息
/// </summary>
public class WsOutgoingMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("data")]
    public object? Data { get; set; }
}

// === 项目管理请求模型 ===

public class CreateProjectRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("parentDir")]
    public string? ParentDir { get; set; }
}

public class AddExistingRequest
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";
}

public class RenameRequest
{
    [JsonPropertyName("newName")]
    public string NewName { get; set; } = "";
}

// === 设置向导 ===

public class SetupRequest
{
    [JsonPropertyName("copyExe")]
    public bool CopyExe { get; set; }

    [JsonPropertyName("installRegistry")]
    public bool InstallRegistry { get; set; }

    [JsonPropertyName("claudeDir")]
    public string? ClaudeDir { get; set; }
}

public class SetupResult
{
    [JsonPropertyName("exeCopied")]
    public bool ExeCopied { get; set; }

    [JsonPropertyName("exeTargetPath")]
    public string? ExeTargetPath { get; set; }

    [JsonPropertyName("exeCopyError")]
    public string? ExeCopyError { get; set; }

    [JsonPropertyName("registryInstalled")]
    public bool RegistryInstalled { get; set; }

    [JsonPropertyName("registryError")]
    public string? RegistryError { get; set; }
}

// === 聊天快照 ===

public class ChatSnapshotEntry
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }
}

// === 历史记录 ===

public class ChatHistoryMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }
}

