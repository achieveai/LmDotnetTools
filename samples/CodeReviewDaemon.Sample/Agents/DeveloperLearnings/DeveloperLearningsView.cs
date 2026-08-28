namespace CodeReviewDaemon.Sample.Agents.DeveloperLearnings;

/// <summary>Which way a pattern's rate moved between the two most recent windows of exposed PRs.</summary>
internal enum TrendDirection
{
    /// <summary>
    /// One or both windows hold too few exposed PRs to compare. Printed as such, never hidden and never
    /// rendered as a flat trend — "we cannot tell" and "it did not move" are different facts.
    /// </summary>
    InsufficientData,

    /// <summary>The recent window's rate is below the prior window's.</summary>
    Improving,

    /// <summary>The two windows produced the same rate.</summary>
    Unchanged,

    /// <summary>The recent window's rate is above the prior window's.</summary>
    Worsening,
}

/// <summary>
/// A pattern's movement across the two most recent windows of exposed PRs.
/// </summary>
/// <param name="Direction">Which way it moved, or that it cannot be judged.</param>
/// <param name="RecentRate">
/// Smoothed rate over the recent window, or null when there is not enough exposure to compute one. Null
/// rather than zero, because a zero here would render as "never happens" — the opposite of "unknown".
/// </param>
/// <param name="PriorRate">Smoothed rate over the window before it, or null for the same reason.</param>
/// <param name="RecentExposed">Exposed PRs in the recent window. The denominator, always rendered with the rate.</param>
/// <param name="PriorExposed">Exposed PRs in the prior window.</param>
internal sealed record PatternTrend(
    TrendDirection Direction,
    double? RecentRate,
    double? PriorRate,
    int RecentExposed,
    int PriorExposed);

/// <summary>One PR in which a pattern was seen, for the "Seen in" list under each pattern.</summary>
/// <param name="SourcePr">Fully qualified PR reference as recorded in the observation.</param>
/// <param name="ObservedAtUtc">When that observation was written.</param>
/// <param name="Severity">The severity the shipped review carried for this hit.</param>
/// <param name="Location">The <c>path:line</c> the finding cited.</param>
internal sealed record PatternSighting(
    string SourcePr,
    string ObservedAtUtc,
    string Severity,
    string Location);

/// <summary>
/// The model-authored body of a pattern file. The only model-originated text in any rendered view — every
/// number, date and state around it is computed by the daemon.
/// </summary>
/// <param name="Title">One-line pattern title.</param>
/// <param name="WhatItIs">Description of the mistake.</param>
/// <param name="WhyItMatters">Consequence.</param>
/// <param name="HowToAvoid">The single most useful line, and the only one the checklist carries.</param>
internal sealed record PatternProse(string Title, string WhatItIs, string WhyItMatters, string HowToAvoid)
{
    /// <summary>
    /// Stands in for a pattern whose file is absent or unreadable.
    /// <para>
    /// Rendered rather than skipped: the counts for that pattern are real and were computed from observation
    /// files, so dropping the pattern would understate the developer's record because of a missing prose
    /// file. Saying the prose is missing keeps the count and names the gap.
    /// </para>
    /// </summary>
    /// <param name="patternId">The pattern whose file could not be read; used as a stand-in title.</param>
    public static PatternProse Missing(string patternId) =>
        new(patternId, MissingText, MissingText, MissingText);

    /// <summary>The text substituted for every missing prose field, so a gap reads as a gap.</summary>
    public const string MissingText = "_Pattern file missing; prose is written once, at pattern creation._";
}

/// <summary>One pattern as the rendered views need it: its computed standing, its prose, and its history.</summary>
/// <param name="Standing">Everything the ledger computed. The renderers never recompute any of it.</param>
/// <param name="Prose">The model-authored body, or <see cref="PatternProse.Missing"/>.</param>
/// <param name="Trend">Movement across the two most recent windows.</param>
/// <param name="Sightings">Every PR where this pattern was hit, oldest first.</param>
internal sealed record PatternView(
    PatternStanding Standing,
    PatternProse Prose,
    PatternTrend Trend,
    IReadOnlyList<PatternSighting> Sightings);

/// <summary>
/// One window of exposed PRs for one dimension, for the progress view's trend table.
/// </summary>
/// <param name="Dimension">The specialist template.</param>
/// <param name="ExposedPrs">PRs in this window where that specialist ran and completed. The denominator.</param>
/// <param name="Findings">
/// Surviving hits in this dimension across those PRs. Counts every pattern, so it CAN exceed
/// <paramref name="ExposedPrs"/> — one PR can carry several distinct mistakes in one dimension.
/// </param>
/// <param name="FindingsPerExposedPr">
/// <paramref name="Findings"/> divided by <paramref name="ExposedPrs"/>. Deliberately NOT called a rate and
/// deliberately not smoothed: the per-pattern <c>Rate</c> is a per-PR probability bounded by 1, this is a
/// count per PR that is not. One name for two quantities is how a bounded number silently starts exceeding
/// its bound.
/// </param>
/// <param name="Partial">
/// True when this window holds fewer than a full window's PRs. Only the oldest displayed window can be
/// partial, and it is marked rather than dropped so the reader is not shown a dip that is really a short window.
/// </param>
internal sealed record DimensionWindow(
    string Dimension,
    int ExposedPrs,
    int Findings,
    double FindingsPerExposedPr,
    bool Partial);

/// <summary>One dimension's recent history, oldest window first.</summary>
/// <param name="Dimension">The specialist template.</param>
/// <param name="Windows">Up to <c>ProgressTrendWindows</c> windows, oldest first. Empty if never exposed.</param>
internal sealed record DimensionTrend(string Dimension, IReadOnlyList<DimensionWindow> Windows);

/// <summary>
/// Everything one developer's four rendered files draw on, computed once.
/// <para>
/// <b>Why a view model and not four renderers reading the ledger.</b> <c>profile.md</c> and
/// <c>checklist.md</c> must agree about which patterns are Active, and <c>_index.md</c> must agree with both
/// about the counts. Computing from a single projection makes that agreement structural. Two renderers
/// each doing their own counting would eventually disagree, and the disagreement would be silent — the same
/// property that made the single-invocation rule worth a test in the findings work.
/// </para>
/// <para>
/// <b>This layer counts; the renderers only format.</b> No renderer in this system computes a number,
/// filters by a threshold, or decides a state. That is why the renderers are safe to write before the state
/// precedence question is settled: they bucket by <see cref="PatternStanding.State"/> and never re-derive it.
/// </para>
/// </summary>
/// <param name="DeveloperSlug">The directory name this developer's files live under.</param>
/// <param name="GeneratedAtUtc">Render time, stamped into every file's banner.</param>
/// <param name="Observations">
/// Observation files read. One is written per closed PR, so this is also "PRs reviewed" — the same number,
/// not two agreeing measurements, and the views say so rather than printing it twice as corroboration.
/// </param>
/// <param name="FirstObservedUtc">Oldest observation, or null when there are none.</param>
/// <param name="LastObservedUtc">Newest observation, or null when there are none.</param>
/// <param name="Patterns">Every pattern with at least one hit, ordered by pattern id.</param>
/// <param name="DimensionTrends">Per-dimension recent history for the progress view.</param>
/// <param name="Thresholds">The thresholds these numbers were produced under, rendered in provenance.</param>
internal sealed record DeveloperLearningsView(
    string DeveloperSlug,
    string GeneratedAtUtc,
    int Observations,
    string? FirstObservedUtc,
    string? LastObservedUtc,
    IReadOnlyList<PatternView> Patterns,
    IReadOnlyList<DimensionTrend> DimensionTrends,
    LearningsThresholds Thresholds)
{
    /// <summary>
    /// Windows of exposed PRs kept in the progress view's per-dimension trend table.
    /// </summary>
    public const int ProgressTrendWindows = 6;

    /// <summary>Patterns that came back after a resolution. These ALSO appear in their state bucket below.</summary>
    public IReadOnlyList<PatternView> Regressions => [.. Patterns.Where(p => p.Standing.Regressed)];

    /// <summary>Patterns in <see cref="PatternState.Active"/>.</summary>
    public IReadOnlyList<PatternView> Active => InState(PatternState.Active);

    /// <summary>Patterns in <see cref="PatternState.Watch"/>.</summary>
    public IReadOnlyList<PatternView> Watch => InState(PatternState.Watch);

    /// <summary>Patterns in <see cref="PatternState.Resolved"/>.</summary>
    public IReadOnlyList<PatternView> Resolved => InState(PatternState.Resolved);

    /// <summary>Patterns in <see cref="PatternState.Unjudgeable"/>.</summary>
    public IReadOnlyList<PatternView> Unjudgeable => InState(PatternState.Unjudgeable);

    /// <summary>
    /// Resolutions reached while their dimension was cohort-suppressed, excluded from the headline resolved
    /// count until a later clean window confirms them.
    /// </summary>
    public IReadOnlyList<PatternView> ProvisionalResolutions =>
        [.. Resolved.Where(p => p.Standing.Provisional)];

    /// <summary>
    /// The headline resolved count: confirmed resolutions only. Provisional ones are rendered separately and
    /// counted separately, because a cohort-wide fall in a dimension makes every developer look improved on
    /// the same day.
    /// </summary>
    public int ConfirmedResolvedCount => Resolved.Count - ProvisionalResolutions.Count;

    private IReadOnlyList<PatternView> InState(PatternState state) =>
        [.. Patterns.Where(p => p.Standing.State == state)];

    /// <summary>
    /// Projects one developer's rendered views from their ledger. Pure: no clock beyond
    /// <paramref name="nowUtc"/>, no filesystem, no model.
    /// </summary>
    /// <param name="developerSlug">The developer's directory name.</param>
    /// <param name="observations">Every observation file for this developer, in any order.</param>
    /// <param name="standings">What the ledger computed. Taken as given; nothing here recomputes a state.</param>
    /// <param name="prose">Pattern id to model-authored body. Missing entries render as missing, not skipped.</param>
    /// <param name="thresholds">Thresholds in force.</param>
    /// <param name="nowUtc">Render time.</param>
    public static DeveloperLearningsView Build(
        string developerSlug,
        IReadOnlyList<DeveloperObservation> observations,
        IReadOnlyList<PatternStanding> standings,
        IReadOnlyDictionary<string, PatternProse> prose,
        LearningsThresholds thresholds,
        DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(developerSlug);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(standings);
        ArgumentNullException.ThrowIfNull(prose);
        ArgumentNullException.ThrowIfNull(thresholds);

        var ordered = DeveloperLearningsLedger.Chronological(observations);

        var patterns = new List<PatternView>();
        foreach (var standing in standings.OrderBy(s => s.PatternId, StringComparer.Ordinal))
        {
            patterns.Add(
                new PatternView(
                    standing,
                    prose.TryGetValue(standing.PatternId, out var body)
                        ? body
                        : PatternProse.Missing(standing.PatternId),
                    ComputeTrend(ordered, standing, thresholds),
                    Sightings(ordered, standing.PatternId)));
        }

        return new DeveloperLearningsView(
            developerSlug,
            Iso(nowUtc),
            ordered.Count,
            ordered.Count == 0 ? null : ordered[0].ObservedAtUtc,
            ordered.Count == 0 ? null : ordered[^1].ObservedAtUtc,
            patterns,
            BuildDimensionTrends(ordered, thresholds),
            thresholds);
    }

    /// <summary>
    /// Renders a timestamp the one way this system writes them, so a view's banner and an observation file
    /// cannot end up in two formats.
    /// </summary>
    /// <param name="value">The instant to render.</param>
    public static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Ranks Active patterns for the checklist by <c>rate / (1 + clean streak)</c> — the spec's
    /// "rate × recency", with recency made explicit as the reciprocal of the clean streak.
    /// <para>
    /// Stated as an expression rather than left as a phrase because a ranking nobody can recompute is a
    /// ranking nobody can check. A pattern hit on the most recent exposed PR keeps its full rate; one that
    /// has been quiet for nine exposed PRs is scored at a tenth of it.
    /// </para>
    /// </summary>
    /// <param name="pattern">The pattern to score.</param>
    public static double ChecklistScore(PatternView pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return pattern.Standing.Rate / (1.0 + pattern.Standing.CleanStreak);
    }

    private static IReadOnlyList<PatternSighting> Sightings(
        IReadOnlyList<DeveloperObservation> ordered,
        string patternId)
    {
        var sightings = new List<PatternSighting>();
        foreach (var observation in ordered)
        {
            foreach (var hit in observation.Hits)
            {
                if (string.Equals(hit.PatternId, patternId, StringComparison.Ordinal))
                {
                    sightings.Add(
                        new PatternSighting(
                            observation.SourcePr, observation.ObservedAtUtc, hit.Severity, hit.Location));
                }
            }
        }

        return sightings;
    }

    /// <summary>
    /// Rate over the last window of exposed PRs against the window before it.
    /// <para>
    /// Both the window size and the minimum exposure to report a trend are derived from
    /// <c>ActiveWindowPrs</c> rather than hardcoded at the spec's 10 and 5. At the default they are exactly
    /// 10 and 5; under a reconfigured window they stay in proportion instead of silently diverging from the
    /// window every other number in the file is measured against.
    /// </para>
    /// <para>
    /// The rate is the ledger's smoothed rate, not a second definition. Two things called "rate" in one file
    /// that were computed differently is the failure this avoids.
    /// </para>
    /// </summary>
    private static PatternTrend ComputeTrend(
        IReadOnlyList<DeveloperObservation> ordered,
        PatternStanding standing,
        LearningsThresholds thresholds)
    {
        var size = Math.Max(1, thresholds.ActiveWindowPrs);
        var minimum = Math.Max(1, size / 2);

        var exposed = ExposedIndices(ordered, standing.Dimension);
        var recent = exposed.Skip(Math.Max(0, exposed.Count - size)).ToArray();
        var priorEnd = Math.Max(0, exposed.Count - size);
        var prior = exposed.Take(priorEnd).Skip(Math.Max(0, priorEnd - size)).ToArray();

        if (recent.Length < minimum || prior.Length < minimum)
        {
            return new PatternTrend(TrendDirection.InsufficientData, null, null, recent.Length, prior.Length);
        }

        var recentRate = DeveloperLearningsLedger.SmoothedRate(
            recent.Count(i => HasHit(ordered[i], standing.PatternId)), recent.Length);
        var priorRate = DeveloperLearningsLedger.SmoothedRate(
            prior.Count(i => HasHit(ordered[i], standing.PatternId)), prior.Length);

        var direction = recentRate < priorRate
            ? TrendDirection.Improving
            : recentRate > priorRate
                ? TrendDirection.Worsening
                : TrendDirection.Unchanged;

        return new PatternTrend(direction, recentRate, priorRate, recent.Length, prior.Length);
    }

    /// <summary>
    /// Per dimension, the most recent <see cref="ProgressTrendWindows"/> windows of exposed PRs, oldest first.
    /// <para>
    /// Chunked backwards from the newest PR so the newest window is exactly the last <c>ActiveWindowPrs</c>
    /// exposed PRs. Chunking forwards would leave the newest window partial, which is the one the reader
    /// compares against and the one a short window would most mislead them about.
    /// </para>
    /// <para>
    /// Every dimension the developer was ever exposed to appears, including those that produced no findings
    /// at all. A dimension that ran sixty times and found nothing is a fact about the developer; omitting it
    /// would leave the table showing only where they were caught.
    /// </para>
    /// </summary>
    private static IReadOnlyList<DimensionTrend> BuildDimensionTrends(
        IReadOnlyList<DeveloperObservation> ordered,
        LearningsThresholds thresholds)
    {
        var size = Math.Max(1, thresholds.ActiveWindowPrs);
        var dimensions = ordered
            .SelectMany(o => o.Exposure)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(d => d, StringComparer.Ordinal);

        var trends = new List<DimensionTrend>();
        foreach (var dimension in dimensions)
        {
            var exposed = ExposedIndices(ordered, dimension);
            var windows = new List<DimensionWindow>();
            for (var end = exposed.Count; end > 0 && windows.Count < ProgressTrendWindows; end -= size)
            {
                var start = Math.Max(0, end - size);
                var slice = exposed.Skip(start).Take(end - start).ToArray();
                var findings = slice.Sum(
                    i => ordered[i].Hits.Count(h => string.Equals(h.Dimension, dimension, StringComparison.Ordinal)));
                windows.Add(
                    new DimensionWindow(
                        dimension,
                        slice.Length,
                        findings,
                        findings / (double)slice.Length,
                        slice.Length < size));
            }

            windows.Reverse();
            trends.Add(new DimensionTrend(dimension, windows));
        }

        return trends;
    }

    private static List<int> ExposedIndices(IReadOnlyList<DeveloperObservation> ordered, string dimension)
    {
        var exposed = new List<int>();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Exposure.Contains(dimension, StringComparer.Ordinal))
            {
                exposed.Add(i);
            }
        }

        return exposed;
    }

    private static bool HasHit(DeveloperObservation observation, string patternId) =>
        observation.Hits.Any(h => string.Equals(h.PatternId, patternId, StringComparison.Ordinal));
}
