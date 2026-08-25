namespace AchieveAi.LmDotnetTools.LmEval.Running;

/// <summary>Why an item contributed no score to the run's conditional metrics.</summary>
public enum ScoreExclusion
{
    /// <summary>It did contribute — a clean, counted score.</summary>
    None,

    /// <summary>A gate short-circuited it before any judge ran. No score exists.</summary>
    GateRejected,

    /// <summary>The panel ran and the judges landed on opposite sides of the threshold.</summary>
    Straddled,

    /// <summary>The panel could not be run, or no ballot survived the abstain filter.</summary>
    NoDecision,

    /// <summary>The verdict is real but degraded, so it is not pooled with full-panel rows.</summary>
    Degraded,

    /// <summary>
    /// The candidate declared no generator family, so the generator-exclusion filter never ran on
    /// it. Never <i>same family as the judge</i> — unknown.
    /// </summary>
    UnknownGeneratorFamily,

    /// <summary>The item faulted, so the run has no verdict for it at all.</summary>
    Faulted,
}

/// <summary>One corpus item's outcome in a run.</summary>
public sealed record EvalItemResult
{
    /// <summary>Which corpus item this is.</summary>
    public required string CandidateId { get; init; }

    /// <summary>
    /// The verdict, or null when the item faulted before one could be produced. Null is not a
    /// failure and not a pass — it is an item the run did not reach a decision on, and it still
    /// occupies the denominator.
    /// </summary>
    public Verdict? Verdict { get; init; }

    /// <summary>
    /// Stable, non-sensitive text naming why the item faulted — an exception type name, never its
    /// message. Null unless <see cref="Verdict"/> is null.
    /// </summary>
    public string? FaultReason { get; init; }

    /// <summary>
    /// Why this item contributed no score, or <see cref="ScoreExclusion.None"/> when it did.
    /// Computed once by the runner so every aggregate and every reader segment on the same
    /// classification.
    /// </summary>
    public required ScoreExclusion Exclusion { get; init; }

    /// <summary>
    /// Host-recorded cost for this item's threads, in USD micro-units. The runner <i>reads</i>
    /// cost; the harness never produces it.
    /// </summary>
    public long? CostMicros { get; init; }

    /// <summary>True when this item's score entered the conditional metrics.</summary>
    public bool IsScored => Exclusion == ScoreExclusion.None;
}

/// <summary>
/// What one replay over one corpus snapshot emitted.
/// <para>
/// <b>The denominator, stated once, because omitting it is how a variant games this.</b> Every rate
/// here is over <see cref="CorpusSize"/> — the item count of the named snapshot, not the count the
/// run managed to process. An item that yielded no score still occupies the denominator and never
/// the numerator. Declining to score a hard item therefore <i>lowers</i>
/// <see cref="PassRate"/> instead of flattering it.
/// </para>
/// <para>
/// <see cref="MeanScore"/> and <see cref="P10Score"/> are the deliberate exception: they are
/// <b>conditional</b>, defined only over scored items, and are never reported without
/// <see cref="Coverage"/> beside them. A mean over a different subset is a different quantity, not
/// a worse one — which is why both are nullable and both sit next to the coverage that qualifies
/// them.
/// </para>
/// </summary>
public sealed record EvalRun
{
    /// <summary>Stable identity of this run.</summary>
    public required string RunId { get; init; }

    /// <summary>The task type. Scores are never compared across task types.</summary>
    public required string TaskType { get; init; }

    /// <summary>The corpus replayed.</summary>
    public required string CorpusId { get; init; }

    /// <summary>Identity of the exact snapshot replayed.</summary>
    public required string CorpusSnapshotHash { get; init; }

    /// <summary>Identity of every score-affecting evaluator input.</summary>
    public required string EvaluatorConfigHash { get; init; }

    /// <summary>The rubric scored against.</summary>
    public required string RubricId { get; init; }

    /// <summary>Its exact version.</summary>
    public required string RubricVersion { get; init; }

    /// <summary>Every item's outcome, in corpus order.</summary>
    public required IReadOnlyList<EvalItemResult> Items { get; init; }

    /// <summary>The denominator: the snapshot's item count.</summary>
    public int CorpusSize => Items.Count;

    /// <summary>How many items yielded a counted, non-excluded score.</summary>
    public int ScoredItems => Items.Count(i => i.IsScored);

    /// <summary>
    /// <see cref="ScoredItems"/> over <see cref="CorpusSize"/>. The qualifier that must travel with
    /// every conditional metric below.
    /// </summary>
    public double Coverage => (double)ScoredItems / CorpusSize;

    /// <summary>
    /// Conditional mean over scored items. <b>Null</b> when nothing was scored — not zero, which
    /// would read as a corpus of uniformly worst answers.
    /// </summary>
    public double? MeanScore =>
        ScoredScores.Count == 0 ? null : ScoredScores.Average();

    /// <summary>
    /// Conditional 10th percentile over scored items, by nearest rank on the ascending order:
    /// index <c>ceil(0.10 * n) - 1</c>, clamped into range. Nearest rank rather than an
    /// interpolation so the value is always one an actual item scored, and so two runs of the same
    /// size are compared on the same rule. Null when nothing was scored.
    /// </summary>
    public double? P10Score
    {
        get
        {
            var scores = ScoredScores;
            if (scores.Count == 0)
            {
                return null;
            }

            var sorted = scores.Order().ToList();
            var rank = (int)Math.Ceiling(0.10 * sorted.Count) - 1;
            return sorted[Math.Clamp(rank, 0, sorted.Count - 1)];
        }
    }

    /// <summary>
    /// Passing items over <see cref="CorpusSize"/>. An excluded row is out of the numerator and
    /// still in the denominator.
    /// </summary>
    public double PassRate =>
        (double)
            Items.Count(i => i.IsScored && i.Verdict?.Outcome == VerdictOutcome.Pass)
        / CorpusSize;

    /// <summary>
    /// Items the panel could not decide, over <see cref="CorpusSize"/>. A run where the panel could
    /// not decide on 30% of items is not a clean result even if the remaining 70% look good.
    /// </summary>
    public int NoDecisionCount =>
        Items.Count(i => i.Verdict?.Outcome == VerdictOutcome.NoDecision);

    /// <summary>The no-decision count as a fraction of the corpus.</summary>
    public double NoDecisionRate => (double)NoDecisionCount / CorpusSize;

    /// <summary>
    /// Items where the two counted ballots landed on opposite sides of the pass threshold, whether
    /// or not an arbiter then resolved it.
    /// <para>
    /// This is the primary judge-reliability signal available without any human labels, and a
    /// rising straddle rate on a stable corpus means the rubric is underspecified at its threshold.
    /// It counts <b>disagreement</b>, not outcome, which is why an arbiter-resolved straddle — a
    /// row that ends up Pass or Fail — is in it.
    /// </para>
    /// </summary>
    public int StraddleCount =>
        Items.Count(i => TieBreakRules.IsStraddle(i.Verdict?.TieBreakRule));

    /// <summary>The straddle count as a fraction of the corpus.</summary>
    public double StraddleRate => (double)StraddleCount / CorpusSize;

    /// <summary>Straddles the arbiter decided.</summary>
    public int ArbiterResolvedStraddles =>
        Items.Count(i => TieBreakRules.IsArbiterResolved(i.Verdict?.TieBreakRule));

    /// <summary>Straddles left standing as a split.</summary>
    public int UnresolvedStraddles => StraddleCount - ArbiterResolvedStraddles;

    /// <summary>
    /// Rows excluded from the aggregates because the panel degraded. Reported, never pooled with
    /// clean rows, and never removed from the denominator.
    /// </summary>
    public int DegradedCount => Items.Count(i => i.Exclusion == ScoreExclusion.Degraded);

    /// <summary>
    /// Rows excluded because the candidate declared no generator family, so the generator-exclusion
    /// filter never ran on them.
    /// </summary>
    public int UnknownGeneratorFamilyCount =>
        Items.Count(i => i.Exclusion == ScoreExclusion.UnknownGeneratorFamily);

    /// <summary>Rows a gate short-circuited before any judge ran.</summary>
    public int GateRejectedCount => Items.Count(i => i.Exclusion == ScoreExclusion.GateRejected);

    /// <summary>Items that faulted, so the run holds no verdict for them.</summary>
    public int FaultedCount => Items.Count(i => i.Exclusion == ScoreExclusion.Faulted);

    /// <summary>Total host-recorded cost across the corpus, in USD micro-units.</summary>
    public long TotalCostMicros => Items.Sum(i => i.CostMicros ?? 0L);

    /// <summary>
    /// Mean cost per <b>corpus item</b>, not per scored item — the same denominator every other
    /// rate uses, so a variant cannot look cheap by declining to score.
    /// </summary>
    public long MeanCostMicros => TotalCostMicros / CorpusSize;

    private IReadOnlyList<double> ScoredScores =>
        [.. Items.Where(i => i.IsScored).Select(i => i.Verdict!.Score!.Value)];
}
