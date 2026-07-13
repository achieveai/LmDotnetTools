using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmAgentInfra.Agents;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmMultiTurn;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Context;

/// <summary>
/// Routes a <c>context_file</c> discovery from the gateway webhook to its target conversation(s):
/// either the specific sub-agent that opened the file (when routing is enabled and the gateway
/// stamped an <c>agent_id</c>) or — the fallback, and all traffic today — every live agent thread
/// bound to the same sandbox session. It dedups gateway retries and enqueues a
/// <see cref="NotifyMessage"/> (kind <c>context-discovery</c>) carrying the formatted file body —
/// delivered to the model via <c>ICanGetText</c> and rendered as a distinct pill by the chat UI.
/// </summary>
/// <remarks>
/// <para>
/// Every failure is logged and swallowed — context discovery is a best-effort enrichment, never
/// a blocking precondition. The controller always returns 200 to the gateway for an
/// authenticated payload regardless of what happens here, so a transient thread teardown / mode
/// switch in flight cannot translate into a webhook retry storm.
/// </para>
/// <para>
/// Dedup is per <c>(target, kind, path)</c> where <c>target</c> is the resolved sub-agent
/// <c>agent_id</c> for a routed delivery, or <see cref="SandboxSessionRegistry.SessionDiscoveryTarget"/>
/// for the session fan-out: the primary and a sub-agent (or two sub-agents) entering the same
/// directory each receive that directory's context once.
/// </para>
/// <para>
/// Routing (gated by <see cref="ContextDiscoveryOptions.RouteToOpeningSubAgent"/>) is deliberately
/// sub-agent-only: a discovery whose <c>agent_id</c> resolves to a sub-agent that is no longer
/// running is DROPPED, and one that resolves to no owner at all (the pre-registration race, or a
/// nested/cross-session sub-agent) is also dropped — NEVER redirected to the primary, which would
/// re-introduce the exact context pollution this feature fixes. Only a null/blank <c>agent_id</c>
/// (or the flag being off) fans out to the primary.
/// </para>
/// </remarks>
public sealed class ContextDiscoveryInjector
{
    /// <summary>
    /// Legacy metadata key. New discoveries are emitted as a <see cref="NotifyMessage"/> and no longer
    /// set this key; it is retained because pre-migration conversations persisted it on a
    /// <see cref="TextMessage"/>, and the chat UI still reads it to render those historical rows as a
    /// context pill.
    /// </summary>
    public const string MetadataKey = "context_discovery";

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
        ILogger<ContextDiscoveryInjector> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Back-compat constructor matching the pre-#198 signature: routing defaults OFF (behaviour
    /// identical to pre-#198) for callers that do not supply options/diagnostics.
    /// </summary>
    public ContextDiscoveryInjector(
        SandboxSessionRegistry registry,
        MultiTurnAgentPool pool,
        ContextDiscoveryFormatter formatter,
        ILogger<ContextDiscoveryInjector> logger)
        : this(registry, pool, formatter, new ContextDiscoveryOptions(), new ContextDiscoveryDiagnostics(), logger)
    {
    }

    /// <summary>
    /// Best-effort: delivers the formatted file body to its target — the opening sub-agent (routed)
    /// or every live thread of the session (fallback). Returns the number of conversations the
    /// message was actually delivered to (0 when nothing is live, the discovery is a duplicate, the
    /// routed target isn't running, or the session is unknown).
    /// </summary>
    public async Task<int> InjectAsync(ContextDiscoveryPayload body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (string.IsNullOrWhiteSpace(body.SessionId))
        {
            _logger.LogInformation("ContextDiscovery context_file: payload missing session_id; dropping.");
            return 0;
        }

        if (string.IsNullOrWhiteSpace(body.Path) || string.IsNullOrEmpty(body.Content))
        {
            // Controller validation rejects null content + missing path upstream; defence-in-depth
            // here keeps the injector safe to call directly from tests / other code paths and also
            // covers the empty-string body case (which validation lets through but the model
            // can't make use of).
            _logger.LogInformation(
                "ContextDiscovery context_file: payload missing path or has empty content; dropping for session {SessionId}.",
                body.SessionId);
            return 0;
        }

        // Route only when the flag is on AND the gateway stamped a NON-BLANK agent_id. Using
        // IsNullOrWhiteSpace (not just != null) is load-bearing: a blank id would otherwise create a
        // rogue "" dedup bucket distinct from __session__ and double-inject into the primary.
        var routeEnabled = _options.RouteToOpeningSubAgent && !string.IsNullOrWhiteSpace(body.AgentId);

        return routeEnabled
            ? await RouteToSubAgentAsync(body, ct).ConfigureAwait(false)
            : await FanOutToSessionAsync(body, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Today's behavior (and every real delivery until the gateway stamps <c>agent_id</c>): dedup
    /// under <see cref="SandboxSessionRegistry.SessionDiscoveryTarget"/>, then fan the file out to
    /// every live thread the session is routing.
    /// </summary>
    private async Task<int> FanOutToSessionAsync(ContextDiscoveryPayload body, CancellationToken ct)
    {
        if (!_registry.TryMarkDiscoverySeen(
                body.SessionId!,
                SandboxSessionRegistry.SessionDiscoveryTarget,
                body.Kind!,
                body.Path!))
        {
            _logger.LogDebug(
                "ContextDiscovery context_file: duplicate delivery for {Path} in session {SessionId}; dropping.",
                body.Path,
                body.SessionId);
            return 0;
        }

        _diagnostics.RecordRoutingOutcome(ContextRoutingOutcome.Fallback);

        var threads = _registry.GetThreads(body.SessionId!);
        if (threads.Count == 0)
        {
            // No live agent thread for this session yet. Discovery arrived between session
            // creation and the agent path being wired; nothing to inject. The dedup mark above
            // means a redelivery after the thread is up also won't re-inject — by design, so
            // the model isn't surprised by a now-stale file delivery. A later turn that needs
            // the file content can still read it directly via the file-system tool.
            _logger.LogInformation(
                "ContextDiscovery context_file: no live thread for session {SessionId}; nothing to inject.",
                body.SessionId);
            return 0;
        }

        var message = BuildMessage(body);

        var injected = 0;
        foreach (var threadId in threads)
        {
            if (!_pool.TryGet(threadId, out var agent) || agent is null)
            {
                // Thread torn down between GetThreads and now — best-effort skip; the pool's
                // ThreadRemoved notifier is what keeps the registry's thread set fresh.
                continue;
            }

            try
            {
                _ = await agent.SendAsync([message], inputId: null, parentRunId: null, ct).ConfigureAwait(false);
                injected++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "ContextDiscovery context_file: SendAsync threw for thread {ThreadId}; continuing with other threads.",
                    threadId);
            }
        }

        _logger.LogInformation(
            "ContextDiscovery context_file: injected {Path} into {Count}/{Total} threads of session {SessionId}.",
            body.Path,
            injected,
            threads.Count,
            body.SessionId);

        return injected;
    }

    /// <summary>
    /// Routes the discovery to the sub-agent identified by <c>agent_id</c>. Marks the dedup ledger
    /// under the agent id FIRST (so a redelivery of an already-resolved discovery drops); then asks
    /// each session thread's <see cref="ISubAgentContextSink"/> in turn. Aggregation: the first
    /// <see cref="SubAgentContextDeliveryResult.Delivered"/> wins (routed); otherwise if any sink
    /// owns the id but can't take it (<see cref="SubAgentContextDeliveryResult.TargetNotDeliverable"/>)
    /// the discovery is dropped WITHOUT fan-out and the mark kept (terminal); if no sink owns it
    /// (all <see cref="SubAgentContextDeliveryResult.NotOwned"/>) the discovery is dropped and the
    /// mark REMOVED so a gateway redelivery can retry once the sub-agent registers (pre-registration
    /// race). A present-but-unresolved <c>agent_id</c> never falls back to the primary.
    /// </summary>
    private async Task<int> RouteToSubAgentAsync(ContextDiscoveryPayload body, CancellationToken ct)
    {
        var agentId = body.AgentId!; // routeEnabled guarantees non-blank

        if (!_registry.TryMarkDiscoverySeen(body.SessionId!, agentId, body.Kind!, body.Path!))
        {
            _logger.LogDebug(
                "ContextDiscovery context_file: duplicate routed delivery for {Path} to agent {AgentId} "
                + "in session {SessionId}; dropping.",
                body.Path,
                agentId,
                body.SessionId);
            return 0;
        }

        var message = BuildMessage(body);
        IReadOnlyList<IMessage> messages = [message];

        var anyNotDeliverable = false;
        foreach (var threadId in _registry.GetThreads(body.SessionId!))
        {
            if (!_pool.TryGet(threadId, out var agent) || agent is not ISubAgentContextSink sink)
            {
                continue;
            }

            SubAgentContextDeliveryResult result;
            try
            {
                result = await sink.TryDeliverContextAsync(agentId, messages, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A misbehaving sink must not strand routing — treat it as not-owned and keep scanning.
                _logger.LogWarning(
                    ex,
                    "ContextDiscovery context_file: sink delivery threw for agent {AgentId} on thread {ThreadId}; "
                    + "treating as not-owned.",
                    agentId,
                    threadId);
                continue;
            }

            if (result == SubAgentContextDeliveryResult.Delivered)
            {
                _logger.LogInformation(
                    "ContextDiscovery context_file: routed {Path} to sub-agent {AgentId} in session {SessionId}.",
                    body.Path,
                    agentId,
                    body.SessionId);
                _diagnostics.RecordRoutingOutcome(ContextRoutingOutcome.Routed);
                return 1;
            }

            if (result == SubAgentContextDeliveryResult.TargetNotDeliverable)
            {
                // Scan the rest before deciding — do NOT short-circuit; another thread's sink may still
                // Deliver (aggregation contract: any Delivered wins).
                anyNotDeliverable = true;
            }
        }

        if (anyNotDeliverable)
        {
            // A sink owns the id but it is not safely running (finished / disposing). Drop cleanly —
            // never redirect to the primary. Keep the mark: terminal, so a retry can't succeed.
            _logger.LogInformation(
                "ContextDiscovery context_file: sub-agent {AgentId} owned but not deliverable for {Path} "
                + "in session {SessionId}; dropping (no fan-out to primary).",
                agentId,
                body.Path,
                body.SessionId);
            _diagnostics.RecordRoutingOutcome(ContextRoutingOutcome.Dropped);
            return 0;
        }

        // No sink owns the id: the sub-agent hasn't registered yet (pre-registration race), or it is a
        // nested / cross-session agent. Drop and un-mark ONLY this discovery's (target, kind, path) so the
        // gateway's redelivery can retry once the sub-agent is live — precise so a concurrent delivery's
        // mark for another path of the same agent survives — but still never fall back to the primary.
        _registry.UnmarkDiscoverySeen(body.SessionId!, agentId, body.Kind!, body.Path!);
        _logger.LogDebug(
            "ContextDiscovery context_file: no live sub-agent owns {AgentId} for {Path} in session {SessionId}; "
            + "dropping and allowing gateway retry.",
            agentId,
            body.Path,
            body.SessionId);
        _diagnostics.RecordRoutingOutcome(ContextRoutingOutcome.Dropped);
        return 0;
    }

    /// <summary>
    /// Builds the typed context-discovery notification. The formatted file body stays in
    /// <c>Detail</c> so it still reaches the LLM (via <c>ICanGetText</c>), and the UI renders it as a
    /// pill; <c>Label</c> carries the file path.
    /// </summary>
    private NotifyMessage BuildMessage(ContextDiscoveryPayload body)
    {
        var truncated = body.Truncated ?? false;
        var text = _formatter.BuildInjectedMessage(body.Path!, body.Content!, truncated);
        return NotifyMessage.Create(NotifyKinds.ContextDiscovery, detail: text, label: body.Path);
    }
}
