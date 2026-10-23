using AchieveAi.LmDotnetTools.LmAgentInfra.Agents;
using AchieveAi.LmDotnetTools.LmAgentInfra.Capabilities;
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
        if (!IsRoutableOwner(body.AgentId))
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
        var message = NotifyMessage.Create(
            NotifyKinds.ContextDiscovery,
            detail: _formatter.BuildInjectedMessage(body.Path, body.Content, body.Truncated ?? false),
            label: body.Path
        );
        return await DeliverToOwnerAsync(body.SessionId, body.AgentId!, body.Kind!, body.Path, message, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// SC6: workspace skills the sandbox found on <paramref name="sessionId"/> for the agents in
    /// <paramref name="agentIds"/>. The gateway keeps one skill registry per session, so every tiered conversation
    /// bound to the session takes the skills new to its catalog (searchable at once; tools[] and the view stay as they
    /// are until a compaction, whose PATCH then names them). Only a finder's conversation is told: its first finder
    /// gets one notice per new skill. Returns the notices delivered.
    /// </summary>
    public async Task<int> AnnounceSkillsAsync(string? sessionId, IReadOnlyList<string?> agentIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agentIds);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            LogNoSkillNotice(sessionId, null, string.Join(',', agentIds), "the batch names no session");
            return 0;
        }

        // Each finder's root conversation, first finder first: that one hears of the skills.
        var finders = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var agentId in agentIds)
        {
            if (string.IsNullOrWhiteSpace(agentId))
            {
                // The agent row's reason for the same finder (ContextDiscoveryBatchHandler).
                LogNoSkillNotice(sessionId, null, agentId, "the finder sent no agent id (a root that does not tier)");
            }
            else if (!IsRoutableOwner(agentId))
            {
                LogNoSkillNotice(sessionId, null, agentId, "the finder is not a routable agent");
            }
            else if (RootThreadOf(sessionId, agentId) is not { } rootThreadId)
            {
                LogNoSkillNotice(sessionId, null, agentId, "the finder has no root thread on the session");
            }
            else
            {
                _ = finders.TryAdd(rootThreadId, agentId);
            }
        }

        var delivered = 0;
        foreach (var threadId in _registry.GetBoundThreads(sessionId))
        {
            var finder = finders.GetValueOrDefault(threadId);
            if (_pool.GetCapabilityTiers(threadId) is not { IsActive: true } tiers)
            {
                if (finder is not null)
                {
                    LogNoSkillNotice(sessionId, threadId, finder, "the finder's conversation is not tiered");
                }

                continue;
            }

            var added = await tiers.RefreshSkillsAfterDiscoveryAsync(ct).ConfigureAwait(false);
            if (finder is null)
            {
                if (added.Count > 0)
                {
                    LogNoSkillNotice(sessionId, threadId, null, "a bystander on the session: indexed, not told");
                }

                continue;
            }

            foreach (var skill in added)
            {
                delivered += await AnnounceAsync(
                        sessionId,
                        threadId,
                        finder,
                        CapabilityCatalog.SkillKind,
                        skill.Key,
                        CapabilityTiers.SkillNotice(skill),
                        ct
                    )
                    .ConfigureAwait(false);
            }
        }

        return delivered;
    }

    private void LogNoSkillNotice(string? sessionId, string? threadId, string? agentId, string reason) =>
        _logger.LogDebug(
            "Capabilities skill notice skipped {SessionId} {ThreadId} {AgentId} {Reason}",
            sessionId,
            threadId,
            agentId,
            reason
        );

    private void LogNoAgentNotice(string sessionId, string threadId, string reason) =>
        _logger.LogDebug(
            "Capabilities agent notice skipped {SessionId} {ThreadId} {Reason}",
            sessionId,
            threadId,
            reason
        );

    /// <summary>
    /// SC6: a sub-agent template that just registered, new, into the conversation of root
    /// <paramref name="rootThreadId"/> under <paramref name="templateKey"/>. When that root tiers agents (its Agent
    /// tool lists only its view, so tools[] stays as it is), it gets one notice. Returns one when delivered.
    /// </summary>
    public async Task<int> AnnounceAgentAsync(
        string sessionId,
        string rootThreadId,
        string templateKey,
        string? description,
        CancellationToken ct
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootThreadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(templateKey);
        ct.ThrowIfCancellationRequested();
        if (!IsRoutableOwner(rootThreadId))
        {
            LogNoAgentNotice(sessionId, rootThreadId, "the root is not a routable agent");
            return 0;
        }

        if (_pool.GetCapabilityTiers(rootThreadId)?.ListedTemplates(rootThreadId) is null)
        {
            LogNoAgentNotice(sessionId, rootThreadId, "the conversation does not tier agents");
            return 0;
        }

        return await AnnounceAsync(
                sessionId,
                rootThreadId,
                rootThreadId,
                CapabilityCatalog.AgentKind,
                CapabilityCatalog.AgentKeyPrefix + templateKey,
                CapabilityTiers.AgentNotice(templateKey, description),
                ct
            )
            .ConfigureAwait(false);
    }

    private async Task<int> AnnounceAsync(
        string sessionId,
        string rootThreadId,
        string owner,
        string kind,
        string key,
        string notice,
        CancellationToken ct
    )
    {
        var message = NotifyMessage.Create(NotifyKinds.ContextDiscovery, detail: notice, label: key);
        var delivered = await DeliverToOwnerAsync(sessionId, owner, kind, key, message, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Capabilities discovered {ThreadId} {Owner} {Key} {Delivered}",
            rootThreadId,
            owner,
            key,
            delivered
        );
        return delivered;
    }

    /// <summary>An owner a discovery can be routed to: routing on, a non-blank id that is not an ordinal.</summary>
    private bool IsRoutableOwner(string? agentId) =>
        _options.RouteToOpeningSubAgent
        && !string.IsNullOrWhiteSpace(agentId)
        && !SubAgentThreadIds.IsOrdinalAgentId(agentId);

    /// <summary>The root thread on <paramref name="sessionId"/> that <paramref name="owner"/> is, or is a sub-agent of.</summary>
    private string? RootThreadOf(string sessionId, string owner) =>
        _registry
            .GetThreads(sessionId)
            .FirstOrDefault(threadId =>
                string.Equals(threadId, owner, StringComparison.Ordinal)
                || SandboxSubAgentIdentity.TryGetSubAgentId(owner, threadId, out _)
            );

    /// <summary>
    /// Delivers <paramref name="message"/> once to <paramref name="owner"/>: a root thread's own run, or the live
    /// sub-agent of a root on the session. Returns one when delivered; zero for a duplicate
    /// (<paramref name="kind"/>, <paramref name="path"/>) or when nothing accepted it (the mark is then rolled back).
    /// </summary>
    private async Task<int> DeliverToOwnerAsync(
        string sessionId,
        string owner,
        string kind,
        string path,
        NotifyMessage message,
        CancellationToken ct
    )
    {
        if (!_registry.TryMarkDiscoverySeen(sessionId, owner, kind, path))
        {
            return 0;
        }
        foreach (var threadId in _registry.GetThreads(sessionId))
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
                    sessionId,
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
                    sessionId,
                    owner,
                    threadId
                );
                _diagnostics.RecordRoutingOutcome(ContextRoutingOutcome.Dropped);
                return 0;
            }
        }
        // No owner accepted anything. A later callback can retry after owner registration.
        _registry.UnmarkDiscoverySeen(sessionId, owner, kind, path);
        _diagnostics.RecordRoutingOutcome(ContextRoutingOutcome.Dropped);
        return 0;
    }
}
