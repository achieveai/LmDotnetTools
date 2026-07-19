using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using Microsoft.AspNetCore.Mvc;

namespace CodeReviewDaemon.Sample.Controllers;

/// <summary>
/// Receives the sandbox gateway's context-discovery webhook (<c>POST /api/discovery/context_discovery</c>).
/// The gateway is told to deliver discovered <c>CLAUDE.md</c>/<c>AGENTS.md</c>/sub-agent files here
/// whenever the auth webhook is configured (<see cref="AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox.SandboxSessionRegistry"/>
/// <c>BuildDiscovery</c>). If this route returns any non-2xx the gateway <b>tears down the sandbox
/// session</b> mid-review (observed live: mcqdb PR #11197 failed 194× when the route 404'd).
/// </summary>
/// <remarks>
/// The daemon does NOT consume pushed discoveries: it already pulls discovered items host-side
/// (<c>RegistryDiscoverySource</c>) and prepends the reviewed repo's guidance to the review input, so
/// injecting a discovery mid-run into the headless collect-only review loop would be both redundant and
/// unsafe (it would restart the collector's generation and could discard the real review). This endpoint
/// therefore authenticates exactly like <c>AuthWebhookController</c> (shared secret in the
/// <c>Authorization</c> header) and returns <b>200 accept-and-ignore</b>. Returning 200 even for an
/// unactionable or malformed body is deliberate — a 400 would tear the session down just as a 404 does —
/// so the body is never bound (no automatic model-state 400).
/// </remarks>
[Route("api/discovery")]
public sealed class DiscoveryController(
    AuthSharedSecret sharedSecret,
    ILogger<DiscoveryController> logger) : ControllerBase
{
    /// <summary>
    /// Authenticated gateway callback carrying a batch of discovered items. Returns 200 for any
    /// authenticated call (the discoveries are ignored here — see the type remarks), 401 on a bad or
    /// missing shared secret.
    /// </summary>
    [HttpPost("context_discovery")]
    public IActionResult ContextDiscovery()
    {
        if (!sharedSecret.Matches(Request.Headers.Authorization.ToString()))
        {
            // Do not reveal whether the header was missing, malformed, or simply wrong.
            logger.LogWarning("Rejected unauthorized context-discovery webhook call.");
            return Unauthorized();
        }

        logger.LogDebug("Context-discovery webhook accepted (accept-and-ignore).");
        return Ok();
    }
}
