using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace LmStreaming.Sample.Auth;

/// <summary>
/// Session-aware <see cref="IAuthWebhookForwarder"/>: forwards auth-required/completed/denied
/// signals to whichever thread in the session registered a webhook URL via
/// <c>Properties["sample.authWebhookUrl"]</c> (set by <c>ConversationsController.Provision</c>).
/// Independent of, and in addition to, the WS-facing <see cref="IAuthEventNotifier"/> broadcast.
/// </summary>
/// <remarks>
/// <see cref="NotifyAuthRequiredAsync"/> resolves the eligible thread ONCE and returns the target;
/// the terminal calls take that same target back rather than re-resolving eligibility, so a thread
/// deleted or a new eligible thread registered mid-hold cannot redirect the terminal outcome to a
/// different webhook than the one that received <c>auth_required</c> (see
/// <c>AuthWebhookController.Evaluate</c>, which captures the target in a local variable spanning
/// both calls).
/// </remarks>
public sealed class SandboxAuthWebhookForwarder(
    SandboxSessionRegistry sessionRegistry,
    IConversationStore conversationStore,
    HttpClient httpClient,
    ILogger<SandboxAuthWebhookForwarder> logger) : IAuthWebhookForwarder
{
    private const string WebhookUrlKey = "sample.authWebhookUrl";
    private const string WebhookProviderIdKey = "sample.authWebhookProviderId";
    private const string WebhookRegisteredAtKey = "sample.authWebhookRegisteredAt";

    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptionsFactory.CreateForProduction();

    public async Task<AuthWebhookTarget?> NotifyAuthRequiredAsync(
        string sessionId,
        string providerId,
        string signinUrl,
        string reason,
        CancellationToken ct)
    {
        var target = await ResolveTargetAsync(sessionId, providerId, ct).ConfigureAwait(false);
        if (target is null)
        {
            logger.LogInformation(
                "Auth-webhook forward: no eligible thread registered for session {SessionId}, provider {ProviderId}; nothing to forward.",
                sessionId,
                providerId);
            return null;
        }

        await PostAsync(
            target.WebhookUrl,
            new AuthWebhookForwardPayload
            {
                Type = "auth_required",
                SessionId = sessionId,
                ThreadId = target.ThreadId,
                RunId = target.RunId,
                ProviderId = providerId,
                SigninUrl = signinUrl,
                Reason = reason,
            },
            ct).ConfigureAwait(false);

        return target;
    }

    public Task NotifyAuthCompletedAsync(AuthWebhookTarget? target, string sessionId, string providerId, CancellationToken ct)
    {
        if (target is null)
        {
            return Task.CompletedTask;
        }

        return PostAsync(
            target.WebhookUrl,
            new AuthWebhookForwardPayload
            {
                Type = "auth_completed",
                SessionId = sessionId,
                ThreadId = target.ThreadId,
                RunId = target.RunId,
                ProviderId = providerId,
            },
            ct);
    }

    public Task NotifyAuthDeniedAsync(AuthWebhookTarget? target, string sessionId, string providerId, string reason, CancellationToken ct)
    {
        if (target is null)
        {
            return Task.CompletedTask;
        }

        return PostAsync(
            target.WebhookUrl,
            new AuthWebhookForwardPayload
            {
                Type = "auth_denied",
                SessionId = sessionId,
                ThreadId = target.ThreadId,
                RunId = target.RunId,
                ProviderId = providerId,
                Reason = reason,
            },
            ct);
    }

    /// <summary>
    /// Scans every thread registered against <paramref name="sessionId"/> for one whose metadata
    /// registered a webhook for <paramref name="providerId"/>; first-wins on
    /// <c>(authWebhookRegisteredAt, threadId)</c> when more than one thread is eligible.
    /// </summary>
    private async Task<AuthWebhookTarget?> ResolveTargetAsync(string sessionId, string providerId, CancellationToken ct)
    {
        var threadIds = sessionRegistry.GetThreads(sessionId);
        if (threadIds.Count == 0)
        {
            return null;
        }

        (string ThreadId, string? RunId, string WebhookUrl, long RegisteredAt)? best = null;
        foreach (var threadId in threadIds)
        {
            var metadata = await conversationStore.LoadMetadataAsync(threadId, ct).ConfigureAwait(false);
            var candidate = TryBuildCandidate(metadata, providerId);
            if (candidate is null)
            {
                continue;
            }

            if (best is null
                || candidate.Value.RegisteredAt < best.Value.RegisteredAt
                || (candidate.Value.RegisteredAt == best.Value.RegisteredAt
                    && string.CompareOrdinal(candidate.Value.ThreadId, best.Value.ThreadId) < 0))
            {
                best = candidate;
            }
        }

        return best is { } b ? new AuthWebhookTarget(b.ThreadId, b.RunId, b.WebhookUrl) : null;
    }

    private static (string ThreadId, string? RunId, string WebhookUrl, long RegisteredAt)? TryBuildCandidate(
        ThreadMetadata? metadata,
        string providerId)
    {
        if (metadata?.Properties is not { } properties)
        {
            return null;
        }

        if (!properties.TryGetValue(WebhookUrlKey, out var urlObj) || AsString(urlObj) is not { Length: > 0 } webhookUrl)
        {
            return null;
        }

        if (!properties.TryGetValue(WebhookProviderIdKey, out var providerObj)
            || !string.Equals(AsString(providerObj), providerId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var registeredAt = properties.TryGetValue(WebhookRegisteredAtKey, out var registeredAtObj)
            ? AsInt64(registeredAtObj) ?? 0
            : 0;

        return (metadata.ThreadId, metadata.CurrentRunId, webhookUrl, registeredAt);
    }

    /// <summary>
    /// Normalizes a <see cref="ThreadMetadata.Properties"/> value that may be a raw CLR
    /// <see cref="string"/> (in-memory store) or a <see cref="JsonElement"/> (file/SQLite stores,
    /// which round-trip <c>Properties</c> through JSON).
    /// </summary>
    private static string? AsString(object? value) => value switch
    {
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
        null => null,
        _ => value.ToString(),
    };

    /// <summary>Same normalization as <see cref="AsString"/>, for the numeric registered-at timestamp.</summary>
    private static long? AsInt64(object? value) => value switch
    {
        long l => l,
        int i => i,
        JsonElement { ValueKind: JsonValueKind.Number } je => je.GetInt64(),
        _ => null,
    };

    private async Task PostAsync(string webhookUrl, AuthWebhookForwardPayload payload, CancellationToken ct)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await httpClient
                .PostAsJsonAsync(webhookUrl, payload, JsonOptions, linked.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Auth-webhook forward ({Type}) for thread {ThreadId}, provider {ProviderId} returned {StatusCode}.",
                    payload.Type,
                    payload.ThreadId,
                    payload.ProviderId,
                    (int)response.StatusCode);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Own timeout elapsed, not the caller's token — best-effort, no retry.
            logger.LogWarning(
                "Auth-webhook forward ({Type}) for thread {ThreadId}, provider {ProviderId} timed out.",
                payload.Type,
                payload.ThreadId,
                payload.ProviderId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Best-effort: never let a delivery failure to an arbitrary caller-registered URL
            // propagate into the gateway's always-200 auth-webhook contract. Host only — never the
            // full URL (may carry credentials/tokens as a query string) or the payload.
            logger.LogWarning(
                ex,
                "Auth-webhook forward ({Type}) for thread {ThreadId}, provider {ProviderId} failed (host {Host}).",
                payload.Type,
                payload.ThreadId,
                payload.ProviderId,
                SafeHost(webhookUrl));
        }
    }

    private static string SafeHost(string webhookUrl) =>
        Uri.TryCreate(webhookUrl, UriKind.Absolute, out var uri) ? uri.Host : "(unparseable)";

    /// <summary>
    /// The outbound webhook wire contract, documented (issue #138) in lower-camel-case. Pinned
    /// explicitly rather than relying on <see cref="JsonOptions"/>'s naming policy so the wire
    /// shape stays fixed regardless of the app's serializer defaults.
    /// </summary>
    private sealed record AuthWebhookForwardPayload
    {
        [JsonPropertyName("type")]
        public required string Type { get; init; }

        [JsonPropertyName("sessionId")]
        public required string SessionId { get; init; }

        [JsonPropertyName("threadId")]
        public required string ThreadId { get; init; }

        [JsonPropertyName("runId")]
        public string? RunId { get; init; }

        [JsonPropertyName("providerId")]
        public required string ProviderId { get; init; }

        [JsonPropertyName("signinUrl")]
        public string? SigninUrl { get; init; }

        [JsonPropertyName("reason")]
        public string? Reason { get; init; }
    }
}
