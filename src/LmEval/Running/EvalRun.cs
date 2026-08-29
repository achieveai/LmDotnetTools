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

    /// <summary>
    /// Identity of the exact snapshot replayed. Never blank: <see cref="BaselineComparer"/> decides
    /// comparability with an ordinal string comparison, and that returns <b>true</b> for two
    /// unknowns — so a pair of runs whose provenance was never recorded would be declared
    /// comparable. <c>required</c> is checked by the compiler at construction sites it can see and
    /// not by a deserializer filling a missing property, which is exactly how an unknown gets in.
    /// </summary>
    public required string CorpusSnapshotHash
    {
        get => _corpusSnapshotHash;
        init
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            _corpusSnapshotHash = value;
        }
    }

    /// <summary>
    /// Identity of every score-affecting evaluator input. Never blank, for the reason on
    /// <see cref="CorpusSnapshotHash"/>.
    /// </summary>
    public required string EvaluatorConfigHash
    {
        get => _evaluatorConfigHash;
        init
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            _evaluatorConfigHash = value;
        }
    }

    private readonly string _corpusSnapshotHash = string.Empty;
    private readonly string _evaluatorConfigHash = string.Empty;
    private readonly IReadOnlyList<EvalItemResult> _items = [];

    /// <summary>The rubric scored against.</summary>
    public required string RubricId { get; init; }

    /// <summary>Its exact version.</summary>
    public required string RubricVersion { get; init; }

    /// <summary>
    /// Every item's outcome, in corpus order. Never empty.
    /// <para>
    /// <see cref="Corpus.CorpusSnapshot.Create"/> already refuses an empty item list, and says why:
    /// an empty denominator makes every rate over it undefined rather than zero. But this record is
    /// public with settable members, so a caller — or a deserializer — can mint one the factory
    /// never saw, and then <see cref="Coverage"/> and <see cref="NoDecisionRate"/> yield NaN and
    /// flow into a comparison silently while <see cref="MeanCostMicros"/> divides by zero. The
    /// invariant belongs on the type that carries it, not only on the factory that usually builds
    /// it.
    /// </para>
    /// </summary>
    public required IReadOnlyList<EvalItemResult> Items
    {
        get => _items;
        init
        {
            ArgumentNullException.ThrowIfNull(value);

            if (value.Count == 0)
            {
                throw new ArgumentException(
                    "A run over no items has an empty denominator, which makes every rate over it "
                        + "undefined rather than zero — and NaN compares false against every "
                        + "threshold, so such a run would clear every regression trigger silently.",
                    nameof(value)
                );
            }

            _items = value;
        }
    }

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
    public double? MeanScore => ScoredScores.Count == 0 ? null : ScoredScores.Average();

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
        (double)Items.Count(i => i.IsScored && i.Verdict?.Outcome == VerdictOutcome.Pass) / CorpusSize;

    /// <summary>
    /// Items the panel could not decide, over <see cref="CorpusSize"/>. A run where the panel could
    /// not decide on 30% of items is not a clean result even if the remaining 70% look good.
    /// </summary>
    public int NoDecisionCount => Items.Count(i => i.Verdict?.Outcome == VerdictOutcome.NoDecision);

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
    public int StraddleCount => Items.Count(i => TieBreakRules.IsStraddle(i.Verdict?.TieBreakRule));

    /// <summary>The straddle count as a fraction of the corpus.</summary>
    public double StraddleRate => (double)StraddleCount / CorpusSize;

    /// <summary>Straddles the arbiter decided.</summary>
    public int ArbiterResolvedStraddles => Items.Count(i => TieBreakRules.IsArbiterResolved(i.Verdict?.TieBreakRule));

    /// <summary>Straddles left standing as a split.</summary>
    public int UnresolvedStraddles => StraddleCount - ArbiterResolvedStraddles;

    /// <summary>
    /// Rows excluded from the aggregates because the panel degraded. Reported, never pooled with
    /// clean rows, and never removed from the denominator.
    /// <para>
    /// This counts the <b>exclusion</b>, whose arms are ordered outcome-first, so a verdict that is
    /// both <see cref="VerdictOutcome.NoDecision"/> and degraded matches the earlier arm and is not
    /// in here. That ordering is deliberate — flipping it would relabel every plain no-decision as
    /// degraded the moment a single judge faulted — so §5.3's "was the panel down" question is
    /// answered by <see cref="DegradedVerdictCount"/> instead.
    /// </para>
    /// </summary>
    public int DegradedCount => Items.Count(i => i.Exclusion == ScoreExclusion.Degraded);

    /// <summary>
    /// Rows whose verdict was produced by a panel that could not be fully staffed, counted over
    /// <see cref="Verdict.Degradation"/> directly and therefore <b>independent</b> of which
    /// exclusion arm the row matched.
    /// <para>
    /// §5.3 exists so a reader can tell "the panel disagreed" from "the panel was down", and the
    /// case where those coincide — a no-decision on an unavailable panel, a straddle the arbiter
    /// could not resolve — is exactly the one an exclusion-based count cannot see. A faulted item
    /// has no verdict at all and is counted by <see cref="FaultedCount"/>, not here.
    /// </para>
    /// </summary>
    public int DegradedVerdictCount => Items.Count(i => i.Verdict is { Degradation: not PanelDegradation.None });

    /// <summary>
    /// Rows excluded because the candidate declared no generator family, so the generator-exclusion
    /// filter never ran on them.
    /// </summary>
    public int UnknownGeneratorFamilyCount => Items.Count(i => i.Exclusion == ScoreExclusion.UnknownGeneratorFamily);

    /// <summary>Rows a gate short-circuited before any judge ran.</summary>
    public int GateRejectedCount => Items.Count(i => i.Exclusion == ScoreExclusion.GateRejected);

    /// <summary>
    /// Items on which at least one gate could not run, counted over
    /// <see cref="Verdict.GateDecisions"/> directly and therefore <b>independent</b> of which
    /// exclusion arm the row matched — the same construction, and the same reason, as
    /// <see cref="DegradedVerdictCount"/>.
    /// <para>
    /// Counted <b>per item</b>, not per gate execution, so it is comparable with
    /// <see cref="FaultedCount"/>: three inconclusive gates on one item is one impaired item, and a
    /// per-execution count would exceed <see cref="CorpusSize"/> and put
    /// <see cref="InconclusiveGateRate"/> above 1.
    /// </para>
    /// </summary>
    public int InconclusiveGateCount =>
        Items.Count(i => i.Verdict?.GateDecisions.Any(g => g.Outcome == GateOutcome.Inconclusive) == true);

    /// <summary>
    /// The impaired-item count as a fraction of the corpus — the gate path's counterpart to
    /// <see cref="FaultRate"/>, and the signal that separates "the candidate cleared the gates" from
    /// "the gates were off".
    /// <para>
    /// <b>Null when the run recorded no gate decision at all</b>, and this is the whole point of the
    /// property's shape. An inconclusive gate does not block, so the item proceeds to the judges and
    /// scores normally: an environmental fault — the checkout is gone, a path template is wrong, a
    /// schema file did not deploy — takes every gate out on every item while pass rate, coverage and
    /// fault rate all stay exactly where they were. Reporting <c>0.0</c> for a run in which no gate
    /// ever ran would answer "did a gate go inconclusive" with "no", which a reader takes as "the
    /// gates checked this run and it was clean" — silently widening <i>unknown</i> into <i>fine</i>,
    /// which is the one reading this signal exists to prevent. Null says the signal is absent, the
    /// way <see cref="MeanScore"/> is null rather than zero when nothing was scored.
    /// </para>
    /// <para>
    /// Null is <b>not</b> a refusal: a harness with no gates configured is a real configuration, and
    /// refusing every one of its runs would make the bound unusable. It is a value the reader cannot
    /// mistake for a measurement. Over <see cref="CorpusSize"/> like every other rate here, so that
    /// a run cannot look intact by gating fewer items.
    /// </para>
    /// <para>
    /// <b>Why not refusing is safe</b>, which is the load-bearing half of the argument: the gate
    /// list and each gate's <c>AppliesTo</c> are inside <see cref="EvaluatorConfigHash"/>. A run
    /// that quietly lost its gates therefore hashes differently from the baseline that had them and
    /// is refused as <see cref="ComparisonRefusal.EvaluatorConfigDiffers"/> before this bound is
    /// ever reached. Null reaches the comparison only when the baseline was <i>also</i> gateless —
    /// two runs that agree there were no gates, which is a comparison of like with like and not a
    /// silent one.
    /// </para>
    /// </summary>
    public double? InconclusiveGateRate =>
        Items.All(i => i.Verdict is null || i.Verdict.GateDecisions.Count == 0)
            ? null
            : (double)InconclusiveGateCount / CorpusSize;

    /// <summary>
    /// The distinct gates that went inconclusive at least once, in the order first seen.
    /// <para>
    /// A refusal that says only "some gates were inconclusive" leaves nothing to act on: the
    /// environmental faults this catches are each identified by <i>which</i> gate stopped working,
    /// so the refusal names them. Gate ids only — a <see cref="GateDecision.Reason"/> is held to a
    /// stable, non-sensitive rail but an id is the part that identifies the broken thing.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> InconclusiveGateIds =>
        [
            .. Items
                .SelectMany(i => i.Verdict?.GateDecisions ?? [])
                .Where(g => g.Outcome == GateOutcome.Inconclusive)
                .Select(g => g.GateId)
                .Distinct(StringComparer.Ordinal),
        ];

    /// <summary>Items that faulted, so the run holds no verdict for them.</summary>
    public int FaultedCount => Items.Count(i => i.Exclusion == ScoreExclusion.Faulted);

    /// <summary>
    /// The faulted count as a fraction of the corpus — the signal that separates "the candidate got
    /// worse" from "the harness could not reach its judges".
    /// <para>
    /// A faulted item has a null verdict, so it does not raise <see cref="NoDecisionRate"/>, and it
    /// is not scored, so it leaves <see cref="PassRate"/>'s numerator while staying in its
    /// denominator. A bad hour at the judge provider therefore reads as a pass-rate collapse with a
    /// flat no-decision rate, which is precisely a candidate regression's signature.
    /// </para>
    /// </summary>
    public double FaultRate => (double)FaultedCount / CorpusSize;

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
