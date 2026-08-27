namespace AchieveAi.LmDotnetTools.LmEval.Running;

/// <summary>
/// A frozen tuple of (corpus snapshot, rubric version, variant config, evaluator config), recorded
/// once and referenced thereafter, never recomputed silently.
/// <para>
/// The fourth element is the one the first three leave out. Corpus, rubric and variant hold the
/// <i>candidate</i> side fixed; nothing held the <i>evaluator</i> side fixed, and it moves on its
/// own — refitting reliability weights changes scores, and against a frozen baseline that reads as
/// a candidate regression.
/// </para>
/// </summary>
public sealed record EvalBaseline
{
    /// <summary>The task type this baseline partitions. Scores never cross task types.</summary>
    public required string TaskType { get; init; }

    /// <summary>Stable identity of this baseline.</summary>
    public required string BaselineId { get; init; }

    /// <summary>The rubric it was measured under.</summary>
    public required string RubricId { get; init; }

    /// <summary>Its exact version. A comparison across versions is refused, not warned about.</summary>
    public required string RubricVersion { get; init; }

    /// <summary>
    /// The corpus item count — the denominator every rate below is over. Always positive: a zero
    /// denominator makes the comparer's baseline-coverage division NaN, and NaN compares false
    /// against every threshold, so the refusal that exists to catch a thin comparison would wave it
    /// through instead.
    /// </summary>
    public required int CorpusSize
    {
        get => _corpusSize;
        init
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    "A baseline over no items has an empty denominator, which makes every rate it "
                        + "froze undefined rather than zero."
                );
            }

            _corpusSize = value;
        }
    }

    private readonly int _corpusSize = 1;
    private readonly string _corpusSnapshotHash = string.Empty;
    private readonly string _evaluatorConfigHash = string.Empty;

    /// <summary>
    /// How many of <see cref="CorpusSize"/> yielded a counted score, so the baseline's own coverage
    /// is frozen beside the conditional metrics it belongs to. Reporting
    /// <see cref="MeanScore"/> or <see cref="P10Score"/> without the coverage they were computed
    /// over is forbidden, and a <i>frozen</i> conditional metric is not an exception to that rule —
    /// it is the case that most needs it, since the run it came from is long gone. Distinct from
    /// <see cref="MinCoverage"/>, which is a floor imposed on the candidate, not a fact about this
    /// baseline.
    /// </summary>
    public required int ScoredItems { get; init; }

    /// <summary>Conditional mean over scored items only. Never read without <see cref="ScoredItems"/>.</summary>
    public required double MeanScore { get; init; }

    /// <summary>Conditional 10th percentile over scored items only — the tail a mean hides.</summary>
    public required double P10Score { get; init; }

    /// <summary>Passing items over <see cref="CorpusSize"/>, not over the scored subset.</summary>
    public required double PassRate { get; init; }

    /// <summary>Host-supplied mean cost per item, in USD micro-units.</summary>
    public required long MeanCostMicros { get; init; }

    /// <summary>
    /// Items the panel could not decide, over <see cref="CorpusSize"/>.
    /// <para>
    /// <b>Spec deviation, deliberate and recorded.</b> The §5.2 record does not carry this field,
    /// but §5.4's third regression trigger is "the NoDecision rate <i>rises</i> materially" — and a
    /// rise has no meaning without a value to rise from. Every other field a trigger consumes is
    /// frozen here; this one was left out, so the trigger was unimplementable as written. It is
    /// added rather than silently reinterpreted as an absolute threshold, because an absolute
    /// threshold is a different rule that would fire on a corpus that was always hard.
    /// </para>
    /// </summary>
    public required double NoDecisionRate { get; init; }

    /// <summary>Identity of the corpus snapshot. A comparison across two values is refused.</summary>
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
    /// Identity of every score-affecting <i>evaluator</i> input. A comparison across two different
    /// values is refused, not warned about.
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

    /// <summary>
    /// Least coverage a candidate run may have and still be compared. It lives on the baseline so
    /// the run being judged cannot relax the bar it is judged against.
    /// </summary>
    public required double MinCoverage
    {
        get => _minCoverage;
        init => _minCoverage = Fraction(value, nameof(MinCoverage), "A coverage floor");
    }

    /// <summary>
    /// Most of the corpus a candidate run may lose to faults and still be compared, in [0,1]. It
    /// lives on the baseline for the same reason <see cref="MinCoverage"/> does: the run being
    /// judged must not be able to relax the bar it is judged against.
    /// <para>
    /// Distinct from the coverage floor, and not subsumed by it. The floor catches only the severe
    /// case — a floor of 0.9 lets a 10% fault rate through untouched, and 10% of a corpus flipping
    /// from pass to not-counted is a large pass-rate delta. The floor also cannot say <i>why</i>
    /// the run is thin, and an infrastructure outage read as a candidate regression is the exact
    /// misreading this whole refusal machinery exists to prevent.
    /// </para>
    /// </summary>
    public double MaxFaultRate
    {
        get => _maxFaultRate;
        init => _maxFaultRate = Fraction(value, nameof(MaxFaultRate), "A fault-rate bound");
    }

    /// <summary>
    /// Most of the corpus a candidate run may have impaired gates on and still be compared, in
    /// [0,1]. It lives on the baseline for the reason <see cref="MaxFaultRate"/> does: the run being
    /// judged must not be able to relax the bar it is judged against.
    /// <para>
    /// The gate path's counterpart to <see cref="MaxFaultRate"/>, and #380's argument for that bound
    /// applies here unchanged — an outage must not read as a candidate regression. It is <b>not</b>
    /// subsumed by <see cref="MinCoverage"/>, and less so than the fault bound is: a faulted item at
    /// least leaves coverage, where an inconclusive gate does not block, so the item scores normally
    /// and coverage never moves at all. The floor cannot see this failure at any severity.
    /// </para>
    /// </summary>
    public double MaxInconclusiveGateRate
    {
        get => _maxInconclusiveGateRate;
        init =>
            _maxInconclusiveGateRate = Fraction(
                value,
                nameof(MaxInconclusiveGateRate),
                "An inconclusive-gate bound"
            );
    }

    private readonly double _minCoverage;
    private readonly double _maxFaultRate = DefaultMaxFaultRate;
    private readonly double _maxInconclusiveGateRate = DefaultMaxInconclusiveGateRate;

    /// <summary>
    /// A bound in [0,1], refused at the accessor rather than only in <see cref="From"/>. A record
    /// built by a factory is still rewritable through a <c>with</c> expression, which walks straight
    /// past the factory's checks, and NaN is the reachable value that does the most damage: every
    /// comparison against it is false, so <c>run.FaultRate &gt; NaN</c> never fires and the refusal
    /// is permanently disarmed. A disarmed check emits nothing, so the loss shows up only as
    /// outages read as candidate regressions.
    /// </summary>
    private static double Fraction(double value, string name, string what)
    {
        if (double.IsNaN(value) || value < 0.0 || value > 1.0)
        {
            throw new ArgumentOutOfRangeException(
                name,
                value,
                $"{what} is a fraction of the corpus and must be in [0,1]."
            );
        }

        return value;
    }

    /// <summary>
    /// The default fault-rate bound. Low, because a fault is an item the harness never measured at
    /// all: an occasional transport failure is normal and refusing on it would make the comparison
    /// unusable, but a rate in the tens of percent is an outage.
    /// </summary>
    public const double DefaultMaxFaultRate = 0.05;

    /// <summary>
    /// The default inconclusive-gate bound. Low, and for the same reason
    /// <see cref="DefaultMaxFaultRate"/> is: one flaky gate on one item is normal and refusing on it
    /// would make the comparison unusable, while a rate in the tens of percent means the gates are
    /// not running — and every item then scores a clean pass with no aggregate moving at all.
    /// </summary>
    public const double DefaultMaxInconclusiveGateRate = 0.05;

    /// <summary>
    /// Freezes a completed run as the baseline for its task type. This is the only supported way to
    /// mint one from measurement: every metric is copied from the run that produced it, so a
    /// baseline can never claim a coverage or a hash belonging to some other run.
    /// </summary>
    /// <param name="baselineId">Stable identity for the new baseline.</param>
    /// <param name="run">The run to freeze.</param>
    /// <param name="minCoverage">
    /// The coverage floor to impose on future candidate runs, in [0,1]. Also applied to
    /// <paramref name="run"/> itself: a run too thin to be compared against a baseline is too thin to
    /// be frozen as one.
    /// </param>
    /// <param name="maxFaultRate">
    /// The fault-rate bound to impose on future candidate runs, in [0,1]. Defaults to
    /// <see cref="DefaultMaxFaultRate"/>. Also applied to <paramref name="run"/> itself.
    /// </param>
    /// <param name="maxInconclusiveGateRate">
    /// The inconclusive-gate bound to impose on future candidate runs, in [0,1]. Defaults to
    /// <see cref="DefaultMaxInconclusiveGateRate"/>. Validated <b>here</b>, before anything reads it,
    /// because this method uses it as a predicate against <paramref name="run"/> and not only as
    /// a value to store: leaving it to <see cref="MaxInconclusiveGateRate"/>'s accessor would let a
    /// negative bound refuse a perfectly clean source run and report it as a gate outage, naming the
    /// run for what is wrong with the argument. The accessor check stays — it is the one a
    /// <c>with</c> expression cannot walk past — and this one runs first.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">A bound is outside [0,1].</exception>
    /// <exception cref="ArgumentException">
    /// The run breaches one of the three bounds this baseline would impose on a candidate — fault
    /// rate, inconclusive-gate rate, coverage — or it scored nothing and so has no conditional
    /// metrics. Checked in that order, mirroring <see cref="BaselineComparer"/>, so freezing a run
    /// and comparing it name the same cause.
    /// </exception>
    public static EvalBaseline From(
        string baselineId,
        EvalRun run,
        double minCoverage,
        double maxFaultRate = DefaultMaxFaultRate,
        double maxInconclusiveGateRate = DefaultMaxInconclusiveGateRate
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baselineId);
        ArgumentNullException.ThrowIfNull(run);

        if (double.IsNaN(minCoverage) || minCoverage < 0.0 || minCoverage > 1.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minCoverage),
                minCoverage,
                "A coverage floor is a fraction of the corpus and must be in [0,1]."
            );
        }

        if (double.IsNaN(maxFaultRate) || maxFaultRate < 0.0 || maxFaultRate > 1.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxFaultRate),
                maxFaultRate,
                "A fault-rate bound is a fraction of the corpus and must be in [0,1]."
            );
        }

        // Validated before it is READ, not merely before it is stored. Every other bound here is
        // only carried, so leaving it to MaxInconclusiveGateRate's accessor was enough; this one is
        // used as a predicate against the run three lines down, and a bound outside [0,1] would
        // otherwise decide that comparison first — a negative bound refusing a perfectly clean
        // source run and reporting it as a gate outage, naming the run for what is wrong with the
        // argument. The accessor check stays: it is the one a `with` expression cannot walk past.
        maxInconclusiveGateRate = Fraction(
            maxInconclusiveGateRate,
            nameof(maxInconclusiveGateRate),
            "An inconclusive-gate bound"
        );

        // The source run is held to EVERY bound this baseline will hold its candidates to, in the
        // order BaselineComparer.Refuse applies them: fault rate, then inconclusive-gate rate, then
        // the coverage floor, then the scored-nothing arm. Held here rather than left to the
        // comparison, because the comparison never sees this side — a poisoned baseline is strictly
        // worse than a poisoned candidate, since the candidate distorts one comparison and is
        // refused while the baseline distorts every comparison after it and is refused by nothing.
        //
        // Each bound is read from the SAME parameter that is stored below, not re-stated as a
        // literal, so the bound a run is frozen under and the bound it will impose cannot drift.

        // Ahead of the gate bound for the reason the comparer gives: a faulted item holds no verdict
        // at all where a gate-impaired item still produced one, so when both break the judge outage
        // is the strictly larger loss and the cause worth naming.
        //
        // Ahead of the coverage floor for a sharper version of the same reason, and that pair is the
        // one most likely to arise: a faulted item yields no score, so a run whose judges faulted is
        // thin BY CONSTRUCTION and breaches the floor as a side effect of the outage. The floor would
        // report it as thin without saying that a judge outage is why — infrastructure failure read
        // as a property of the run, which is the misreading this machinery exists to prevent.
        // FaultRate is a plain double, never
        // null — a run with no faults has a rate of 0.0, which is a measurement and not an absence,
        // because FaultedCount counts rows this run definitely holds no verdict for.
        if (run.FaultRate > maxFaultRate)
        {
            throw new ArgumentException(
                $"Run '{run.RunId}' has a fault rate of {run.FaultRate:F4}, above the "
                    + $"{maxFaultRate:F4} bound this baseline would impose; {run.FaultedCount} of "
                    + $"{run.CorpusSize} items hold no verdict at all, so the harness could not "
                    + "reach its judges and this run's pass rate must not be frozen as the number "
                    + "every later run is compared against.",
                nameof(run)
            );
        }

        // An inconclusive gate does not block, so an outage run scores every item: it
        // walks past the "scored nothing" check below with a full pass rate, a full coverage and a
        // zero fault rate, and freezes a pass rate measured with the gates off as the number every
        // later run is judged against. A poisoned baseline is strictly worse than a poisoned
        // candidate — the candidate distorts one comparison and is refused, the baseline distorts
        // every comparison after it and is refused by nothing.
        //
        // Ahead of the coverage floor and of the scored-nothing check, mirroring
        // BaselineComparer.Refuse. Ahead of the floor because the floor cannot see this failure at
        // ANY severity — an inconclusive gate does not block, so every impaired item still scores
        // and coverage never moves for that reason at all. Freezing a run and comparing it then name
        // the same cause, and a reader is never told "this run scored nothing" about a run whose
        // gates were the reason.
        //
        // A null rate is the run that recorded no gate decision at all, and it is deliberately NOT
        // refused, exactly as at comparison: a harness with no gates configured is a real
        // configuration, and refusing every baseline it could mint would make the bound unusable
        // rather than safe. The pattern match is what keeps that case from silently comparing false
        // the way a NaN would.
        if (
            run.InconclusiveGateRate is { } inconclusiveRate
            && inconclusiveRate > maxInconclusiveGateRate
        )
        {
            throw new ArgumentException(
                $"Run '{run.RunId}' has an inconclusive-gate rate of {inconclusiveRate:F4}, above "
                    + $"the {maxInconclusiveGateRate:F4} bound this baseline would impose; "
                    + $"{run.InconclusiveGateCount} of {run.CorpusSize} items had a gate that could "
                    + $"not run ({string.Join(", ", run.InconclusiveGateIds)}). The deterministic "
                    + "layer was off, so this run's pass rate is a pass rate measured with the "
                    + "gates off and must not be frozen as the number every later run is compared "
                    + "against.",
                nameof(run)
            );
        }

        // Ahead of the scored-nothing arm, where the comparer also puts it: the two share one
        // ComparisonRefusal value there, and a run that scored nothing has a coverage of zero, so it
        // breaches every positive floor as well. The floor is the fact that generalises, and a
        // caller who set no floor at all still reaches the arm below.
        if (run.Coverage < minCoverage)
        {
            throw new ArgumentException(
                $"Run '{run.RunId}' has a coverage of {run.Coverage:F4}, below the {minCoverage:F4} "
                    + $"floor this baseline would impose; only {run.ScoredItems} of "
                    + $"{run.CorpusSize} items yielded a counted score. The run is too thin to "
                    + "compare against, and so too thin to be frozen as the thing every later run "
                    + "is compared against.",
                nameof(run)
            );
        }

        if (run.MeanScore is not { } mean || run.P10Score is not { } p10)
        {
            throw new ArgumentException(
                $"Run '{run.RunId}' scored none of its {run.CorpusSize} corpus items, so it has no "
                    + "conditional mean or P10 to freeze. A baseline with no scored items would "
                    + "compare every future run against nothing.",
                nameof(run)
            );
        }

        return new EvalBaseline
        {
            BaselineId = baselineId,
            TaskType = run.TaskType,
            RubricId = run.RubricId,
            RubricVersion = run.RubricVersion,
            CorpusSize = run.CorpusSize,
            ScoredItems = run.ScoredItems,
            MeanScore = mean,
            P10Score = p10,
            PassRate = run.PassRate,
            MeanCostMicros = run.MeanCostMicros,
            NoDecisionRate = run.NoDecisionRate,
            CorpusSnapshotHash = run.CorpusSnapshotHash,
            EvaluatorConfigHash = run.EvaluatorConfigHash,
            MinCoverage = minCoverage,
            MaxFaultRate = maxFaultRate,
            MaxInconclusiveGateRate = maxInconclusiveGateRate,
        };
    }
}
