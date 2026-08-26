using System.Text.Json;
using AchieveAi.LmDotnetTools.LmEval;
using AchieveAi.LmDotnetTools.LmEval.Corpus;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Eval;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging;

namespace CodeReviewDaemon.Sample.Tests.Eval;

/// <summary>
/// The consumer #400 is about. Two claims carry the weight, and each is the one that fails silently
/// if it is wrong:
/// <list type="bullet">
/// <item>the window <b>advances</b>, and the value that advances it outlives the process — a window
/// nobody advanced re-reads the same oldest rows for ever, and the snapshot hash staying stable
/// makes every comparability refusal downstream agree that nothing changed;</item>
/// <item>a <b>v1</b> <c>judge</c> row is never averaged — its <c>0</c> is ambiguous between the
/// worst grade this rubric defines and "the harness would not put a number on it", and averaging it
/// drags the mean toward zero in proportion to how much history the corpus covers.</item>
/// </list>
/// Driven over a real <see cref="ReviewStore"/> on a temp SQLite file, because the cursor's survival
/// across a process is a claim about that file and not about a field.
/// </summary>
public sealed class EvalCorpusSweepTests : IDisposable
{
    private readonly TempSqliteDatabase _db = new();
    private ReviewStore _store;

    public EvalCorpusSweepTests() => _store = new ReviewStore(_db.ConnectionString);

    public void Dispose()
    {
        _store.Dispose();
        _db.Dispose();
    }

    /// <summary>
    /// Closes this store and opens a new one over the same file — the closest a test gets to a
    /// daemon restart, and the only way to tell a persisted cursor from a remembered one.
    /// </summary>
    private void Restart()
    {
        _store.Dispose();
        _store = new ReviewStore(_db.ConnectionString);
    }

    private long CreateRun(string prId, string? modelId = "openai/gpt-5")
    {
        var repoId = _store.EnsureRepo(
            new RepoIdentity
            {
                Provider = "github",
                OrgOrOwner = "achieveai",
                RepoName = "LmDotnetTools",
                RepoStableId = "repo-stable-1",
            }
        );

        return _store
            .CreateOrGetReviewRun(
                new ReviewRun
                {
                    RepoId = repoId,
                    PrId = prId,
                    HeadSha = $"head-{prId}",
                    BaseSha = $"base-{prId}",
                    TriggerWatermark = $"wm-{prId}",
                    ReviewKind = "full",
                    VariantId = "primary",
                    Mode = "collect-only",
                    ModelId = modelId,
                    Stage = ReviewStage.Reviewed,
                    WorkflowStatus = WorkflowStatus.Completed,
                    PrLifecycleState = PrLifecycleState.Merged,
                }
            )
            .Id;
    }

    private void AddArtifact(long runId, int schemaVersion, string kind, object payload) =>
        _ = _store.AddArtifact(
            new ReviewArtifact
            {
                ReviewRunId = runId,
                ArtifactSchemaVersion = schemaVersion,
                ArtifactKind = kind,
                Provider = "github",
                Payload = JsonSerializer.Serialize(payload),
            }
        );

    /// <summary>A reviewed run with an input, a review, and no judge row.</summary>
    private long Reviewed(string prId, string reviewText)
    {
        var runId = CreateRun(prId);
        AddArtifact(
            runId,
            1,
            DaemonReviewStageExecutor.ContextArtifactKind,
            new ContextArtifactPayload(prId, "base", "head", $"diff for {prId}")
        );
        AddArtifact(
            runId,
            1,
            DaemonReviewStageExecutor.ReviewArtifactKind,
            new ReviewArtifactPayload(reviewText, "run-1", "primary")
        );
        return runId;
    }

    /// <summary>A <c>judge</c> row as schema v2 wrote it: a nullable score with provenance.</summary>
    private void AddV2Judge(long runId, int? score, string variantId = "primary") =>
        AddArtifact(
            runId,
            JudgeAgent.JudgeArtifactSchemaVersion,
            JudgeAgent.JudgeArtifactKind,
            new JudgeArtifactPayload(
                score,
                "because",
                variantId,
                "openai/gpt-5",
                "anthropic/claude",
                SelfGraded: false,
                BallotCount: score is null ? 0 : 1
            )
        );

    /// <summary>
    /// A <c>judge</c> row as schema v1 wrote it. The payload shape is today's, because that is what
    /// a reader deserialising an old row actually gets: the stored JSON carries a plain <c>0</c>,
    /// which lands in the nullable field as <c>0</c> and not as null. Only the version column tells
    /// the two apart, which is the entire point.
    /// </summary>
    private void AddV1Judge(long runId, int score, string variantId = "primary") =>
        AddArtifact(
            runId,
            1,
            JudgeAgent.JudgeArtifactKind,
            new JudgeArtifactPayload(
                score,
                "because",
                variantId,
                JudgeModelId: null,
                GeneratorModelId: null,
                SelfGraded: null,
                BallotCount: 0
            )
        );

    /// <summary>Writes an artifact whose payload is arbitrary text rather than a serialized shape.</summary>
    private void AddRawArtifact(long runId, int schemaVersion, string kind, string payload) =>
        _ = _store.AddArtifact(
            new ReviewArtifact
            {
                ReviewRunId = runId,
                ArtifactSchemaVersion = schemaVersion,
                ArtifactKind = kind,
                Provider = "github",
                Payload = payload,
            }
        );

    private EvalCorpusSweep Sweep(
        int limit = 1000,
        ReviewArtifactReader? readArtifacts = null,
        ILogger<EvalCorpusSweep>? logger = null
    ) =>
        new(
            // The production binding by default, so every case below runs over the rows the daemon
            // actually hands the sweep — judge rows only (#453) — rather than over a listing no
            // deployment uses.
            readArtifacts ?? EvalCorpusSweep.GradeArtifactReader(_store),
            new DaemonCorpusReader(_store, modelId => modelId?.Split('/')[0]),
            new EvalCorpusWatermark(_store),
            limit,
            logger
        );

    private Task<EvalSweepReport> SweepAsync(int limit = 1000) =>
        Sweep(limit).SweepOnceAsync(CancellationToken.None);

    // ---- the window advances, and survives the process ------------------------------------------

    /// <summary>
    /// Acceptance: "A test asserts the second run of the consumer covers runs the first did not."
    /// This is the whole of #400's correctness argument — the reader can state an honest window, and
    /// nothing made the caller advance it.
    /// </summary>
    [Fact]
    public async Task The_second_sweep_covers_runs_the_first_did_not()
    {
        var first = Reviewed("118", "[Blocker] src/Foo.cs:12 leaks a handle.");

        var firstReport = await SweepAsync();

        firstReport.CandidateCount.Should().Be(1);
        firstReport.FromCursor.Should().Be(0, "nothing was recorded before this sweep");
        firstReport.ToCursor.Should().Be(first);

        var second = Reviewed("119", "[Nit] src/Bar.cs:3 could be clearer.");

        var secondReport = await SweepAsync();

        secondReport
            .FromCursor.Should()
            .Be(first, "the first sweep's edge is where the second starts");
        secondReport.ToCursor.Should().Be(second);
        secondReport
            .CandidateCount.Should()
            .Be(1, "the first sweep's rows are behind the window, not swept twice");
    }

    /// <summary>
    /// Acceptance: "the value that advances the window survives a process restart." A cursor held in
    /// memory passes the test above and fails in production on the first redeploy, so the store is
    /// closed and reopened over the same file between the two sweeps.
    /// </summary>
    [Fact]
    public async Task The_window_survives_a_restart()
    {
        var first = Reviewed("118", "src/Foo.cs:12 is wrong.");

        (await SweepAsync()).ToCursor.Should().Be(first);

        Restart();

        var second = Reviewed("119", "src/Bar.cs:3 is wrong.");
        var report = await SweepAsync();

        report
            .FromCursor.Should()
            .Be(first, "the cursor was read back from the database, not from a field");
        report.CandidateCount.Should().Be(1);
        report.ToCursor.Should().Be(second);
    }

    /// <summary>
    /// A sweep that finds nothing is the normal case, and it must leave the window where it was: a
    /// cursor that reset would make the next sweep re-read the whole history, every time nothing
    /// happened to land.
    /// </summary>
    [Fact]
    public async Task A_sweep_with_nothing_new_holds_the_window_and_reports_no_corpus()
    {
        var runId = Reviewed("118", "src/Foo.cs:12 is wrong.");
        _ = await SweepAsync();

        var report = await SweepAsync();

        report.CandidateCount.Should().Be(0);
        report.FromCursor.Should().Be(runId);
        report.ToCursor.Should().Be(runId, "an empty sweep must not rewind the window");
        report.MeanRecordedScore.Should().BeNull("nothing was scored, and zero is a real grade");
    }

    /// <summary>
    /// Truncation reaches the caller. A limit that binds means the window stopped short of the
    /// recorded history, and the next sweep has to come back for the rest rather than wait out an
    /// interval — which a log warning cannot make it do.
    /// </summary>
    [Fact]
    public async Task A_truncated_window_is_reported_and_the_next_sweep_resumes_from_it()
    {
        var ids = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add(Reviewed($"pr-{i}", $"src/File{i}.cs:1 is wrong."));
        }

        var first = await SweepAsync(limit: 2);

        first.Truncated.Should().BeTrue();
        first.CandidateCount.Should().Be(2);
        first.ToCursor.Should().Be(ids[1]);

        var second = await SweepAsync(limit: 2);

        second.Truncated.Should().BeFalse("only one run is left");
        second.FromCursor.Should().Be(ids[1]);
        second.CandidateCount.Should().Be(1);
        second.ToCursor.Should().Be(ids[2]);
    }

    // ---- the v1 / v2 judge branch ----------------------------------------------------------------

    /// <summary>
    /// The branch #400 §3 asks for. A v1 row's <c>0</c> is ambiguous between the worst grade this
    /// rubric defines and "the harness would not put a number on the reply", and it deserialises
    /// into today's nullable field as <c>0</c> — a reader that skipped the version column would see
    /// a number and average it.
    /// <para>
    /// The distinguishing input is a v1 <c>0</c> beside a v2 <c>8</c>. Averaging both gives 4; the
    /// mean must be 8, over one candidate, with the legacy row counted on its own — and counted as
    /// its own third thing, not folded into "unscored", which would claim the harness declined to
    /// grade something it graded.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_v1_judge_row_is_marked_rather_than_averaged()
    {
        AddV1Judge(Reviewed("118", "src/Foo.cs:1 is wrong."), score: 0);
        AddV2Judge(Reviewed("119", "src/Bar.cs:1 is wrong."), score: 8);

        var report = await SweepAsync();

        report.CandidateCount.Should().Be(2);
        report.ScoredCandidates.Should().Be(1, "only the v2 row carries a readable score");
        report
            .MeanRecordedScore.Should()
            .Be(8.0, "averaging the legacy zero in would give 4 — the exact silent drag");
        report.AmbiguousLegacyGradeCandidates.Should().Be(1);
        report
            .UnscoredCandidates.Should()
            .Be(0, "a legacy row is unreadable, not a declined grade");
        report.UngradedCandidates.Should().Be(0, "both candidates were judged");
    }

    /// <summary>
    /// The three ways a candidate can lack a usable score are three different facts, and the report
    /// keeps them apart. An explicit v2 null is a <i>measurement</i> — the harness ran and declined;
    /// a v1 row is unreadable; no row at all is unjudged. Collapsing any pair of them loses the one
    /// distinction a reader would act on.
    /// </summary>
    [Fact]
    public async Task An_unscored_row_a_legacy_row_and_no_row_are_three_different_facts()
    {
        AddV2Judge(Reviewed("118", "src/A.cs:1 is wrong."), score: null);
        AddV1Judge(Reviewed("119", "src/B.cs:1 is wrong."), score: 0);
        _ = Reviewed("120", "src/C.cs:1 is wrong.");
        AddV2Judge(Reviewed("121", "src/D.cs:1 is wrong."), score: 6);

        var report = await SweepAsync();

        report.CandidateCount.Should().Be(4);
        report.UnscoredCandidates.Should().Be(1);
        report.AmbiguousLegacyGradeCandidates.Should().Be(1);
        report.UngradedCandidates.Should().Be(1);
        report.ScoredCandidates.Should().Be(1);
        report.MeanRecordedScore.Should().Be(6.0);

        (
            report.UnscoredCandidates
            + report.AmbiguousLegacyGradeCandidates
            + report.UngradedCandidates
            + report.ScoredCandidates
        )
            .Should()
            .Be(report.CandidateCount, "the four arms partition the corpus, with no row in two");
    }

    /// <summary>
    /// The latest judge row wins, and the version that decides how to read it is that row's own — a
    /// run re-judged under v2 is no longer ambiguous, and one whose newest row is still v1 is.
    /// </summary>
    [Fact]
    public async Task A_run_rejudged_under_v2_is_read_as_v2()
    {
        var runId = Reviewed("118", "src/Foo.cs:1 is wrong.");
        AddV1Judge(runId, score: 0);
        AddV2Judge(runId, score: 9);

        var report = await SweepAsync();

        report.ScoredCandidates.Should().Be(1);
        report.MeanRecordedScore.Should().Be(9.0);
        report.AmbiguousLegacyGradeCandidates.Should().Be(0);
    }

    /// <summary>
    /// A version this reader has never heard of is read <b>forward</b>, not as legacy. The ambiguity
    /// belongs to v1 and to nothing else — the payload contract is append-compatible, so a later
    /// version still carries a nullable score — and a branch phrased as "anything but the version I
    /// know" would quietly reclassify every row the day the schema next moves, which is this branch's
    /// own defect one version later.
    /// </summary>
    [Fact]
    public async Task A_judge_row_from_a_later_schema_version_is_read_as_scored()
    {
        var runId = Reviewed("118", "src/Foo.cs:1 is wrong.");
        AddArtifact(
            runId,
            JudgeAgent.JudgeArtifactSchemaVersion + 1,
            JudgeAgent.JudgeArtifactKind,
            new JudgeArtifactPayload(
                7,
                "because",
                "primary",
                "openai/gpt-5",
                "anthropic/claude",
                SelfGraded: false,
                BallotCount: 1
            )
        );

        var report = await SweepAsync();

        report.ScoredCandidates.Should().Be(1);
        report.MeanRecordedScore.Should().Be(7.0);
        report
            .AmbiguousLegacyGradeCandidates.Should()
            .Be(0, "only v1 is ambiguous; a later version is not unknown-in-the-same-way");
    }

    /// <summary>
    /// Grades are matched per variant. One run holds a judge row per arm it graded, and grading the
    /// A arm says nothing about the B arm — matching by run alone would hand the B candidate the A
    /// arm's score, which is a wrong number rather than a missing one.
    /// </summary>
    [Fact]
    public async Task A_grade_is_matched_to_the_arm_it_graded()
    {
        var runId = Reviewed("118", "the A review of src/Foo.cs:1");
        AddArtifact(
            runId,
            1,
            VariantReviewer.VariantReviewArtifactKind,
            new VariantReviewArtifactPayload("b", "anthropic/claude", "the B review", "run-2")
        );

        AddV2Judge(runId, score: 9, variantId: "primary");

        var report = await SweepAsync();

        report.CandidateCount.Should().Be(2, "both arms are candidates over the same input");
        report.ScoredCandidates.Should().Be(1, "only the A arm was graded");
        report.MeanRecordedScore.Should().Be(9.0);
        report
            .UngradedCandidates.Should()
            .Be(1, "the B arm has no grade — it must not inherit the A arm's");
    }

    /// <summary>
    /// An unreadable <b>newest</b> judge row must not hand the candidate the row it superseded.
    /// <para>
    /// The lookup walks this run's judge rows newest-first and stops at the first one whose variant
    /// matches. Skipping past a row it could not deserialise sounds harmless and is not: the row's
    /// variant is exactly the field that could not be read, so it cannot be ruled out as this
    /// candidate's — and stepping over it silently promotes a grade the daemon has already replaced.
    /// The sweep then reports a score for a review that was re-judged, with nothing anywhere saying
    /// the number is stale.
    /// </para>
    /// <para>
    /// The rule is the one <see cref="DaemonCorpusReader"/> already applies on the same table: the
    /// newest row of a kind is the answer, and if it cannot be read there is no answer. Ungraded is
    /// the honest report — the sweep separates "never judged" from "judged inconclusively" precisely
    /// so that a missing grade is not read as a bad one.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_unreadable_newest_judge_row_does_not_promote_the_grade_it_superseded()
    {
        var runId = Reviewed("118", "src/Foo.cs:1 is wrong.");

        // The older grade the fall-through would have resurrected.
        AddV2Judge(runId, score: 8);

        // Re-judged, and the newer row is corrupt.
        AddRawArtifact(
            runId,
            JudgeAgent.JudgeArtifactSchemaVersion,
            JudgeAgent.JudgeArtifactKind,
            "{ this is not a judge payload"
        );

        var logger = new CapturingLogger<EvalCorpusSweep>();
        var report = await Sweep(logger: logger).SweepOnceAsync(CancellationToken.None);

        report.CandidateCount.Should().Be(1);
        report
            .ScoredCandidates.Should()
            .Be(0, "the superseded 8 must not be reported as this candidate's grade");
        report.MeanRecordedScore.Should().BeNull("zero is a real grade; null is the absence");
        report
            .UngradedCandidates.Should()
            .Be(1, "no readable grade is a missing grade, not a bad one");
        logger.WarningCount("did not deserialize").Should().Be(1);
    }

    /// <summary>
    /// The non-vacuity half: with no corrupt row in the way, the newest readable grade IS reported.
    /// Without this, "always ungraded" satisfies the case above.
    /// </summary>
    [Fact]
    public async Task The_newest_readable_judge_row_is_the_grade()
    {
        var runId = Reviewed("118", "src/Foo.cs:1 is wrong.");

        AddV2Judge(runId, score: 3);
        AddV2Judge(runId, score: 9);

        var report = await SweepAsync();

        report.ScoredCandidates.Should().Be(1);
        report.MeanRecordedScore.Should().Be(9.0, "the re-judged score supersedes the first one");
    }

    // ---- one artifact read per run, not per candidate ---------------------------------------------

    /// <summary>
    /// The grade lookup used to re-read a run's <b>entire</b> artifact list once per candidate, and
    /// a run yields two candidates. That list includes the <c>review-context</c> row, which carries
    /// the whole diff — so at a window of a thousand runs the sweep materialised thousands of full
    /// diffs out of SQLite to find a judge row it had already read.
    /// <para>
    /// Counted rather than timed, because a timing assertion on this would be a flake and a read
    /// count is the thing that actually changed. Two candidates over one run, one read.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Two_candidates_over_one_run_read_that_runs_artifacts_once()
    {
        var runId = Reviewed("118", "the A review of src/Foo.cs:1");
        AddArtifact(
            runId,
            1,
            VariantReviewer.VariantReviewArtifactKind,
            new VariantReviewArtifactPayload("b", "anthropic/claude", "the B review", "run-2")
        );
        AddV2Judge(runId, score: 9, variantId: "primary");
        AddV2Judge(runId, score: 4, variantId: "b");

        var reads = new List<long>();
        var report = await Sweep(
                readArtifacts: id =>
                {
                    reads.Add(id);
                    return _store.GetArtifacts(id);
                }
            )
            .SweepOnceAsync(CancellationToken.None);

        report.CandidateCount.Should().Be(2, "both arms are candidates over the same input");
        reads
            .Should()
            .Equal([runId], "one read for the run, not one per candidate over it");

        // The non-vacuity half: caching the read must not also collapse the per-variant match into
        // whichever arm was judged last. Both arms keep their own grade.
        report.ScoredCandidates.Should().Be(2);
        report.MeanRecordedScore.Should().Be(6.5, "(9 + 4) / 2 — each arm on its own row");
    }

    /// <summary>
    /// The sweep never sees a <c>review-context</c> payload (#453).
    /// <para>
    /// This defect is invisible in the report — the numbers are byte-identical whether the reader
    /// filtered in SQL or in memory — so the only assertable fact is what the reader handed over.
    /// The run below records every kind the daemon writes, including a diff, and the production
    /// reader returns judge rows and nothing else. Reintroducing an unfiltered listing here fails
    /// this and nothing else in the suite.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_grade_lookup_is_never_handed_a_review_context_diff()
    {
        var runId = Reviewed("118", "src/Foo.cs:1 is wrong.");
        AddArtifact(
            runId,
            1,
            VariantReviewer.VariantReviewArtifactKind,
            new VariantReviewArtifactPayload("b", "anthropic/claude", "the B review", "run-2")
        );
        AddV2Judge(runId, score: 7);

        var production = EvalCorpusSweep.GradeArtifactReader(_store);
        var seen = new List<ReviewArtifact>();

        var report = await Sweep(
                readArtifacts: id =>
                {
                    var artifacts = production(id);
                    seen.AddRange(artifacts);
                    return artifacts;
                }
            )
            .SweepOnceAsync(CancellationToken.None);

        seen.Should().NotBeEmpty("a vacuous pass here would prove nothing about the filter");
        seen.Select(a => a.ArtifactKind)
            .Should()
            .AllBe(
                JudgeAgent.JudgeArtifactKind,
                "the sweep grades judge rows; the diff it would discard stays in SQLite"
            );

        // Non-vacuity on the other side: the run really does hold the payloads that were filtered
        // out, so an unfiltered read would have carried them.
        _store
            .GetArtifacts(runId)
            .Select(a => a.ArtifactKind)
            .Should()
            .Contain(DaemonReviewStageExecutor.ContextArtifactKind);

        report.ScoredCandidates.Should().Be(1, "filtering must not cost the sweep its grade");
    }

    /// <summary>
    /// The memo remembers exactly ONE run, and this is the test that says so out loud (#455).
    /// <para>
    /// A map keyed by run id gives the same read count as a single-entry memo only because
    /// <see cref="DaemonCorpusReader"/> adds both of a run's candidate arms inside one iteration of
    /// its own loop, so a run's candidates are contiguous in the snapshot. That contiguity is what
    /// makes the memo free, and it is a property of a different class — so it is asserted here from
    /// the outside: hand the sweep a snapshot in which a run's candidates are <b>not</b> contiguous,
    /// and the memo re-reads. A map would not, and would grow with the window instead: at the default
    /// window of a thousand runs that is a thousand retained artifact lists, each carrying a
    /// <c>review-context</c> diff.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_artifact_memo_holds_one_run_not_the_whole_window()
    {
        var reads = new List<long>();

        var sweep = new EvalCorpusSweep(
            runId =>
            {
                reads.Add(runId);
                return [];
            },
            new InterleavedCorpusReader([(1L, "primary"), (2L, "primary"), (1L, "b")]),
            new EvalCorpusWatermark(_store)
        );

        var report = await sweep.SweepOnceAsync(CancellationToken.None);

        report.CandidateCount.Should().Be(3);
        reads
            .Should()
            .Equal(
                [1L, 2L, 1L],
                "the memo holds the LAST run only, so a run revisited after another is read again"
            );
    }

    /// <summary>
    /// The memo is scoped to one sweep and not to the sweep object (#455 item 2).
    /// <para>
    /// The doc has always said the scoping matters — "a cached artifact list held across sweeps would
    /// hide a re-judge recorded between them" — and nothing made it true: hoisting the memo to an
    /// instance field went green on the whole suite. This is that sentence as an input. The window is
    /// rewound between the two sweeps so the same run is genuinely inside both, which is the only
    /// arrangement in which a per-instance memo is reachable at all.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_re_judge_recorded_between_two_sweeps_is_read_by_the_second()
    {
        var runId = Reviewed("118", "src/Foo.cs:1 is wrong.");
        AddV2Judge(runId, score: 8);

        var sweep = Sweep();

        (await sweep.SweepOnceAsync(CancellationToken.None))
            .MeanRecordedScore.Should()
            .Be(8.0);

        // Re-judged after the first sweep read this run's artifacts.
        AddV2Judge(runId, score: 3);

        // Rewind the window so the same run is inside the second sweep too. Written through the
        // watermark rather than by reaching into the sweep, because the window living in the store
        // is exactly what makes this reachable in production: a redeploy from a restored database
        // resumes behind rows a long-lived sweep object has already read.
        new EvalCorpusWatermark(_store).Save(EvalCorpusSweep.CorpusId, 0);

        var second = await sweep.SweepOnceAsync(CancellationToken.None);

        second
            .MeanRecordedScore.Should()
            .Be(3.0, "the re-judge supersedes the grade the first sweep read");
    }

    /// <summary>
    /// A snapshot whose candidates are deliberately NOT grouped by run — the arrangement the memo's
    /// single entry is measured against. It reads no store: the run ids are metadata, and the grade
    /// lookup is what the test is watching.
    /// </summary>
    private sealed class InterleavedCorpusReader(IReadOnlyList<(long RunId, string VariantId)> items)
        : ICorpusReader
    {
        public Task<CorpusPage> LoadAsync(
            string corpusId,
            long afterCursor,
            int limit,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new CorpusPage
                {
                    Snapshot = CorpusSnapshot.Create(
                        corpusId,
                        [
                            .. items.Select(item => new Candidate
                            {
                                CandidateId = $"{item.RunId}:{item.VariantId}",
                                TaskType = DaemonCorpusReader.CodeReviewTaskType,
                                TaskInput = "a diff",
                                Content = "a review",
                                VariantId = item.VariantId,
                                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                                {
                                    [DaemonCorpusReader.ReviewRunIdMetadataKey] =
                                        item.RunId.ToString(
                                            System.Globalization.CultureInfo.InvariantCulture
                                        ),
                                },
                            }),
                        ]
                    ),
                    NextCursor = afterCursor,
                    Truncated = false,
                }
            );
    }

    // ---- the finding-level signal ----------------------------------------------------------------

    /// <summary>
    /// Acceptance: "<c>ReviewFindingParser</c> is either called from production or removed." It is
    /// called from here, and what it produces is the citation surface of each review — the signal
    /// the spec's S2 needs and the one thing about review quality the store can answer with no model
    /// call.
    /// <para>
    /// A finding that names a file and no resolvable line is counted as a finding and not as an
    /// anchored one: "cited but not resolvable" is a distinct defect from "cited nothing", and
    /// collapsing them would make a review that cites badly indistinguishable from one that cites
    /// well.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_citation_surface_of_each_review_is_measured()
    {
        _ = Reviewed(
            "118",
            "[Blocker] src/Foo.cs:12 leaks a handle.\n[Nit] tests/Bar.cs:7 reads oddly."
        );
        _ = Reviewed("119", "Looks good to me.");

        // The citation that separates the two counts. The anchor pattern accepts it — it is a
        // citation, and counted as one — but the line number overflows an int, so it resolves to
        // nothing. Without a row like this every finding in the window is anchored, the two totals
        // are equal by accident, and reading either one for the other passes every assertion.
        _ = Reviewed("120", "[Major] src/Hallucinated.cs:99999999999999999999 is wrong.");

        var report = await SweepAsync();

        report.CandidateCount.Should().Be(3);
        report.FindingCount.Should().Be(3);
        report
            .AnchoredFindingCount.Should()
            .Be(
                2,
                "the overflowing line number is cited but not resolvable, which is a different "
                    + "review defect from citing nothing and must not be counted as an anchor"
            );
        report
            .AnchoredFindingCount.Should()
            .BeLessThan(
                report.FindingCount,
                "the two totals must be able to disagree, or neither measures anything"
            );
        report
            .CandidatesCitingNothing.Should()
            .Be(1, "a review citing no file is the finding-level signal's own worst case");
    }

    // ---- the cursor payload says what it knows ---------------------------------------------------

    /// <summary>Writes a raw payload into this reader's cursor row, behind the watermark's back.</summary>
    private void WriteRawCursor(string payload) =>
        _store.SaveCursor(
            new OpaqueCursor
            {
                Provider = EvalCorpusWatermark.CursorProvider,
                Scope = EvalCorpusSweep.CorpusId,
                CursorVersion = EvalCorpusWatermark.CursorVersion,
                CursorPayload = payload,
                HighWaterMark = null,
            }
        );

    /// <summary>
    /// An <b>absent</b> id is not a recorded zero. A positional record with a non-nullable
    /// <c>long</c> binds <c>{}</c> to <c>default(long)</c> without throwing, so a payload that lost
    /// its only field deserialises cleanly to cursor 0 — and the sweep restarts over the whole
    /// history looking exactly like a cursor that never advanced, which is the one outcome the
    /// warning exists to prevent. Zero is still returned, because a corrupt cursor must not wedge
    /// the daemon; the difference the test pins is that it is now said out loud.
    /// </summary>
    [Theory]
    [InlineData("{}", "an absent field")]
    [InlineData("{\"AfterReviewRunId\":null}", "an explicit null")]
    [InlineData("not json at all", "unparseable text")]
    public void A_cursor_payload_that_carries_no_id_is_unreadable_rather_than_a_restart(
        string payload,
        string because
    )
    {
        WriteRawCursor(payload);

        var logger = new CapturingLogger<EvalCorpusWatermark>();
        var read = new EvalCorpusWatermark(_store, logger).Read(EvalCorpusSweep.CorpusId);

        read.Should().Be(0, "a cursor that cannot be read restarts from the beginning");
        logger
            .WarningCount("unreadable payload")
            .Should()
            .Be(1, $"{because} is a real event, and a silent reset is indistinguishable from one");
    }

    /// <summary>
    /// The non-vacuity half: a payload this reader actually wrote is read back silently. Without
    /// this, "warn on everything" satisfies the case above and the warning stops carrying
    /// information.
    /// </summary>
    [Fact]
    public void A_cursor_payload_this_reader_wrote_is_read_back_without_a_warning()
    {
        var logger = new CapturingLogger<EvalCorpusWatermark>();
        var watermark = new EvalCorpusWatermark(_store, logger);

        watermark.Save(EvalCorpusSweep.CorpusId, 42);

        watermark.Read(EvalCorpusSweep.CorpusId).Should().Be(42);
        logger.WarningCount("unreadable payload").Should().Be(0);
    }

    /// <summary>
    /// A recorded <c>0</c> is a legitimate value — the edge of a history nothing has advanced past
    /// yet — and it reaches the caller as a value rather than as the unreadable case. It is the
    /// same number either way, which is exactly why the warning is the only thing that can tell
    /// them apart.
    /// </summary>
    [Fact]
    public void A_recorded_zero_is_a_value_and_not_the_unreadable_case()
    {
        WriteRawCursor("{\"AfterReviewRunId\":0}");

        var logger = new CapturingLogger<EvalCorpusWatermark>();
        var read = new EvalCorpusWatermark(_store, logger).Read(EvalCorpusSweep.CorpusId);

        read.Should().Be(0);
        logger.WarningCount("unreadable payload").Should().Be(0);
    }
}
