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

    /// <summary>
    ///     Known-cost subtotal of each attempt's <see cref="UsageRecord.PreferredCostMicros" /> (provider-reported
    ///     when present, else the public estimate), or null when no attempt had either.
    /// </summary>
    public long? PreferredCostMicros { get; init; }

    /// <summary>
    ///     Completeness of <see cref="EstimatedPublicCostMicros" /> (#682): <see cref="CostCompleteness.Unavailable" />
    ///     when the subtotal is null, <see cref="CostCompleteness.Complete" /> only when every folded attempt's
    ///     estimate is complete, else <see cref="CostCompleteness.Partial" /> — including an attempt whose
    ///     estimate predates completeness stamping, and an attempt of a priced model that carries no estimate.
    /// </summary>
    public CostCompleteness EstimatedCostCompleteness { get; init; } = CostCompleteness.Unavailable;

    /// <summary>Number of distinct billable attempts folded into this row.</summary>
    public int AttemptCount { get; init; }
}

/// <summary>
///     Per-execution rollup row (#681; spec 679 §4.3): the same fold as <see cref="ModelUsageRow" />, keyed by
///     <see cref="AgentExecutionRef.ExecutionIdOf" /> instead of model, so one agent loop's spend — its own
///     turns, continuations and compaction passes — reads as one row. Not persisted: derived on read from the
///     conversation's canonical records.
/// </summary>
public sealed record ExecutionUsageRow
{
    /// <summary>The execution (thread) id these totals belong to.</summary>
    public required string ExecutionId { get; init; }

    /// <summary>The distinct execution kinds folded in, in first-seen order.</summary>
    public IReadOnlyList<UsageExecutionKind> ExecutionKinds { get; init; } = [];

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

    /// <summary>Known-cost subtotal of each attempt's preferred figure, or null when no attempt had either.</summary>
    public long? PreferredCostMicros { get; init; }

    /// <summary>
    ///     Which source the preferred figure rests on: <see cref="CostProvenance.ProviderReported" /> only when
    ///     every priced attempt was provider-reported, <see cref="CostProvenance.PublicEstimate" /> when any
    ///     attempt fell back to an estimate, <see cref="CostProvenance.Unavailable" /> when there is no figure.
    /// </summary>
    public CostProvenance CostProvenance { get; init; } = CostProvenance.Unavailable;

    /// <summary>Completeness of <see cref="EstimatedPublicCostMicros" />, folded like <see cref="ModelUsageRow" />.</summary>
    public CostCompleteness EstimatedCostCompleteness { get; init; } = CostCompleteness.Unavailable;

    /// <summary>Number of distinct billable attempts folded into this row.</summary>
    public int AttemptCount { get; init; }

    /// <summary>How many of those attempts were <see cref="UsageExecutionKind.Compaction" /> passes.</summary>
    public int CompactionAttemptCount { get; init; }
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

    /// <summary>
    ///     Grand preferred-display cost (per attempt: provider-reported when present, else the public
    ///     estimate), or null when <b>any</b> model has neither — the same strict fold as the other totals.
    /// </summary>
    public long? PreferredCostMicros { get; init; }

    /// <summary>
    ///     Completeness of <see cref="EstimatedPublicCostMicros" /> across the conversation (#682): Unavailable
    ///     when the strict total is null, Complete only when every model's estimate is complete, else Partial.
    ///     A consumer that shows the total must show this beside it; a partial figure is a lower bound.
    /// </summary>
    public CostCompleteness EstimatedCostCompleteness { get; init; } = CostCompleteness.Unavailable;

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
                PreferredCostMicros = SumKnown(g.Select(r => r.PreferredCostMicros)),
                EstimatedCostCompleteness = FoldCompleteness(
                    SumKnown(g.Select(r => r.EstimatedPublicCostMicros)),
                    g.Select(r => r.CostCompleteness)
                ),
                AttemptCount = g.Count(),
            })
            .ToList();

        var estimatedTotal = SumStrict(perModel.Select(m => m.EstimatedPublicCostMicros));

        return new ConversationUsageAggregate
        {
            RootConversationId = rootConversationId,
            FoldedRevision = foldedRevision,
            Completeness = completeness,
            PerModel = perModel,
            TotalTokens = perModel.Sum(m => m.TotalTokens),
            EstimatedPublicCostMicros = estimatedTotal,
            ProviderReportedCostMicros = SumStrict(perModel.Select(m => m.ProviderReportedCostMicros)),
            PreferredCostMicros = SumStrict(perModel.Select(m => m.PreferredCostMicros)),
            EstimatedCostCompleteness = FoldCompleteness(
                estimatedTotal,
                perModel.Select(m => m.EstimatedCostCompleteness)
            ),
        };
    }

    /// <summary>
    ///     Folds the same atomic records into per-execution rows (#681): deduplicates by
    ///     <see cref="UsageRecord.ProviderAttemptId" /> exactly as <see cref="Fold" /> does, then sums additively
    ///     grouped by <see cref="AgentExecutionRef.ExecutionIdOf" />. Rows are ordered by execution id; the sum
    ///     over rows equals the conversation total by construction, because both are one fold of one ledger.
    /// </summary>
    /// <param name="records">The atomic records to fold (may contain superseded revisions).</param>
    public static IReadOnlyList<ExecutionUsageRow> FoldByExecution(IEnumerable<UsageRecord> records)
    {
        return
        [
            .. DedupeByAttempt(records)
                .GroupBy(AgentExecutionRef.ExecutionIdOf)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g =>
                {
                    var estimated = SumKnown(g.Select(r => r.EstimatedPublicCostMicros));
                    var preferred = SumKnown(g.Select(r => r.PreferredCostMicros));
                    return new ExecutionUsageRow
                    {
                        ExecutionId = g.Key,
                        ExecutionKinds = [.. g.Select(r => r.ExecutionKind).Distinct()],
                        InputTokens = g.Sum(r => r.InputTokens),
                        OutputTokens = g.Sum(r => r.OutputTokens),
                        CacheReadTokens = g.Sum(r => r.CacheReadTokens),
                        CacheWriteTokens = g.Sum(r => r.CacheWriteTokens),
                        ReasoningTokens = g.Sum(r => r.ReasoningTokens),
                        TotalTokens = g.Sum(r => r.TotalTokens),
                        EstimatedPublicCostMicros = estimated,
                        ProviderReportedCostMicros = SumKnown(g.Select(r => r.ProviderReportedCostMicros)),
                        PreferredCostMicros = preferred,
                        CostProvenance =
                            preferred is null ? CostProvenance.Unavailable
                            : g.All(r => r.PreferredCostMicros is null || r.ProviderReportedCostMicros is not null)
                                ? CostProvenance.ProviderReported
                            : CostProvenance.PublicEstimate,
                        EstimatedCostCompleteness = FoldCompleteness(estimated, g.Select(r => r.CostCompleteness)),
                        AttemptCount = g.Count(),
                        CompactionAttemptCount = g.Count(r => r.ExecutionKind == UsageExecutionKind.Compaction),
                    };
                }),
        ];
    }

    /// <summary>
    ///     Completeness of a summed estimate: no figure at all is Unavailable; a figure is Complete only when
    ///     every part is Complete, else Partial. A part stamped Unavailable while the sum exists (a priced
    ///     model's attempt that carries no estimate, or a row persisted before completeness existed) makes the
    ///     sum Partial, not Unavailable — there is a number, but it is not the whole number.
    /// </summary>
    public static CostCompleteness FoldCompleteness(long? sum, IEnumerable<CostCompleteness> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        if (sum is null)
        {
            return CostCompleteness.Unavailable;
        }

        return parts.All(p => p == CostCompleteness.Complete) ? CostCompleteness.Complete : CostCompleteness.Partial;
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
    /// <summary>Sums the known values, or null when none is known — an absent figure is never a zero.</summary>
    public static long? SumKnown(IEnumerable<long?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

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
