using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.Sandbox;
using LmStreaming.Sample.Services.Discovery;
using Microsoft.AspNetCore.Mvc;

namespace LmStreaming.Sample.Controllers;

/// <summary>
/// Receiver for the gateway's unified sandbox webhook (gateway ADR 0043, <c>Docs/webhooks.md</c>), registered
/// when <see cref="WebhookOptions.UseSandboxWebhook"/> is on. Every subscribed event for a session arrives
/// here as one envelope shape; this app acts on <c>context_discovery</c> (same items as the legacy discovery
/// webhook) and <c>task_completed</c> (a background task ended), and acknowledges any other event unread.
/// </summary>
/// <remarks>
/// SECURITY: the gateway presents the per-session secret verbatim as the <c>Authorization</c> header; it is
/// checked in constant time before anything else is read, and never logged. Deduplication on the event id
/// (the <c>Idempotency-Key</c> is <c>session_id/event_id</c>) runs only after authentication, so an
/// unauthenticated caller cannot burn a real event's id.
/// </remarks>
[ApiController]
[Route("api/sandbox/webhook")]
public sealed class SandboxWebhookController(
    SessionSecretStore sessionSecretStore,
    SandboxSessionRegistry sessionRegistry,
    ContextDiscoveryBatchHandler discoveryHandler,
    SandboxTaskCompletionNotifier taskNotifier,
    ILogger<SandboxWebhookController> logger
) : ControllerBase
{
    /// <summary>
    /// Returns 200 for a handled, duplicate or unknown event; 401 for a missing session id or a wrong
    /// secret; 400 for an envelope or event payload that cannot be acted on.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> ReceiveAsync(
        [FromBody] SandboxWebhookEnvelope? envelope,
        CancellationToken cancellationToken
    )
    {
        if (envelope is null)
        {
            logger.LogWarning("Rejected sandbox webhook call with a malformed envelope.");
            return BadRequest();
        }

        if (string.IsNullOrEmpty(envelope.SessionId))
        {
            logger.LogWarning("Rejected sandbox webhook call: missing session id.");
            return Unauthorized();
        }

        if (
            !await sessionSecretStore.MatchesAsync(
                envelope.SessionId,
                Request.Headers.Authorization.ToString(),
                cancellationToken
            )
        )
        {
            // Do not reveal whether the header was missing, malformed, or simply wrong.
            logger.LogWarning("Rejected unauthorized sandbox webhook call.");
            return Unauthorized();
        }

        if (envelope.Event is not (SandboxWebhookEvents.ContextDiscovery or SandboxWebhookEvents.TaskCompleted))
        {
            // The contract adds events without a schema bump; an unknown one is acknowledged, not refused.
            logger.LogDebug(
                "Sandbox webhook: ignoring event {Event} ({EventId}) for session {SessionId}.",
                envelope.Event,
                envelope.EventId,
                envelope.SessionId
            );
            return Ok();
        }

        // Delivery is at-least-once. Without an event id there is nothing to deduplicate on, so the event is
        // handled; both handlers tolerate a repeat (the discovery ledger, first-wins registration).
        var dedup = !string.IsNullOrWhiteSpace(envelope.EventId);
        if (dedup && !sessionRegistry.TryMarkWebhookEventSeen(envelope.SessionId, envelope.EventId!))
        {
            logger.LogInformation(
                "Sandbox webhook: duplicate delivery of {Event} {EventId} for session {SessionId}; acknowledged.",
                envelope.Event,
                envelope.EventId,
                envelope.SessionId
            );
            return Ok();
        }

        bool handled;
        try
        {
            handled =
                envelope.Event == SandboxWebhookEvents.ContextDiscovery
                    ? await HandleDiscoveryAsync(envelope, cancellationToken).ConfigureAwait(false)
                    : await HandleTaskCompletedAsync(envelope, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Let the gateway's single retry of a 5xx be processed rather than dropped as a duplicate.
            if (dedup)
            {
                sessionRegistry.UnmarkWebhookEventSeen(envelope.SessionId, envelope.EventId!);
            }

            throw;
        }

        if (!handled)
        {
            if (dedup)
            {
                sessionRegistry.UnmarkWebhookEventSeen(envelope.SessionId, envelope.EventId!);
            }

            logger.LogWarning(
                "Rejected sandbox webhook {Event} {EventId} for session {SessionId}: malformed data.",
                envelope.Event,
                envelope.EventId,
                envelope.SessionId
            );
            return BadRequest();
        }

        return Ok();
    }

    private async Task<bool> HandleDiscoveryAsync(SandboxWebhookEnvelope envelope, CancellationToken ct)
    {
        var data = ReadData<ContextDiscoveryEventData>(envelope);
        if (data?.Discoveries is null)
        {
            return false;
        }

        return await discoveryHandler
            .HandleAsync(
                new ContextDiscoveryEnvelope
                {
                    Event = envelope.Event,
                    SessionId = envelope.SessionId,
                    AppId = envelope.AppId,
                    AgentId = envelope.AgentId,
                    Discoveries = data.Discoveries,
                },
                ct
            )
            .ConfigureAwait(false);
    }

    private async Task<bool> HandleTaskCompletedAsync(SandboxWebhookEnvelope envelope, CancellationToken ct)
    {
        var data = ReadData<TaskCompletedEventData>(envelope);
        if (data is null || string.IsNullOrWhiteSpace(data.Status) || string.IsNullOrWhiteSpace(envelope.TaskId))
        {
            return false;
        }

        _ = await taskNotifier
            .NotifyAsync(
                new SandboxTaskCompletion(
                    envelope.SessionId!,
                    envelope.AgentId,
                    envelope.TaskId,
                    data.Status,
                    data.ExitCode,
                    data.DurationMs,
                    data.StdoutTail,
                    data.StderrTail,
                    data.OutputTruncated ?? false
                ),
                ct
            )
            .ConfigureAwait(false);
        return true;
    }

    private static T? ReadData<T>(SandboxWebhookEnvelope envelope)
        where T : class
    {
        if (envelope.Data is not { ValueKind: JsonValueKind.Object } data)
        {
            return null;
        }

        try
        {
            return data.Deserialize<T>();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The unified sandbox webhook envelope (<c>sbx.webhook/1</c>): the base fields every event carries, plus
/// the event-specific <see cref="Data"/>. Only the fields this app reads are bound; the rest are ignored, as
/// the contract asks.
/// </summary>
public sealed record SandboxWebhookEnvelope
{
    [JsonPropertyName("schema")]
    public string? Schema { get; init; }

    [JsonPropertyName("event")]
    public string? Event { get; init; }

    /// <summary><c>"&lt;gateway_id&gt;-&lt;seq&gt;"</c>, unique within the session; the dedup key.</summary>
    [JsonPropertyName("event_id")]
    public string? EventId { get; init; }

    [JsonPropertyName("seq")]
    public long? Seq { get; init; }

    [JsonPropertyName("app_id")]
    public string? AppId { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    /// <summary>The agent the event is about: <c>""</c> for the default agent, null when none.</summary>
    [JsonPropertyName("agent_id")]
    public string? AgentId { get; init; }

    [JsonPropertyName("task_id")]
    public string? TaskId { get; init; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; init; }
}

/// <summary><c>data</c> of a <c>context_discovery</c> event.</summary>
public sealed record ContextDiscoveryEventData
{
    [JsonPropertyName("discoveries")]
    public List<ContextDiscoveryItem>? Discoveries { get; init; }
}

/// <summary><c>data</c> of a <c>task_completed</c> event.</summary>
public sealed record TaskCompletedEventData
{
    /// <summary><c>completed</c>, <c>failed</c> or <c>terminated</c>.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    /// <summary>Null when killed. Always 0 for a task that ended on its own, until the gateway captures it.</summary>
    [JsonPropertyName("exit_code")]
    public int? ExitCode { get; init; }

    [JsonPropertyName("duration_ms")]
    public long? DurationMs { get; init; }

    [JsonPropertyName("stdout_tail")]
    public string? StdoutTail { get; init; }

    [JsonPropertyName("stderr_tail")]
    public string? StderrTail { get; init; }

    [JsonPropertyName("output_truncated")]
    public bool? OutputTruncated { get; init; }
}
