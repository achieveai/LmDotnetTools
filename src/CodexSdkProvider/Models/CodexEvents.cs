using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.CodexSdkProvider.Models;

public record CodexEventBase
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }
}

public sealed record CodexThreadStartedEvent : CodexEventBase
{
    [JsonPropertyName("thread_id")]
    public required string ThreadId { get; init; }
}

public sealed record CodexTurnCompletedEvent : CodexEventBase
{
    [JsonPropertyName("usage")]
    public CodexUsage? Usage { get; init; }
}

public sealed record CodexTurnFailedEvent : CodexEventBase
{
    [JsonPropertyName("error")]
    public CodexError? Error { get; init; }
}

public sealed record CodexThreadErrorEvent : CodexEventBase
{
    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

public sealed record CodexItemEvent : CodexEventBase
{
    [JsonPropertyName("item")]
    public required CodexItem Item { get; init; }
}

public sealed record CodexItem
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    [JsonPropertyName("status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; init; }

    [JsonPropertyName("query")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Query { get; init; }

    [JsonPropertyName("command")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Command { get; init; }

    [JsonPropertyName("server")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Server { get; init; }

    [JsonPropertyName("tool")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tool { get; init; }

    [JsonPropertyName("arguments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Arguments { get; init; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Result { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CodexError? Error { get; init; }

    [JsonPropertyName("changes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Changes { get; init; }

    [JsonPropertyName("items")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Items { get; init; }
}

public sealed record CodexUsage
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; init; }

    [JsonPropertyName("cached_input_tokens")]
    public int CachedInputTokens { get; init; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; init; }
}

public sealed record CodexError
{
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}
