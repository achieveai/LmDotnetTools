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

    /// <summary>Creates a ledger scoped to a single root conversation.</summary>
    /// <param name="rootConversationId">The root conversation to accumulate usage for.</param>
    /// <param name="pricingResolver">
    ///     Optional public-pricing resolver. When supplied, an observation that arrives without an estimated
    ///     public cost has one filled in from the resolved rates for its effective model.
    /// </param>
    public UsageLedger(string rootConversationId, IPricingResolver? pricingResolver = null)
    {
        RootConversationId = rootConversationId;
        _pricingResolver = pricingResolver;
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
    ///     cumulative MAX per count, finalized once any finalized observation is seen — assigns a fresh
    ///     committed revision, and returns the merged record. Idempotent under replay and safe out-of-order,
    ///     so cumulative streaming updates for one attempt collapse to a single billable record.
    /// </summary>
    public UsageRecord UpsertAttempt(UsageRecord observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        lock (_gate)
        {
            _ = _byAttempt.TryGetValue(observation.ProviderAttemptId, out var existing);
            var merged = Merge(existing, observation);
            var revision = _watermark.Allocate();

            // The ledger is scoped to one root conversation, so every observation it receives is
            // attributed to that root — callers (e.g. the sub-agent relay) need not know the root id.
            merged = merged with { Revision = revision, RootConversationId = RootConversationId };
            merged = WithEstimatedCost(merged);
            _byAttempt[observation.ProviderAttemptId] = merged;
            _watermark.Commit(revision);
            return merged;
        }
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
                _ = _byAttempt.TryAdd(record.ProviderAttemptId, record);
            }

            _watermark.SeedPrefix(foldedRevision);
        }
    }

    private UsageRecord WithEstimatedCost(UsageRecord record)
    {
        // Only fill an estimate the observation didn't already carry — a provider-reported estimate wins.
        if (_pricingResolver is null || record.EstimatedPublicCostMicros is not null)
        {
            return record;
        }

        var pricing = _pricingResolver.Resolve(record.EffectiveModelId);
        return pricing is null
            ? record
            : record with { EstimatedPublicCostMicros = pricing.EstimateMicros(record.InputTokens, record.OutputTokens) };
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
            ReasoningTokens = Math.Max(existing.ReasoningTokens, observation.ReasoningTokens),
            EstimatedPublicCostMicros =
                MaxNullable(existing.EstimatedPublicCostMicros, observation.EstimatedPublicCostMicros),
            ProviderReportedCostMicros =
                MaxNullable(existing.ProviderReportedCostMicros, observation.ProviderReportedCostMicros),
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
