using AchieveAi.LmDotnetTools.LmAgentInfra.Context;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using Microsoft.AspNetCore.Mvc;

namespace LmStreaming.Sample.Controllers;

/// <summary>
/// Read-only diagnostics for the context-discovery (CLAUDE.md / AGENTS.md) feature. Answers
/// "is discovery actually happening?" without booting a real sandbox: it reports where the gateway
/// was told to deliver discoveries (so an operator can recognise an unreachable/loopback callback
/// host), whether discovery is even enabled, and the per-session count + timestamp of webhooks the
/// app has actually received. A live session whose received count stays at zero is the signature of
/// the gateway failing to reach the callback host — not an app-side bug.
/// </summary>
[ApiController]
[Route("api/diagnostics")]
public sealed class ContextDiscoveryDiagnosticsController(
    SandboxSessionRegistry registry,
    ContextDiscoveryDiagnostics diagnostics) : ControllerBase
{
    [HttpGet("context-discovery")]
    public IActionResult Get()
    {
        var received = diagnostics.Snapshot();

        // Union of live sessions and sessions that have received a discovery (the latter may have
        // since been torn down but their counts are still worth reporting). Ordered for stable output.
        var sessionIds = new SortedSet<string>(StringComparer.Ordinal);
        sessionIds.UnionWith(registry.GetActiveSessionIds());
        sessionIds.UnionWith(received.Keys);

        var sessions = sessionIds
            .Select(id =>
            {
                _ = received.TryGetValue(id, out var r);
                return new ContextDiscoverySessionInfo(
                    SessionId: id,
                    Active: registry.TryGetSessionById(id, out _),
                    ReceivedCount: r?.Count ?? 0,
                    LastReceivedAt: r?.LastReceivedAt,
                    LastKind: r?.LastKind,
                    LastPath: r?.LastPath);
            })
            .ToList();

        return Ok(new ContextDiscoveryDiagnosticsResponse(
            DiscoveryEnabled: registry.DiscoveryEnabled,
            WebhookUrl: registry.DiscoveryWebhookUrl,
            Sessions: sessions));
    }
}

/// <summary>Top-level response for <c>GET /api/diagnostics/context-discovery</c>.</summary>
public sealed record ContextDiscoveryDiagnosticsResponse(
    bool DiscoveryEnabled,
    string WebhookUrl,
    IReadOnlyList<ContextDiscoverySessionInfo> Sessions);

/// <summary>Per-session discovery state. <see cref="ReceivedCount"/> is 0 (with null timestamps)
/// for a live session that has not yet received any discovery webhook.</summary>
public sealed record ContextDiscoverySessionInfo(
    string SessionId,
    bool Active,
    long ReceivedCount,
    DateTimeOffset? LastReceivedAt,
    string? LastKind,
    string? LastPath);
