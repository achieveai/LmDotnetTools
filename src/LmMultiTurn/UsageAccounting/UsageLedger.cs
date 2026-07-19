using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;

/// <summary>
///     Execution-scoped collector that normalizes many streaming usage observations into one durable
///     <see cref="UsageRecord" /> per provider attempt, and produces the conversation aggregate snapshot
///     with a complete-prefix watermark. Additive across the whole conversation tree (issue #196): the
///     root conversation creates one ledger and every descendant relays its observations into it.
/// </summary>
public sealed class UsageLedger
{
    private readonly string _rootConversationId;
    private readonly object _gate = new();
    private readonly Dictionary<string, UsageRecord> _byAttempt = new(StringComparer.Ordinal);
    private readonly RevisionWatermark _watermark = new();

    /// <summary>Creates a ledger scoped to a single root conversation.</summary>
    public UsageLedger(string rootConversationId)
    {
        _rootConversationId = rootConversationId;
    }

    /// <summary>The root conversation this ledger accumulates usage for.</summary>
    public string RootConversationId => _rootConversationId;

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
            _byAttempt.TryGetValue(observation.ProviderAttemptId, out var existing);
            var merged = Merge(existing, observation);
            var revision = _watermark.Allocate();
            merged = merged with { Revision = revision };
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
        lock (_gate)
        {
            return ConversationUsageAggregate.Fold(
                _rootConversationId,
                _byAttempt.Values,
                _watermark.Prefix,
                completeness);
        }
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
