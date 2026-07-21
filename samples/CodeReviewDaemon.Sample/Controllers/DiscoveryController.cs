using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using Microsoft.AspNetCore.Mvc;

namespace CodeReviewDaemon.Sample.Controllers;

/// <summary>
/// Receives the sandbox gateway's context-discovery webhook (<c>POST /api/discovery/context_discovery</c>).
/// The gateway is told to deliver discovered <c>CLAUDE.md</c>/<c>AGENTS.md</c>/sub-agent files here
/// whenever the auth webhook is configured (<see cref="AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox.SandboxSessionRegistry"/>
/// <c>BuildDiscovery</c>). If this route is <b>missing</b> the gateway sees a 404 and <b>tears down the
/// sandbox session</b> mid-review, killing the review agent's MCP connection (observed live: mcqdb PR
/// #11197 failed 194× when the route 404'd). Its existence — returning 200 for the gateway's authenticated
/// call — is what keeps the session alive.
/// </summary>
/// <remarks>
/// The daemon does NOT consume pushed discoveries: it already pulls discovered items host-side
/// (<c>RegistryDiscoverySource</c>) and prepends the reviewed repo's guidance to the review input, so
/// injecting a discovery mid-run into the headless collect-only review loop would be both redundant and
/// unsafe (it would restart the collector's generation and could discard the real review). This endpoint
/// therefore authenticates exactly like <see cref="AchieveAi.LmDotnetTools.LmAgentInfra.Controllers.AuthWebhookController"/>
/// and LmStreaming's <c>ContextDiscoveryController</c> — a <b>per-session</b> secret
/// (<see cref="SessionSecretStore"/>) carried in the <c>Authorization</c> header, keyed by the
/// <c>session_id</c> in the body — and returns <b>200 accept-and-ignore</b>; a bad/missing secret or
/// session id ⇒ 401. Only the session id is bound from the body (the discovery payload itself is ignored).
/// </remarks>
[ApiController]
[Route("api/discovery")]
public sealed class DiscoveryController(
    SessionSecretStore sessionSecretStore,
    ILogger<DiscoveryController> logger) : ControllerBase
{
    /// <summary>
    /// Authenticated gateway callback carrying a batch of discovered items. Returns 200 for any
    /// authenticated call (the discoveries are ignored here — see the type remarks), 401 on a bad or
    /// missing per-session secret / session id.
    /// </summary>
    [HttpPost("context_discovery")]
    public async Task<IActionResult> ContextDiscovery(
        [FromBody] DiscoveryAuthEnvelope? body,
        CancellationToken cancellationToken = default)
    {
        if (body is null || string.IsNullOrEmpty(body.SessionId))
        {
            logger.LogWarning("Rejected context-discovery webhook call: missing session id.");
            return Unauthorized();
        }

        if (!await sessionSecretStore.MatchesAsync(body.SessionId, Request.Headers.Authorization.ToString(), cancellationToken))
        {
            // Do not reveal whether the header was missing, malformed, or simply wrong.
            logger.LogWarning("Rejected unauthorized context-discovery webhook call.");
            return Unauthorized();
        }

        logger.LogDebug("Context-discovery webhook accepted (accept-and-ignore) for session {SessionId}.", body.SessionId);
        return Ok();
    }
}

/// <summary>
/// Minimal binding target: only the gateway's <c>session_id</c> is needed to authenticate the call
/// against the per-session <see cref="SessionSecretStore"/>. The rest of the discovery envelope
/// (<c>discoveries[]</c>, …) is intentionally not bound — the daemon ignores pushed discoveries (it
/// pulls them host-side). Unknown fields are tolerated by System.Text.Json so the gateway can evolve
/// its envelope without breaking this endpoint.
/// </summary>
public sealed record DiscoveryAuthEnvelope
{
    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }
}
