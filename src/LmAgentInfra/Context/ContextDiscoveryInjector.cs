using AchieveAi.LmDotnetTools.LmAgentInfra.Agents;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Context;

/// <summary>
/// Delivers discovered context only to its identified owner, without starting a model turn.
/// Missing ownership never falls back to session-wide delivery. Explicit file reads remain available.
/// </summary>
public sealed class ContextDiscoveryInjector
{
    /// <summary>Metadata key retained for persisted pre-notification context messages.</summary>
    public const string MetadataKey = "context_discovery";

    /// <summary>Prefix retained for attribution of historical queued context inputs.</summary>
    internal const string ContextDiscoveryInputIdPrefix = "ctxdisc-";

    private readonly SandboxSessionRegistry _registry;
    private readonly MultiTurnAgentPool _pool;
    private readonly ContextDiscoveryFormatter _formatter;
    private readonly ContextDiscoveryOptions _options;
    private readonly ContextDiscoveryDiagnostics _diagnostics;
    private readonly ILogger<ContextDiscoveryInjector> _logger;

    public ContextDiscoveryInjector(
        SandboxSessionRegistry registry,
        MultiTurnAgentPool pool,
        ContextDiscoveryFormatter formatter,
        ContextDiscoveryOptions options,
        ContextDiscoveryDiagnostics diagnostics,
        ILogger<ContextDiscoveryInjector> logger
    )
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Compatibility constructor; unidentified discoveries are still dropped.</summary>
    public ContextDiscoveryInjector(
        SandboxSessionRegistry registry,
        MultiTurnAgentPool pool,
        ContextDiscoveryFormatter formatter,
        ILogger<ContextDiscoveryInjector> logger
    )
        : this(registry, pool, formatter, new ContextDiscoveryOptions(), new ContextDiscoveryDiagnostics(), logger) { }

    /// <summary>Returns one for owner delivery, zero for a duplicate or undeliverable discovery.</summary>
    public async Task<int> InjectAsync(ContextDiscoveryPayload body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        ct.ThrowIfCancellationRequested();
        if (
            string.IsNullOrWhiteSpace(body.SessionId)
            || string.IsNullOrWhiteSpace(body.Path)
            || string.IsNullOrEmpty(body.Content)
        )
        {
            return 0;
        }
        if (
            !_options.RouteToOpeningSubAgent
            || string.IsNullOrWhiteSpace(body.AgentId)
            || SubAgentThreadIds.IsOrdinalAgentId(body.AgentId)
        )
        {
            _logger.LogDebug(
                "ContextDiscovery dropped: session {SessionId}, owner {AgentId}, routing enabled {RoutingEnabled}; no broadcast.",
                body.SessionId,
                body.AgentId,
                _options.RouteToOpeningSubAgent
            );
            _diagnostics.RecordRoutingOutcome(ContextRoutingOutcome.Dropped);
            return 0;
        }
        var owner = body.AgentId;
        if (!_registry.TryMarkDiscoverySeen(body.SessionId, owner, body.Kind!, body.Path))
        {
            return 0;
        }
        var message = NotifyMessage.Create(
            NotifyKinds.ContextDiscovery,
            detail: _formatter.BuildInjectedMessage(body.Path, body.Content, body.Truncated ?? false),
            label: body.Path
        );
        foreach (var threadId in _registry.GetThreads(body.SessionId))
        {
            if (!_pool.TryGet(threadId, out var agent) || agent is null)
            {
                continue;
            }
            try
            {
                SubAgentContextDeliveryResult result;
                if (string.Equals(threadId, owner, StringComparison.Ordinal))
                {
                    result =
                        agent is IContextNotificationSink passive
                        && await passive.TryAppendContextAsync(message, ct).ConfigureAwait(false)
                            ? SubAgentContextDeliveryResult.Delivered
                            : SubAgentContextDeliveryResult.TargetNotDeliverable;
                }
                else if (agent is ISubAgentContextSink sink)
                {
                    // The gateway reports the id the sub-agent presented on X-Sbx-Agent-ID, which the host
                    // derived from (root thread, sub-agent id) — see SandboxSubAgentIdentity. Map it back to
                    // that sub-agent's scoped thread under this root; any other id is passed through and the
                    // sink answers NotOwned.
                    var sinkThreadId = SandboxSubAgentIdentity.TryGetSubAgentId(owner, threadId, out var subAgentId)
                        ? SubAgentThreadIds.For(threadId, subAgentId)
                        : owner;
                    result = await sink.TryDeliverContextAsync(sinkThreadId, [message], ct).ConfigureAwait(false);
                }
                else
                {
                    continue;
                }
                if (result == SubAgentContextDeliveryResult.NotOwned)
                {
                    continue;
                }
                _diagnostics.RecordRoutingOutcome(
                    result == SubAgentContextDeliveryResult.Delivered
                        ? ContextRoutingOutcome.Routed
                        : ContextRoutingOutcome.Dropped
                );
                _logger.LogInformation(
                    "ContextDiscovery routed outcome: session {SessionId}, owner {AgentId}, root thread {ThreadId}, outcome {Outcome}; no input queued.",
                    body.SessionId,
                    owner,
                    threadId,
                    result
                );
                return result == SubAgentContextDeliveryResult.Delivered ? 1 : 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Delivery may already have appended history. Keep the mark, preventing duplicates.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "ContextDiscovery ambiguous delivery: session {SessionId}, owner {AgentId}, root thread {ThreadId}; keeping dedup mark.",
                    body.SessionId,
                    owner,
                    threadId
                );
                _diagnostics.RecordRoutingOutcome(ContextRoutingOutcome.Dropped);
                return 0;
            }
        }
        // No owner accepted anything. A later callback can retry after owner registration.
        _registry.UnmarkDiscoverySeen(body.SessionId, owner, body.Kind!, body.Path);
        _diagnostics.RecordRoutingOutcome(ContextRoutingOutcome.Dropped);
        return 0;
    }
}
