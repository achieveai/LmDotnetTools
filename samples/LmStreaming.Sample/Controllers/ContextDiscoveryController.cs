using System.Text.Json.Serialization;
using LmStreaming.Sample.Services.Auth;
using Microsoft.AspNetCore.Mvc;

namespace LmStreaming.Sample.Controllers;

/// <summary>
/// Webhook endpoint the sandbox gateway calls when it discovers a new context file in the
/// workspace (sub-agent, skill, …). This iteration is <b>log-only</b>: the controller verifies
/// the shared secret in constant time, logs a structured event, and returns 200. There is no
/// in-memory queue and no dynamic activation — those are tracked in #77 and land with their
/// consumer (mid-session sub-agent catalog refresh / dynamic Agent tool reload).
/// </summary>
/// <remarks>
/// SECURITY: mirrors <see cref="AuthWebhookController"/>'s pattern: the gateway authenticates
/// itself with a shared secret in the <c>Authorization</c> header, compared in constant time
/// over fixed-width hashes. The controller NEVER logs the Authorization header value or the
/// shared secret — only the discovery payload's kind/name/path and the allow decision.
/// </remarks>
[ApiController]
[Route("api/discovery")]
public sealed class ContextDiscoveryController(
    AuthSharedSecret sharedSecret,
    ILogger<ContextDiscoveryController> logger) : ControllerBase
{
    /// <summary>
    /// Gateway callback for one discovered context item. Returns 200 on success, 401 if the
    /// shared secret doesn't match, 400 if the payload is malformed.
    /// </summary>
    [HttpPost("context_discovery")]
    public IActionResult Notify([FromBody] ContextDiscoveryPayload? body)
    {
        if (!sharedSecret.Matches(Request.Headers.Authorization.ToString()))
        {
            // Do not reveal whether the header was missing, malformed, or simply wrong.
            logger.LogWarning("Rejected unauthorized context-discovery webhook call.");
            return Unauthorized();
        }

        if (body is null || string.IsNullOrWhiteSpace(body.Kind) || string.IsNullOrWhiteSpace(body.Name))
        {
            logger.LogWarning("Rejected context-discovery webhook with malformed payload.");
            return BadRequest();
        }

        // Description is logged alongside kind/name/path so the field carries diagnostic value
        // in this iteration even though dynamic activation (the consumer that mutates the catalog
        // from these events) is deferred to #77.
        logger.LogInformation(
            "ContextDiscovery: kind={Kind} name={Name} path={Path} description={Description}",
            body.Kind,
            body.Name,
            body.Path,
            body.Description);

        return Ok();
    }
}

/// <summary>
/// Gateway → app payload for a single discovered context item. Field names are the gateway's
/// wire contract (snake_case), pinned via <see cref="JsonPropertyNameAttribute"/> so they bind
/// regardless of the app's JSON naming defaults. Unknown fields are tolerated by System.Text.Json
/// by default so the gateway can add new fields without breaking older app builds.
/// </summary>
public sealed record ContextDiscoveryPayload
{
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }
}
