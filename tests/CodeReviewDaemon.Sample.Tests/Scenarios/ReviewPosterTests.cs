using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P4.3 — the <see cref="ReviewPoster"/> posts a review comment <b>exactly once</b> (plan §11). These
/// tests pin its two-guard contract: the safe collect-only default never touches the provider; a live
/// post happens once and records the response id; replaying an already-terminal outbox row is a no-op;
/// and the provider-side backstop scan adopts a comment that a crashed prior attempt already posted
/// rather than posting a duplicate.
/// </summary>
public sealed class ReviewPosterTests : LoggingTestBase
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Workflow_receipt_reconciliation_after_expiry_only_adopts_visible_provider_proof(bool visible)
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var run = store.GetReviewRun(SeedRun(store))!;
        var publisher = new FakeReviewCommentPublisher { PostFailure = new IOException("Lost provider response.") };
        publisher.OnFind = () =>
            store
                .GetArtifacts(run.Id)
                .Should()
                .Contain(a => a.ArtifactKind.StartsWith("workflow-publication-intent:", StringComparison.Ordinal));
        var tools = new ReviewPublicationTools(
            run,
            Repo,
            "round",
            Poster(publisher, store),
            new PublicationStateProvider(),
            new DiffManifest(run.BaseSha, run.HeadSha, []),
            true,
            () => true
        );
        await tools
            .Invoking(value => value.PublishSummaryAsync("summary", "Exact body", default))
            .Should()
            .ThrowAsync<IOException>();
        var receipt = store.GetOutboxForRun(run.Id).Single();
        var intent = store
            .GetArtifacts(run.Id)
            .Single(a => a.ArtifactKind.StartsWith("workflow-publication-intent:", StringComparison.Ordinal));
        intent.Payload.Should().Contain("Exact body").And.NotContain("IsStillAuthorized");
        if (visible)
            publisher.SeedExistingComment(receipt.IdempotencyKey, "provider-confirmed");
        var resumed = new ReviewPublicationTools(
            run,
            Repo,
            "round",
            Poster(publisher, store),
            new PublicationStateProvider(),
            new DiffManifest(run.BaseSha, run.HeadSha, []),
            false,
            () => false
        );
        resumed.LimitToDeadline(DateTimeOffset.UtcNow.AddMinutes(-1));
        await resumed.ReconcileAsync(default);
        store.GetOutbox(receipt.Id)!.Status.Should().Be(visible ? OutboxStatus.Posted : OutboxStatus.Sending);
        publisher.FindCallCount.Should().Be(2);
        publisher.PostCount.Should().Be(0);
    }

    [Fact]
    public async Task Workflow_receipt_without_intent_never_guesses_a_provider_target()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var runId = SeedRun(store);
        store.EnqueueOutbox(
            new OutboxEntry
            {
                ReviewRunId = runId,
                Provider = "github",
                ArtifactKind = "workflow-publication",
                IdempotencyKey = "unknown",
                Operation = "workflow-publish-summary",
                Status = OutboxStatus.Sending,
            }
        );
        var publisher = new FakeReviewCommentPublisher();
        await Poster(publisher, store).ReconcilePublicationsAsync(runId, Repo, default);
        publisher.FindCallCount.Should().Be(0);
        publisher.PostCount.Should().Be(0);
    }

    [Fact]
    public async Task Workflow_receipt_intent_with_changed_body_is_rejected_before_provider_access()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var run = store.GetReviewRun(SeedRun(store))!;
        var publisher = new FakeReviewCommentPublisher { PostFailure = new IOException("Lost provider response.") };
        var tools = new ReviewPublicationTools(
            run,
            Repo,
            "round",
            Poster(publisher, store),
            new PublicationStateProvider(),
            new DiffManifest(run.BaseSha, run.HeadSha, []),
            true,
            () => true
        );
        await tools
            .Invoking(value => value.PublishSummaryAsync("summary", "original-body", default))
            .Should()
            .ThrowAsync<IOException>();
        var intent = store
            .GetArtifacts(run.Id)
            .Single(a => a.ArtifactKind.StartsWith("workflow-publication-intent:", StringComparison.Ordinal));
        store.AddArtifact(
            intent with
            {
                Id = 0,
                Payload = intent.Payload.Replace("original-body", "modified-body", StringComparison.Ordinal),
            }
        );
        await Poster(publisher, store)
            .Invoking(value => value.ReconcilePublicationsAsync(run.Id, Repo, default))
            .Should()
            .ThrowAsync<InvalidOperationException>();
        publisher.FindCallCount.Should().Be(1);
        publisher.PostCount.Should().Be(0);
    }

    private const string Provider = "github";
    private static readonly RepoIdentity Repo = new()
    {
        Provider = "github",
        OrgOrOwner = "acme",
        RepoName = "widgets",
        RepoStableId = "R_node_123",
    };

    public ReviewPosterTests(ITestOutputHelper output)
        : base(output) { }

    [Fact]
    public async Task PostReviewAsync_collect_only_default_records_without_posting()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var runId = SeedRun(store);
        var publisher = new FakeReviewCommentPublisher();

        var outcome = await Poster(publisher, store)
            .PostReviewAsync(Request(runId, livePostingAuthorized: false), CancellationToken.None);

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

        var outcome = await Poster(publisher, store)
            .PostReviewAsync(Request(runId, livePostingAuthorized: true), CancellationToken.None);

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

        var leased = store.EnqueueOutbox(
            new OutboxEntry
            {
                IdempotencyKey = key,
                Provider = Provider,
                ReviewRunId = runId,
                Operation = ReviewPoster.PostReviewCommentOperation,
                ArtifactKind = request.Key.ArtifactKind,
                Status = OutboxStatus.Pending,
            }
        );
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

    [Fact]
    public async Task PostReviewAsync_posts_a_previously_collected_row_once_live_posting_is_authorized()
    {
        // A Posted-stage failure (or a run whose first attempt ran before posting was authorized) can leave a
        // Collected row behind. Collected is only terminal for an unauthorized collect-only replay: once a
        // later attempt IS authorized to post live, the row must reopen and actually reach the provider,
        // otherwise the run completes claiming delivery it never made.
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var runId = SeedRun(store);
        var publisher = new FakeReviewCommentPublisher();
        var poster = Poster(publisher, store);

        var collected = await poster.PostReviewAsync(
            Request(runId, livePostingAuthorized: false),
            CancellationToken.None
        );
        collected.Kind.Should().Be(PostOutcomeKind.CollectedOnly);

        var posted = await poster.PostReviewAsync(Request(runId, livePostingAuthorized: true), CancellationToken.None);

        posted.Kind.Should().Be(PostOutcomeKind.Posted);
        posted.OutboxId.Should().Be(collected.OutboxId, "the same idempotency key collapses to one outbox row");
        publisher.PostCount.Should().Be(1);
        var entry = store.GetOutbox(posted.OutboxId)!;
        entry.Status.Should().Be(OutboxStatus.Posted);
        entry.ProviderResponseId.Should().Be(posted.ProviderResponseId).And.NotBeNull();

        // ...and posting stays exactly-once afterwards.
        var replay = await poster.PostReviewAsync(Request(runId, livePostingAuthorized: true), CancellationToken.None);
        replay.Kind.Should().Be(PostOutcomeKind.ReplayNoOp);
        publisher.PostCount.Should().Be(1);
    }

    private ReviewPoster Poster(FakeReviewCommentPublisher publisher, ReviewStore store) =>
        new(publisher, store, LoggerFactory.CreateLogger<ReviewPoster>());

    [Fact]
    public async Task WorkflowPublication_UnknownSendDoesNotRepeatAfterNegativeScan()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var runId = SeedRun(store);
        var publisher = new FakeReviewCommentPublisher();
        var poster = Poster(publisher, store);
        var request = Request(runId, false) with { RequireConfirmedOutcome = true };
        var collected = await poster.PostReviewAsync(request, CancellationToken.None);
        store.TryTransitionOutbox(collected.OutboxId, OutboxStatus.Collected, OutboxStatus.Sending).Should().BeTrue();

        await poster
            .Invoking(p => p.PostReviewAsync(request with { LivePostingAuthorized = true }, CancellationToken.None))
            .Should()
            .ThrowAsync<ReviewPublicationUncertainException>();

        publisher.PostCount.Should().Be(0);
        store.GetOutbox(collected.OutboxId)!.Status.Should().Be(OutboxStatus.Sending);
    }

    [Fact]
    public async Task WorkflowPublication_RejectsChangedBodyForExistingAction()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var publisher = new FakeReviewCommentPublisher();
        var poster = Poster(publisher, store);
        var request = Request(SeedRun(store), false) with { RequireConfirmedOutcome = true };
        _ = await poster.PostReviewAsync(request, CancellationToken.None);

        await poster
            .Invoking(p =>
                p.PostReviewAsync(
                    request with
                    {
                        Body = "different",
                        LivePostingAuthorized = true,
                    },
                    CancellationToken.None
                )
            )
            .Should()
            .ThrowAsync<InvalidOperationException>();

        publisher.PostCount.Should().Be(0);
    }

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
                HeadSha: "wm-1",
                VariantId: "primary"
            ),
            Target: new ReviewCommentTarget(Repo, "7"),
            Body: "## Review\nLooks good.",
            LivePostingAuthorized: livePostingAuthorized
        );

    [Fact]
    public async Task ScopedPublication_UsesAgentBodyVerbatim_AndStopsAtChangedHead()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var run = store.GetReviewRun(SeedRun(store))!;
        var publisher = new FakeReviewCommentPublisher();
        var provider = new PublicationStateProvider();
        var tools = new ReviewPublicationTools(
            run,
            Repo,
            "round-1",
            Poster(publisher, store),
            provider,
            new DiffManifest(run.BaseSha, run.HeadSha, []),
            true,
            () => true
        );

        _ = await tools.PublishSummaryAsync("summary", "Agent-chosen body.\nExact wording.", CancellationToken.None);
        publisher.PostedBodies.Should().Equal("Agent-chosen body.\nExact wording.");

        provider.Head = "new-head";
        await tools
            .Invoking(t => t.PublishSummaryAsync("another", "Another body", CancellationToken.None))
            .Should()
            .ThrowAsync<InvalidOperationException>();
        publisher.PostCount.Should().Be(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ScopedPublication_RechecksAChangedHeadAfterPreflight(bool livePostingAuthorized)
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var run = store.GetReviewRun(SeedRun(store))!;
        var publisher = new FakeReviewCommentPublisher();
        var provider = new PublicationStateProvider();
        provider.AfterFirstHeadRead = () => provider.Head = "new-head";
        var tools = new ReviewPublicationTools(
            run,
            Repo,
            "round-1",
            Poster(publisher, store),
            provider,
            new DiffManifest(run.BaseSha, run.HeadSha, []),
            livePostingAuthorized,
            () => true
        );

        await tools
            .Invoking(t => t.PublishSummaryAsync("summary", "body", CancellationToken.None))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*current open PR at the admitted head*");

        publisher.PostCount.Should().Be(0);
        store.GetOutboxForRun(run.Id).Should().ContainSingle().Which.Status.Should().Be(OutboxStatus.Pending);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ScopedPublication_RechecksAClosedPrAfterPreflight(bool livePostingAuthorized)
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var run = store.GetReviewRun(SeedRun(store))!;
        var publisher = new FakeReviewCommentPublisher();
        var provider = new PublicationStateProvider();
        provider.AfterFirstHeadRead = () => provider.Lifecycle = PrLifecycle.Abandoned;
        var tools = new ReviewPublicationTools(
            run,
            Repo,
            "round-1",
            Poster(publisher, store),
            provider,
            new DiffManifest(run.BaseSha, run.HeadSha, []),
            livePostingAuthorized,
            () => true
        );

        await tools
            .Invoking(t => t.PublishSummaryAsync("summary", "body", CancellationToken.None))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*current open PR at the admitted head*");

        publisher.PostCount.Should().Be(0);
        store.GetOutboxForRun(run.Id).Should().ContainSingle().Which.Status.Should().Be(OutboxStatus.Pending);
    }

    [Fact]
    public async Task ScopedPublication_ExpiredScopeNeverCallsProvider()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var run = store.GetReviewRun(SeedRun(store))!;
        var publisher = new FakeReviewCommentPublisher();
        var tools = new ReviewPublicationTools(
            run,
            Repo,
            "round-1",
            Poster(publisher, store),
            new PublicationStateProvider(),
            new DiffManifest(run.BaseSha, run.HeadSha, []),
            true,
            () => false
        );

        await tools
            .Invoking(t => t.PublishSummaryAsync("summary", "body", CancellationToken.None))
            .Should()
            .ThrowAsync<InvalidOperationException>();

        publisher.FindCallCount.Should().Be(0);
        publisher.PostCount.Should().Be(0);
        store.GetOutboxForRun(run.Id).Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScopedPublication_DeadlineExpirationPreventsNewEffect(bool expireDuringProviderRead)
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var run = store.GetReviewRun(SeedRun(store))!;
        var publisher = new FakeReviewCommentPublisher();
        var provider = new PublicationStateProvider();
        var tools = new ReviewPublicationTools(
            run,
            Repo,
            "round-1",
            Poster(publisher, store),
            provider,
            new DiffManifest(run.BaseSha, run.HeadSha, []),
            true,
            () => true
        );
        tools.LimitToDeadline(DateTimeOffset.UtcNow.AddMinutes(expireDuringProviderRead ? 5 : -5));
        provider.OnHeadRead = () => tools.LimitToDeadline(DateTimeOffset.UtcNow.AddMinutes(-5));

        await tools
            .Invoking(t => t.PublishSummaryAsync("summary", "body", CancellationToken.None))
            .Should()
            .ThrowAsync<InvalidOperationException>();

        publisher.FindCallCount.Should().Be(0);
        publisher.PostCount.Should().Be(0);
        store.GetOutboxForRun(run.Id).Should().BeEmpty();
    }

    [Fact]
    public async Task ScopedPublication_DeadlineExpirationDuringDedupeScanPreventsPost()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var run = store.GetReviewRun(SeedRun(store))!;
        var publisher = new FakeReviewCommentPublisher();
        var tools = new ReviewPublicationTools(
            run,
            Repo,
            "round-1",
            Poster(publisher, store),
            new PublicationStateProvider(),
            new DiffManifest(run.BaseSha, run.HeadSha, []),
            true,
            () => true
        );
        tools.LimitToDeadline(DateTimeOffset.UtcNow.AddMinutes(5));
        publisher.OnFind = () => tools.LimitToDeadline(DateTimeOffset.UtcNow.AddMinutes(-5));

        await tools
            .Invoking(t => t.PublishSummaryAsync("summary", "body", CancellationToken.None))
            .Should()
            .ThrowAsync<InvalidOperationException>();

        publisher.FindCallCount.Should().Be(1);
        publisher.PostCount.Should().Be(0);
        store.GetOutboxForRun(run.Id).Should().ContainSingle().Which.Status.Should().Be(OutboxStatus.Pending);
    }

    private sealed class PublicationStateProvider : IPrProvider
    {
        public string Provider => "github";
        public string Head { get; set; } = "head-sha";
        public PrLifecycle Lifecycle { get; set; } = PrLifecycle.Open;
        public Action? AfterFirstHeadRead { get; set; }
        public Action? OnHeadRead { get; set; }
        private int _headReadCount;

        public Task<PullRequestPage> ListOpenPullRequestsAsync(
            PrPollRequest request,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<PrLifecycle> GetPrStateAsync(RepoIdentity repo, string prId, CancellationToken cancellationToken) =>
            Task.FromResult(Lifecycle);

        public Task<string?> GetCurrentHeadShaAsync(RepoIdentity repo, string prId, CancellationToken cancellationToken)
        {
            var head = Head;
            if (_headReadCount++ == 0)
            {
                AfterFirstHeadRead?.Invoke();
            }
            OnHeadRead?.Invoke();
            return Task.FromResult<string?>(head);
        }
    }

    private static long SeedRun(ReviewStore store)
    {
        var repoId = store.EnsureRepo(Repo);
        return store
            .CreateOrGetReviewRun(
                new ReviewRun
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
                }
            )
            .Id;
    }
}
