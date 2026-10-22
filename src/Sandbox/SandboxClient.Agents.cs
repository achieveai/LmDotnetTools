using System.Net.Http.Json;
using System.Text.Json;
using AchieveAi.LmDotnetTools.Sandbox.Wire;

namespace AchieveAi.LmDotnetTools.Sandbox;

public sealed partial class SandboxClient
{
    /// <summary>
    /// Lists the agents that have made tool calls on <paramref name="sessionId"/>'s sandbox (gateway ADR
    /// 0040), oldest first. The default agent appears with an empty <see cref="SandboxAgentInfo.AgentId"/>
    /// once it has made a call. An unknown or foreign session is <see cref="SandboxErrorKind.NotFound"/>.
    /// </summary>
    public async Task<IReadOnlyList<SandboxAgentInfo>> ListAgentsAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var operation = $"listing agents for session '{sessionId}'";
        using var response = await SendRestAsync(
                HttpMethod.Get,
                $"api/v1/sandboxes/{Uri.EscapeDataString(sessionId)}/agents",
                body: null,
                sessionId: null,
                ct
            )
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await MapErrorResponseAsync(response, operation, ct).ConfigureAwait(false);
        }

        var statusCode = (int)response.StatusCode;
        SessionAgentsResponseDto? payload;
        try
        {
            payload = await response
                .Content.ReadFromJsonAsync<SessionAgentsResponseDto>(SandboxJson.RestOptions, ct)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned a malformed agents response for session '{sessionId}'.",
                statusCode,
                ex
            );
        }

        if (payload?.Agents is null)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned an agents response without an 'agents' list for session '{sessionId}'.",
                statusCode
            );
        }

        return SelectNonNullOrThrow(
            payload.Agents,
            dto => ToAgentInfo(dto, operation, statusCode),
            operation,
            statusCode
        );
    }

    /// <summary>
    /// Forgets one agent's state on a sandbox (gateway ADR 0040 §4): its read marks, loaded skills,
    /// discovery-notification markers, PowerShell contexts and background tasks. The sandbox, its
    /// discovered rows and every other agent are untouched; a later call under the same id starts a
    /// fresh agent.
    /// </summary>
    /// <remarks>
    /// The gateway's agent-route errors carry no <c>error_code</c>, so they are classified by status:
    /// an agent the sandbox has never seen (or an unknown/foreign session) is
    /// <see cref="SandboxErrorKind.NotFound"/>; the default agent is <see cref="SandboxErrorKind.Conflict"/>
    /// (unreachable from here — the id is validated non-empty first); a sandbox in a maintenance critical
    /// section (<c>503 sandbox_busy</c>) is <see cref="SandboxErrorKind.Unavailable"/>.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="agentId"/> is not a valid, non-default agent id, or is <c>.</c> or <c>..</c>: legal ids, but
    /// URI dot-segment normalization would send the DELETE to a different route, so no URL can address them.
    /// </exception>
    public async Task DeleteAgentAsync(string sessionId, string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SandboxAgentId.ThrowIfInvalid(agentId, nameof(agentId));
        if (agentId is "." or "..")
        {
            throw new ArgumentException(
                $"Agent id '{agentId}' cannot be addressed in a URL path: dot segments are normalized away.",
                nameof(agentId)
            );
        }

        using var response = await SendRestAsync(
                HttpMethod.Delete,
                $"api/v1/sandboxes/{Uri.EscapeDataString(sessionId)}/agents/{Uri.EscapeDataString(agentId)}",
                body: null,
                sessionId: null,
                ct
            )
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await MapErrorResponseAsync(response, $"deleting agent '{agentId}' on session '{sessionId}'", ct)
                .ConfigureAwait(false);
        }
    }

    private static SandboxAgentInfo ToAgentInfo(SessionAgentDto dto, string operation, int statusCode)
    {
        if (
            dto.AgentId is null
            || dto.CreatedAt is not { } createdAt
            || dto.LastUsedAt is not { } lastUsedAt
            || dto.OpenTasks is not { } openTasks
            || dto.LoadedSkills is not { } loadedSkills
            || (dto.AgentId.Length > 0 && !SandboxAgentId.IsValid(dto.AgentId))
        )
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned an agent with a missing or invalid field for {operation}.",
                statusCode
            );
        }

        return new SandboxAgentInfo(dto.AgentId, createdAt, lastUsedAt, openTasks, loadedSkills, dto.IncludedSkills);
    }
}
