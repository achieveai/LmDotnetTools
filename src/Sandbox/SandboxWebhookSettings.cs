namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// The unified sandbox webhook (gateway PR #205, <c>Docs/webhooks.md</c>): one URL that receives every event
/// about a sandbox in one envelope. Fixed for the sandbox's life. The legacy
/// <see cref="SandboxDiscoverySettings"/> still works, but registering both delivers every discovery twice, once
/// per URL. A gateway at or below v0.1.16 ignores this block.
/// </summary>
public sealed class SandboxWebhookSettings
{
    /// <summary>Endpoint every event is POSTed to. The gateway applies its SSRF rules (HTTPS except an allowlisted loopback).</summary>
    public string Url { get; }

    /// <summary>Sent verbatim as the <c>Authorization</c> header on every POST, or <c>null</c> for none. Never echoed back.</summary>
    public string? AuthHeader { get; }

    /// <summary>
    /// Event allowlist (see <see cref="SandboxWebhookEvents"/>), defensively copied, or <c>null</c> for every event,
    /// including ones added by later gateways. An unknown name is a create-time <c>400</c>.
    /// </summary>
    public IReadOnlyList<string>? Events { get; }

    public SandboxWebhookSettings(string url, string? authHeader = null, IReadOnlyList<string>? events = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (events is not null)
        {
            // The gateway rejects an empty list: a webhook that receives nothing is almost certainly a mistake.
            ArgumentOutOfRangeException.ThrowIfZero(events.Count, nameof(events));
            foreach (var name in events)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(events));
            }
        }

        Url = url;
        AuthHeader = authHeader;
        Events = events is null ? null : [.. events];
    }

    /// <summary>Redacted rendering — never prints <see cref="AuthHeader"/>.</summary>
    public override string ToString() =>
        $"SandboxWebhookSettings {{ Url = {Url}, AuthHeader = {(AuthHeader is null ? "none" : "[REDACTED]")}, "
        + $"Events = {(Events is null ? "all" : string.Join(",", Events))} }}";
}

/// <summary>The event names the gateway's sandbox webhook delivers (<c>Docs/webhooks.md</c>, "Events").</summary>
public static class SandboxWebhookEvents
{
    /// <summary>Subscribes to every event, including ones added by later gateways.</summary>
    public const string All = "*";
    public const string SandboxCreated = "sandbox_created";
    public const string SandboxDeleted = "sandbox_deleted";
    public const string ContextDiscovery = "context_discovery";
    public const string AuthRequest = "auth_request";
    public const string AuthPostResponseFailure = "auth_post_response_failure";
    public const string TaskCompleted = "task_completed";
}

/// <summary>
/// The webhook a sandbox registered, as <c>GET /api/v1/sandboxes/{id}</c> reports it. The auth header value is
/// never reported, only whether one is set.
/// </summary>
/// <param name="Url">The registered endpoint.</param>
/// <param name="Events">The registered allowlist, or <c>null</c> when none was sent (every event).</param>
/// <param name="AuthHeaderSet">Whether an <c>auth_header</c> is registered.</param>
public sealed record SandboxWebhookInfo(string Url, IReadOnlyList<string>? Events, bool AuthHeaderSet);
