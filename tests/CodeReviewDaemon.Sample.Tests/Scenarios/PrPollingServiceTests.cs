using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P2.2 — one poll pass: resync-from-null on the first poll, turn each discovered PR into an
/// orchestrated <c>review_run</c>, advance and persist the opaque cursor (§12), and skip targets with
/// no registered provider rather than throwing.
/// </summary>
public sealed class PrPollingServiceTests
{
    private const string Provider = "github";
    private const string Scope = "achieveai/lmdotnettools:open-prs";

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
        var orchestrator = new PrOrchestrator(store, new RecordingStageExecutor(), NullLogger<PrOrchestrator>.Instance);
        // Provider registered for "github" but the target asks for "azure-devops".
        var provider = new MockPrProvider(Provider, [PrDescriptor("118")], NextCursor());
        var target = new PrPollTarget { Provider = "azure-devops", Repo = SampleRepo(), Scope = Scope };
        var poller = new PrPollingService(
            [target], [provider], store, orchestrator, NullLogger<PrPollingService>.Instance);

        var act = async () => await poller.PollOnceAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        provider.CallCount.Should().Be(0, "no provider matched the target");
    }

    // ── fixtures ──────────────────────────────────────────────────────────────────────────────────

    private static PrPollingService BuildPoller(ReviewStore store, IPrProvider provider)
    {
        var orchestrator = new PrOrchestrator(store, new RecordingStageExecutor(), NullLogger<PrOrchestrator>.Instance);
        var target = new PrPollTarget { Provider = Provider, Repo = SampleRepo(), Scope = Scope };
        return new PrPollingService(
            [target], [provider], store, orchestrator, NullLogger<PrPollingService>.Instance);
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
