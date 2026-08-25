using System.Text.Json;
using AchieveAi.LmDotnetTools.LmEval.Corpus;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Eval;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;

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

    private static Task<CorpusSnapshot> LoadAsync(
        DaemonCorpusReader reader,
        string corpusId = "daemon-corpus"
    ) => reader.LoadAsync(corpusId, CancellationToken.None);

    private DaemonCorpusReader Reader(ModelFamilyResolver? resolver = null) =>
        new(_store, resolver ?? (_ => null));

    [Fact]
    public async Task A_recorded_review_is_paired_with_the_input_it_answered()
    {
        var runId = CreateRun("118");
        AddContext(runId, "diff --git a/Foo.cs b/Foo.cs");
        AddReview(runId, "[Blocker] src/Foo.cs:1 is wrong.");

        var snapshot = await LoadAsync(Reader());

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

        var snapshot = await LoadAsync(Reader());

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

        var snapshot = await LoadAsync(Reader());

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

        var snapshot = await LoadAsync(Reader());

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

        var snapshot = await LoadAsync(Reader(_ => null));

        Assert.Single(snapshot.Items).GeneratorFamily.Should().BeNull();
    }

    [Fact]
    public async Task A_resolved_model_family_reaches_the_candidate()
    {
        var runId = CreateRun("118", modelId: "openai/gpt-5");
        AddContext(runId, "a diff");
        AddReview(runId, "a review");

        var snapshot = await LoadAsync(
            Reader(modelId => modelId?.Split('/')[0])
        );

        Assert.Single(snapshot.Items).GeneratorFamily.Should().Be("openai");
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

        Assert.Single((await LoadAsync(Reader())).Items).Reference.Should().BeNull();
    }

    [Fact]
    public async Task The_latest_review_artifact_wins_over_a_superseded_one()
    {
        var runId = CreateRun("118");
        AddContext(runId, "a diff");
        AddReview(runId, "the first pass");
        AddReview(runId, "the re-review");

        Assert.Single((await LoadAsync(Reader())).Items).Content.Should().Be("the re-review");
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

        var snapshot = await LoadAsync(Reader());

        Assert.Single(snapshot.Items).Content.Should().Be("a review");
    }

    [Fact]
    public async Task The_snapshot_hash_is_stable_across_two_reads_of_the_same_rows()
    {
        var runId = CreateRun("118");
        AddContext(runId, "a diff");
        AddReview(runId, "a review");

        var first = await LoadAsync(Reader());
        var second = await LoadAsync(Reader());

        second.SnapshotHash.Should().Be(first.SnapshotHash);
    }

    [Fact]
    public async Task A_new_review_landing_changes_the_snapshot_hash()
    {
        var first = CreateRun("118");
        AddContext(first, "a diff");
        AddReview(first, "a review");

        var before = await LoadAsync(Reader());

        var second = CreateRun("119");
        AddContext(second, "another diff");
        AddReview(second, "another review");

        (await LoadAsync(Reader())).SnapshotHash.Should().NotBe(before.SnapshotHash);
    }
}
