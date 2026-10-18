using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.Sandbox.Wire;

// --- Per-agent state REST contract (gateway ADR 0040, crates/mcp-gateway/src/api/sandboxes.rs) ---
// GET /api/v1/sandboxes/{session_id}/agents → { session_id, agents: [SessionAgent] }. Every field of
// SessionAgent is non-optional upstream; the DTO keeps them nullable so a missing one is reported as a
// Protocol error by the mapper instead of silently defaulting.

internal sealed record SessionAgentDto(
    [property: JsonPropertyName("agent_id")] string? AgentId,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt,
    [property: JsonPropertyName("last_used_at")] DateTimeOffset? LastUsedAt,
    [property: JsonPropertyName("open_tasks")] int? OpenTasks,
    [property: JsonPropertyName("loaded_skills")] int? LoadedSkills
);

internal sealed record SessionAgentsResponseDto(
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("agents")] IReadOnlyList<SessionAgentDto?>? Agents
);
