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

    [Fact]
    public async Task Draft_and_unknown_are_excluded_without_consuming_cap_or_blocking_ready_work()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var executor = new RecordingStageExecutor();
        var orchestrator = new PrOrchestrator(store, executor, LoggerFactory.CreateLogger<PrOrchestrator>());
        var provider = new MockPrProvider(
            Provider,
            [
                PrDescriptor("draft") with
                {
                    DraftState = PrDraftState.Draft,
                },
                PrDescriptor("unknown") with
                {
                    DraftState = PrDraftState.Unknown,
                },
                PrDescriptor("ready-1"),
                PrDescriptor("ready-2"),
            ],
            NextCursor()
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
            LoggerFactory.CreateLogger<PrPollingService>(),
            maxReviewsPerTargetPerCycle: 2
        );

        await poller.PollOnceAsync(CancellationToken.None);

        executor.ExecutedStages.Should().HaveCount(8, "both ready PRs execute all four stages");
        var repoId = store.EnsureRepo(SampleRepo());
        store
            .UpdateIncompletePrDraftState(repoId, "draft", PrDraftState.Ready)
            .Should()
            .Be(0, "a draft observation creates no run");
        store
            .UpdateIncompletePrDraftState(repoId, "unknown", PrDraftState.Ready)
            .Should()
            .Be(0, "an unknown observation creates no run");
        store
            .ReadCursor(Provider, Scope, PrPollingService.CursorVersion)
            .ShouldResync.Should()
            .BeFalse("excluded PRs do not hold the cursor or consume the ready-work cap");
    }

    [Fact]
    public async Task Ready_list_rows_deferred_at_preflight_do_not_starve_later_ready_rows_across_capped_cycles()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var prs = new[]
        {
            PrDescriptor("draft-at-preflight"),
            PrDescriptor("unknown-at-preflight"),
            PrDescriptor("ready-1"),
            PrDescriptor("ready-2"),
        };
        var provider = new PerPrStatusProvider(
            Provider,
            prs,
            NextCursor(),
            new Dictionary<string, PrStatus>
            {
                ["draft-at-preflight"] = new(PrLifecycle.Open, PrDraftState.Draft),
                ["unknown-at-preflight"] = new(PrLifecycle.Open, PrDraftState.Unknown),
            }
        );
        var executor = new RecordingStageExecutor();
        var orchestrator = new PrOrchestrator(
            store,
            executor,
            LoggerFactory.CreateLogger<PrOrchestrator>(),
            providers: [provider]
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
            LoggerFactory.CreateLogger<PrPollingService>(),
            maxReviewsPerTargetPerCycle: 1
        );

        await poller.PollOnceAsync(CancellationToken.None);

        var repoId = store.EnsureRepo(SampleRepo());
        store.CreateOrGetReviewRun(SeedFor(repoId, "ready-1")).Stage.Should().Be(ReviewStage.Posted);
        store.CreateOrGetReviewRun(SeedFor(repoId, "ready-2")).Stage.Should().Be(ReviewStage.Discovered);
        store
            .ReadCursor(Provider, Scope, PrPollingService.CursorVersion)
            .ShouldResync.Should()
            .BeTrue("one eligible attempt consumed the cap while later ready work remains");

        await poller.PollOnceAsync(CancellationToken.None);

        store
            .CreateOrGetReviewRun(SeedFor(repoId, "ready-2"))
            .Stage.Should()
            .Be(ReviewStage.Posted, "deferred rows and the already-complete row must not permanently consume the cap");
        executor.ExecutedStages.Should().HaveCount(8, "exactly the two genuinely eligible PRs ran stages");
        store
            .ReadCursor(Provider, Scope, PrPollingService.CursorVersion)
            .ShouldResync.Should()
            .BeFalse("the cursor advances after the second eligible attempt drains the page");
    }

    [Fact]
    public async Task Draft_only_page_advances_and_a_later_ready_observation_creates_work()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var draft = new MockPrProvider(
            Provider,
            [PrDescriptor("118") with { DraftState = PrDraftState.Draft }],
            NextCursor()
        );
        await BuildPoller(store, draft).PollOnceAsync(CancellationToken.None);

        store.ReadCursor(Provider, Scope, PrPollingService.CursorVersion).ShouldResync.Should().BeFalse();
        var repoId = store.EnsureRepo(SampleRepo());
        store.UpdateIncompletePrDraftState(repoId, "118", PrDraftState.Ready).Should().Be(0);

        var ready = new MockPrProvider(Provider, [PrDescriptor("118")], NextCursor());
        await BuildPoller(store, ready).PollOnceAsync(CancellationToken.None);

        store.CreateOrGetReviewRun(SeedFor(repoId, "118")).Stage.Should().Be(ReviewStage.Posted);
    }

    [Fact]
    public async Task Existing_incomplete_run_is_deferred_by_a_draft_observation_without_execution()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());
        var existing = store.CreateOrGetReviewRun(
            SeedFor(repoId, "118") with
            {
                Stage = ReviewStage.ContextReady,
                WorkflowStatus = WorkflowStatus.RetryPending,
            }
        );
        var executor = new RecordingStageExecutor();
        var orchestrator = new PrOrchestrator(store, executor, LoggerFactory.CreateLogger<PrOrchestrator>());
        var provider = new MockPrProvider(
            Provider,
            [PrDescriptor("118") with { DraftState = PrDraftState.Draft }],
            NextCursor()
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

        executor.ExecutedStages.Should().BeEmpty();
        store.GetReviewRun(existing.Id)!.PrDraftState.Should().Be(PrDraftState.Draft);
        store.GetReviewRun(existing.Id)!.Stage.Should().Be(ReviewStage.ContextReady);
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

    // The PR-lifecycle sweep no longer runs on this loop, so the test that pinned it against the poll body
    // ("The_lifecycle_sweep_runs_even_while_the_poll_body_is_still_working") moved to
    // MaintenanceSweepServiceTests, where it is asserted across BOTH services with its evidence intact —
    // together with its mirror, that a long sweep no longer holds off the poll body either.

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

    private sealed class PerPrStatusProvider(
        string provider,
        IReadOnlyList<PullRequestDescriptor> pullRequests,
        OpaqueCursor nextCursor,
        IReadOnlyDictionary<string, PrStatus> statuses
    ) : IPrProvider
    {
        public string Provider { get; } = provider;

        public Task<PullRequestPage> ListOpenPullRequestsAsync(
            PrPollRequest request,
            CancellationToken cancellationToken
        ) => Task.FromResult(new PullRequestPage { PullRequests = pullRequests, NextCursor = nextCursor });

        public Task<PrStatus> GetPrStateAsync(RepoIdentity repo, string prId, CancellationToken cancellationToken) =>
            Task.FromResult(statuses.GetValueOrDefault(prId, new PrStatus(PrLifecycle.Open, PrDraftState.Ready)));
    }

    /// <summary>An <see cref="IPrProvider"/> that always throws — a poison target for isolation tests.</summary>
    private sealed class ThrowingPrProvider(string provider) : IPrProvider
    {
        public string Provider { get; } = provider;

        public Task<PullRequestPage> ListOpenPullRequestsAsync(
            PrPollRequest request,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException("simulated provider failure");

        public Task<PrStatus> GetPrStateAsync(RepoIdentity repo, string prId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated provider failure");
    }

    /// <summary>
    /// A BUSY target must not starve the targets behind it. This is the sibling of
    /// <see cref="A_poison_target_does_not_starve_the_other_targets"/> and it guards the failure that
    /// actually happens: that one anticipates an <em>exception</em> on target[0], which is caught per target,
    /// so the loop moves on. Nothing anticipated target[0] simply having a lot of work.
    /// <para>
    /// <c>PollTargetAsync</c> awaits <c>PrOrchestrator.RunAsync</c> inline for every PR it discovered, and
    /// each of those is a whole review. So the second target is not polled until the first target's entire
    /// backlog has been reviewed end to end. Live, that is measured at ~10 min per review against ~43
    /// in-window PRs — roughly 7 hours to reach target[1] — and the daemon restarts long before that, which
    /// is why four of five enabled repos have never been polled at all.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_busy_target_does_not_starve_the_targets_behind_it()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);

        const int BusyPrCount = 40;
        var busyPrs = Enumerable.Range(1, BusyPrCount).Select(i => PrDescriptor(i.ToString())).ToArray();
        var executor = new RecordingStageExecutor();
        var orchestrator = new PrOrchestrator(store, executor, LoggerFactory.CreateLogger<PrOrchestrator>());

        var busy = new MockPrProvider(Provider, busyPrs, NextCursor());
        // Snapshots how many reviews had already completed at the moment this target was first polled.
        var starved = new ObservingPrProvider(
            "azure-devops",
            new MockPrProvider("azure-devops", [PrDescriptor("999")], NextCursor()),
            () => executor.ReleaseCount
        );

        var targets = new[]
        {
            new PrPollTarget
            {
                Provider = Provider,
                Repo = SampleRepo(),
                Scope = Scope,
            },
            new PrPollTarget
            {
                Provider = "azure-devops",
                Repo = SampleRepo(),
                Scope = "ado:active",
            },
        };
        var poller = new PrPollingService(
            targets,
            [busy, starved],
            store,
            orchestrator,
            LoggerFactory.CreateLogger<PrPollingService>()
        );

        await poller.PollOnceAsync(CancellationToken.None);

        starved.ReviewsDoneAtFirstCall.Should().NotBeNull("the second target must be polled at all");
        starved
            .ReviewsDoneAtFirstCall.Should()
            .BeLessThan(
                BusyPrCount,
                "a target must not have to wait for another target's ENTIRE backlog before it is polled once; "
                    + "at production review durations that wait is hours, and the daemon restarts first"
            );
    }

    /// <summary>Wraps an <see cref="IPrProvider"/> and records an observation the first time it is polled, so
    /// a test can assert on the INTERLEAVING of the poll loop rather than only on its end state.</summary>
    private sealed class ObservingPrProvider(string provider, IPrProvider inner, Func<int> probe) : IPrProvider
    {
        public string Provider { get; } = provider;

        /// <summary>The probe's value when this provider was first asked for PRs; null if it never was.</summary>
        public int? ReviewsDoneAtFirstCall { get; private set; }

        public Task<PullRequestPage> ListOpenPullRequestsAsync(
            PrPollRequest request,
            CancellationToken cancellationToken
        )
        {
            ReviewsDoneAtFirstCall ??= probe();
            return inner.ListOpenPullRequestsAsync(request, cancellationToken);
        }

        public Task<PrStatus> GetPrStateAsync(RepoIdentity repo, string prId, CancellationToken cancellationToken) =>
            inner.GetPrStateAsync(repo, prId, cancellationToken);
    }

    /// <summary>
    /// The per-target cap and the cursor have to agree, and the dangerous direction is silent. The provider's
    /// NextCursor points past the WHOLE page, so a cycle that reviewed only the first few PRs and then saved
    /// it would step over the PRs it deliberately skipped — and because the cursor only moves forward, those
    /// PRs are never listed again. That turns a fairness fix into permanent data loss, which is strictly worse
    /// than the starvation it replaces: starved PRs are late, skipped PRs never happen.
    /// <para>
    /// The second half is the trap the first half sets. Holding the cursor put means the next cycle re-lists
    /// the same page, so the already-finished PRs at its head are seen again — and if those consumed cap slots
    /// the page could never drain. Both properties are asserted here because either alone is satisfiable by a
    /// broken implementation.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_capped_pass_holds_the_cursor_and_still_drains_the_page_across_cycles()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var prs = Enumerable
            .Range(1, PrPollingService.DefaultMaxReviewsPerTargetPerCycle + 2)
            .Select(i => PrDescriptor(i.ToString()))
            .ToArray();
        var provider = new MockPrProvider(Provider, prs, NextCursor());
        var poller = BuildPoller(store, provider);

        await poller.PollOnceAsync(CancellationToken.None);

        store
            .ReadCursor(Provider, Scope, PrPollingService.CursorVersion)
            .ShouldResync.Should()
            .BeTrue("a capped pass left PRs unreviewed on this page; advancing past them would lose them for good");
        var repoId = store.EnsureRepo(SampleRepo());
        store
            .CreateOrGetReviewRun(SeedFor(repoId, prs[^1].PrId))
            .Stage.Should()
            .Be(ReviewStage.Discovered, "the PRs past the cap were deliberately not reviewed this cycle");

        // Re-listing the same page must make progress, not spin: the finished PRs at its head cost a lookup
        // and must not consume the cap a second time.
        await poller.PollOnceAsync(CancellationToken.None);

        store
            .CreateOrGetReviewRun(SeedFor(repoId, prs[^1].PrId))
            .Stage.Should()
            .Be(ReviewStage.Posted, "the second cycle must reach the PRs the capped one left behind");
        store
            .ReadCursor(Provider, Scope, PrPollingService.CursorVersion)
            .ShouldResync.Should()
            .BeFalse("the page finally drained, so the cursor may now advance past it");
    }

    /// <summary>
    /// Rotation has to be DURABLE, not just fair within one process. The daemon restarted eight times in a
    /// single day; a loop that is fair per cycle but always re-enters at target[0] gives the later targets
    /// only whatever time is left after target[0]'s share, every time, forever. That is why four of five
    /// enabled repos had never been polled — and it is the failure mode #60 already demonstrated once, where
    /// a mechanism looked correct in a single-process test and never fired in production.
    /// <para>
    /// Here the first cycle is interrupted while the FIRST target is being polled — the case that matters,
    /// because it is the target whose backlog outlives the process. A fresh service over the same store must
    /// then begin at the SECOND target. This is what makes writing the rotation position before the work
    /// rather than after it load-bearing: on completion it would never be written for this target at all.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_interrupted_cycle_resumes_at_the_next_target_after_a_restart()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        using var cts = new CancellationTokenSource();

        var orchestrator = new PrOrchestrator(
            store,
            new RecordingStageExecutor(),
            LoggerFactory.CreateLogger<PrOrchestrator>()
        );
        var targets = new[]
        {
            new PrPollTarget
            {
                Provider = Provider,
                Repo = SampleRepo(),
                Scope = Scope,
            },
            new PrPollTarget
            {
                Provider = "azure-devops",
                Repo = SampleRepo(),
                Scope = "ado:active",
            },
        };

        // Process A: the cycle dies while target[0] is in flight, before it ever finished.
        var first = new CancellingPrProvider(
            Provider,
            new MockPrProvider(Provider, [PrDescriptor("118")], NextCursor()),
            cts
        );
        var secondA = new MockPrProvider("azure-devops", [PrDescriptor("999")], NextCursor());
        var processA = new PrPollingService(
            targets,
            [first, secondA],
            store,
            orchestrator,
            LoggerFactory.CreateLogger<PrPollingService>()
        );

        var interrupted = async () => await processA.PollOnceAsync(cts.Token);
        _ = await interrupted.Should().ThrowAsync<OperationCanceledException>();
        secondA.CallCount.Should().Be(0, "the cycle died before reaching the second target");

        // Process B: a restart. Same store, fresh service — exactly what eight restarts a day look like.
        // Both providers are wrapped so the assertion can pin the ORDER they were polled in. Reaching the
        // second target is not evidence of anything: an un-rotated cycle reaches it too, just last, which is
        // precisely the bug. Only "second target FIRST" distinguishes a durable rotation from no rotation.
        var seq = 0;
        var firstB = new ObservingPrProvider(
            Provider,
            new MockPrProvider(Provider, [PrDescriptor("118")], NextCursor()),
            () => seq++
        );
        var secondB = new ObservingPrProvider(
            "azure-devops",
            new MockPrProvider("azure-devops", [PrDescriptor("999")], NextCursor()),
            () => seq++
        );
        var processB = new PrPollingService(
            targets,
            [firstB, secondB],
            store,
            orchestrator,
            LoggerFactory.CreateLogger<PrPollingService>()
        );

        await processB.PollOnceAsync(CancellationToken.None);

        secondB
            .ReviewsDoneAtFirstCall.Should()
            .Be(0, "the restarted cycle must BEGIN at the target the interrupted one never reached");
        firstB.ReviewsDoneAtFirstCall.Should().Be(1, "the interrupted target goes to the back of the rotation");
        var repoId = store.EnsureRepo(SampleRepo());
        store
            .CreateOrGetReviewRun(SeedFor(repoId, "999"))
            .Stage.Should()
            .Be(ReviewStage.Posted, "the previously-unreachable target's PR was actually reviewed");
    }

    /// <summary>Cancels the cycle the first time it is polled, modelling a process that dies while its first
    /// target is still working — the shape that made rotation-on-completion never fire.</summary>
    private sealed class CancellingPrProvider(string provider, IPrProvider inner, CancellationTokenSource cts)
        : IPrProvider
    {
        public string Provider { get; } = provider;

        public Task<PullRequestPage> ListOpenPullRequestsAsync(
            PrPollRequest request,
            CancellationToken cancellationToken
        )
        {
            cts.Cancel();
            return inner.ListOpenPullRequestsAsync(request, cancellationToken);
        }

        public Task<PrStatus> GetPrStateAsync(RepoIdentity repo, string prId, CancellationToken cancellationToken) =>
            inner.GetPrStateAsync(repo, prId, cancellationToken);
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

    /// <summary>
    /// The poll payload is the ONE moment the daemon holds what the PR says about itself: the review runs
    /// later — minutes to hours later, and on a retry possibly after the PR has been edited, retargeted or
    /// closed. Anything not carried onto the seeded run here is gone, and the reviewer is left judging a diff
    /// with no claim to judge it against.
    /// </summary>
    [Fact]
    public async Task A_discovered_prs_stated_intent_is_carried_onto_the_run_it_seeds()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var descriptor = PrDescriptor("118") with
        {
            Author = "jane.doe@contoso.com",
            Title = "Revert the Contoso revenue report to the Q3 layout",
            Description = "Rolls back the Q4 rewrite; drill-through was broken on three pages.",
            TargetBranch = "release/2026.08",
        };
        var provider = new MockPrProvider(Provider, [descriptor], NextCursor());
        var poller = BuildPoller(store, provider);

        await poller.PollOnceAsync(CancellationToken.None);

        var repoId = store.EnsureRepo(SampleRepo());
        var run = store.CreateOrGetReviewRun(SeedFor(repoId, "118"));
        run.PrAuthor.Should().Be("jane.doe@contoso.com");
        run.PrTitle.Should().Be("Revert the Contoso revenue report to the Q3 layout");
        run.PrDescription.Should().Be("Rolls back the Q4 rewrite; drill-through was broken on three pages.");
        run.PrTargetBranch.Should().Be("release/2026.08");
    }

    /// <summary>
    /// The confidentiality trust signal must reach the run, because <c>AllowsCrossRepoCoLocation</c> reads it
    /// off the run and nothing else can. It went unwired for the daemon's whole life: measured on the NOVA
    /// store, all 138 runs carried <c>is_fork_pr=1, is_target_repo_public=1</c> — the fail-closed defaults —
    /// so the gate was unconditionally false and every configured sibling was refused. The visible symptom was
    /// 416 "submodule … is not on the allow-list" denials across 104 runs: exactly the 4 non-reviewed
    /// submodules, every run.
    /// </summary>
    [Fact]
    public async Task A_discovered_prs_trust_signal_is_carried_onto_the_run_it_seeds()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var descriptor = PrDescriptor("118") with { IsForkPr = false, IsTargetRepoPublic = false };
        var provider = new MockPrProvider(Provider, [descriptor], NextCursor());
        var poller = BuildPoller(store, provider);

        await poller.PollOnceAsync(CancellationToken.None);

        var repoId = store.EnsureRepo(SampleRepo());
        var run = store.CreateOrGetReviewRun(SeedFor(repoId, "118"));
        run.IsForkPr.Should().BeFalse("the provider positively established the head is not from a fork");
        run.IsTargetRepoPublic.Should().BeFalse("the provider positively established the target repo is private");
    }

    /// <summary>
    /// The other half of the same carry, and the more important one: a provider that could NOT determine the
    /// signal reports null, and null must land as <c>true</c> — the fail-closed value. Getting this backwards
    /// would co-locate private sibling repos beside a PR whose trust was never established, which is precisely
    /// the risk the gate exists for.
    /// </summary>
    [Fact]
    public async Task A_trust_signal_the_provider_could_not_determine_stays_fail_closed_on_the_run()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var descriptor = PrDescriptor("118") with { IsForkPr = null, IsTargetRepoPublic = null };
        var provider = new MockPrProvider(Provider, [descriptor], NextCursor());
        var poller = BuildPoller(store, provider);

        await poller.PollOnceAsync(CancellationToken.None);

        var repoId = store.EnsureRepo(SampleRepo());
        var run = store.CreateOrGetReviewRun(SeedFor(repoId, "118"));
        run.IsForkPr.Should().BeTrue("unknown trust is treated exactly like a confirmed fork PR");
        run.IsTargetRepoPublic.Should().BeTrue("unknown visibility is treated exactly like a public repo");
    }

    private static PullRequestDescriptor PrDescriptor(string prId) =>
        new()
        {
            PrId = prId,
            HeadSha = $"head-{prId}",
            BaseSha = "base-sha",
            TriggerWatermark = "wm-1",
            LifecycleState = PrLifecycleState.Open,
            DraftState = PrDraftState.Ready,
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
            DraftState = PrDraftState.Ready,
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
