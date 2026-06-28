using System.Collections.Concurrent;

namespace LmStreaming.Sample.Services;

/// <summary>
/// In-memory, process-lifetime tracker of context-discovery webhooks the app has actually received,
/// keyed by sandbox session. Surfaced by <c>GET /api/diagnostics/context-discovery</c> so an
/// operator can answer "is discovery happening?" at a glance: a session whose
/// <see cref="SessionReceived.Count"/> stays at zero while threads are live is the signature of the
/// gateway failing to reach the callback host (the loopback trap), not an app-side bug.
/// </summary>
/// <remarks>
/// Registered as a singleton. Only the count/last-arrival metadata is retained — never the file
/// body and never the gateway shared secret — so this is safe to expose over the diagnostics
/// endpoint.
/// </remarks>
public sealed class ContextDiscoveryDiagnostics
{
    private readonly ConcurrentDictionary<string, SessionReceived> _received =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Records one authenticated, well-formed discovery webhook for <paramref name="sessionId"/>.
    /// No-op when the session id is blank (the arrival can't be attributed to a session). Thread
    /// safe: concurrent gateway callbacks for the same session fold into a monotonic count.
    /// </summary>
    public void RecordReceived(string? sessionId, string? kind, string? path)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        _ = _received.AddOrUpdate(
            sessionId,
            _ => new SessionReceived(1, now, kind, path),
            (_, existing) => existing with
            {
                Count = existing.Count + 1,
                LastReceivedAt = now,
                LastKind = kind,
                LastPath = path,
            });
    }

    /// <summary>
    /// Point-in-time copy of every session's received-discovery state. The returned dictionary is a
    /// snapshot — safe to iterate without holding any lock.
    /// </summary>
    public IReadOnlyDictionary<string, SessionReceived> Snapshot() =>
        new Dictionary<string, SessionReceived>(_received, StringComparer.Ordinal);
}

/// <summary>
/// Per-session tally of received context-discovery webhooks. <see cref="LastKind"/> /
/// <see cref="LastPath"/> describe the most recent arrival only.
/// </summary>
public sealed record SessionReceived(
    long Count,
    DateTimeOffset LastReceivedAt,
    string? LastKind,
    string? LastPath);
