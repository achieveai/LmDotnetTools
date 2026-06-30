using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P4.3 — the <see cref="ReviewPoster"/> posts a review comment <b>exactly once</b> (plan §11). These
/// tests pin its two-guard contract: the safe collect-only default never touches the provider; a live
/// post happens once and records the response id; replaying an already-terminal outbox row is a no-op;
/// and the provider-side backstop scan adopts a comment that a crashed prior attempt already posted
/// rather than posting a duplicate.
/// </summary>
public sealed class ReviewPosterTests
{
    private const string Provider = "github";
    private static readonly RepoIdentity Repo = new()
    {
        Provider = "github",
        OrgOrOwner = "acme",
        RepoName = "widgets",
        RepoStableId = "R_node_123",
    };

    [Fact]
    public async Task PostReviewAsync_collect_only_default_records_without_posting()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var runId = SeedRun(store);
        var publisher = new FakeReviewCommentPublisher();

        var outcome = await Poster(publisher, store).PostReviewAsync(
            Request(runId, livePostingAuthorized: false),
            CancellationToken.None);

        outcome.Kind.Should().Be(PostOutcomeKind.CollectedOnly);
        outcome.ProviderResponseId.Should().BeNull();
        publisher.PostCount.Should().Be(0, "collect-only must never touch the provider");
        store.GetOutbox(outcome.OutboxId)!.Status.Should().Be(OutboxStatus.Collected);
    }

    [Fact]
    public async Task PostReviewAsync_live_posts_once_and_records_the_response_id()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var runId = SeedRun(store);
        var publisher = new FakeReviewCommentPublisher();

        var outcome = await Poster(publisher, store).PostReviewAsync(
            Request(runId, livePostingAuthorized: true),
            CancellationToken.None);

        outcome.Kind.Should().Be(PostOutcomeKind.Posted);
        publisher.PostCount.Should().Be(1);
        var entry = store.GetOutbox(outcome.OutboxId)!;
        entry.Status.Should().Be(OutboxStatus.Posted);
        entry.ProviderResponseId.Should().Be(outcome.ProviderResponseId).And.NotBeNull();
    }

    [Fact]
    public async Task PostReviewAsync_replaying_a_posted_run_does_not_post_again()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var runId = SeedRun(store);
        var publisher = new FakeReviewCommentPublisher();
        var poster = Poster(publisher, store);

        var first = await poster.PostReviewAsync(Request(runId, livePostingAuthorized: true), CancellationToken.None);
        var replay = await poster.PostReviewAsync(Request(runId, livePostingAuthorized: true), CancellationToken.None);

        first.Kind.Should().Be(PostOutcomeKind.Posted);
        replay.Kind.Should().Be(PostOutcomeKind.ReplayNoOp);
        replay.OutboxId.Should().Be(first.OutboxId, "the same idempotency key collapses to one outbox row");
        replay.ProviderResponseId.Should().Be(first.ProviderResponseId);
        publisher.PostCount.Should().Be(1, "the replay must not post a second comment");
    }

    [Fact]
    public async Task PostReviewAsync_replaying_a_collect_only_run_is_a_no_op()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var runId = SeedRun(store);
        var publisher = new FakeReviewCommentPublisher();
        var poster = Poster(publisher, store);

        _ = await poster.PostReviewAsync(Request(runId, livePostingAuthorized: false), CancellationToken.None);
        var replay = await poster.PostReviewAsync(Request(runId, livePostingAuthorized: false), CancellationToken.None);

        replay.Kind.Should().Be(PostOutcomeKind.ReplayNoOp);
        publisher.PostCount.Should().Be(0);
    }

    [Fact]
    public async Task PostReviewAsync_backstop_adopts_a_comment_a_crashed_attempt_already_posted()
    {
        // Crash-replay scenario: a prior attempt posted the comment provider-side but crashed before the
        // outbox transitioned past Sending. We reconstruct that state — an outbox row leased in Sending
        // plus a matching comment already present on the provider — then prove the poster adopts the
        // existing comment (Sending → Posted) instead of posting a duplicate.
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var runId = SeedRun(store);
        var publisher = new FakeReviewCommentPublisher();

        var request = Request(runId, livePostingAuthorized: true);
        var key = IdempotencyKey.Build(request.Key);

        var leased = store.EnqueueOutbox(new OutboxEntry
        {
            IdempotencyKey = key,
            Provider = Provider,
            ReviewRunId = runId,
            Operation = ReviewPoster.PostReviewCommentOperation,
            ArtifactKind = request.Key.ArtifactKind,
            Status = OutboxStatus.Pending,
        });
        store.TryTransitionOutbox(leased.Id, OutboxStatus.Pending, OutboxStatus.Sending).Should().BeTrue();
        publisher.SeedExistingComment(key, "resp-already-there");

        var outcome = await Poster(publisher, store).PostReviewAsync(request, CancellationToken.None);

        outcome.Kind.Should().Be(PostOutcomeKind.AlreadyPostedBackstop);
        outcome.ProviderResponseId.Should().Be("resp-already-there");
        publisher.PostCount.Should().Be(0, "the comment already existed — posting again would duplicate it");
        var entry = store.GetOutbox(outcome.OutboxId)!;
        entry.Status.Should().Be(OutboxStatus.Posted);
        entry.ProviderResponseId.Should().Be("resp-already-there");
    }

    private static ReviewPoster Poster(FakeReviewCommentPublisher publisher, ReviewStore store) =>
        new(publisher, store, NullLogger<ReviewPoster>.Instance);

    private static PostReviewRequest Request(long runId, bool livePostingAuthorized) =>
        new(
            ReviewRunId: runId,
            Key: new IdempotencyKeyComponents(
                Provider: Provider,
                OrgOrOwner: "acme",
                Project: null,
                RepoStableId: "R_node_123",
                PrId: "7",
                Operation: ReviewPoster.PostReviewCommentOperation,
                ArtifactKind: "review",
                ArtifactSubject: "summary",
                TriggerWatermark: "wm-1",
                VariantId: "primary"),
            Target: new ReviewCommentTarget(Repo, "7"),
            Body: "## Review\nLooks good.",
            LivePostingAuthorized: livePostingAuthorized);

    private static long SeedRun(ReviewStore store)
    {
        var repoId = store.EnsureRepo(Repo);
        return store.CreateOrGetReviewRun(new ReviewRun
        {
            RepoId = repoId,
            PrId = "7",
            HeadSha = "head-sha",
            BaseSha = "base-sha",
            TriggerWatermark = "wm-1",
            ReviewKind = "full",
            VariantId = "primary",
            Mode = "post",
            Stage = ReviewStage.Judged,
            WorkflowStatus = WorkflowStatus.Running,
            PrLifecycleState = PrLifecycleState.Open,
        }).Id;
    }
}
