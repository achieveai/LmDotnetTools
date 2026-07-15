using System.Collections.Concurrent;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Context;

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

    private long _routedCount;
    private long _droppedCount;
    private long _fallbackCount;

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
    /// Records the routing decision the injector made for one <c>context_file</c> discovery. A
    /// routed delivery renders no "Context loaded" pill in the primary view, so this per-outcome
    /// counter is the operator's only signal that sub-agent routing is happening (vs. today's
    /// session fan-out or an unresolved drop). Thread safe.
    /// </summary>
    public void RecordRoutingOutcome(ContextRoutingOutcome outcome)
    {
        switch (outcome)
        {
            case ContextRoutingOutcome.Routed:
                _ = Interlocked.Increment(ref _routedCount);
                break;
            case ContextRoutingOutcome.Dropped:
                _ = Interlocked.Increment(ref _droppedCount);
                break;
            case ContextRoutingOutcome.Fallback:
                _ = Interlocked.Increment(ref _fallbackCount);
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// Point-in-time copy of every session's received-discovery state. The returned dictionary is a
    /// snapshot — safe to iterate without holding any lock.
    /// </summary>
    public IReadOnlyDictionary<string, SessionReceived> Snapshot() =>
        new Dictionary<string, SessionReceived>(_received, StringComparer.Ordinal);

    /// <summary>
    /// Point-in-time copy of the per-outcome routing counters (routed / dropped / fallback).
    /// </summary>
    public RoutingOutcomeCounts RoutingSnapshot() =>
        new(
            Interlocked.Read(ref _routedCount),
            Interlocked.Read(ref _droppedCount),
            Interlocked.Read(ref _fallbackCount));
}

/// <summary>
/// Outcome of routing one <c>context_file</c> discovery: <see cref="Routed"/> = delivered to the
/// opening sub-agent; <see cref="Dropped"/> = a routed discovery whose target could not receive it
/// (finished/disposed/never-registered) — NOT redirected to the primary; <see cref="Fallback"/> =
/// today's session fan-out (no/blank <c>agent_id</c> or the flag is off).
/// </summary>
public enum ContextRoutingOutcome
{
    Routed,
    Dropped,
    Fallback,
}

/// <summary>Per-outcome routing tally surfaced by the context-discovery diagnostics endpoint.</summary>
public sealed record RoutingOutcomeCounts(long Routed, long Dropped, long Fallback);

/// <summary>
/// Per-session tally of received context-discovery webhooks. <see cref="LastKind"/> /
/// <see cref="LastPath"/> describe the most recent arrival only.
/// </summary>
public sealed record SessionReceived(
    long Count,
    DateTimeOffset LastReceivedAt,
    string? LastKind,
    string? LastPath);
