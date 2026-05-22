using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.AgentRuntime;

namespace AchieveAi.LmDotnetTools.CopilotSdkProvider.Models;

/// <summary>
/// Options used to start (or resume) a Copilot ACP session. Property names mirror
/// the ACP camelCase protocol wire format.
/// </summary>
public sealed record CopilotBridgeInitOptions
{
    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("cwd")]
    public string? WorkingDirectory { get; init; }

    [JsonPropertyName("baseInstructions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BaseInstructions { get; init; }

    [JsonPropertyName("developerInstructions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeveloperInstructions { get; init; }

    [JsonPropertyName("modelInstructionsFile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModelInstructionsFile { get; init; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<CopilotDynamicToolSpec>? Tools { get; init; }

    [JsonPropertyName("baseUrl")]
    public string? BaseUrl { get; init; }

    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    /// <summary>
    /// External MCP servers to advertise to the Copilot CLI. Non-null/non-empty
    /// values are written to a per-session JSON config file and passed via
    /// <c>--additional-mcp-config=@&lt;path&gt;</c> on the CLI command line. The
    /// ACP <c>session/new</c> request always carries an empty <c>mcpServers</c>
    /// array (it rejects stdio entries) — this dictionary fully replaces it.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, McpServerConfig>? McpServers { get; init; }
}

/// <summary>
/// Envelope that wraps a single Copilot ACP event (either an incoming
/// <c>session/update</c> notification or a synthesized lifecycle event) and
/// carries correlation fields into the translator layer.
/// </summary>
public sealed record CopilotTurnEventEnvelope
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("event")]
    public required JsonElement Event { get; init; }

    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }

    [JsonPropertyName("sessionId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionId { get; init; }
}

/// <summary>
/// Dynamic tool specification sent to the Copilot ACP server on <c>session/new</c>
/// so the agent can call client-side tools.
/// </summary>
public sealed record CopilotDynamicToolSpec
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonPropertyName("inputSchema")]
    public JsonElement InputSchema { get; init; }
}

public sealed record CopilotDynamicToolCallRequest
{
    [JsonPropertyName("sessionId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionId { get; init; }

    [JsonPropertyName("callId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CallId { get; init; }

    [JsonPropertyName("tool")]
    public required string Tool { get; init; }

    [JsonPropertyName("arguments")]
    public JsonElement Arguments { get; init; }
}

public sealed record CopilotDynamicToolCallResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("contentItems")]
    public IReadOnlyList<CopilotDynamicToolContentItem> ContentItems { get; init; } = [];
}

public sealed record CopilotDynamicToolContentItem
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    [JsonPropertyName("imageUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageUrl { get; init; }
}
