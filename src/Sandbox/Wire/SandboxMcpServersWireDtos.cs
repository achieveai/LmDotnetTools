using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.Sandbox.Wire;

// --- Plugin MCP servers REST contract (gateway issue #224, crates/mcp-gateway/src/api/mcp_servers.rs) ---
// GET /api/v1/sandboxes/{session_id}/mcp-servers. Required fields are nullable here so a missing one is reported as
// a Protocol error by the mapper, not defaulted. SECRET: url, header values and extra can carry credentials; these
// DTOs are never logged.

internal sealed record McpServersResponseDto(
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("mcp_servers")] IReadOnlyList<McpServerDto?>? McpServers
);

internal sealed record McpServerDto(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("plugin")] string? Plugin,
    [property: JsonPropertyName("marketplace")] string? Marketplace,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("supported")] bool? Supported,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("headers")] IReadOnlyList<McpHeaderDto?>? Headers,
    [property: JsonPropertyName("extra")] Dictionary<string, JsonElement>? Extra
);

internal sealed record McpHeaderDto(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("value")] string? Value,
    [property: JsonPropertyName("placeholder")] string? Placeholder
);
