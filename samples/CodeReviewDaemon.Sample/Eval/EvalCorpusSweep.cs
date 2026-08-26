using AchieveAi.LmDotnetTools.LmEval;
using AchieveAi.LmDotnetTools.LmEval.Corpus;
using AchieveAi.LmDotnetTools.LmEval.Findings;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Eval;

/// <summary>
/// Reads one review run's recorded artifacts.
/// <para>
/// A delegate rather than the store itself, for the same reason <see cref="ModelFamilyResolver"/> is
/// one: it is the whole of what this sweep needs from persistence, and naming it makes the sweep's
/// read pattern something a test can observe. The count of calls is the claim — one per run, not one
/// per candidate — and there is no way to state it against a concrete store.
/// </para>
/// </summary>
/// <param name="reviewRunId">The run whose artifacts to read.</param>
internal delegate IReadOnlyList<ReviewArtifact> ReviewArtifactReader(long reviewRunId);

/// <summary>
/// What one sweep measured over one window of recorded reviews.
/// <para>
/// Every count is over <see cref="CandidateCount"/>, and the four grading counts partition it
/// exactly — that is asserted, because a partition whose arms silently overlap is how an "unknown"
/// bucket gets quietly absorbed into a "fine" one.
/// </para>
/// </summary>
internal sealed record EvalSweepReport
{
    /// <summary>The corpus swept.</summary>
    public required string CorpusId { get; init; }

    /// <summary>The exclusive lower edge this sweep started from.</summary>
    public required long FromCursor { get; init; }

    /// <summary>The edge it reached — the value persisted for the next sweep.</summary>
    public required long ToCursor { get; init; }

    /// <summary>The limit cut this window short, so more history is waiting.</summary>
    public required bool Truncated { get; init; }

    /// <summary>Candidates the window yielded. Zero is a normal sweep, not a failure.</summary>
    public required int CandidateCount { get; init; }

    // ---- the finding-level signal (ReviewFindingParser) ----------------------------------------

    /// <summary>Findings cited across every candidate, duplicates included.</summary>
    public required int FindingCount { get; init; }

    /// <summary>
    /// Of those, how many cite a line number that parsed. A finding that names a file and no
    /// resolvable line is a distinct defect from one that names nothing, so it is counted, not
    /// dropped.
    /// </summary>
    public required int AnchoredFindingCount { get; init; }

    /// <summary>Candidates whose review cited no file at all.</summary>
    public required int CandidatesCitingNothing { get; init; }

    // ---- the recorded grade, segmented by artifact schema version ------------------------------

    /// <summary>Candidates whose latest <c>judge</c> row is v2 and carries a score.</summary>
    public required int ScoredCandidates { get; init; }

    /// <summary>
    /// Mean of those scores, or <b>null</b> when none was scored — never <c>0.0</c>, which on this
    /// rubric is a legitimate worst grade and would read as "the reviews were terrible" rather than
    /// "nothing here was graded".
    /// </summary>
    public required double? MeanRecordedScore { get; init; }

    /// <summary>
    /// Candidates whose latest <c>judge</c> row is v2 and carries an explicit null score: the
    /// harness ran and would not put a number on the reply. A measurement, not an absence.
    /// </summary>
    public required int UnscoredCandidates { get; init; }

    /// <summary>
    /// Candidates whose latest <c>judge</c> row predates the nullable score. Its <c>0</c> is
    /// permanently ambiguous between "worst grade" and "could not be scored", and it carries no
    /// judge or generator provenance. Counted on its own and averaged into nothing.
    /// </summary>
    public required int AmbiguousLegacyGradeCandidates { get; init; }

    /// <summary>Candidates with no <c>judge</c> row at all — never judged, as opposed to judged
    /// inconclusively.</summary>
    public required int UngradedCandidates { get; init; }
}

/// <summary>
/// The production consumer of the eval corpus: one pass over the reviews recorded since the last
/// pass, summarised into <see cref="EvalSweepReport"/> and followed by advancing the persisted
/// window.
/// <para>
/// <b>Why this exists at all.</b> The corpus reader and the findings parser were both complete,
/// tested, and constructed only by their own tests — which reads as shipped capability and is not
/// one (#400). More sharply: the reader's correctness argument depended on its caller stating an
/// honest window, and until a real caller existed there was nothing to state one.
/// </para>
/// <para>
/// <b>What it deliberately does not do.</b> It does not re-judge the corpus through
/// <see cref="AchieveAi.LmDotnetTools.LmEval.Running.EvalRunner"/>. Re-judging costs a model call
/// per candidate and needs a live judge panel, a frozen baseline to compare against and somewhere to
/// keep it; none of that exists yet, and inventing it here would put the expensive half of the loop
/// in front of the cheap half that can already be trusted. What this sweep measures is what the
/// store already knows: the citation surface of each review, and the grade the daemon's own judge
/// recorded for it. Both are computable without a model call, and the second is the one that has to
/// be read carefully.
/// </para>
/// </summary>
internal sealed class EvalCorpusSweep
{
    /// <summary>
    /// The corpus this sweep reads, and the <c>scope</c> its cursor is stored under. One id, because
    /// the window and the corpus it windows must not be able to drift apart.
    /// </summary>
    public const string CorpusId = "daemon-reviews";

    /// <summary>
    /// The last <c>judge</c> artifact schema version whose <c>Score</c> is ambiguous. Written as an
    /// explicit ceiling rather than <c>&lt; JudgeAgent.JudgeArtifactSchemaVersion</c>: that
    /// comparison silently reclassifies today's rows as legacy the moment a v3 lands, which is the
    /// bug this branch exists to prevent, one version later.
    /// </summary>
    private const int LastAmbiguousJudgeSchemaVersion = 1;

    private readonly ReviewArtifactReader _readArtifacts;
    private readonly ICorpusReader _reader;
    private readonly EvalCorpusWatermark _watermark;
    private readonly int _limit;
    private readonly ILogger<EvalCorpusSweep>? _logger;

    /// <summary>Builds the sweep.</summary>
    /// <param name="readArtifacts">
    /// Reads a run's recorded artifacts; in production <c>ReviewStore.GetArtifacts</c>.
    /// </param>
    /// <param name="reader">The corpus reader; in production <see cref="DaemonCorpusReader"/>.</param>
    /// <param name="watermark">Where the window edge is kept between sweeps.</param>
    /// <param name="limit">Most review runs one sweep considers.</param>
    /// <param name="logger">Optional diagnostics.</param>
    public EvalCorpusSweep(
        ReviewArtifactReader readArtifacts,
        ICorpusReader reader,
        EvalCorpusWatermark watermark,
        int limit = 1000,
        ILogger<EvalCorpusSweep>? logger = null
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        _readArtifacts = readArtifacts ?? throw new ArgumentNullException(nameof(readArtifacts));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _watermark = watermark ?? throw new ArgumentNullException(nameof(watermark));
        _limit = limit;
        _logger = logger;
    }

    /// <summary>
    /// This sweep's memo of <see cref="ReviewArtifactReader"/>, holding the <b>last</b> run read and
    /// nothing else.
    /// <para>
    /// One run yields two candidates over the same input — the primary arm and the collect-only B
    /// arm — and the grade lookup ran once per candidate against a read that returns the run's whole
    /// artifact list. At a window of a thousand runs that was thousands of reads to find judge rows
    /// already in hand.
    /// </para>
    /// <para>
    /// <b>One entry, not a map.</b> <see cref="DaemonCorpusReader"/> adds both of a run's candidate
    /// arms inside ONE iteration of its own loop, so a run's candidates are contiguous in the
    /// snapshot and a single entry gives the identical read count — with memory that does not grow
    /// with the window. A map keyed by run id would retain one artifact list per run in the window
    /// for the length of the sweep, duplicating what <see cref="CorpusSnapshot"/> already holds; the
    /// window is an operator knob whose comment describes a work bound, so a memory bound hiding
    /// behind it is a bound nobody set. Contiguity is a property of another class, so it is asserted
    /// from the outside rather than assumed: a snapshot that interleaves two runs re-reads, and a
    /// test says so.
    /// </para>
    /// <para>
    /// Scoped to the sweep and not to the instance: an artifact list held across sweeps would hide a
    /// re-judge recorded between them, and the sweep object outlives every one of its passes.
    /// </para>
    /// </summary>
    private sealed class ArtifactMemo(ReviewArtifactReader read)
    {
        private long _reviewRunId;
        private IReadOnlyList<ReviewArtifact>? _artifacts;

        public IReadOnlyList<ReviewArtifact> For(long reviewRunId)
        {
            if (_artifacts is null || _reviewRunId != reviewRunId)
            {
                _artifacts = read(reviewRunId);
                _reviewRunId = reviewRunId;
            }

            return _artifacts;
        }
    }

    /// <summary>Runs one sweep and advances the window.</summary>
    public async Task<EvalSweepReport> SweepOnceAsync(CancellationToken cancellationToken)
    {
        var from = _watermark.Read(CorpusId);
        var page = await _reader.LoadAsync(CorpusId, from, _limit, cancellationToken);
        var report = Summarise(from, page);

        // Advanced AFTER the window has been summarised, and never backwards. A sweep that throws
        // between the load and here leaves the cursor where it was, so the next one re-reads the
        // window rather than stepping over it: re-reading a review is idempotent, and skipping one
        // removes it from every corpus for ever with nothing to show that it happened.
        if (page.NextCursor > from)
        {
            _watermark.Save(CorpusId, page.NextCursor);
        }

        _logger?.LogInformation(
            "Eval sweep of '{CorpusId}' covered ({FromCursor}, {ToCursor}] and summarised "
                + "{CandidateCount} candidates: {FindingCount} findings ({AnchoredFindingCount} "
                + "anchored), {ScoredCandidates} scored (mean {MeanRecordedScore}), "
                + "{UnscoredCandidates} unscored, {AmbiguousLegacyGradeCandidates} on an ambiguous "
                + "legacy grade, {UngradedCandidates} never graded. Truncated: {Truncated}.",
            report.CorpusId,
            report.FromCursor,
            report.ToCursor,
            report.CandidateCount,
            report.FindingCount,
            report.AnchoredFindingCount,
            report.ScoredCandidates,
            report.MeanRecordedScore,
            report.UnscoredCandidates,
            report.AmbiguousLegacyGradeCandidates,
            report.UngradedCandidates,
            report.Truncated
        );

        return report;
    }

    private EvalSweepReport Summarise(long from, CorpusPage page)
    {
        var candidates = page.Snapshot?.Items ?? [];
        var artifacts = new ArtifactMemo(_readArtifacts);

        var findingCount = 0;
        var anchoredFindingCount = 0;
        var citingNothing = 0;
        var scores = new List<int>();
        var unscored = 0;
        var ambiguousLegacy = 0;
        var ungraded = 0;

        foreach (var candidate in candidates)
        {
            var findings = ReviewFindingParser.Parse(candidate.Content);
            findingCount += findings.Count;
            anchoredFindingCount += findings.Count(f => f.Line is not null);

            if (findings.Count == 0)
            {
                citingNothing++;
            }

            switch (RecordedGrade(candidate, artifacts))
            {
                case { } grade when grade.Ambiguous:
                    ambiguousLegacy++;
                    break;
                case { Score: { } score }:
                    scores.Add(score);
                    break;
                case not null:
                    unscored++;
                    break;
                default:
                    ungraded++;
                    break;
            }
        }

        return new EvalSweepReport
        {
            CorpusId = CorpusId,
            FromCursor = from,
            ToCursor = page.NextCursor,
            Truncated = page.Truncated,
            CandidateCount = candidates.Count,
            FindingCount = findingCount,
            AnchoredFindingCount = anchoredFindingCount,
            CandidatesCitingNothing = citingNothing,
            ScoredCandidates = scores.Count,

            // Null, not zero, when nothing was scored. Zero is a real grade on this rubric.
            MeanRecordedScore = scores.Count == 0 ? null : scores.Average(),
            UnscoredCandidates = unscored,
            AmbiguousLegacyGradeCandidates = ambiguousLegacy,
            UngradedCandidates = ungraded,
        };
    }

    /// <summary>
    /// The grade the daemon's own judge recorded for this candidate, or null when it never judged
    /// it.
    /// <para>
    /// <b>The version branch is the whole point of this method.</b> A v1 <c>judge</c> row wrote a
    /// non-nullable score in which <c>0</c> means either "the worst grade this rubric defines" or
    /// "the harness would not put a number on the reply", and it carries no judge or generator
    /// provenance at all. Deserialising it into today's nullable-score shape yields <c>0</c>, not
    /// null — so a reader that skipped the version would average a value that is not a measurement,
    /// and would do it silently, dragging the mean toward zero in exact proportion to how much
    /// history the corpus covers. Marked here and excluded from the mean, never averaged.
    /// </para>
    /// </summary>
    private RecordedJudgeGrade? RecordedGrade(Candidate candidate, ArtifactMemo artifacts)
    {
        if (
            !candidate.Metadata.TryGetValue(
                DaemonCorpusReader.ReviewRunIdMetadataKey,
                out var reviewRunIdText
            )
            || !long.TryParse(
                reviewRunIdText,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var reviewRunId
            )
        )
        {
            return null;
        }

        // Latest wins, per variant. One run holds a judge row per arm it graded, and grading the A
        // arm says nothing about the B arm's score — matching them by run alone would hand every
        // candidate of a run whichever arm happened to be judged last.
        foreach (
            var artifact in artifacts
                .For(reviewRunId)
                .Where(a =>
                    string.Equals(
                        a.ArtifactKind,
                        JudgeAgent.JudgeArtifactKind,
                        StringComparison.Ordinal
                    )
                )
                .OrderByDescending(a => a.Id)
        )
        {
            var payload = EvalArtifactJson.TryRead<JudgeArtifactPayload>(
                artifact.Payload,
                out var failure
            );

            if (payload is null)
            {
                // STOPS here rather than stepping over the row. The field that would rule this row
                // out as some other arm's is the field that could not be read, so skipping past it
                // is not "this row was not ours" — it is promoting a grade the daemon has already
                // superseded, silently, with a stale number reported as current. This is the same
                // rule DaemonCorpusReader.Latest<T> applies on the same table: the newest row of a
                // kind is the answer, and if it cannot be read there is no answer.
                //
                // Ungraded is the honest report. The three-way split below exists precisely so a
                // missing grade is not read as a bad one, and one unreadable row still costs this
                // candidate its grade rather than the sweep its window.
                _logger?.LogWarning(
                    failure,
                    "Judge artifact {ArtifactId} for review run {ReviewRunId} did not deserialize; "
                        + "its candidate counts as ungraded rather than inheriting the grade this "
                        + "row superseded.",
                    artifact.Id,
                    reviewRunId
                );
                return null;
            }

            if (
                !string.Equals(payload.VariantId, candidate.VariantId, StringComparison.Ordinal)
            )
            {
                continue;
            }

            return artifact.ArtifactSchemaVersion <= LastAmbiguousJudgeSchemaVersion
                ? new RecordedJudgeGrade(null, Ambiguous: true)
                : new RecordedJudgeGrade(payload.Score, Ambiguous: false);
        }

        return null;
    }

    /// <summary>
    /// A recorded grade. <paramref name="Ambiguous"/> is a third state beside a score and a null
    /// score, not a flavour of either: the row exists, it holds a number, and the number cannot be
    /// read. Folding it into "unscored" would claim the harness declined to grade, and folding it
    /// into a score would put a non-measurement in the mean.
    /// </summary>
    private readonly record struct RecordedJudgeGrade(int? Score, bool Ambiguous);
}
