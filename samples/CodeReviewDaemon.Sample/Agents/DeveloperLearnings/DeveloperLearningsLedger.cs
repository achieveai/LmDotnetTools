using System.Globalization;

namespace CodeReviewDaemon.Sample.Agents.DeveloperLearnings;

/// <summary>Where a pattern stands. <c>Regressed</c> is deliberately absent — see <see cref="PatternStanding"/>.</summary>
internal enum PatternState
{
    /// <summary>Hit within the last <c>ActiveWindowPrs</c> exposed PRs.</summary>
    Active,

    /// <summary>A clean streak has begun, but luck has not yet been ruled out.</summary>
    Watch,

    /// <summary>The clean streak is long enough that chance is an implausible explanation.</summary>
    Resolved,

    /// <summary>
    /// Too little exposure to say anything. NOT progress: a developer who stopped writing that kind of code
    /// has not improved, and folding this into Resolved is exactly how a progress view becomes fiction.
    /// </summary>
    Unjudgeable,
}

/// <summary>
/// One pattern's computed standing. Every field here is derived by the daemon from observation files; none
/// of it is ever read back from a model.
/// </summary>
/// <param name="PatternId">The pattern slug, matching its file under <c>patterns/</c>.</param>
/// <param name="Dimension">The specialist template that raises this pattern. Fixes the denominator.</param>
/// <param name="Occurrences">PRs with a hit for this pattern. Never reset — see <paramref name="Regressed"/>.</param>
/// <param name="ExposedInWindow">PRs between first and last hit inclusive where this dimension ran.</param>
/// <param name="Rate">Laplace-smoothed per-PR probability.</param>
/// <param name="CleanStreak">Clean EXPOSED PRs since the last hit. Not days, not all PRs.</param>
/// <param name="LuckProbability"><c>(1-Rate)^CleanStreak</c> — the chance a clean streak this long is luck.</param>
/// <param name="Regressed">
/// A FLAG, not a state. A pattern that came back is Active again and carries its whole history; making it a
/// state would need somewhere to put the history, and a relocated pattern reads as brand new on its return.
/// </param>
/// <param name="Provisional">Resolution reached during a cohort-wide drop for this dimension.</param>
/// <param name="State">Where the pattern stands. See <see cref="PatternState"/> for the precedence.</param>
/// <param name="FirstSeenUtc">Observation time of the first hit.</param>
/// <param name="LastSeenUtc">Observation time of the most recent hit.</param>
/// <param name="RegressedAtUtc">When a resolved pattern came back, or null if it never did.</param>
/// <param name="StreakBrokenAt">The clean streak, in exposed PRs, that the regression broke.</param>
internal sealed record PatternStanding(
    string PatternId,
    string Dimension,
    int Occurrences,
    int ExposedInWindow,
    double Rate,
    int CleanStreak,
    double LuckProbability,
    PatternState State,
    bool Regressed,
    bool Provisional,
    string FirstSeenUtc,
    string LastSeenUtc,
    string? RegressedAtUtc,
    int? StreakBrokenAt);

/// <summary>Thresholds in force for one rendering. Recorded alongside the numbers they produced.</summary>
/// <param name="ResolutionConfidence">Confidence required to call a pattern resolved.</param>
/// <param name="ActiveWindowPrs">Exposed-PR window within which a hit still counts as Active.</param>
/// <param name="ExposureStalenessDays">Calendar staleness that forces Unjudgeable regardless of streak.</param>
/// <param name="CohortDropThreshold">Population-wide dimension-rate fall that marks resolutions provisional.</param>
internal sealed record LearningsThresholds(
    double ResolutionConfidence,
    int ActiveWindowPrs,
    int ExposureStalenessDays,
    double CohortDropThreshold)
{
    public static LearningsThresholds Defaults { get; } = new(0.95, 10, 90, 0.40);
}

/// <summary>
/// Computes every count, rate, streak and state in the system from the immutable observation files.
/// <para>
/// <b>The model classifies; this counts.</b> Nothing in this type reads model output. The classifier's only
/// contribution upstream is a <c>patternId</c> per finding, validated against a known set before it ever
/// arrives here — so the worst a bad classification can do is attribute a real finding to the wrong
/// pattern, never invent a number.
/// </para>
/// <para>
/// <b>Why exposed PRs and not calendar time.</b> A pattern can only recur where its dimension ran. Measuring
/// a clean streak in days or in all-PRs means a developer's record improves when the reviewer gets narrower
/// or when they ship less — both of which are the opposite of what the number claims to show.
/// </para>
/// </summary>
internal static class DeveloperLearningsLedger
{
    /// <summary>
    /// Exposed PRs required before a clean streak is judged at all. Below this, <see cref="PatternState.Unjudgeable"/>
    /// regardless of arithmetic: three observations cannot distinguish a fixed habit from a quiet quarter.
    /// </summary>
    private const int MinimumExposedToJudge = 3;

    /// <summary>
    /// Laplace-smoothed per-PR rate: <c>(occurrences + 1) / (exposed + 2)</c>.
    /// <para>
    /// <b>Smoothing is required, not a refinement.</b> Unsmoothed, a single occurrence in a single exposed PR
    /// gives <c>p = 1.0</c>, so <c>(1-p)^n = 0</c> for any streak at all and the very next clean PR declares
    /// the pattern resolved. The smoothed value for that case is 2/3, which needs four clean exposed PRs —
    /// the honest answer for one data point.
    /// </para>
    /// </summary>
    public static double SmoothedRate(int occurrences, int exposedInWindow) =>
        (occurrences + 1.0) / (exposedInWindow + 2.0);

    /// <summary>
    /// The chance that a clean streak this long happened by luck at this rate. Falling below
    /// <c>1 - ResolutionConfidence</c> is what licenses calling a pattern resolved.
    /// <para>
    /// Derived per pattern rather than compared to a flat streak constant, because a rare pattern needs a
    /// far longer silence to mean anything: at p=0.50 five clean exposed PRs suffice, at p=0.10 it takes
    /// about twenty-nine. A single constant would either declare rare patterns dead on no evidence or never
    /// resolve common ones.
    /// </para>
    /// </summary>
    public static double LuckProbability(double rate, int cleanStreak) =>
        Math.Pow(1.0 - rate, cleanStreak);

    /// <summary>
    /// Computes standings for every pattern in <paramref name="observations"/>.
    /// </summary>
    /// <param name="observations">The developer's ledger. Order is irrelevant; this sorts by observation time.</param>
    /// <param name="patternDimensions">Pattern to dimension, from the pattern files' daemon-authored frontmatter.</param>
    /// <param name="thresholds">Thresholds in force.</param>
    /// <param name="suppressedDimensions">
    /// Dimensions whose population-wide finding rate has fallen far enough this window that an apparent
    /// improvement is not attributable to the developer. Resolutions inside these are marked provisional.
    /// </param>
    /// <param name="nowUtc">Clock, injected so staleness is testable without waiting ninety days.</param>
    public static IReadOnlyList<PatternStanding> Compute(
        IReadOnlyList<DeveloperObservation> observations,
        IReadOnlyDictionary<string, string> patternDimensions,
        LearningsThresholds thresholds,
        IReadOnlySet<string> suppressedDimensions,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(patternDimensions);
        ArgumentNullException.ThrowIfNull(thresholds);
        ArgumentNullException.ThrowIfNull(suppressedDimensions);

        var ordered = Chronological(observations);

        var standings = new List<PatternStanding>();
        foreach (var (patternId, dimension) in patternDimensions.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var standing = ComputeOne(ordered, patternId, dimension, thresholds, suppressedDimensions, nowUtc);
            if (standing is not null)
            {
                standings.Add(standing);
            }
        }

        return standings;
    }

    private static PatternStanding? ComputeOne(
        IReadOnlyList<DeveloperObservation> ordered,
        string patternId,
        string dimension,
        LearningsThresholds thresholds,
        IReadOnlySet<string> suppressedDimensions,
        DateTimeOffset nowUtc)
    {
        // Indices of PRs that exercised this pattern's dimension. Everything positional below counts within
        // THIS list, which is what makes every streak an exposed-PR streak by construction rather than by a
        // filter someone could later forget to apply.
        var exposedIndices = new List<int>();
        var hitIndices = new List<int>();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (!ordered[i].Exposure.Contains(dimension, StringComparer.Ordinal))
            {
                continue;
            }

            exposedIndices.Add(i);
            if (ordered[i].Hits.Any(h => string.Equals(h.PatternId, patternId, StringComparison.Ordinal)))
            {
                hitIndices.Add(i);
            }
        }

        if (hitIndices.Count == 0)
        {
            return null;
        }

        var firstHit = hitIndices[0];
        var lastHit = hitIndices[^1];

        // The window is first hit to last hit INCLUSIVE, counted in exposed PRs. Using all PRs here would
        // inflate the denominator with rounds where the dimension never ran, depressing the rate and making
        // resolution easier the less the reviewer looked.
        var exposedInWindow = exposedIndices.Count(i => i >= firstHit && i <= lastHit);
        var rate = SmoothedRate(hitIndices.Count, exposedInWindow);

        var cleanStreak = exposedIndices.Count(i => i > lastHit);
        var luck = LuckProbability(rate, cleanStreak);

        // Regression is read off the hit sequence, not stored: if the pattern was ever silent long enough to
        // have been resolved and then came back, that is a regression whatever any earlier render said. The
        // count deliberately continues through it — a reset would erase the very history that makes a
        // returning pattern the highest-signal event here.
        string? regressedAtUtc = null;
        int? streakBrokenAt = null;
        for (var h = 1; h < hitIndices.Count; h++)
        {
            var gap = exposedIndices.Count(i => i > hitIndices[h - 1] && i < hitIndices[h]);
            var priorWindow = exposedIndices.Count(i => i >= firstHit && i <= hitIndices[h - 1]);
            var priorRate = SmoothedRate(h, priorWindow);
            if (LuckProbability(priorRate, gap) < 1 - thresholds.ResolutionConfidence)
            {
                regressedAtUtc = ordered[hitIndices[h]].ObservedAtUtc;
                streakBrokenAt = gap;
            }
        }

        var state = ClassifyState(ordered, exposedIndices, cleanStreak, luck, thresholds, nowUtc);

        // Provisional applies ONLY to a resolution. A cohort-wide drop cannot make an Active pattern less
        // active, and flagging one would say the daemon doubts a finding it just recorded.
        var provisional = state == PatternState.Resolved && suppressedDimensions.Contains(dimension);

        return new PatternStanding(
            patternId,
            dimension,
            hitIndices.Count,
            exposedInWindow,
            rate,
            cleanStreak,
            luck,
            state,
            regressedAtUtc is not null,
            provisional,
            ordered[firstHit].ObservedAtUtc,
            ordered[lastHit].ObservedAtUtc,
            regressedAtUtc,
            streakBrokenAt);
    }

    /// <summary>
    /// Places a pattern in exactly one state.
    /// <para>
    /// <b>The spec's four rules overlap and it states no precedence, so this fixes one.</b> A pattern at
    /// rate 0.50 with a five-PR clean streak satisfies BOTH "hit within the last 10 exposed PRs" (Active)
    /// and "P(luck) &lt; 0.05" (Resolved); a pattern at rate 0.25 with the same streak satisfies both Active
    /// and Watch. The order below is chosen so that §8's own worked table is reachable — it says rate 0.50
    /// resolves at five clean exposed PRs, which is impossible if Active wins at anything under ten.
    /// </para>
    /// <para>
    /// The principle: <b>absence of evidence outranks everything, a completed judgement outranks recency,
    /// and recency outranks the residual.</b> Watch is the residual — a streak long enough to be past the
    /// active window but not yet long enough to rule out luck.
    /// </para>
    /// </summary>
    private static PatternState ClassifyState(
        IReadOnlyList<DeveloperObservation> ordered,
        IReadOnlyList<int> exposedIndices,
        int cleanStreak,
        double luck,
        LearningsThresholds thresholds,
        DateTimeOffset nowUtc)
    {
        // 1. Nobody has exercised this dimension in months. Silence from a dimension no reviewer read is not
        //    a clean streak, and treating it as one credits the developer for the reviewer's absence. This
        //    outranks every other rule, including a long arithmetic streak.
        var lastExposure = ParseObservedAt(ordered[exposedIndices[^1]].ObservedAtUtc);
        if ((nowUtc - lastExposure).TotalDays > thresholds.ExposureStalenessDays)
        {
            return PatternState.Unjudgeable;
        }

        // 2. Too few clean exposed PRs to judge a streak at all. Still Active while the last hit is inside
        //    the active window — a hit two PRs ago is live, not unknown — and Unjudgeable only if an
        //    operator has configured the window below the judging minimum.
        if (cleanStreak < MinimumExposedToJudge)
        {
            return cleanStreak < thresholds.ActiveWindowPrs ? PatternState.Active : PatternState.Unjudgeable;
        }

        // 3. A completed judgement. Before recency, or §8's worked table is unreachable.
        if (luck < 1 - thresholds.ResolutionConfidence)
        {
            return PatternState.Resolved;
        }

        // 4. Hit recently, not yet ruled out. 5. Otherwise the residual.
        return cleanStreak < thresholds.ActiveWindowPrs ? PatternState.Active : PatternState.Watch;
    }

    /// <summary>
    /// Dimensions whose population-wide finding rate in the current window has fallen more than
    /// <paramref name="threshold"/> below the trailing median of earlier windows.
    /// <para>
    /// <b>The trap this exists for.</b> Three things make a pattern stop appearing: the developer improved,
    /// they stopped writing that kind of code, or THE REVIEWER STOPPED CATCHING IT. The third is systemic
    /// and invisible from any single developer's file — change the review model and every developer appears
    /// to improve on the same day. This does not block resolution (that could stall forever); it marks it
    /// provisional and makes the reason renderable.
    /// </para>
    /// </summary>
    /// <param name="windowRatesByDimension">
    /// Per dimension, findings-per-exposed-PR for successive 10-PR windows across ALL developers, oldest
    /// first. The last entry is the current window.
    /// </param>
    /// <param name="threshold">Fractional fall below the trailing median that marks a dimension suppressed.</param>
    public static IReadOnlySet<string> SuppressedDimensions(
        IReadOnlyDictionary<string, IReadOnlyList<double>> windowRatesByDimension,
        double threshold)
    {
        ArgumentNullException.ThrowIfNull(windowRatesByDimension);

        var suppressed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (dimension, windows) in windowRatesByDimension)
        {
            // Two windows is the minimum that can show a fall at all; with one there is no trailing median
            // and "fell 40%" has nothing to be 40% of.
            if (windows.Count < 2)
            {
                continue;
            }

            var trailing = windows.Take(windows.Count - 1).OrderBy(v => v).ToArray();
            var median = trailing.Length % 2 == 1
                ? trailing[trailing.Length / 2]
                : (trailing[(trailing.Length / 2) - 1] + trailing[trailing.Length / 2]) / 2.0;
            if (median <= 0)
            {
                continue;
            }

            if ((median - windows[^1]) / median > threshold)
            {
                _ = suppressed.Add(dimension);
            }
        }

        return suppressed;
    }

    private static DateTimeOffset ParseObservedAt(string value) =>
        DateTimeOffset.TryParse(
            value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    /// <summary>
    /// The one chronological order in this system: observation time, then source PR to break ties.
    /// <para>
    /// Public because the view projection needs the same order and a second implementation of it would be a
    /// second chance to get it wrong. Everything positional in this system — "since the last hit", "the last
    /// N exposed PRs", "the prior N" — is computed against this sequence, so two orderings that disagreed
    /// would produce two different clean streaks for one ledger and neither would look wrong on its own.
    /// </para>
    /// <para>
    /// A ledger assembled from a directory listing arrives in whatever order the filesystem chose, which is
    /// why sorting happens here rather than being assumed of the caller.
    /// </para>
    /// </summary>
    /// <param name="observations">The developer's observation files, in any order.</param>
    public static IReadOnlyList<DeveloperObservation> Chronological(
        IReadOnlyList<DeveloperObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        return
        [
            .. observations
                .OrderBy(o => ParseObservedAt(o.ObservedAtUtc))
                .ThenBy(o => o.SourcePr, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Parses an observation timestamp, or <see cref="DateTimeOffset.MinValue"/> when it cannot be read.
    /// <para>
    /// Unparseable sorts oldest rather than throwing: an observation file is immutable, so a bad timestamp
    /// cannot be repaired in place, and refusing to render the whole developer because one historical file is
    /// malformed would lose every other fact about them.
    /// </para>
    /// </summary>
    /// <param name="value">An ISO-8601 round-trip timestamp as written into the observation file.</param>
    public static DateTimeOffset ParseTimestamp(string value) => ParseObservedAt(value);
}
