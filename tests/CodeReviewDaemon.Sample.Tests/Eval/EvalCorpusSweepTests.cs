using System.Text.Json;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Eval;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;

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

    private EvalCorpusSweep Sweep(int limit = 1000) =>
        new(
            _store,
            new DaemonCorpusReader(_store, modelId => modelId?.Split('/')[0]),
            new EvalCorpusWatermark(_store),
            limit
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

        var report = await SweepAsync();

        report.CandidateCount.Should().Be(2);
        report.FindingCount.Should().Be(2);
        report.AnchoredFindingCount.Should().Be(2);
        report
            .CandidatesCitingNothing.Should()
            .Be(1, "a review citing no file is the finding-level signal's own worst case");
    }
}
