using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;

/// <summary>
///     Execution-scoped collector that normalizes many streaming usage observations into one durable
///     <see cref="UsageRecord" /> per provider attempt, and produces the conversation aggregate snapshot
///     with a complete-prefix watermark. Additive across the whole conversation tree (issue #196): the
///     root conversation creates one ledger and every descendant relays its observations into it.
/// </summary>
public sealed class UsageLedger : IUsageSink
{
    private readonly object _gate = new();
    private readonly Dictionary<string, UsageRecord> _byAttempt = new(StringComparer.Ordinal);
    private readonly RevisionWatermark _watermark = new();
    private readonly IPricingResolver? _pricingResolver;
    private readonly IUsageSink? _forwardTo;
    private readonly Action<ConversationUsageAggregate>? _onAggregateUpdated;

    /// <summary>Creates a ledger scoped to a single root conversation.</summary>
    /// <param name="rootConversationId">The root conversation to accumulate usage for.</param>
    /// <param name="pricingResolver">
    ///     Optional public-pricing resolver. When supplied, an observation that arrives without an estimated
    ///     public cost has one filled in from the resolved rates for its effective model.
    /// </param>
    /// <param name="forwardTo">
    ///     Optional external root sink each merged record is also relayed to, so a nested root conversation
    ///     (e.g. a workflow controller loop) can fold its whole subtree's usage — its own turns AND every
    ///     descendant that already relays into THIS ledger — into a parent conversation's total (issue #196).
    ///     The forwarded record keeps its <see cref="UsageRecord.ProviderAttemptId" />, so the parent sink
    ///     dedups it the same way and re-stamps its own root id/revision. Null keeps the historical behaviour.
    /// </param>
    /// <param name="onAggregateUpdated">
    ///     Optional callback invoked (outside the ledger lock, with an InProgress snapshot) after every
    ///     accepted observation, so the owning loop can broadcast a live usage frame to the parent run's
    ///     subscribers as descendant spend folds in (#196). Terminal completeness is not the ledger's concern
    ///     — it is stamped by the owner's persist path — so live snapshots are always InProgress here.
    /// </param>
    public UsageLedger(
        string rootConversationId,
        IPricingResolver? pricingResolver = null,
        IUsageSink? forwardTo = null,
        Action<ConversationUsageAggregate>? onAggregateUpdated = null
    )
    {
        RootConversationId = rootConversationId;
        _pricingResolver = pricingResolver;
        _forwardTo = forwardTo;
        _onAggregateUpdated = onAggregateUpdated;
    }

    /// <summary>The root conversation this ledger accumulates usage for.</summary>
    public string RootConversationId { get; }

    /// <inheritdoc />
    public void RecordUsage(UsageRecord observation)
    {
        _ = UpsertAttempt(observation);
    }

    /// <summary>
    ///     Merges an observation into the record for its <see cref="UsageRecord.ProviderAttemptId" /> —
    ///     cumulative MAX per count, finalized once any finalized observation is seen, and first-wins for
    ///     <see cref="UsageRecord.OccurredAtUtc" /> — assigns a fresh committed revision, and returns the
    ///     merged record. Idempotent under replay and safe out-of-order, so cumulative streaming updates for
    ///     one attempt collapse to a single billable record.
    /// </summary>
    public UsageRecord UpsertAttempt(UsageRecord observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        UsageRecord merged;
        lock (_gate)
        {
            _ = _byAttempt.TryGetValue(observation.ProviderAttemptId, out var existing);
            merged = Merge(existing, observation);
            var revision = _watermark.Allocate();

            // The ledger is scoped to one root conversation, so every observation it receives is
            // attributed to that root — callers (e.g. the sub-agent relay) need not know the root id.
            merged = merged with
            {
                Revision = revision,
                RootConversationId = RootConversationId,
            };
            merged = WithEstimatedCost(merged, observation);
            _byAttempt[observation.ProviderAttemptId] = merged;
            _watermark.Commit(revision);
        }

        // Relay the merged record to an optional external root sink OUTSIDE the lock, so a slow parent
        // sink never blocks concurrent usage updates on this ledger. Forwarding the merged (cumulative)
        // record keeps the same ProviderAttemptId, so the parent re-merges idempotently and re-stamps its
        // own root id/revision — cumulative streaming updates collapse to one billable record there too.
        // The forwarded copy names THIS ledger's root as its parent execution when it had none (#681): inside
        // this ledger a Primary record is the root's own turn, but in the parent conversation it is the spend of
        // a nested execution - a workflow controller's own turns - and must fold onto that execution's row, not
        // the parent's. Descendant records already carry their own parent and pass through unchanged.
        _forwardTo?.RecordUsage(
            merged.ParentExecutionId is null ? merged with { ParentExecutionId = RootConversationId } : merged
        );

        // Notify observers (e.g. the owning loop, which broadcasts a live usage frame to the parent run's
        // subscribers) that the folded aggregate changed. Fired OUTSIDE the lock with an InProgress snapshot;
        // terminal Complete/Partial is stamped by the owner's persist path, not by these live updates (#196).
        _onAggregateUpdated?.Invoke(Snapshot());

        return merged;
    }

    /// <summary>
    ///     Produces the current aggregate snapshot folded over all attempts, stamped with the gap-free
    ///     watermark and the given completeness state.
    /// </summary>
    public ConversationUsageAggregate Snapshot(UsageCompleteness completeness = UsageCompleteness.InProgress)
    {
        UsageRecord[] records;
        long prefix;
        lock (_gate)
        {
            // Copy the records and watermark under the lock, then fold (group/sort) OUTSIDE it, so reporting
            // cost does not hold the mutation lock and block concurrent usage updates as history grows.
            records = [.. _byAttempt.Values];
            prefix = _watermark.Prefix;
        }

        return ConversationUsageAggregate.Fold(RootConversationId, records, prefix, completeness);
    }

    /// <summary>
    ///     Returns the current deduped canonical records (one per provider attempt) under the lock, so a
    ///     caller can persist them durably. These are the source of truth the aggregate is folded from.
    /// </summary>
    public IReadOnlyList<UsageRecord> SnapshotRecords()
    {
        lock (_gate)
        {
            return [.. _byAttempt.Values];
        }
    }

    /// <summary>
    ///     Rebuilds ledger state from durable records (e.g. after a process/agent restart), restoring the
    ///     watermark to <paramref name="foldedRevision" /> so subsequent usage continues strictly above the
    ///     persisted baseline. A live in-memory observation for an attempt is never overwritten by a seed.
    /// </summary>
    public void SeedFromRecords(IEnumerable<UsageRecord> records, long foldedRevision)
    {
        ArgumentNullException.ThrowIfNull(records);

        lock (_gate)
        {
            foreach (var record in records)
            {
                _ = _byAttempt.TryAdd(record.ProviderAttemptId, WithDerivedCompleteness(WithDerivedProvenance(record)));
            }

            _watermark.SeedPrefix(foldedRevision);
        }
    }

    private static UsageRecord WithDerivedProvenance(UsageRecord record)
    {
        // A row persisted before CostProvenance existed (#367) deserializes with the default Unavailable even
        // when a real cost sits right beside it. Re-derive the provenance on seed from which cost field is
        // populated — the same "higher information wins" reasoning the merge path uses — rather than restoring
        // the misleading default verbatim (#393). Provider-reported outranks a public estimate; a row with no
        // cost at all has nothing to derive and stays Unavailable. Only the default is derived over: an
        // explicitly stamped provenance is trusted as-is.
        if (record.CostProvenance != CostProvenance.Unavailable)
        {
            return record;
        }

        if (record.ProviderReportedCostMicros is not null)
        {
            return record with { CostProvenance = CostProvenance.ProviderReported };
        }

        if (record.EstimatedPublicCostMicros is not null)
        {
            return record with { CostProvenance = CostProvenance.PublicEstimate };
        }

        return record;
    }

    private static UsageRecord WithDerivedCompleteness(UsageRecord record)
    {
        // Same shape as WithDerivedProvenance, for #682's field: a row persisted before CostCompleteness
        // existed deserializes with the Unavailable default beside a populated estimate. That estimate was
        // computed from two categories (input + output) with every cache category ignored, so it is a
        // lower bound — Partial — not unavailable and certainly not complete. Only the default is derived
        // over; an explicitly stamped completeness is trusted as-is.
        if (record.CostCompleteness != CostCompleteness.Unavailable || record.EstimatedPublicCostMicros is null)
        {
            return record;
        }

        return record with
        {
            CostCompleteness = CostCompleteness.Partial,
        };
    }

    private UsageRecord WithEstimatedCost(UsageRecord merged, UsageRecord observation)
    {
        // An observation that already carries an estimate was priced upstream (a child ledger relaying into
        // this one, or a caller that resolved its own catalog); its figure and completeness stamp are the
        // record of truth and are never re-priced here. Only an observation that arrives WITHOUT one is
        // priced — and it is priced from the MERGED (cumulative max) counts, not the observation's, so a
        // cumulative stream that grows the counts grows the estimate with them rather than freezing the
        // first chunk's figure onto the record (#682).
        if (_pricingResolver is null || observation.EstimatedPublicCostMicros is not null)
        {
            return merged;
        }

        var pricing = _pricingResolver.Resolve(merged.EffectiveModelId);
        if (pricing is null)
        {
            return merged;
        }

        var estimate = pricing.Estimate(merged);
        return merged with
        {
            EstimatedPublicCostMicros = estimate.Micros,
            CostCompleteness = estimate.Completeness,
            // A provider-reported figure is the ground truth for provenance, whether it arrived already
            // stamped or only as the populated field; filling in a public estimate alongside it (kept for
            // comparison) must not downgrade that. And a null estimate (nothing priceable) resolves no cost,
            // so it stamps no provenance.
            CostProvenance =
                merged.CostProvenance == CostProvenance.ProviderReported
                || merged.ProviderReportedCostMicros is not null
                    ? CostProvenance.ProviderReported
                : estimate.Micros is not null ? CostProvenance.PublicEstimate
                : merged.CostProvenance,
        };
    }

    private static UsageRecord Merge(UsageRecord? existing, UsageRecord observation)
    {
        if (existing is null)
        {
            return observation;
        }

        return observation with
        {
            InputTokens = Math.Max(existing.InputTokens, observation.InputTokens),
            OutputTokens = Math.Max(existing.OutputTokens, observation.OutputTokens),
            CacheReadTokens = Math.Max(existing.CacheReadTokens, observation.CacheReadTokens),
            CacheWriteTokens = Math.Max(existing.CacheWriteTokens, observation.CacheWriteTokens),
            CacheWrite1hTokens = MaxNullable(existing.CacheWrite1hTokens, observation.CacheWrite1hTokens),
            ReasoningTokens = Math.Max(existing.ReasoningTokens, observation.ReasoningTokens),
            EstimatedPublicCostMicros = MaxNullable(
                existing.EstimatedPublicCostMicros,
                observation.EstimatedPublicCostMicros
            ),
            // The completeness stamp travels with the estimate it describes: an observation that carries
            // its own estimate carries the stamp for it; one that carries none inherits the existing stamp,
            // which WithEstimatedCost then replaces together with the recomputed figure.
            CostCompleteness = observation.EstimatedPublicCostMicros is not null
                ? observation.CostCompleteness
                : existing.CostCompleteness,
            CompactionCheckpointId = observation.CompactionCheckpointId ?? existing.CompactionCheckpointId,
            ProviderReportedCostMicros = MaxNullable(
                existing.ProviderReportedCostMicros,
                observation.ProviderReportedCostMicros
            ),
            // Higher-information provenance wins: the enum is ordered Unavailable < PublicEstimate <
            // ProviderReported, so the max ordinal is the more-informative value — mirroring the MaxNullable
            // cost merges above rather than defaulting to the incoming (last) observation (#367).
            CostProvenance = (CostProvenance)Math.Max((int)existing.CostProvenance, (int)observation.CostProvenance),
            // First-wins, NOT the record-with default of taking the incoming (last) value: a cumulative
            // stream re-observes one attempt many times, so last-wins would stamp when the final chunk
            // arrived rather than when the attempt happened — misfiling an attempt that straddles a UTC-day
            // boundary in a per-day rollup (#307).
            OccurredAtUtc = UsageRecord.EarliestOccurredAt(existing.OccurredAtUtc, observation.OccurredAtUtc),
            Finalized = existing.Finalized || observation.Finalized,
        };
    }

    private static long? MaxNullable(long? left, long? right)
    {
        if (left is null)
        {
            return right;
        }

        if (right is null)
        {
            return left;
        }

        return Math.Max(left.Value, right.Value);
    }
}
