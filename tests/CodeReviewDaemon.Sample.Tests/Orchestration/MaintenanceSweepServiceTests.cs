using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// The daemon's maintenance cadence, which exists because BOTH orderings of the old single loop were wrong
/// for the same reason. Sequenced AFTER the poll body, the PR-lifecycle sweep waited on a cycle that reviews
/// every PR inline and so never finishes — it had not executed once in the daemon's life, and the Knowledge
/// Base was empty because of it. Moved BEFORE the poll body, it ran, but its 125-PR first backlog then held
/// off every review for the roughly two hours it took. Maintenance and work were serialized into one loop,
/// so whichever ran first starved the other; the fix is that they do not share a loop at all.
/// <para>
/// All waits are driven by <see cref="FakeTimeProvider"/> — there are no real sleeps here.
/// </para>
/// </summary>
public sealed class MaintenanceSweepServiceTests : LoggingTestBase
{
    private const string Provider = "github";
    private const string Scope = "achieveai/lmdotnettools:open-prs";
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    public MaintenanceSweepServiceTests(ITestOutputHelper output)
        : base(output) { }

    /// <summary>
    /// Startup behaviour, and not a detail: the live daemon's Knowledge Base went from empty to its first
    /// entries 27 seconds after a restart, because the sweep runs on entry rather than after one interval.
    /// Waiting out the interval first would leave a freshly restarted daemon doing no maintenance for the
    /// whole of it — precisely the window in which an operator is watching to see whether the restart worked.
    /// </summary>
    [Fact]
    public async Task Sweep_runs_immediately_at_startup_rather_than_after_the_first_interval()
    {
        var clock = new ObservableFakeClock(DateTimeOffset.UtcNow);
        var swept = new TaskCompletionSource();
        var service = Build(
            clock,
            _ =>
            {
                swept.TrySetResult();
                return Task.CompletedTask;
            }
        );

        await service.StartAsync(CancellationToken.None);
        try
        {
            // The clock is NEVER advanced in this test. If the loop delayed before its first sweep, this
            // would hang out its guard and fail, which is the whole point.
            var reached = await Task.WhenAny(swept.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            reached
                .Should()
                .Be(
                    swept.Task,
                    "maintenance must start on entry, not one interval later — a 15-minute silent window after "
                        + "every restart is exactly when someone is looking to see whether the restart helped"
                );
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The overlap guard. A sweep merges notes branches and deletes them; two running at once would have two
    /// processes resolving the same branch, and the first backlog is long enough (125 PRs, about a minute
    /// each) that several intervals elapse inside a single sweep. A tick that arrives mid-sweep has to be
    /// dropped, never queued — a queue would fire the whole missed backlog the instant the sweep returned,
    /// which is worse than the overlap it was trying to avoid.
    /// </summary>
    [Fact]
    public async Task Sweep_never_runs_concurrently_with_itself_however_long_it_takes()
    {
        var clock = new ObservableFakeClock(DateTimeOffset.UtcNow);
        var release = new TaskCompletionSource();
        var started = 0;
        var concurrent = 0;
        var peak = 0;
        var firstStarted = new TaskCompletionSource();

        var service = Build(
            clock,
            async ct =>
            {
                var now = Interlocked.Increment(ref concurrent);
                _ = Interlocked.Exchange(ref peak, Math.Max(Volatile.Read(ref peak), now));
                if (Interlocked.Increment(ref started) == 1)
                {
                    firstStarted.TrySetResult();
                    await release.Task.ConfigureAwait(false);
                }

                _ = Interlocked.Decrement(ref concurrent);
            }
        );

        await service.StartAsync(CancellationToken.None);
        try
        {
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Five intervals' worth of ticks arrive while the first sweep is still working.
            for (var i = 0; i < 5; i++)
            {
                clock.Advance(Interval);
            }

            Volatile
                .Read(ref started)
                .Should()
                .Be(
                    1,
                    "no second sweep may begin while the first is still merging branches — the backlog sweep "
                        + "outlasts several intervals, so this is the normal case, not an edge one"
                );

            release.TrySetResult();

            // Let the released sweep finish and the loop park on its next wait.
            _ = await clock.WaitForNextWaitAsync(TimeSpan.FromSeconds(5));
            Volatile
                .Read(ref peak)
                .Should()
                .Be(1, "observed concurrency of 2 means two processes were resolving the same notes branches");
            Volatile
                .Read(ref started)
                .Should()
                .BeLessThan(
                    5,
                    "the ticks that arrived mid-sweep are DROPPED, not queued — a queue would discharge every "
                        + "missed interval back-to-back the moment the long sweep returned"
                );
        }
        finally
        {
            release.TrySetResult();
            await service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Degrade-not-throw, carried over from the poll loop's try/catch. The sweeper is already best-effort per
    /// PR, so an exception reaching this loop means something unhandled — and killing the daemon's only
    /// maintenance for the rest of the process lifetime over one bad cycle is how a transient provider error
    /// becomes a permanently cold Knowledge Base.
    /// </summary>
    [Fact]
    public async Task A_throwing_sweep_is_logged_and_the_next_one_still_runs()
    {
        var clock = new ObservableFakeClock(DateTimeOffset.UtcNow);
        var calls = 0;
        var secondRan = new TaskCompletionSource();
        var service = Build(
            clock,
            _ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    throw new InvalidOperationException("provider unavailable");
                }

                secondRan.TrySetResult();
                return Task.CompletedTask;
            }
        );

        await service.StartAsync(CancellationToken.None);
        try
        {
            _ = await clock.WaitForNextWaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(Interval);

            var reached = await Task.WhenAny(secondRan.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            reached
                .Should()
                .Be(
                    secondRan.Task,
                    "one failed cycle must not end maintenance for the process lifetime — that is how a "
                        + "transient provider error turns into a permanently cold Knowledge Base"
                );
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The property this suite inherits from <c>PrPollingServiceTests</c>, where it was pinned against the
    /// old single loop as <c>The_lifecycle_sweep_runs_even_while_the_poll_body_is_still_working</c>. It moved
    /// rather than retired: separate services obviously cannot block each other, and "obviously" is what
    /// stops being true the day someone reintroduces a seam between them. So it is asserted across the real
    /// composition — both services running, a poll body that never returns — which is the shape production
    /// has and the shape that was broken twice.
    /// <para>
    /// The original evidence, unchanged: <c>SweepAsync</c>'s unguarded entry log appeared zero times in 3,603
    /// log lines; all 123 reviewed PRs were still 'Open'; not one knowledge artifact existed; the Knowledge
    /// Base table of contents listed 0 entries; and every review brief reported prior-knowledge=0.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_lifecycle_sweep_runs_even_while_the_poll_body_is_still_working()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var clock = new ObservableFakeClock(DateTimeOffset.UtcNow);

        // The poll body never finishes on its own — the live shape, compressed.
        var blocked = new TaskCompletionSource();
        var provider = new MockPrProvider(Provider, [PrDescriptor("118")], NextCursor());
        var orchestrator = new PrOrchestrator(
            store,
            new RecordingStageExecutor(gate: blocked.Task),
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

        var swept = new TaskCompletionSource();
        var sweeps = Build(
            clock,
            _ =>
            {
                swept.TrySetResult();
                return Task.CompletedTask;
            }
        );

        await poller.StartAsync(CancellationToken.None);
        await sweeps.StartAsync(CancellationToken.None);
        try
        {
            var reached = await Task.WhenAny(swept.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            reached
                .Should()
                .Be(
                    swept.Task,
                    "the sweep must not wait on a poll cycle that reviews every PR inline — that cycle takes "
                        + "hours in production, which is why the sweep had never run once"
                );
        }
        finally
        {
            blocked.TrySetResult();
            await sweeps.StopAsync(CancellationToken.None);
            await poller.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The mirror of the test above, and the regression this task exists for. Moving the sweep ahead of the
    /// poll body fixed the sweep and broke reviewing: the live daemon reviewed nothing for the roughly two
    /// hours its 125-PR first backlog took, because the poll body could not start until the sweep returned.
    /// Neither direction of starvation is acceptable, so both are pinned.
    /// </summary>
    [Fact]
    public async Task The_poll_body_runs_even_while_a_long_sweep_is_still_working()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var clock = new ObservableFakeClock(DateTimeOffset.UtcNow);

        // The sweep never finishes on its own — the 125-PR backlog, compressed.
        var blocked = new TaskCompletionSource();
        var sweeps = Build(clock, _ => blocked.Task);

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

        await sweeps.StartAsync(CancellationToken.None);
        await poller.StartAsync(CancellationToken.None);
        try
        {
            var repoId = store.EnsureRepo(SampleRepo());
            var reviewed = await WaitUntilAsync(
                () => store.CreateOrGetReviewRun(SeedFor(repoId, "118")).Stage == ReviewStage.Posted,
                TimeSpan.FromSeconds(5)
            );

            reviewed
                .Should()
                .BeTrue(
                    "a maintenance backlog must not hold off reviewing — moving the sweep ahead of the poll "
                        + "body cost the live daemon roughly two hours of reviewing nothing at all"
                );
        }
        finally
        {
            blocked.TrySetResult();
            await poller.StopAsync(CancellationToken.None);
            await sweeps.StopAsync(CancellationToken.None);
        }
    }

    private MaintenanceSweepService Build(TimeProvider clock, Func<CancellationToken, Task> sweepAsync) =>
        new("PR-lifecycle", sweepAsync, Interval, LoggerFactory.CreateLogger<MaintenanceSweepService>(), clock);

    /// <summary>Polls <paramref name="condition"/> until it holds or <paramref name="guard"/> elapses. The
    /// services under test run on their own tasks, so the observable effect lands asynchronously.</summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan guard)
    {
        var deadline = DateTime.UtcNow + guard;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return condition();
    }

    /// <summary>
    /// A clock that also reports WHEN the code under test has parked on its next wait. The loop sweeps and
    /// then awaits <c>Task.Delay(interval, clock)</c>, whose continuation resumes on the thread pool — so a
    /// test that advances blindly can move the clock before the loop has registered the timer it is about to
    /// wait on. Registration is the one observable moment that says "the loop is now waiting", which turns
    /// that race into a handshake with no real sleeping. Same device as
    /// <c>ReviewSubAgentCompletionBarrierTests</c>, for the same reason.
    /// </summary>
    private sealed class ObservableFakeClock(DateTimeOffset start) : FakeTimeProvider(start)
    {
        private readonly SemaphoreSlim _waitsRegistered = new(0);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            _ = _waitsRegistered.Release();
            return timer;
        }

        /// <summary>Completes once a wait has been registered that this method has not already accounted
        /// for. The semaphore COUNTS registrations rather than latching, so one that happened before the
        /// call is still observed.</summary>
        public Task<bool> WaitForNextWaitAsync(TimeSpan guard) => _waitsRegistered.WaitAsync(guard);
    }

    private static RepoIdentity SampleRepo() =>
        new()
        {
            Provider = Provider,
            OrgOrOwner = "achieveai",
            RepoName = "LmDotnetTools",
            RepoStableId = "R_node_123",
        };

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

    private static OpaqueCursor NextCursor() =>
        new()
        {
            Provider = Provider,
            Scope = Scope,
            CursorVersion = PrPollingService.CursorVersion,
            CursorPayload = "{\"page\":2}",
            HighWaterMark = "2026-06-01T00:00:00Z",
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
