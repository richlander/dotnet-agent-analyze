using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentAnalyze.Core.Parsing;

/// <summary>Outer envelope of every events.jsonl line.</summary>
internal sealed class CopilotEvent
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }
    [JsonPropertyName("parentId")] public string? ParentId { get; set; }
    [JsonPropertyName("data")] public JsonElement Data { get; set; }
}

internal sealed class CopilotSessionStart
{
    [JsonPropertyName("sessionId")] public string? SessionId { get; set; }
    [JsonPropertyName("copilotVersion")] public string? CopilotVersion { get; set; }
    [JsonPropertyName("startTime")] public string? StartTime { get; set; }
    [JsonPropertyName("context")] public CopilotSessionContext? Context { get; set; }
}

internal sealed class CopilotSessionContext
{
    [JsonPropertyName("cwd")] public string? Cwd { get; set; }
}

internal sealed class CopilotModelChange
{
    [JsonPropertyName("newModel")] public string? NewModel { get; set; }
}

internal sealed class CopilotUserMessage
{
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("transformedContent")] public string? TransformedContent { get; set; }
    [JsonPropertyName("parentAgentTaskId")] public string? ParentAgentTaskId { get; set; }
}

internal sealed class CopilotAssistantMessage
{
    [JsonPropertyName("messageId")] public string? MessageId { get; set; }
    [JsonPropertyName("turnId")] public string? TurnId { get; set; }
    [JsonPropertyName("requestId")] public string? RequestId { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("outputTokens")] public int? OutputTokens { get; set; }
    [JsonPropertyName("parentToolCallId")] public string? ParentToolCallId { get; set; }
    [JsonPropertyName("toolRequests")] public List<CopilotToolRequest>? ToolRequests { get; set; }
}

internal sealed class CopilotToolRequest
{
    [JsonPropertyName("toolCallId")] public string? ToolCallId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("arguments")] public JsonElement Arguments { get; set; }
}

internal sealed class CopilotToolStart
{
    [JsonPropertyName("toolCallId")] public string? ToolCallId { get; set; }
    [JsonPropertyName("toolName")] public string? ToolName { get; set; }
    [JsonPropertyName("turnId")] public string? TurnId { get; set; }
    [JsonPropertyName("arguments")] public JsonElement Arguments { get; set; }
}

internal sealed class CopilotToolComplete
{
    [JsonPropertyName("toolCallId")] public string? ToolCallId { get; set; }
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("result")] public CopilotToolResult? Result { get; set; }
}

internal sealed class CopilotToolResult
{
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("detailedContent")] public string? DetailedContent { get; set; }
}

internal sealed class CopilotTurnStart
{
    [JsonPropertyName("turnId")] public string? TurnId { get; set; }
    [JsonPropertyName("interactionId")] public string? InteractionId { get; set; }
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(CopilotEvent))]
[JsonSerializable(typeof(CopilotSessionStart))]
[JsonSerializable(typeof(CopilotModelChange))]
[JsonSerializable(typeof(CopilotUserMessage))]
[JsonSerializable(typeof(CopilotAssistantMessage))]
[JsonSerializable(typeof(CopilotToolRequest))]
[JsonSerializable(typeof(CopilotToolStart))]
[JsonSerializable(typeof(CopilotToolComplete))]
[JsonSerializable(typeof(CopilotToolResult))]
[JsonSerializable(typeof(CopilotTurnStart))]
[JsonSerializable(typeof(List<CopilotToolRequest>))]
internal partial class CopilotJsonContext : JsonSerializerContext
{
}
