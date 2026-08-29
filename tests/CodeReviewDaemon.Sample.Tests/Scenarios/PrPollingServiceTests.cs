using System.Text.Json;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
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
        : base(output) { }

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

    /// <summary>
    /// The prose half of the knowledge-retrieval key (#544) has exactly one hop to survive: the poll payload
    /// into the seed, and the seed into the persisted <c>review_run</c> row. The Reviewed stage ranks off the
    /// stored row and not off the page, which by then is long gone — so if the two assignments in
    /// <see cref="PrPollingService"/> stopped copying <c>Title</c>/<c>Description</c>, retrieval would fall
    /// back to exactly the path-only ranking that existed before the feature, in production, with the whole
    /// suite still green. Asserted off the STORE rather than off the seed object, because the store is what
    /// the consumer reads and a seed-level assertion would pass on a column that was never written.
    /// </summary>
    [Fact]
    public async Task A_discovered_prs_title_and_description_reach_the_persisted_run()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var provider = new MockPrProvider(
            Provider,
            [DescribedDescriptor("118", "Rank knowledge on what the PR says", "Siblings share no path token.")],
            NextCursor()
        );
        var poller = BuildPoller(store, provider);

        await poller.PollOnceAsync(CancellationToken.None);

        var repoId = store.EnsureRepo(SampleRepo());
        var run = store.CreateOrGetReviewRun(SeedFor(repoId, "118"));
        run.PrTitle.Should()
            .Be(
                "Rank knowledge on what the PR says",
                "the title is captured at poll time because the Reviewed stage runs after the poll page is gone, "
                    + "and possibly in a different process"
            );
        run.PrDescription.Should()
            .Be(
                "Siblings share no path token.",
                "the description is the other half of the same retrieval key and rides the same hop"
            );
    }

    [Fact]
    public async Task A_target_with_no_registered_provider_is_skipped_not_thrown()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var orchestrator = new PrOrchestrator(
            store,
            new RecordingStageExecutor(),
            LoggerFactory.CreateLogger<PrOrchestrator>()
        );
        // Provider registered for "github" but the target asks for "azure-devops".
        var provider = new MockPrProvider(Provider, [PrDescriptor("118")], NextCursor());
        var target = new PrPollTarget
        {
            Provider = "azure-devops",
            Repo = SampleRepo(),
            Scope = Scope,
        };
        var poller = new PrPollingService(
            [target],
            [provider],
            store,
            orchestrator,
            LoggerFactory.CreateLogger<PrPollingService>()
        );

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
            store,
            new RecordingStageExecutor(throwForPrId: "118"),
            LoggerFactory.CreateLogger<PrOrchestrator>()
        );
        var target = new PrPollTarget
        {
            Provider = Provider,
            Repo = SampleRepo(),
            Scope = Scope,
        };
        var poller = new PrPollingService(
            [target],
            [provider],
            store,
            orchestrator,
            LoggerFactory.CreateLogger<PrPollingService>()
        );

        var act = async () => await poller.PollOnceAsync(CancellationToken.None);

        await act.Should().NotThrowAsync("one poison PR must not abort the poll cycle");
        var repoId = store.EnsureRepo(SampleRepo());
        store
            .CreateOrGetReviewRun(SeedFor(repoId, "119"))
            .Stage.Should()
            .Be(ReviewStage.Posted, "the healthy PR completed");
        store
            .CreateOrGetReviewRun(SeedFor(repoId, "118"))
            .WorkflowStatus.Should()
            .Be(WorkflowStatus.RetryPending, "the failed PR is left for reconcile, not lost");
    }

    [Fact]
    public async Task A_poison_target_does_not_starve_the_other_targets()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var poison = new ThrowingPrProvider("azure-devops");
        var healthy = new MockPrProvider(Provider, [PrDescriptor("118")], NextCursor());
        var orchestrator = new PrOrchestrator(
            store,
            new RecordingStageExecutor(),
            LoggerFactory.CreateLogger<PrOrchestrator>()
        );
        var targets = new[]
        {
            new PrPollTarget
            {
                Provider = "azure-devops",
                Repo = SampleRepo(),
                Scope = "ado:active",
            },
            new PrPollTarget
            {
                Provider = Provider,
                Repo = SampleRepo(),
                Scope = Scope,
            },
        };
        var poller = new PrPollingService(
            targets,
            [poison, healthy],
            store,
            orchestrator,
            LoggerFactory.CreateLogger<PrPollingService>()
        );

        var act = async () => await poller.PollOnceAsync(CancellationToken.None);

        await act.Should().NotThrowAsync("a failing target must not abort the whole cycle");
        var repoId = store.EnsureRepo(SampleRepo());
        store
            .CreateOrGetReviewRun(SeedFor(repoId, "118"))
            .Stage.Should()
            .Be(ReviewStage.Posted, "the healthy target's PR was still processed");
    }

    /// <summary>An <see cref="IPrProvider"/> that always throws — a poison target for isolation tests.</summary>
    private sealed class ThrowingPrProvider(string provider) : IPrProvider
    {
        public string Provider { get; } = provider;

        public Task<PullRequestPage> ListOpenPullRequestsAsync(
            PrPollRequest request,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException("simulated provider failure");

        public Task<PrLifecycle> GetPrStateAsync(RepoIdentity repo, string prId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated provider failure");

        public Task<string?> GetCurrentHeadShaAsync(
            RepoIdentity repo,
            string prId,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException("simulated provider failure");
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
            store,
            new RecordingStageExecutor(),
            LoggerFactory.CreateLogger<PrOrchestrator>()
        );
        var target = new PrPollTarget
        {
            Provider = Provider,
            Repo = SampleRepo(),
            Scope = Scope,
            MaxPrAgeDays = 7,
        };
        var poller = new PrPollingService(
            [target],
            [provider],
            store,
            orchestrator,
            LoggerFactory.CreateLogger<PrPollingService>(),
            timeProvider: new FixedTimeProvider(now)
        );

        await poller.PollOnceAsync(CancellationToken.None);

        var repoId = store.EnsureRepo(SampleRepo());
        store
            .CreateOrGetReviewRun(SeedFor(repoId, "200"))
            .Stage.Should()
            .Be(ReviewStage.Posted, "the recent PR is inside the window and was reviewed");
        store
            .CreateOrGetReviewRun(SeedFor(repoId, "202"))
            .Stage.Should()
            .Be(ReviewStage.Posted, "an undated PR is kept — the filter never silently drops a PR it can't date");
        store
            .CreateOrGetReviewRun(SeedFor(repoId, "201"))
            .Stage.Should()
            .Be(
                ReviewStage.Discovered,
                "the stale PR was filtered out before orchestration, so this call just created it fresh"
            );
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
            store,
            new RecordingStageExecutor(),
            LoggerFactory.CreateLogger<PrOrchestrator>()
        );
        var target = new PrPollTarget
        {
            Provider = Provider,
            Repo = SampleRepo(),
            Scope = Scope,
            MaxPrAgeDays = 0,
        };
        var poller = new PrPollingService(
            [target],
            [provider],
            store,
            orchestrator,
            LoggerFactory.CreateLogger<PrPollingService>(),
            timeProvider: new FixedTimeProvider(now)
        );

        await poller.PollOnceAsync(CancellationToken.None);

        var repoId = store.EnsureRepo(SampleRepo());
        store
            .CreateOrGetReviewRun(SeedFor(repoId, "201"))
            .Stage.Should()
            .Be(ReviewStage.Posted, "with the filter off (0), even a year-old PR is reviewed");
    }

    [Fact]
    public async Task A_recency_window_hands_the_cutoff_to_the_provider()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);

        var now = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
        var provider = new MockPrProvider(Provider, [PrDescriptor("118")], NextCursor());
        var orchestrator = new PrOrchestrator(
            store,
            new RecordingStageExecutor(),
            LoggerFactory.CreateLogger<PrOrchestrator>()
        );
        var target = new PrPollTarget
        {
            Provider = Provider,
            Repo = SampleRepo(),
            Scope = Scope,
            MaxPrAgeDays = 7,
        };
        var poller = new PrPollingService(
            [target],
            [provider],
            store,
            orchestrator,
            LoggerFactory.CreateLogger<PrPollingService>(),
            timeProvider: new FixedTimeProvider(now)
        );

        await poller.PollOnceAsync(CancellationToken.None);

        provider
            .LastRecencyCutoff.Should()
            .Be(
                now - TimeSpan.FromDays(7),
                "the poller hands the provider the window cutoff to resolve a last-activity signal"
            );
    }

    [Fact]
    public async Task No_recency_window_hands_a_null_cutoff_to_the_provider()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);

        var provider = new MockPrProvider(Provider, [PrDescriptor("118")], NextCursor());
        var orchestrator = new PrOrchestrator(
            store,
            new RecordingStageExecutor(),
            LoggerFactory.CreateLogger<PrOrchestrator>()
        );
        var target = new PrPollTarget
        {
            Provider = Provider,
            Repo = SampleRepo(),
            Scope = Scope,
        };
        var poller = new PrPollingService(
            [target],
            [provider],
            store,
            orchestrator,
            LoggerFactory.CreateLogger<PrPollingService>()
        );

        await poller.PollOnceAsync(CancellationToken.None);

        provider.LastRecencyCutoff.Should().BeNull("no window means no cutoff and no extra provider work");
    }

    /// <summary>A <see cref="TimeProvider"/> pinned to a fixed instant so the recency-window cutoff is
    /// deterministic across the age-filter tests.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // ── fixtures ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Four sweeps share the poller's one maintenance seam, and its failure line named the first of
    /// them for all four (#455 item 5). An operator reading "PR-lifecycle sweep failed" while the eval
    /// corpus sweep was the one throwing looks in the wrong component first — and the wrong component
    /// looks healthy, because it is.
    /// </summary>
    [Fact]
    public async Task A_failing_maintenance_sweep_is_logged_by_name()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var logger = new CapturingLogger<PrPollingService>();
        var poller = BuildPoller(
            store,
            new MockPrProvider(Provider, [], NextCursor()),
            logger,
            _ => throw new MaintenanceSweepException("eval-corpus", new IOException("the store is gone"))
        );

        var kept = await poller.RunMaintenanceSweepAsync(CancellationToken.None);

        kept.Should().BeTrue("a failed sweep is logged, never a reason to stop the poller");
        logger.CountAtLevel(LogLevel.Error, "The eval-corpus maintenance sweep failed").Should().Be(1);
        logger
            .CountAtLevel(LogLevel.Error, "PR-lifecycle")
            .Should()
            .Be(0, "the sweep that threw is the one named, not the first one ever chained here");
        logger
            .CountAtLevelWithExceptionText(LogLevel.Error, "the store is gone")
            .Should()
            .Be(1, "the cause is still carried, not replaced by the name");
    }

    /// <summary>
    /// A sweep that was never composed through the named seam — every hand-rolled one in this suite —
    /// still logs, with the name honestly absent rather than guessed at.
    /// </summary>
    [Fact]
    public async Task An_unnamed_sweep_failure_still_logs_rather_than_escaping()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var logger = new CapturingLogger<PrPollingService>();
        var poller = BuildPoller(
            store,
            new MockPrProvider(Provider, [], NextCursor()),
            logger,
            _ => throw new InvalidOperationException("raw")
        );

        (await poller.RunMaintenanceSweepAsync(CancellationToken.None)).Should().BeTrue();

        logger.CountAtLevel(LogLevel.Error, "The unnamed maintenance sweep failed").Should().Be(1);
    }

    /// <summary>
    /// Shutdown is told from failure by type: a cancelled sweep stops the loop and is not an error
    /// line. Without this, wrapping every throw in a named exception would turn every clean stop into
    /// a logged failure.
    /// </summary>
    [Fact]
    public async Task A_sweep_cancelled_by_shutdown_stops_the_loop_without_an_error_line()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        var logger = new CapturingLogger<PrPollingService>();
        var poller = BuildPoller(
            store,
            new MockPrProvider(Provider, [], NextCursor()),
            logger,
            ct => throw new OperationCanceledException(ct)
        );

        var kept = await poller.RunMaintenanceSweepAsync(stopping.Token);

        kept.Should().BeFalse("the caller's loop has to stop");
        logger.CountAtLevel(LogLevel.Error, "maintenance sweep failed").Should().Be(0);
    }

    // ── the standing first-review sentinel check, reported once per start ────────────────────────────
    // The per-run guard in DaemonReviewStageExecutor makes one false "no new findings since the last review"
    // impossible; it cannot say whether the fleet is healthy, because a guard only ever speaks about the run
    // it refused. These two pin the WIRING of the population view — that it is measured at all, on every
    // start, healthy case included — which is the half that would rot silently: a check nobody calls and a
    // fleet nobody is watching produce exactly the same log.

    [Fact]
    public async Task Startup_warns_when_a_recent_first_review_claimed_nothing_had_changed()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        SeedFirstReview(store, "No new findings since the last review.");
        var progress = new CapturingLogger<ReviewProgressReporter>();
        var poller = BuildPoller(
            store,
            new MockPrProvider(Provider, [], NextCursor()),
            new ReviewProgressReporter(progress)
        );

        await poller.StartAsync(CancellationToken.None);
        await poller.StopAsync(CancellationToken.None);

        progress
            .CountAtLevel(LogLevel.Warning, "on a PR that had no last review")
            .Should()
            .Be(1, "a first-ever review cannot have findings to be new since, so a non-zero count is the alarm");
    }

    [Fact]
    public async Task Startup_reports_the_healthy_value_too_so_nobody_looked_cannot_pass_for_nothing_wrong()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        SeedFirstReview(store, "## Review\nMust: null check missing in Foo.cs:10.");
        var progress = new CapturingLogger<ReviewProgressReporter>();
        var poller = BuildPoller(
            store,
            new MockPrProvider(Provider, [], NextCursor()),
            new ReviewProgressReporter(progress)
        );

        await poller.StartAsync(CancellationToken.None);
        await poller.StopAsync(CancellationToken.None);

        progress.CountAtLevel(LogLevel.Information, "That is the healthy value.").Should().Be(1);
        progress
            .CountAtLevel(LogLevel.Warning, "on a PR that had no last review")
            .Should()
            .Be(0, "a real review is not the sentinel and must not be counted as one");
    }

    /// <summary>
    /// The lookback is a real term, and the option that sets it has to reach the query. Driven from the far
    /// side of the window: the seeded first review is a sentinel, so the ONLY thing keeping the warning quiet
    /// is that it fell outside the window the configured value asked for.
    /// </summary>
    [Fact]
    public async Task Startup_ignores_a_first_review_sentinel_from_before_the_lookback_window()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        SeedFirstReview(store, "No new findings since the last review.");
        // Two days past a one-day window, so the artifact the store just stamped is outside it.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(2));
        var progress = new CapturingLogger<ReviewProgressReporter>();
        var poller = BuildPoller(
            store,
            new MockPrProvider(Provider, [], NextCursor()),
            new ReviewProgressReporter(progress),
            timeProvider: clock,
            lookbackDays: 1
        );

        await poller.StartAsync(CancellationToken.None);
        await poller.StopAsync(CancellationToken.None);

        progress
            .CountAtLevel(LogLevel.Warning, "on a PR that had no last review")
            .Should()
            .Be(0, "a sentinel from before the window is history, not a statement about the fleet now");
        progress
            .CountAtLevel(LogLevel.Information, "nothing to report yet")
            .Should()
            .Be(1, "and the empty window says so rather than reporting a healthy count it did not measure");
    }

    /// <summary>A PR whose one and only review carries <paramref name="reviewText"/>, on the primary variant,
    /// so the startup check sees exactly one first review.</summary>
    private static void SeedFirstReview(ReviewStore store, string reviewText)
    {
        var repoId = store.EnsureRepo(SampleRepo());
        var run = store.CreateOrGetReviewRun(SeedFor(repoId, "118"));
        _ = store.AddArtifact(
            new ReviewArtifact
            {
                ReviewRunId = run.Id,
                ArtifactSchemaVersion = DaemonReviewStageExecutor.ReviewArtifactSchemaVersion,
                ArtifactKind = DaemonReviewStageExecutor.ReviewArtifactKind,
                Provider = Provider,
                Payload = JsonSerializer.Serialize(new ReviewArtifactPayload(reviewText, "run-1", "primary")),
            }
        );
    }

    private PrPollingService BuildPoller(
        ReviewStore store,
        IPrProvider provider,
        ReviewProgressReporter progress,
        TimeProvider? timeProvider = null,
        int? lookbackDays = null
    )
    {
        var orchestrator = new PrOrchestrator(
            store,
            new RecordingStageExecutor(),
            LoggerFactory.CreateLogger<PrOrchestrator>()
        );
        var target = new PrPollTarget
        {
            Provider = Provider,
            Repo = SampleRepo(),
            Scope = Scope,
        };
        return new PrPollingService(
            [target],
            [provider],
            store,
            orchestrator,
            LoggerFactory.CreateLogger<PrPollingService>(),
            timeProvider: timeProvider,
            progress: progress,
            firstReviewLookbackDays: lookbackDays
        );
    }

    private PrPollingService BuildPoller(
        ReviewStore store,
        IPrProvider provider,
        ILogger<PrPollingService> logger,
        Func<CancellationToken, Task> sweepAsync
    )
    {
        var orchestrator = new PrOrchestrator(
            store,
            new RecordingStageExecutor(),
            LoggerFactory.CreateLogger<PrOrchestrator>()
        );
        var target = new PrPollTarget
        {
            Provider = Provider,
            Repo = SampleRepo(),
            Scope = Scope,
        };
        return new PrPollingService([target], [provider], store, orchestrator, logger, sweepAsync: sweepAsync);
    }

    private PrPollingService BuildPoller(ReviewStore store, IPrProvider provider)
    {
        var orchestrator = new PrOrchestrator(
            store,
            new RecordingStageExecutor(),
            LoggerFactory.CreateLogger<PrOrchestrator>()
        );
        var target = new PrPollTarget
        {
            Provider = Provider,
            Repo = SampleRepo(),
            Scope = Scope,
        };
        return new PrPollingService(
            [target],
            [provider],
            store,
            orchestrator,
            LoggerFactory.CreateLogger<PrPollingService>()
        );
    }

    private static OpaqueCursor NextCursor() =>
        new()
        {
            Provider = Provider,
            Scope = Scope,
            CursorVersion = PrPollingService.CursorVersion,
            CursorPayload = "{\"page\":2}",
            HighWaterMark = "2026-06-01T00:00:00Z",
        };

    private static PullRequestDescriptor PrDescriptor(string prId) =>
        new()
        {
            PrId = prId,
            HeadSha = $"head-{prId}",
            BaseSha = "base-sha",
            TriggerWatermark = "wm-1",
            LifecycleState = PrLifecycleState.Open,
        };

    /// <summary>A discovered PR carrying the author's prose — the ranking input schema v7 persists.</summary>
    private static PullRequestDescriptor DescribedDescriptor(string prId, string? title, string? description) =>
        new()
        {
            PrId = prId,
            HeadSha = $"head-{prId}",
            BaseSha = "base-sha",
            TriggerWatermark = "wm-1",
            LifecycleState = PrLifecycleState.Open,
            Title = title,
            Description = description,
        };

    private static PullRequestDescriptor DatedDescriptor(
        string prId,
        DateTimeOffset? updatedAt,
        DateTimeOffset? createdAt = null
    ) =>
        new()
        {
            PrId = prId,
            HeadSha = $"head-{prId}",
            BaseSha = "base-sha",
            TriggerWatermark = "wm-1",
            LifecycleState = PrLifecycleState.Open,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
        };

    private static RepoIdentity SampleRepo() =>
        new()
        {
            Provider = Provider,
            OrgOrOwner = "achieveai",
            RepoName = "LmDotnetTools",
            RepoStableId = "R_node_123",
        };

    private static ReviewRun SeedFor(long repoId, string prId) =>
        new()
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
