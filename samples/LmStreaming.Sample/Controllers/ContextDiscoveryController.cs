using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using AchieveAi.LmDotnetTools.LmAgentInfra.Context;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using LmStreaming.Sample.Services.Discovery;
using Microsoft.AspNetCore.Mvc;

namespace LmStreaming.Sample.Controllers;

/// <summary>
/// Known values of <see cref="ContextDiscoveryPayload.Kind"/>. Strings (not an enum) because
/// the gateway may add new kinds without breaking older app builds — unknown kinds are accepted
/// and logged as a no-op rather than rejected.
/// </summary>
internal static class ContextDiscoveryKinds
{
    public const string SubAgent = "subagent";
    public const string ContextFile = "context_file";

    /// <summary>A workspace skill; a tiered conversation indexes it and tells its owner (SC6).</summary>
    public const string Skill = "skill";
}

/// <summary>
/// Webhook endpoint the sandbox gateway calls when it discovers a new context file in the
/// workspace (sub-agent, skill, …). For <c>kind == "subagent"</c> payloads this resolves the
/// session, loads the markdown via <see cref="WorkspaceSubAgentLoader.LoadOneAsync"/>, and
/// registers the resulting template into the session's
/// <see cref="MutableSubAgentTemplateSource"/> so it surfaces in the Agent tool on the next turn.
/// Other kinds are still log-only.
/// </summary>
/// <remarks>
/// SECURITY: mirrors <see cref="AchieveAi.LmDotnetTools.LmAgentInfra.Controllers.AuthWebhookController"/>'s pattern: the gateway authenticates
/// itself with a shared secret in the <c>Authorization</c> header, compared in constant time
/// over fixed-width hashes. The controller NEVER logs the Authorization header value or the
/// shared secret — only the discovery payload's kind/name/path and the activation decision.
/// </remarks>
[ApiController]
[Route("api/discovery")]
public sealed class ContextDiscoveryController(
    SessionSecretStore sessionSecretStore,
    ContextDiscoveryBatchHandler batchHandler,
    ILogger<ContextDiscoveryController> logger
) : ControllerBase
{
    /// <summary>
    /// Gateway callback for a batch of discovered context items. The gateway batches every newly
    /// discovered item for a session into one POST — a <see cref="ContextDiscoveryEnvelope"/>
    /// carrying the session id plus a <c>discoveries</c> array. Returns 200 on success (including
    /// best-effort no-ops), 401 if the shared secret doesn't match, 400 if the envelope is null or
    /// any item is malformed for its kind.
    /// </summary>
    [HttpPost("context_discovery")]
    public async Task<IActionResult> NotifyAsync(
        [FromBody] ContextDiscoveryEnvelope? body,
        CancellationToken cancellationToken
    )
    {
        if (body is null)
        {
            logger.LogWarning("Rejected context-discovery webhook with malformed payload.");
            return BadRequest();
        }

        if (string.IsNullOrEmpty(body.SessionId))
        {
            logger.LogWarning("Rejected context-discovery webhook call: missing session id.");
            return Unauthorized();
        }

        if (
            !await sessionSecretStore.MatchesAsync(
                body.SessionId,
                Request.Headers.Authorization.ToString(),
                cancellationToken
            )
        )
        {
            // Do not reveal whether the header was missing, malformed, or simply wrong.
            logger.LogWarning("Rejected unauthorized context-discovery webhook call.");
            return Unauthorized();
        }

        if (!await batchHandler.HandleAsync(body, cancellationToken).ConfigureAwait(false))
        {
            return BadRequest();
        }

        return Ok();
    }
}

/// <summary>
/// Internal helper that wraps the controller's payload fields in the shape
/// <see cref="WorkspaceSubAgentLoader.LoadOneAsync"/> expects, without leaking the controller's
/// public payload contract into the loader.
/// </summary>
internal readonly record struct SubAgentSessionRegistryItem(
    string Kind,
    string Name,
    string? Description,
    string Path,
    string? Content,
    string? QualifiedName
)
{
    public SandboxSessionRegistry.DiscoveredItem ToDiscovered() =>
        new(Kind, Name, Description, Path, Content, QualifiedName);
}

/// <summary>
/// Gateway → app batch payload for a <c>context_discovery</c> webhook delivery. The gateway
/// batches every newly-discovered item for a session into ONE POST: a top-level envelope carrying
/// the session id plus a <c>discoveries</c> array of per-kind items (see SandboxedOstoolsMcpServer
/// <c>Docs/context-discovery.md</c> §Webhook payload). Field names are the gateway's wire contract
/// (snake_case), pinned via <see cref="JsonPropertyNameAttribute"/>. Unknown fields (e.g.
/// <c>event</c>, <c>app_id</c>) are tolerated by System.Text.Json so the gateway can evolve its
/// envelope without breaking older app builds.
/// </summary>
public sealed record ContextDiscoveryEnvelope
{
    [JsonPropertyName("event")]
    public string? Event { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    [JsonPropertyName("app_id")]
    public string? AppId { get; init; }

    /// <summary>
    /// The agent this batch is delivered to (gateway ADR 0040): the id a sub-agent presented on
    /// <c>X-Sbx-Agent-ID</c>, or <c>""</c> for the default (primary) agent. The gateway notifies per
    /// agent, so the same discovery may arrive once per agent id.
    /// </summary>
    [JsonPropertyName("agent_id")]
    public string? AgentId { get; init; }

    [JsonPropertyName("discoveries")]
    public List<ContextDiscoveryItem>? Discoveries { get; init; }
}

/// <summary>
/// One discovered item inside a <see cref="ContextDiscoveryEnvelope"/>, discriminated by
/// <see cref="Kind"/>. Only the fields the app acts on are bound here; the gateway's richer
/// per-kind fields (<c>frontmatter</c>/<c>prompt</c>/<c>directory</c>/<c>plugin</c>/…) are
/// tolerated and ignored.
/// </summary>
public sealed record ContextDiscoveryItem
{
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("qualified_name")]
    public string? QualifiedName { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>Body of a discovered context file (CLAUDE.md / AGENTS.md). Present for
    /// <c>kind == "context_file"</c> items; other kinds resolve their content elsewhere.</summary>
    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("truncated")]
    public bool? Truncated { get; init; }

    /// <summary>Optional id/name of the sub-agent that triggered this discovery (cf. #187). Stamped
    /// per item, so the controller copies it onto the per-item <see cref="ContextDiscoveryPayload"/>
    /// rather than reading a batch-wide value.</summary>
    [JsonPropertyName("agent_id")]
    public string? AgentId { get; init; }
}
