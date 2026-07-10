using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P2.2 — one poll pass: resync-from-null on the first poll, turn each discovered PR into an
/// orchestrated <c>review_run</c>, advance and persist the opaque cursor (§12), and skip targets with
/// no registered provider rather than throwing.
/// </summary>
public sealed class PrPollingServiceTests : LoggingTestBase
{
    private const string Provider = "github";
    private const string Scope = "achieveai/lmdotnettools:open-prs";

    public PrPollingServiceTests(ITestOutputHelper output)
        : base(output)
    {
    }

    [Fact]
    public async Task First_poll_resyncs_discovers_prs_creates_runs_and_advances_the_cursor()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var provider = new MockPrProvider(Provider, [PrDescriptor("118")], NextCursor());
        var poller = BuildPoller(store, provider);

        await poller.PollOnceAsync(CancellationToken.None);

        provider.LastRequestedCursor.Should().BeNull("the first poll resyncs — there is no persisted cursor yet");

        // The discovered PR was orchestrated to completion (full pipeline via the recording executor).
        var repoId = store.EnsureRepo(SampleRepo());
        var run = store.CreateOrGetReviewRun(SeedFor(repoId, "118"));
        run.Stage.Should().Be(ReviewStage.Posted);
        run.WorkflowStatus.Should().Be(WorkflowStatus.Completed);

        // The cursor advanced and is persisted for the next poll.
        var cursor = store.ReadCursor(Provider, Scope, PrPollingService.CursorVersion);
        cursor.ShouldResync.Should().BeFalse();
        cursor.Cursor!.CursorPayload.Should().Be("{\"page\":2}");
    }

    [Fact]
    public async Task The_next_poll_hands_the_persisted_cursor_back_to_the_provider()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var provider = new MockPrProvider(Provider, [PrDescriptor("118")], NextCursor());
        var poller = BuildPoller(store, provider);

        await poller.PollOnceAsync(CancellationToken.None);
        await poller.PollOnceAsync(CancellationToken.None);

        provider.CallCount.Should().Be(2);
        provider.LastRequestedCursor.Should().NotBeNull("the second poll resumes from the saved cursor");
        provider.LastRequestedCursor!.CursorPayload.Should().Be("{\"page\":2}");
    }

    [Fact]
    public async Task Each_discovered_pr_becomes_its_own_review_run()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var provider = new MockPrProvider(Provider, [PrDescriptor("118"), PrDescriptor("119")], NextCursor());
        var poller = BuildPoller(store, provider);

        await poller.PollOnceAsync(CancellationToken.None);

        var repoId = store.EnsureRepo(SampleRepo());
        store.CreateOrGetReviewRun(SeedFor(repoId, "118")).Stage.Should().Be(ReviewStage.Posted);
        store.CreateOrGetReviewRun(SeedFor(repoId, "119")).Stage.Should().Be(ReviewStage.Posted);
    }

    [Fact]
    public async Task A_target_with_no_registered_provider_is_skipped_not_thrown()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var orchestrator = new PrOrchestrator(store, new RecordingStageExecutor(), LoggerFactory.CreateLogger<PrOrchestrator>());
        // Provider registered for "github" but the target asks for "azure-devops".
        var provider = new MockPrProvider(Provider, [PrDescriptor("118")], NextCursor());
        var target = new PrPollTarget { Provider = "azure-devops", Repo = SampleRepo(), Scope = Scope };
        var poller = new PrPollingService(
            [target], [provider], store, orchestrator, LoggerFactory.CreateLogger<PrPollingService>());

        var act = async () => await poller.PollOnceAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        provider.CallCount.Should().Be(0, "no provider matched the target");
    }

    [Fact]
    public async Task A_poison_pr_does_not_starve_the_rest_of_the_targets_prs()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var provider = new MockPrProvider(Provider, [PrDescriptor("118"), PrDescriptor("119")], NextCursor());
        // PR 118's orchestration throws; 119 must still be processed to completion.
        var orchestrator = new PrOrchestrator(
            store, new RecordingStageExecutor(throwForPrId: "118"), LoggerFactory.CreateLogger<PrOrchestrator>());
        var target = new PrPollTarget { Provider = Provider, Repo = SampleRepo(), Scope = Scope };
        var poller = new PrPollingService(
            [target], [provider], store, orchestrator, LoggerFactory.CreateLogger<PrPollingService>());

        var act = async () => await poller.PollOnceAsync(CancellationToken.None);

        await act.Should().NotThrowAsync("one poison PR must not abort the poll cycle");
        var repoId = store.EnsureRepo(SampleRepo());
        store.CreateOrGetReviewRun(SeedFor(repoId, "119")).Stage.Should().Be(ReviewStage.Posted, "the healthy PR completed");
        store.CreateOrGetReviewRun(SeedFor(repoId, "118")).WorkflowStatus.Should()
            .Be(WorkflowStatus.RetryPending, "the failed PR is left for reconcile, not lost");
    }

    [Fact]
    public async Task A_poison_target_does_not_starve_the_other_targets()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var poison = new ThrowingPrProvider("azure-devops");
        var healthy = new MockPrProvider(Provider, [PrDescriptor("118")], NextCursor());
        var orchestrator = new PrOrchestrator(store, new RecordingStageExecutor(), LoggerFactory.CreateLogger<PrOrchestrator>());
        var targets = new[]
        {
            new PrPollTarget { Provider = "azure-devops", Repo = SampleRepo(), Scope = "ado:active" },
            new PrPollTarget { Provider = Provider, Repo = SampleRepo(), Scope = Scope },
        };
        var poller = new PrPollingService(
            targets, [poison, healthy], store, orchestrator, LoggerFactory.CreateLogger<PrPollingService>());

        var act = async () => await poller.PollOnceAsync(CancellationToken.None);

        await act.Should().NotThrowAsync("a failing target must not abort the whole cycle");
        var repoId = store.EnsureRepo(SampleRepo());
        store.CreateOrGetReviewRun(SeedFor(repoId, "118")).Stage.Should()
            .Be(ReviewStage.Posted, "the healthy target's PR was still processed");
    }

    /// <summary>An <see cref="IPrProvider"/> that always throws — a poison target for isolation tests.</summary>
    private sealed class ThrowingPrProvider(string provider) : IPrProvider
    {
        public string Provider { get; } = provider;

        public Task<PullRequestPage> ListOpenPullRequestsAsync(PrPollRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated provider failure");

        public Task<PrLifecycle> GetPrStateAsync(RepoIdentity repo, string prId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated provider failure");
    }

    [Fact]
    public async Task Prs_outside_the_recency_window_are_skipped_and_do_not_become_runs()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);

        var now = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
        // updated_at: within / outside a 7-day window, plus one the provider gives no date for.
        var recent = DatedDescriptor("200", updatedAt: now.AddDays(-1));
        var stale = DatedDescriptor("201", updatedAt: now.AddDays(-30));
        var undated = DatedDescriptor("202", updatedAt: null);
        var provider = new MockPrProvider(Provider, [recent, stale, undated], NextCursor());
        var orchestrator = new PrOrchestrator(
            store, new RecordingStageExecutor(), LoggerFactory.CreateLogger<PrOrchestrator>());
        var target = new PrPollTarget { Provider = Provider, Repo = SampleRepo(), Scope = Scope, MaxPrAgeDays = 7 };
        var poller = new PrPollingService(
            [target], [provider], store, orchestrator, LoggerFactory.CreateLogger<PrPollingService>(),
            timeProvider: new FixedTimeProvider(now));

        await poller.PollOnceAsync(CancellationToken.None);

        var repoId = store.EnsureRepo(SampleRepo());
        store.CreateOrGetReviewRun(SeedFor(repoId, "200")).Stage.Should()
            .Be(ReviewStage.Posted, "the recent PR is inside the window and was reviewed");
        store.CreateOrGetReviewRun(SeedFor(repoId, "202")).Stage.Should()
            .Be(ReviewStage.Posted, "an undated PR is kept — the filter never silently drops a PR it can't date");
        store.CreateOrGetReviewRun(SeedFor(repoId, "201")).Stage.Should()
            .Be(ReviewStage.Discovered, "the stale PR was filtered out before orchestration, so this call just created it fresh");
    }

    [Fact]
    public async Task A_zero_recency_window_reviews_every_pr()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);

        var now = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
        var ancient = DatedDescriptor("201", updatedAt: now.AddDays(-365));
        var provider = new MockPrProvider(Provider, [ancient], NextCursor());
        var orchestrator = new PrOrchestrator(
            store, new RecordingStageExecutor(), LoggerFactory.CreateLogger<PrOrchestrator>());
        var target = new PrPollTarget { Provider = Provider, Repo = SampleRepo(), Scope = Scope, MaxPrAgeDays = 0 };
        var poller = new PrPollingService(
            [target], [provider], store, orchestrator, LoggerFactory.CreateLogger<PrPollingService>(),
            timeProvider: new FixedTimeProvider(now));

        await poller.PollOnceAsync(CancellationToken.None);

        var repoId = store.EnsureRepo(SampleRepo());
        store.CreateOrGetReviewRun(SeedFor(repoId, "201")).Stage.Should()
            .Be(ReviewStage.Posted, "with the filter off (0), even a year-old PR is reviewed");
    }

    /// <summary>A <see cref="TimeProvider"/> pinned to a fixed instant so the recency-window cutoff is
    /// deterministic across the age-filter tests.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // ── fixtures ──────────────────────────────────────────────────────────────────────────────────

    private PrPollingService BuildPoller(ReviewStore store, IPrProvider provider)
    {
        var orchestrator = new PrOrchestrator(store, new RecordingStageExecutor(), LoggerFactory.CreateLogger<PrOrchestrator>());
        var target = new PrPollTarget { Provider = Provider, Repo = SampleRepo(), Scope = Scope };
        return new PrPollingService(
            [target], [provider], store, orchestrator, LoggerFactory.CreateLogger<PrPollingService>());
    }

    private static OpaqueCursor NextCursor() => new()
    {
        Provider = Provider,
        Scope = Scope,
        CursorVersion = PrPollingService.CursorVersion,
        CursorPayload = "{\"page\":2}",
        HighWaterMark = "2026-06-01T00:00:00Z",
    };

    private static PullRequestDescriptor PrDescriptor(string prId) => new()
    {
        PrId = prId,
        HeadSha = $"head-{prId}",
        BaseSha = "base-sha",
        TriggerWatermark = "wm-1",
        LifecycleState = PrLifecycleState.Open,
    };

    private static PullRequestDescriptor DatedDescriptor(
        string prId, DateTimeOffset? updatedAt, DateTimeOffset? createdAt = null) => new()
        {
            PrId = prId,
            HeadSha = $"head-{prId}",
            BaseSha = "base-sha",
            TriggerWatermark = "wm-1",
            LifecycleState = PrLifecycleState.Open,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
        };

    private static RepoIdentity SampleRepo() => new()
    {
        Provider = Provider,
        OrgOrOwner = "achieveai",
        RepoName = "LmDotnetTools",
        RepoStableId = "R_node_123",
    };

    private static ReviewRun SeedFor(long repoId, string prId) => new()
    {
        RepoId = repoId,
        PrId = prId,
        HeadSha = $"head-{prId}",
        BaseSha = "base-sha",
        TriggerWatermark = "wm-1",
        ReviewKind = "full",
        VariantId = "primary",
        Mode = "collect-only",
        Stage = ReviewStage.Discovered,
        WorkflowStatus = WorkflowStatus.Pending,
        PrLifecycleState = PrLifecycleState.Open,
    };
}
