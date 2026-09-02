using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;

/// <summary>How current a reported observation is (#681; spec 679 §4.5).</summary>
public static class ContextFreshness
{
    /// <summary>A live loop vouched for the observation.</summary>
    public const string Fresh = "Fresh";

    /// <summary>Only the persisted observation is available; no live loop confirmed it.</summary>
    public const string Stale = "Stale";

    /// <summary>The thread has never been observed.</summary>
    public const string None = "None";
}

/// <summary>The compaction state a row reports (#681; spec 679 §3.5, §9).</summary>
public static class CompactionStates
{
    /// <summary>No checkpoint has ever been recorded.</summary>
    public const string None = "None";

    /// <summary>A checkpoint is prepared, validated or committed but not yet active.</summary>
    public const string InFlight = "InFlight";

    /// <summary>A checkpoint is active.</summary>
    public const string Active = "Active";

    /// <summary>The last checkpoint was rejected; <see cref="AgentCompactionStatus.Reason" /> says why.</summary>
    public const string Rejected = "Rejected";

    /// <summary>The last checkpoint was rolled back.</summary>
    public const string RolledBack = "RolledBack";

    /// <summary>A checkpoint was superseded and nothing newer is active.</summary>
    public const string Superseded = "Superseded";

    /// <summary>The loop runs on a provider-side session the host cannot observe or compact (§9).</summary>
    public const string Unsupported = "Unsupported";
}

/// <summary>One agent's compaction state as the report shows it.</summary>
public sealed record AgentCompactionStatus
{
    /// <summary>One of <see cref="CompactionStates" />.</summary>
    public required string State { get; init; }

    /// <summary>The checkpoint the state refers to, when there is one.</summary>
    public string? CheckpointId { get; init; }

    /// <summary>The typed reason for a rejected checkpoint, when there is one.</summary>
    public string? Reason { get; init; }
}

/// <summary>One agent loop in the report.</summary>
public sealed record AgentContextRow
{
    /// <summary><c>root</c> or the sub-agent id.</summary>
    public required string AgentId { get; init; }

    /// <summary>The loop's own thread.</summary>
    public required string ThreadId { get; init; }

    /// <summary>The spawning agent's id; null for the root.</summary>
    public string? ParentAgentId { get; init; }

    /// <summary>How the loop was produced. Serialized by name (spec 679 §4.3), independent of the host's options.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public UsageExecutionKind ExecutionKind { get; init; }

    /// <summary>The latest observation, live when a loop vouched for one, else persisted; null when none.</summary>
    public ContextObservation? Observation { get; init; }

    /// <summary>One of <see cref="ContextFreshness" />.</summary>
    public required string Freshness { get; init; }

    /// <summary>Expected prompt-cache reuse, from durable activity against the cache TTL (§4.4).</summary>
    public CacheTemperature CacheTemperature { get; init; } = CacheTemperature.Unknown;

    /// <summary>Compaction state for the loop.</summary>
    public required AgentCompactionStatus Compaction { get; init; }

    /// <summary>This execution's spend, folded from the root ledger; null when it has none.</summary>
    public ExecutionUsageRow? Usage { get; init; }
}

/// <summary>The conversation total: the sum over every row, from the same fold.</summary>
public sealed record ConversationCostTotal
{
    /// <summary>Summed input tokens.</summary>
    public long InputTokens { get; init; }

    /// <summary>Summed output tokens.</summary>
    public long OutputTokens { get; init; }

    /// <summary>Summed cached-read tokens.</summary>
    public long CacheReadTokens { get; init; }

    /// <summary>Summed cache-creation tokens.</summary>
    public long CacheWriteTokens { get; init; }

    /// <summary>Summed reasoning tokens.</summary>
    public long ReasoningTokens { get; init; }

    /// <summary>Summed total tokens.</summary>
    public long TotalTokens { get; init; }

    /// <summary>Known-cost subtotal of each row's preferred figure, or null when no row has one.</summary>
    public long? PreferredCostMicros { get; init; }

    /// <summary>Provider-reported only when every priced row was; public estimate when any fell back.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CostProvenance CostProvenance { get; init; } = CostProvenance.Unavailable;

    /// <summary>Completeness of the summed public estimate.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CostCompleteness CostCompleteness { get; init; } = CostCompleteness.Unavailable;

    /// <summary>Whether the persisted ledger is still accumulating; null when nothing was persisted.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public UsageCompleteness? UsageCompleteness { get; init; }
}

/// <summary>Read-time knobs for <see cref="ConversationContextReport.BuildAsync" />.</summary>
public sealed record ConversationContextReportOptions
{
    /// <summary>The default prompt-cache TTL (spec 679 §4.4).</summary>
    public static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     A live loop's in-memory latest observation for a thread, when the host has one. A non-null answer
    ///     makes the row <see cref="ContextFreshness.Fresh" /> and is reported in place of the persisted one.
    /// </summary>
    public Func<string, ContextObservation?>? LiveObservation { get; init; }

    /// <summary>The clock the cache temperature is judged against.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>The prompt-cache TTL.</summary>
    public TimeSpan CacheTtl { get; init; } = DefaultCacheTtl;
}

/// <summary>
///     The read model behind <c>GET /api/conversations/{id}/context</c> (#681; spec 679 §4.1–4.5): one row
///     per agent in the roster and the root total, assembled from durable state so it reads the same before
///     and after a restart. Content-free by construction — counts, ratios, ids and statuses only.
/// </summary>
public sealed record ConversationContextReport
{
    /// <summary>The payload schema version this build writes.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The root conversation.</summary>
    public required string RootThreadId { get; init; }

    /// <summary>Schema version of this payload.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>When the report was assembled.</summary>
    public DateTimeOffset GeneratedAtUtc { get; init; }

    /// <summary>One row per agent, root first, then in roster order.</summary>
    public IReadOnlyList<AgentContextRow> Agents { get; init; } = [];

    /// <summary>The conversation total.</summary>
    public required ConversationCostTotal Total { get; init; }

    /// <summary>
    ///     Assembles the report. <paramref name="roster" /> names the agents to report; the root is always
    ///     included, first, whether or not the roster lists it.
    /// </summary>
    public static async Task<ConversationContextReport> BuildAsync(
        IConversationStore store,
        string rootThreadId,
        IReadOnlyList<AgentExecutionRef> roster,
        ConversationContextReportOptions? options = null,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(rootThreadId);
        ArgumentNullException.ThrowIfNull(roster);
        options ??= new ConversationContextReportOptions();

        var now = options.TimeProvider.GetUtcNow();

        // Usage is read ONCE from the root: every descendant relays into the root ledger, so the root's
        // records are the whole tree's, and one fold gives every row and the total from the same numbers.
        var rootMetadata = await store.LoadMetadataAsync(rootThreadId, ct).ConfigureAwait(false);
        var aggregate = ConversationUsageProjection.FromMetadata(rootMetadata);
        var usageRows = ConversationUsageAggregate
            .FoldByExecution(ConversationUsageProjection.RecordsFromMetadata(rootMetadata))
            .ToDictionary(r => r.ExecutionId, StringComparer.Ordinal);

        var agents = new List<AgentContextRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var agent in roster.Prepend(AgentExecutionRef.Root(rootThreadId)))
        {
            if (!seen.Add(agent.ThreadId))
            {
                continue;
            }

            var metadata =
                agent.ThreadId == rootThreadId
                    ? rootMetadata
                    : await store.LoadMetadataAsync(agent.ThreadId, ct).ConfigureAwait(false);
            agents.Add(await RowAsync(store, agent, metadata, usageRows, options, now, ct).ConfigureAwait(false));
        }

        return new ConversationContextReport
        {
            RootThreadId = rootThreadId,
            GeneratedAtUtc = now,
            Agents = agents,
            Total = FoldTotal(usageRows.Values, aggregate),
        };
    }

    private static async Task<AgentContextRow> RowAsync(
        IConversationStore store,
        AgentExecutionRef agent,
        ThreadMetadata? metadata,
        IReadOnlyDictionary<string, ExecutionUsageRow> usageRows,
        ConversationContextReportOptions options,
        DateTimeOffset now,
        CancellationToken ct
    )
    {
        _ = usageRows.TryGetValue(agent.ThreadId, out var usage);

        // §9: a loop bound to a provider-side session owns its own context. The host can neither size
        // nor cut it, so it reports no observation at all rather than a stale or fabricated one.
        if (metadata?.SessionMappings is { Count: > 0 })
        {
            return new AgentContextRow
            {
                AgentId = agent.AgentId,
                ThreadId = agent.ThreadId,
                ParentAgentId = agent.ParentAgentId,
                ExecutionKind = agent.ExecutionKind,
                Freshness = ContextFreshness.None,
                Compaction = new AgentCompactionStatus { State = CompactionStates.Unsupported },
                Usage = usage,
            };
        }

        var persisted = ContextObservationProjection.LatestFromMetadata(metadata);
        var live = options.LiveObservation?.Invoke(agent.ThreadId);
        var observation = live ?? persisted;
        var freshness =
            live is not null ? ContextFreshness.Fresh
            : persisted is not null ? ContextFreshness.Stale
            : ContextFreshness.None;

        // Temperature is judged from DURABLE activity (§4.4) so it agrees across restarts; it is only
        // meaningful for a loop that sends with caching on, which the observation records.
        var cachingEnabled = observation?.PromptCachingEnabled ?? false;
        var lastActivity = cachingEnabled
            ? await ConversationActivity.GetLastActivityAsync(store, agent.ThreadId, ct).ConfigureAwait(false)
            : null;

        return new AgentContextRow
        {
            AgentId = agent.AgentId,
            ThreadId = agent.ThreadId,
            ParentAgentId = agent.ParentAgentId,
            ExecutionKind = agent.ExecutionKind,
            Observation = observation,
            Freshness = freshness,
            CacheTemperature = ConversationActivity.ResolveCacheTemperature(
                lastActivity,
                now,
                options.CacheTtl,
                cachingEnabled
            ),
            Compaction = CompactionStatus(CompactionStateProjection.FromMetadata(metadata)),
            Usage = usage,
        };
    }

    private static AgentCompactionStatus CompactionStatus(CompactionState? state)
    {
        if (state is null || state.History.Count == 0)
        {
            return new AgentCompactionStatus { State = CompactionStates.None };
        }

        if (state.Active is { } active)
        {
            return new AgentCompactionStatus { State = CompactionStates.Active, CheckpointId = active.CheckpointId };
        }

        if (state.InFlight.LastOrDefault() is { } inFlight)
        {
            return new AgentCompactionStatus
            {
                State = CompactionStates.InFlight,
                CheckpointId = inFlight.CheckpointId,
            };
        }

        var last = state.History[^1];
        return new AgentCompactionStatus
        {
            State = last.Status switch
            {
                CheckpointStatus.Rejected => CompactionStates.Rejected,
                CheckpointStatus.RolledBack => CompactionStates.RolledBack,
                CheckpointStatus.Superseded => CompactionStates.Superseded,
                _ => CompactionStates.None,
            },
            CheckpointId = last.CheckpointId,
            Reason = last.Reason,
        };
    }

    private static ConversationCostTotal FoldTotal(
        IEnumerable<ExecutionUsageRow> rows,
        ConversationUsageAggregate? aggregate
    )
    {
        var list = rows.ToList();
        var preferred = ConversationUsageAggregate.SumKnown(list.Select(r => r.PreferredCostMicros));
        var estimated = ConversationUsageAggregate.SumKnown(list.Select(r => r.EstimatedPublicCostMicros));

        return new ConversationCostTotal
        {
            InputTokens = list.Sum(r => r.InputTokens),
            OutputTokens = list.Sum(r => r.OutputTokens),
            CacheReadTokens = list.Sum(r => r.CacheReadTokens),
            CacheWriteTokens = list.Sum(r => r.CacheWriteTokens),
            ReasoningTokens = list.Sum(r => r.ReasoningTokens),
            TotalTokens = list.Sum(r => r.TotalTokens),
            PreferredCostMicros = preferred,
            CostProvenance =
                preferred is null ? CostProvenance.Unavailable
                : list.All(r => r.PreferredCostMicros is null || r.CostProvenance == CostProvenance.ProviderReported)
                    ? CostProvenance.ProviderReported
                : CostProvenance.PublicEstimate,
            CostCompleteness = ConversationUsageAggregate.FoldCompleteness(
                estimated,
                list.Select(r => r.EstimatedCostCompleteness)
            ),
            UsageCompleteness = aggregate?.Completeness,
        };
    }
}
