namespace AchieveAi.LmDotnetTools.LmCore.Models;

/// <summary>
///     Completeness of a <see cref="ConversationUsageAggregate" /> — whether it reflects every billable
///     attempt for the root conversation, or is still being accumulated / known to be missing usage.
/// </summary>
public enum UsageCompleteness
{
    /// <summary>The conversation (or a descendant) is still running; more usage may arrive.</summary>
    InProgress,

    /// <summary>Terminal, but some incurred usage could not be captured — the total is a lower bound.</summary>
    Partial,

    /// <summary>Terminal and every known descendant's usage is durably recorded.</summary>
    Complete,
}

/// <summary>
///     Per-model rollup row within a <see cref="ConversationUsageAggregate" />.
/// </summary>
public sealed record ModelUsageRow
{
    /// <summary>The effective model id these totals belong to.</summary>
    public required string ModelId { get; init; }

    /// <summary>Summed input tokens.</summary>
    public long InputTokens { get; init; }

    /// <summary>Summed output tokens.</summary>
    public long OutputTokens { get; init; }

    /// <summary>Summed cached-read tokens (subset of input).</summary>
    public long CacheReadTokens { get; init; }

    /// <summary>Summed cache-creation tokens (additive).</summary>
    public long CacheWriteTokens { get; init; }

    /// <summary>Summed reasoning tokens (subset of output).</summary>
    public long ReasoningTokens { get; init; }

    /// <summary>Summed total tokens (input + cache-write + output).</summary>
    public long TotalTokens { get; init; }

    /// <summary>Known-cost subtotal for the public estimate, or null when no attempt had one.</summary>
    public long? EstimatedPublicCostMicros { get; init; }

    /// <summary>Known-cost subtotal for provider-reported cost, or null when no attempt had one.</summary>
    public long? ProviderReportedCostMicros { get; init; }

    /// <summary>Number of distinct billable attempts folded into this row.</summary>
    public int AttemptCount { get; init; }
}

/// <summary>
///     The authoritative, rebuildable read projection of a conversation's token usage and cost — the sum
///     of every <see cref="UsageRecord" /> across the whole conversation tree (issue #196).
/// </summary>
/// <remarks>
///     Aggregation is strictly additive and grouped by effective model — never overwritten or max'd the
///     way the single-generation <see cref="Utils.UsageAccumulator" /> is. Cost fields are known-cost
///     subtotals: a null means no contributing attempt had that cost dimension, so callers must render it
///     as "unavailable" rather than <c>0</c>.
/// </remarks>
public sealed record ConversationUsageAggregate
{
    /// <summary>The root conversation these totals belong to.</summary>
    public required string RootConversationId { get; init; }

    /// <summary>Schema version of the persisted/serialized projection.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    ///     Watermark: the projection reflects a complete, gap-free prefix of every record revision through
    ///     this value. Computed by the ledger's serialized fold; the pure <see cref="Fold" /> records
    ///     whatever boundary the caller proved.
    /// </summary>
    public long FoldedRevision { get; init; }

    /// <summary>Whether the projection is still accumulating, partial, or complete.</summary>
    public UsageCompleteness Completeness { get; init; } = UsageCompleteness.InProgress;

    /// <summary>Per-model rollup rows, ordered by model id.</summary>
    public IReadOnlyList<ModelUsageRow> PerModel { get; init; } = [];

    /// <summary>Grand total tokens across all models.</summary>
    public long TotalTokens { get; init; }

    /// <summary>
    ///     Grand public-estimate cost across all models, or null when <b>any</b> model is unpriced — a strict
    ///     fold, so this is never a confident number that silently omits an unpriced model (#377).
    /// </summary>
    public long? EstimatedPublicCostMicros { get; init; }

    /// <summary>
    ///     Grand provider-reported cost across all models, or null when <b>any</b> model lacks a reported
    ///     cost — a strict fold, for the same reason as <see cref="EstimatedPublicCostMicros" /> (#377).
    /// </summary>
    public long? ProviderReportedCostMicros { get; init; }

    /// <summary>ISO currency code for the cost figures.</summary>
    public string Currency { get; init; } = "USD";

    /// <summary>
    ///     Folds a set of atomic <see cref="UsageRecord" />s into a conversation aggregate: deduplicates by
    ///     <see cref="UsageRecord.ProviderAttemptId" /> (latest <see cref="UsageRecord.Revision" /> wins),
    ///     then sums additively grouped by <see cref="UsageRecord.EffectiveModelId" />.
    /// </summary>
    /// <param name="rootConversationId">The root conversation id.</param>
    /// <param name="records">The atomic records to fold (may contain superseded revisions).</param>
    /// <param name="foldedRevision">The proven gap-free watermark this fold covers.</param>
    /// <param name="completeness">The completeness state to stamp on the projection.</param>
    public static ConversationUsageAggregate Fold(
        string rootConversationId,
        IEnumerable<UsageRecord> records,
        long foldedRevision,
        UsageCompleteness completeness = UsageCompleteness.InProgress
    )
    {
        var deduped = DedupeByAttempt(records);

        var perModel = deduped
            .GroupBy(r => r.EffectiveModelId)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new ModelUsageRow
            {
                ModelId = g.Key,
                InputTokens = g.Sum(r => r.InputTokens),
                OutputTokens = g.Sum(r => r.OutputTokens),
                CacheReadTokens = g.Sum(r => r.CacheReadTokens),
                CacheWriteTokens = g.Sum(r => r.CacheWriteTokens),
                ReasoningTokens = g.Sum(r => r.ReasoningTokens),
                TotalTokens = g.Sum(r => r.TotalTokens),
                EstimatedPublicCostMicros = SumKnown(g.Select(r => r.EstimatedPublicCostMicros)),
                ProviderReportedCostMicros = SumKnown(g.Select(r => r.ProviderReportedCostMicros)),
                AttemptCount = g.Count(),
            })
            .ToList();

        return new ConversationUsageAggregate
        {
            RootConversationId = rootConversationId,
            FoldedRevision = foldedRevision,
            Completeness = completeness,
            PerModel = perModel,
            TotalTokens = perModel.Sum(m => m.TotalTokens),
            EstimatedPublicCostMicros = SumStrict(perModel.Select(m => m.EstimatedPublicCostMicros)),
            ProviderReportedCostMicros = SumStrict(perModel.Select(m => m.ProviderReportedCostMicros)),
        };
    }

    /// <summary>
    ///     Collapses observations of one provider attempt into a single record, keyed by
    ///     <see cref="UsageRecord.ProviderAttemptId" />: the highest <see cref="UsageRecord.Revision" />
    ///     replaces earlier ones (cumulative streaming / retry).
    /// </summary>
    /// <remarks>
    ///     <see cref="UsageRecord.OccurredAtUtc" /> is the one exception to "highest revision wins": it is
    ///     first-wins, taking the earliest non-null value across the attempt's revisions. A later revision
    ///     records when the last cumulative chunk arrived, not when the attempt happened, so adopting it
    ///     would misfile an attempt straddling a UTC-day boundary in a per-day rollup. Records arriving from
    ///     two writers (a post-restart rebuild, a second instance, a relay) can also disagree, and the
    ///     higher-revision one is not necessarily the earlier observation.
    /// </remarks>
    /// <param name="records">The atomic records to collapse (may contain superseded revisions).</param>
    public static IReadOnlyList<UsageRecord> DedupeByAttempt(IEnumerable<UsageRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        return
        [
            .. records
                .GroupBy(r => r.ProviderAttemptId)
                .Select(g =>
                {
                    var latest = g.OrderByDescending(r => r.Revision).First();
                    var occurredAt = g.Aggregate(
                        (DateTimeOffset?)null,
                        (earliest, r) => UsageRecord.EarliestOccurredAt(earliest, r.OccurredAtUtc)
                    );
                    return latest with { OccurredAtUtc = occurredAt };
                }),
        ];
    }

    /// <summary>
    ///     Sums cost figures treating null as "unknown": null contributes nothing and the result is null
    ///     only when every value is null (a known-cost subtotal — never surfaces <c>0</c> for entirely
    ///     unknown pricing).
    /// </summary>
    private static long? SumKnown(IEnumerable<long?> values)
    {
        long sum = 0;
        var anyKnown = false;
        foreach (var value in values)
        {
            if (value.HasValue)
            {
                sum += value.Value;
                anyKnown = true;
            }
        }

        return anyKnown ? sum : null;
    }

    /// <summary>
    ///     Sums cost figures across a set of components strictly: any single null poisons the whole total,
    ///     so the result is null unless <b>every</b> component is known. Used to fold the per-model cost
    ///     subtotals into the conversation total — a conversation that used a priced model and an unpriced
    ///     one must surface <c>null</c> ("cost incomplete"), never a confident number that silently omits the
    ///     unpriced model. For a cost figure the failure direction matters: an under-count reads as "cheaper
    ///     than it was" and goes unquestioned, whereas an absent total is visible and recoverable (#377). The
    ///     per-attempt subtotal within a single model deliberately keeps <see cref="SumKnown" /> — pricing is
    ///     a property of the model, so an attempt-level null is a missing observation of a known-priced model,
    ///     not an unpriced one.
    /// </summary>
    private static long? SumStrict(IEnumerable<long?> values)
    {
        long sum = 0;
        var any = false;
        foreach (var value in values)
        {
            if (!value.HasValue)
            {
                return null;
            }

            sum += value.Value;
            any = true;
        }

        return any ? sum : null;
    }
}
