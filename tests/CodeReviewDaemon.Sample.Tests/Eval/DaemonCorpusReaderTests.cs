using System.Text.Json;
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
/// The corpus reader is the only thing standing between the daemon's recorded reviews and an eval
/// run, and it is driven over a real <see cref="ReviewStore"/> on a temp SQLite file because the
/// pairing it performs is expressed in that schema's rows.
/// <para>
/// The load-bearing facts are the negative ones. A run with a review but no recorded input forms no
/// pair and must be dropped rather than admitted with an empty task input, and a candidate whose
/// model the host cannot classify must carry a NULL generator family — unknown — rather than a
/// guess that would let generator-family exclusion silently not apply.
/// </para>
/// </summary>
public sealed class DaemonCorpusReaderTests : IDisposable
{
    private readonly TempSqliteDatabase _db = new();
    private readonly ReviewStore _store;

    public DaemonCorpusReaderTests() => _store = new ReviewStore(_db.ConnectionString);

    public void Dispose()
    {
        _store.Dispose();
        _db.Dispose();
    }

    private long CreateRun(
        string prId,
        ReviewStage stage = ReviewStage.Reviewed,
        string variantId = "primary",
        string? modelId = "openai/gpt-5"
    )
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
                    VariantId = variantId,
                    Mode = "collect-only",
                    ModelId = modelId,
                    Stage = stage,
                    WorkflowStatus = WorkflowStatus.Completed,
                    PrLifecycleState = PrLifecycleState.Merged,
                }
            )
            .Id;
    }

    private void AddArtifact(long runId, string kind, object payload) =>
        _ = _store.AddArtifact(
            new ReviewArtifact
            {
                ReviewRunId = runId,
                ArtifactSchemaVersion = 1,
                ArtifactKind = kind,
                Provider = "github",
                Payload = JsonSerializer.Serialize(payload),
            }
        );

    private void AddContext(long runId, string diff) =>
        AddArtifact(
            runId,
            DaemonReviewStageExecutor.ContextArtifactKind,
            new ContextArtifactPayload("118", "base", "head", diff)
        );

    private void AddReview(long runId, string text, string variantId = "primary") =>
        AddArtifact(
            runId,
            DaemonReviewStageExecutor.ReviewArtifactKind,
            new ReviewArtifactPayload(text, "run-1", variantId)
        );

    private static Task<CorpusPage> LoadAsync(
        DaemonCorpusReader reader,
        long afterCursor = 0,
        int limit = 1000,
        string corpusId = "daemon-corpus"
    ) => reader.LoadAsync(corpusId, afterCursor, limit, CancellationToken.None);

    /// <summary>
    /// The snapshot a load produced, asserting first that it produced one. Every case below that
    /// reads items expects a non-empty corpus, and a null snapshot there is a different failure from
    /// a wrong one.
    /// </summary>
    private static async Task<CorpusSnapshot> SnapshotAsync(
        DaemonCorpusReader reader,
        long afterCursor = 0,
        int limit = 1000
    )
    {
        var page = await LoadAsync(reader, afterCursor, limit);
        page.Snapshot.Should().NotBeNull("the window held candidates");
        return page.Snapshot!;
    }

    private DaemonCorpusReader Reader(ModelFamilyResolver? resolver = null) =>
        new(_store, resolver ?? (_ => null));

    [Fact]
    public async Task A_recorded_review_is_paired_with_the_input_it_answered()
    {
        var runId = CreateRun("118");
        AddContext(runId, "diff --git a/Foo.cs b/Foo.cs");
        AddReview(runId, "[Blocker] src/Foo.cs:1 is wrong.");

        var snapshot = await SnapshotAsync(Reader());

        var candidate = Assert.Single(snapshot.Items);
        candidate.TaskInput.Should().Be("diff --git a/Foo.cs b/Foo.cs");
        candidate.Content.Should().Be("[Blocker] src/Foo.cs:1 is wrong.");
        candidate.TaskType.Should().Be(DaemonCorpusReader.CodeReviewTaskType);
        candidate.VariantId.Should().Be("primary");
        candidate.ModelId.Should().Be("openai/gpt-5");
    }

    [Fact]
    public async Task The_b_arm_becomes_a_second_candidate_over_the_same_input()
    {
        // Paired by construction: the executor hands both arms the same review input under one
        // review_run_id, which is the shape an A/B judgement needs and nothing else in the store
        // has.
        var runId = CreateRun("118");
        AddContext(runId, "the shared diff");
        AddReview(runId, "the A review");
        AddArtifact(
            runId,
            VariantReviewer.VariantReviewArtifactKind,
            new VariantReviewArtifactPayload("b", "anthropic/claude", "the B review", "run-2")
        );

        var snapshot = await SnapshotAsync(Reader());

        snapshot.Items.Should().HaveCount(2);
        snapshot.Items.Should().OnlyContain(c => c.TaskInput == "the shared diff");
        snapshot.Items.Select(c => c.Content).Should().BeEquivalentTo("the A review", "the B review");
        snapshot.Items.Select(c => c.VariantId).Should().BeEquivalentTo("primary", "b");

        // The variant is part of the id, or the two arms would collide and the snapshot would
        // refuse the corpus outright.
        snapshot.Items.Select(c => c.CandidateId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task A_run_with_no_recorded_input_forms_no_pair()
    {
        var paired = CreateRun("118");
        AddContext(paired, "a diff");
        AddReview(paired, "a review");

        var orphan = CreateRun("119");
        AddReview(orphan, "a review with no diff behind it");

        var snapshot = await SnapshotAsync(Reader());

        Assert.Single(snapshot.Items).Content.Should().Be("a review");
    }

    [Fact]
    public async Task A_run_that_never_reached_the_review_stage_is_not_in_the_corpus()
    {
        var reviewed = CreateRun("118");
        AddContext(reviewed, "a diff");
        AddReview(reviewed, "a review");

        // Both artifacts present, stage not advanced — the shape a run takes when it dies between
        // writing its review and recording that it got there. The pairing alone would happily admit
        // it, so the stage floor is what keeps a run the daemon does not consider reviewed out of
        // the corpus its own quality is then measured on.
        var pending = CreateRun("119", stage: ReviewStage.ContextReady);
        AddContext(pending, "another diff");
        AddReview(pending, "a review the run never committed to");

        var snapshot = await SnapshotAsync(Reader());

        Assert.Single(snapshot.Items).TaskInput.Should().Be("a diff");
    }

    [Fact]
    public async Task An_unclassifiable_model_yields_a_null_generator_family_not_a_guess()
    {
        // NULL means the exclusion filter never ran on this row. It must never read as "not the
        // judge's family", which is what any non-null fallback would amount to.
        var runId = CreateRun("118", modelId: "some/unknown-model");
        AddContext(runId, "a diff");
        AddReview(runId, "a review");

        var snapshot = await SnapshotAsync(Reader(_ => null));

        Assert.Single(snapshot.Items).GeneratorFamily.Should().BeNull();
    }

    [Fact]
    public async Task A_resolved_model_family_reaches_the_candidate()
    {
        var runId = CreateRun("118", modelId: "openai/gpt-5");
        AddContext(runId, "a diff");
        AddReview(runId, "a review");

        // A literal family keyed off the id, NOT a parsing rule: this pins that whatever the injected
        // resolver answers reaches the candidate, which is the reader's job. Re-deriving a family here
        // would test the resolver instead — that lives in ModelFamilyTests — and would leave a second
        // copy of a family rule in the tree, which is the defect #456 closed.
        var snapshot = await SnapshotAsync(
            Reader(modelId => modelId == "openai/gpt-5" ? "openai" : null)
        );

        Assert.Single(snapshot.Items).GeneratorFamily.Should().Be("openai");
    }

    [Fact]
    public async Task An_escalated_run_is_attributed_to_the_model_that_actually_ran()
    {
        // review_run.model_id is INSERT-only: no UPDATE statement in the store touches it, so a run
        // that escalated mid-review still carries the model it was SEEDED with. Attributing the
        // review to that model credits the wrong generator, and every aggregate keyed on model or
        // family inherits the error.
        //
        // The model that actually ran is already recorded — the executor writes
        // `modelOverride ?? run.ModelId` into the lifecycle identity on the review-provisional
        // checkpoint, which sits in the very artifact list this reader already fetches.
        var runId = CreateRun("118", modelId: "openai/gpt-5");
        AddContext(runId, "a diff");
        AddReview(runId, "a review");
        AddArtifact(
            runId,
            DaemonReviewStageExecutor.ProvisionalReviewArtifactKind,
            new ReviewArtifactPayload(
                string.Empty,
                "run-1",
                "primary",
                Lifecycle: new ReviewLifecycleIdentity(
                    "s2s",
                    "thread-1",
                    "ws-1",
                    "anthropic/claude-escalated",
                    ToolAssisted: true,
                    ContextGeneration: 1
                )
            )
        );

        var snapshot = await SnapshotAsync(Reader(ModelFamilies.Of));

        var candidate = Assert.Single(snapshot.Items);
        candidate.ModelId.Should().Be("anthropic/claude-escalated");

        // The family follows the corrected id, or the fix would stop at the label and leave the
        // exclusion still keyed on the seeded model.
        candidate.GeneratorFamily.Should().Be("anthropic");
    }

    [Fact]
    public async Task A_run_that_never_escalated_is_still_attributed_to_its_seeded_model()
    {
        // The fallback, pinned separately: most runs record no lifecycle checkpoint at all, and
        // preferring a missing one must not erase the only model id such a run has.
        var runId = CreateRun("118", modelId: "openai/gpt-5");
        AddContext(runId, "a diff");
        AddReview(runId, "a review");

        var snapshot = await SnapshotAsync(Reader(ModelFamilies.Of));

        var candidate = Assert.Single(snapshot.Items);
        candidate.ModelId.Should().Be("openai/gpt-5");
        candidate.GeneratorFamily.Should().Be("openai");
    }

    [Fact]
    public async Task A_lifecycle_checkpoint_that_names_no_model_does_not_erase_the_seeded_one()
    {
        // ReviewLifecycleIdentity.ModelId is nullable. A checkpoint written by a run that recorded
        // no model must fall through to the run's own id rather than blanking it — otherwise adding
        // a checkpoint would LOSE attribution that was previously correct.
        var runId = CreateRun("118", modelId: "openai/gpt-5");
        AddContext(runId, "a diff");
        AddReview(runId, "a review");
        AddArtifact(
            runId,
            DaemonReviewStageExecutor.ProvisionalReviewArtifactKind,
            new ReviewArtifactPayload(
                string.Empty,
                "run-1",
                "primary",
                Lifecycle: new ReviewLifecycleIdentity(
                    "s2s",
                    "thread-1",
                    "ws-1",
                    ModelId: null,
                    ToolAssisted: true,
                    ContextGeneration: 1
                )
            )
        );

        Assert.Single((await SnapshotAsync(Reader(ModelFamilies.Of))).Items)
            .ModelId.Should()
            .Be("openai/gpt-5");
    }

    [Fact]
    public async Task The_b_arm_keeps_its_own_model_and_is_not_reattributed_by_the_escalation()
    {
        // The lifecycle checkpoint describes the PRIMARY arm's conversation. The B arm records its
        // own model on its own artifact, and must not inherit the primary's escalation.
        var runId = CreateRun("118", modelId: "openai/gpt-5");
        AddContext(runId, "the shared diff");
        AddReview(runId, "the A review");
        AddArtifact(
            runId,
            VariantReviewer.VariantReviewArtifactKind,
            new VariantReviewArtifactPayload("b", "anthropic/claude-haiku", "the B review", "run-2")
        );
        AddArtifact(
            runId,
            DaemonReviewStageExecutor.ProvisionalReviewArtifactKind,
            new ReviewArtifactPayload(
                string.Empty,
                "run-1",
                "primary",
                Lifecycle: new ReviewLifecycleIdentity(
                    "s2s",
                    "thread-1",
                    "ws-1",
                    "openai/gpt-5-escalated",
                    ToolAssisted: true,
                    ContextGeneration: 1
                )
            )
        );

        var snapshot = await SnapshotAsync(Reader(ModelFamilies.Of));

        snapshot
            .Items.Single(c => c.VariantId == "primary")
            .ModelId.Should()
            .Be("openai/gpt-5-escalated");
        snapshot
            .Items.Single(c => c.VariantId == "b")
            .ModelId.Should()
            .Be("anthropic/claude-haiku");
    }

    [Fact]
    public async Task No_candidate_carries_a_reference_because_the_store_holds_no_accepted_output()
    {
        // Reference-guided grading is the largest accuracy lever a judge has, and its absence here
        // is a property of the data. Synthesising one from the review being judged would make every
        // judge grade an answer against itself.
        var runId = CreateRun("118");
        AddContext(runId, "a diff");
        AddReview(runId, "a review");

        Assert.Single((await SnapshotAsync(Reader())).Items).Reference.Should().BeNull();
    }

    [Fact]
    public async Task The_latest_review_artifact_wins_over_a_superseded_one()
    {
        var runId = CreateRun("118");
        AddContext(runId, "a diff");
        AddReview(runId, "the first pass");
        AddReview(runId, "the re-review");

        Assert.Single((await SnapshotAsync(Reader())).Items).Content.Should().Be("the re-review");
    }

    [Fact]
    public async Task A_malformed_payload_costs_one_item_rather_than_the_whole_corpus()
    {
        var good = CreateRun("118");
        AddContext(good, "a diff");
        AddReview(good, "a review");

        var broken = CreateRun("119");
        AddContext(broken, "another diff");
        _ = _store.AddArtifact(
            new ReviewArtifact
            {
                ReviewRunId = broken,
                ArtifactSchemaVersion = 1,
                ArtifactKind = DaemonReviewStageExecutor.ReviewArtifactKind,
                Provider = "github",
                Payload = "{ this is not json",
            }
        );

        var snapshot = await SnapshotAsync(Reader());

        Assert.Single(snapshot.Items).Content.Should().Be("a review");
    }

    [Fact]
    public async Task The_snapshot_hash_is_stable_across_two_reads_of_the_same_rows()
    {
        var runId = CreateRun("118");
        AddContext(runId, "a diff");
        AddReview(runId, "a review");

        var first = await SnapshotAsync(Reader());
        var second = await SnapshotAsync(Reader());

        second.SnapshotHash.Should().Be(first.SnapshotHash);
    }

    [Fact]
    public async Task A_new_review_landing_changes_the_snapshot_hash()
    {
        var first = CreateRun("118");
        AddContext(first, "a diff");
        AddReview(first, "a review");

        var before = await SnapshotAsync(Reader());

        var second = CreateRun("119");
        AddContext(second, "another diff");
        AddReview(second, "another review");

        (await SnapshotAsync(Reader())).SnapshotHash.Should().NotBe(before.SnapshotHash);
    }

    /// <summary>
    /// <c>ORDER BY id LIMIT n</c> takes the OLDEST n rows. Once the store held more than the limit,
    /// every subsequent snapshot was byte-identical to the last, drawn entirely from the earliest
    /// history, and no review recorded after that point could ever enter an evaluation — silently,
    /// because the snapshot hash stays stable and the comparability refusal is perfectly happy: the
    /// corpus genuinely has not changed.
    /// <para>
    /// The window is stated by the caller now, so "the oldest n" is a choice a reader can see rather
    /// than an accident of the ordering.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_corpus_is_drawn_from_the_window_the_caller_states()
    {
        var ids = new List<long>();
        for (var i = 0; i < 4; i++)
        {
            var id = CreateRun($"pr-{i}");
            AddContext(id, $"diff {i}");
            AddReview(id, $"review {i}");
            ids.Add(id);
        }

        var page = await LoadAsync(
            new DaemonCorpusReader(_store, _ => "openai"),
            afterCursor: ids[1],
            limit: 10
        );

        page.Snapshot.Should().NotBeNull();
        page.Snapshot!.Items.Select(i => i.CandidateId)
            .Should()
            .BeEquivalentTo([$"{ids[2]}:primary", $"{ids[3]}:primary"]);

        page.Truncated.Should().BeFalse("the window reached the end of the history");
        page.NextCursor
            .Should()
            .Be(ids[3], "the caller must be told the edge it reached, not left to derive it");
    }

    /// <summary>
    /// And when the limit cuts the window short, that is said out loud. Truncation is the exact
    /// condition under which the corpus stops accumulating, and the silence is what made the
    /// original defect invisible — the ordering only decided which end it froze at.
    /// </summary>
    [Fact]
    public async Task A_window_the_limit_cuts_short_is_reported_rather_than_silently_truncated()
    {
        for (var i = 0; i < 3; i++)
        {
            var id = CreateRun($"pr-{i}");
            AddContext(id, $"diff {i}");
            AddReview(id, $"review {i}");
        }

        var logger = new CapturingLogger<DaemonCorpusReader>();

        var page = await LoadAsync(
            new DaemonCorpusReader(_store, _ => "openai", logger),
            limit: 2
        );

        page.Snapshot.Should().NotBeNull();
        page.Snapshot!.Size.Should().Be(2);
        page.Truncated
            .Should()
            .BeTrue("truncation is returned to the caller, not only written to a log nobody reads");
        logger.CountAtLevel(LogLevel.Warning, "did not reach the end").Should().Be(1);
    }

    /// <summary>
    /// A window in which nothing new was recorded is the normal outcome of a scheduled sweep, and it
    /// must not rewind the cursor. Returning <c>0</c> — or anything below the incoming edge — would
    /// make the next window re-read history the caller has already covered, for ever, on every
    /// sweep that happened to find nothing.
    /// </summary>
    [Fact]
    public async Task An_empty_window_holds_the_cursor_where_it_was()
    {
        var runId = CreateRun("118");
        AddContext(runId, "a diff");
        AddReview(runId, "a review");

        var page = await LoadAsync(Reader(), afterCursor: runId);

        page.NextCursor.Should().Be(runId, "an empty sweep must not rewind the window");
        page.Truncated.Should().BeFalse();
    }

    /// <summary>
    /// The corpus is null rather than empty when the window yielded no candidate.
    /// <see cref="CorpusSnapshot.Create"/> refuses an empty item list because an empty denominator
    /// makes every rate over it undefined rather than zero — so "nothing to evaluate" has to be
    /// representable as something other than a corpus.
    /// </summary>
    [Fact]
    public async Task A_window_with_no_candidates_yields_no_corpus_rather_than_an_empty_one()
    {
        (await LoadAsync(Reader())).Snapshot.Should().BeNull("the store holds no reviewed run");
    }

    /// <summary>
    /// The cursor tracks the runs the reader <b>looked at</b>, not the ones that yielded candidates.
    /// A run with a review and no recorded diff forms no pair and never will — leaving the cursor
    /// behind it would make every later window start by re-reading it and stop before reaching
    /// anything new.
    /// </summary>
    [Fact]
    public async Task The_cursor_advances_past_a_run_that_yielded_no_candidate()
    {
        var paired = CreateRun("118");
        AddContext(paired, "a diff");
        AddReview(paired, "a review");

        var orphan = CreateRun("119");
        AddReview(orphan, "a review with no diff behind it");

        var page = await LoadAsync(Reader());

        page.Snapshot.Should().NotBeNull();
        page.Snapshot!.Size.Should().Be(1, "only one run formed a pair");
        page.NextCursor
            .Should()
            .Be(orphan, "the reader reached the orphan and will learn nothing new from it");
    }

    /// <summary>
    /// The case that distinguishes "the edge reached" from "the edge of what yielded candidates":
    /// a window in which <b>nothing</b> paired. When even one run in the window forms a candidate
    /// the two readings coincide — the last run's id is the answer either way — so only a window
    /// that produced no corpus at all can tell them apart. Reading the yield instead of the reach
    /// parks the cursor on these rows for ever: every later sweep re-reads the same unusable runs,
    /// never reaches what came after them, and reports an empty corpus while the store fills up.
    /// </summary>
    [Fact]
    public async Task A_window_in_which_nothing_paired_still_advances_the_cursor()
    {
        var firstOrphan = CreateRun("118");
        AddReview(firstOrphan, "a review with no diff behind it");

        var lastOrphan = CreateRun("119");
        AddReview(lastOrphan, "another review with no diff behind it");

        var page = await LoadAsync(Reader());

        page.Snapshot.Should().BeNull("no run in the window formed a pair");
        page.NextCursor
            .Should()
            .Be(
                lastOrphan,
                "the reader read both runs and will learn nothing new from either; leaving the "
                    + "cursor behind them re-reads them for ever and never reaches what came after"
            );
    }

    /// <summary>
    /// The window is stated per call, so two loads from a reader that has been held across both
    /// cover different rows. That is the whole reason the window left the constructor: a reader
    /// built around one fixed lower edge returns the same oldest history for ever, and the snapshot
    /// hash staying stable makes every comparability refusal downstream agree with it.
    /// </summary>
    [Fact]
    public async Task Resuming_from_the_returned_cursor_covers_rows_the_first_load_did_not()
    {
        var first = CreateRun("118");
        AddContext(first, "a diff");
        AddReview(first, "a review");

        var reader = Reader();
        var firstPage = await LoadAsync(reader);

        var second = CreateRun("119");
        AddContext(second, "another diff");
        AddReview(second, "another review");

        var secondPage = await LoadAsync(reader, afterCursor: firstPage.NextCursor);

        secondPage.Snapshot.Should().NotBeNull();
        secondPage
            .Snapshot!.Items.Select(i => i.CandidateId)
            .Should()
            .BeEquivalentTo([$"{second}:primary"], "the first load's rows are behind the cursor");
    }

    /// <summary>
    /// Records <paramref name="count"/> reviewed runs, each with an input and a review.
    /// </summary>
    private void Reviewed(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var id = CreateRun($"pr-{i}");
            AddContext(id, $"diff {i}");
            AddReview(id, $"review {i}");
        }
    }

    /// <summary>
    /// The exact-fit boundary, and the case the truncation signal used to get wrong. A history that
    /// ends <b>exactly</b> at the limit fills the window without stopping short of anything:
    /// <c>Truncated</c> promises "there is more to read", and at exact fit there is not.
    /// <para>
    /// The consequence is not cosmetic. The caller acts on this flag — a truncated sweep is meant to
    /// come back immediately rather than wait out its interval — so a false positive here makes
    /// every sweep that happens to land on a round number re-run at once, and the operator warning
    /// that names the freeze condition fires on a window that reached the end of the history.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_window_that_ends_exactly_at_the_limit_is_not_reported_as_truncated()
    {
        Reviewed(3);

        var logger = new CapturingLogger<DaemonCorpusReader>();

        var page = await LoadAsync(new DaemonCorpusReader(_store, _ => "openai", logger), limit: 3);

        page.Snapshot.Should().NotBeNull();
        page.Snapshot!.Size.Should().Be(3, "the window still yields every run it was asked for");
        page.Truncated
            .Should()
            .BeFalse("the history ended at the limit; it did not continue past it");
        logger.CountAtLevel(LogLevel.Warning, "did not reach the end").Should().Be(0);
    }

    /// <summary>
    /// The other side of the same boundary, and the non-vacuity half: one more run than the limit is
    /// genuinely truncated. Without this, "never truncated" satisfies the case above.
    /// </summary>
    [Fact]
    public async Task A_window_with_one_run_beyond_the_limit_is_reported_as_truncated()
    {
        Reviewed(4);

        var page = await LoadAsync(Reader(), limit: 3);

        page.Snapshot.Should().NotBeNull();
        page.Snapshot!.Size.Should().Be(3, "the window is capped at the limit, not at the limit + 1");
        page.Truncated.Should().BeTrue();
    }

    /// <summary>
    /// The run beyond the limit is <b>left for the next window</b>, not consumed by this one. The
    /// over-fetch that tells exact fit from truncation must not leak into what the caller is handed
    /// or into the edge it resumes from — otherwise the boundary fix would silently drop one run
    /// out of every corpus.
    /// </summary>
    [Fact]
    public async Task The_run_that_proves_truncation_is_left_for_the_next_window()
    {
        Reviewed(4);

        var reader = Reader();
        var first = await LoadAsync(reader, limit: 3);
        var second = await LoadAsync(reader, afterCursor: first.NextCursor, limit: 3);

        second.Snapshot.Should().NotBeNull("the fourth run is still waiting to be read");
        second.Snapshot!.Size.Should().Be(1);
        second.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task A_negative_cursor_or_a_non_positive_limit_is_refused()
    {
        var reader = Reader();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => LoadAsync(reader, afterCursor: -1)
        );
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => LoadAsync(reader, limit: 0));
    }
}
