using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Persistence.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Agents;

/// <summary>
/// The provider-neutral shared-deadline barrier (<see cref="ReviewSubAgentCompletionBarrier.WaitAsync"/>)
/// is the single gate every review-completion source (in-process, S2S — Task 4) waits behind before the
/// daemon is allowed to synthesize/post. It never posts, judges, or fabricates a budget; it only reports
/// when a scripted <see cref="IReviewSubAgentCompletionSource"/> has produced two IDENTICAL all-terminal
/// snapshots separated by the configured quiet period, capped throughout by the ONE absolute deadline the
/// caller supplies. All waits are driven by <see cref="FakeTimeProvider"/> — no real sleeps anywhere here.
/// </summary>
public sealed class ReviewSubAgentCompletionBarrierTests : LoggingTestBase
{
    public ReviewSubAgentCompletionBarrierTests(ITestOutputHelper output)
        : base(output) { }

    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(2);

    private ReviewSubAgentCompletionBarrier CreateBarrier(
        ScriptedCompletionSource source,
        FakeTimeProvider clock,
        TimeSpan? quietPeriod = null) =>
        new(source, quietPeriod ?? QuietPeriod, LoggerFactory.CreateLogger<ReviewSubAgentCompletionBarrier>(), clock);

    /// <summary>
    /// A clock that also reports WHEN the code under test has parked on its next wait. The barrier polls
    /// and then awaits <c>Task.Delay(interval, clock)</c>, whose continuation resumes on the thread pool —
    /// so a pump that advances blindly can move the clock before the barrier has registered the timer it is
    /// about to wait on, and under a busy pool it can burn every step that way. Registration is the one
    /// observable moment that says "the barrier is now waiting", which turns that race into a handshake
    /// with no real sleeping anywhere.
    /// </summary>
    private sealed class ObservableFakeClock(DateTimeOffset start) : FakeTimeProvider(start)
    {
        private readonly SemaphoreSlim _waitsRegistered = new(0);

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            _waitsRegistered.Release();
            return timer;
        }

        /// <summary>
        /// Completes once a wait has been registered that this method has not already accounted for. The
        /// semaphore COUNTS registrations rather than latching, so one that happened before the call is
        /// still observed. <paramref name="guard"/> only bounds a genuine hang — on the happy path the
        /// count is already there and the wait completes synchronously.
        /// </summary>
        public Task<bool> WaitForNextWaitAsync(TimeSpan guard) => _waitsRegistered.WaitAsync(guard);
    }

    private static ReviewRun TestRun() =>
        new()
        {
            RepoId = 1,
            PrId = "42",
            HeadSha = "head-sha",
            BaseSha = "base-sha",
            TriggerWatermark = "watermark-1",
            ReviewKind = "full",
            VariantId = "primary",
            Mode = "collect-only",
            Stage = ReviewStage.Reviewed,
            WorkflowStatus = WorkflowStatus.Running,
            PrLifecycleState = PrLifecycleState.Open,
        };

    private static ReviewSubAgentNode Node(
        string agentId,
        string parentThreadId,
        int depth,
        ReviewSubAgentStatus status,
        string threadId = "") =>
        new()
        {
            AgentId = agentId,
            ThreadId = string.IsNullOrEmpty(threadId) ? $"thread-{agentId}" : threadId,
            ParentThreadId = parentThreadId,
            Depth = depth,
            Status = status,
            Template = "reviewer",
            Name = null,
            TerminalAtUtc = null,
            FailureCode = null,
        };

    private static Task NoopValidator(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Drives the clock forward one wait step at a time until the awaited task settles, or fails
    /// loudly once it has taken more steps than the scripted source could possibly need.</summary>
    private static async Task<T> PumpUntilSettledAsync<T>(
        Task<T> task,
        ObservableFakeClock clock,
        TimeSpan step,
        int maxSteps = 50)
    {
        // Bounds a genuine hang (the barrier stopped waiting AND stopped settling). It is never reached on
        // the happy path, where the registration has already been counted by the time it is asked for.
        var hangGuard = TimeSpan.FromSeconds(30);

        for (var i = 0; i < maxSteps && !task.IsCompleted; i++)
        {
            // Advance only once the barrier is parked on its next wait, so no step can be spent on a timer
            // that has not been registered yet.
            var parked = clock.WaitForNextWaitAsync(hangGuard);
            if (ReferenceEquals(await Task.WhenAny(task, parked), task))
            {
                break;
            }

            if (!await parked)
            {
                throw new InvalidOperationException(
                    $"The barrier neither settled nor registered another wait within {hangGuard}.");
            }

            clock.Advance(step);
        }

        if (!task.IsCompleted)
        {
            throw new InvalidOperationException(
                $"The barrier never settled within {maxSteps} wait steps of {step} — it is not re-polling as expected.");
        }

        return await task;
    }

    [Fact]
    public async Task WaitAsync_ThreeChildrenSettleInDifferentOrders_StaysClosedUntilAllTerminal()
    {
        // Brief bullet 1: three running children resolve in different orders; the barrier stays closed
        // (never returns) until every one of them is terminal, regardless of the settling order.
        var clock = new ObservableFakeClock(DateTimeOffset.UtcNow);
        var run = TestRun();
        var source = new ScriptedCompletionSource(
            new ReviewSubAgentTreeSnapshot(
                [
                    Node("a", "root", 1, ReviewSubAgentStatus.Running),
                    Node("b", "root", 1, ReviewSubAgentStatus.Running),
                    Node("c", "root", 1, ReviewSubAgentStatus.Running),
                ]
            ),
            new ReviewSubAgentTreeSnapshot(
                [
                    Node("a", "root", 1, ReviewSubAgentStatus.Completed),
                    Node("b", "root", 1, ReviewSubAgentStatus.Running),
                    Node("c", "root", 1, ReviewSubAgentStatus.Running),
                ]
            ),
            new ReviewSubAgentTreeSnapshot(
                [
                    Node("a", "root", 1, ReviewSubAgentStatus.Completed),
                    Node("b", "root", 1, ReviewSubAgentStatus.Completed),
                    Node("c", "root", 1, ReviewSubAgentStatus.Running),
                ]
            ),
            new ReviewSubAgentTreeSnapshot(
                [
                    Node("a", "root", 1, ReviewSubAgentStatus.Completed),
                    Node("b", "root", 1, ReviewSubAgentStatus.Completed),
                    Node("c", "root", 1, ReviewSubAgentStatus.Completed),
                ]
            ),
            // Second identical all-terminal snapshot (stability confirmation) — same shape as the previous one.
            new ReviewSubAgentTreeSnapshot(
                [
                    Node("a", "root", 1, ReviewSubAgentStatus.Completed),
                    Node("b", "root", 1, ReviewSubAgentStatus.Completed),
                    Node("c", "root", 1, ReviewSubAgentStatus.Completed),
                ]
            )
        );
        var barrier = CreateBarrier(source, clock);
        var deadline = clock.GetUtcNow() + TimeSpan.FromMinutes(30);

        var task = barrier.WaitAsync(run, "root", deadline, NoopValidator, CancellationToken.None);
        var result = await PumpUntilSettledAsync(task, clock, TimeSpan.FromSeconds(5));

        result.Nodes.Should().HaveCount(3);
        result.Nodes.Should().OnlyContain(n => n.Status == ReviewSubAgentStatus.Completed);
        source.CallCount.Should().BeGreaterThanOrEqualTo(5, "the barrier must keep re-polling while any child is still running");
    }

    [Fact]
    public async Task WaitAsync_ErrorAndStoppedAreTerminal_RunningAndUnknownBlock_MixedForegroundBackgroundBlocks()
    {
        // Brief bullet 2: Error/Stopped are terminal (do not block), Running/Unknown block, and a snapshot
        // mixing a terminal "foreground" node with a still-running "background" node stays blocked.
        var clock = new ObservableFakeClock(DateTimeOffset.UtcNow);
        var run = TestRun();
        var source = new ScriptedCompletionSource(
            // Mixed: foreground already Error (terminal) but a background node is Running — must block.
            new ReviewSubAgentTreeSnapshot(
                [
                    Node("fg", "root", 1, ReviewSubAgentStatus.Error),
                    Node("bg", "root", 1, ReviewSubAgentStatus.Running),
                ]
            ),
            // Background settles to Stopped (also terminal); Unknown still blocks.
            new ReviewSubAgentTreeSnapshot(
                [
                    Node("fg", "root", 1, ReviewSubAgentStatus.Error),
                    Node("bg", "root", 1, ReviewSubAgentStatus.Unknown),
                ]
            ),
            new ReviewSubAgentTreeSnapshot(
                [
                    Node("fg", "root", 1, ReviewSubAgentStatus.Error),
                    Node("bg", "root", 1, ReviewSubAgentStatus.Stopped),
                ]
            ),
            new ReviewSubAgentTreeSnapshot(
                [
                    Node("fg", "root", 1, ReviewSubAgentStatus.Error),
                    Node("bg", "root", 1, ReviewSubAgentStatus.Stopped),
                ]
            )
        );
        var barrier = CreateBarrier(source, clock);
        var deadline = clock.GetUtcNow() + TimeSpan.FromMinutes(30);

        var task = barrier.WaitAsync(run, "root", deadline, NoopValidator, CancellationToken.None);
        var result = await PumpUntilSettledAsync(task, clock, TimeSpan.FromSeconds(5));

        result
            .Nodes.Should()
            .Contain(n => n.AgentId == "fg" && n.Status == ReviewSubAgentStatus.Error)
            .And.Contain(n => n.AgentId == "bg" && n.Status == ReviewSubAgentStatus.Stopped);
        source.CallCount.Should().BeGreaterThanOrEqualTo(4);
    }

    [Fact]
    public async Task WaitAsync_SecondIdenticalTerminalSnapshotOpens_EmptyTreeAlsoNeedsTwoSnapshots()
    {
        // Brief bullet 3: an empty descendant tree is vacuously "all terminal" but STILL requires two
        // identical observations separated by the quiet period before the barrier opens — a single empty
        // observation must not short-circuit.
        var clock = new ObservableFakeClock(DateTimeOffset.UtcNow);
        var run = TestRun();
        var source = new ScriptedCompletionSource(
            new ReviewSubAgentTreeSnapshot([]),
            new ReviewSubAgentTreeSnapshot([])
        );
        var barrier = CreateBarrier(source, clock);
        var deadline = clock.GetUtcNow() + TimeSpan.FromMinutes(30);

        var task = barrier.WaitAsync(run, "root", deadline, NoopValidator, CancellationToken.None);

        // Immediately after the first (only) observation, the barrier must NOT have opened yet — it needs
        // the quiet-period-separated second look. Advancing by less than the quiet period must not settle it.
        clock.Advance(TimeSpan.FromSeconds(1));
        task.IsCompleted.Should().BeFalse("one empty snapshot alone must not open the barrier");

        var result = await PumpUntilSettledAsync(task, clock, TimeSpan.FromSeconds(2));

        result.Nodes.Should().BeEmpty();
        source.CallCount.Should().BeGreaterThanOrEqualTo(2);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("reparent")]
    [InlineData("status")]
    public async Task WaitAsync_RosterChangeBetweenObservations_ResetsStability(string change)
    {
        // Brief bullet 4: roster addition, removal, parent change, or status change between the candidate
        // and what would otherwise be the confirming snapshot resets stability — the barrier must NOT open
        // on that pair; it needs a fresh pair of truly identical observations afterwards.
        var clock = new ObservableFakeClock(DateTimeOffset.UtcNow);
        var run = TestRun();

        var baseline = new ReviewSubAgentTreeSnapshot(
            [
                Node("a", "root", 1, ReviewSubAgentStatus.Completed),
                Node("b", "root", 1, ReviewSubAgentStatus.Completed),
            ]
        );
        var changed = change switch
        {
            "add" => new ReviewSubAgentTreeSnapshot(
                [
                    Node("a", "root", 1, ReviewSubAgentStatus.Completed),
                    Node("b", "root", 1, ReviewSubAgentStatus.Completed),
                    Node("c", "root", 1, ReviewSubAgentStatus.Completed),
                ]
            ),
            "remove" => new ReviewSubAgentTreeSnapshot([Node("a", "root", 1, ReviewSubAgentStatus.Completed)]),
            "reparent" => new ReviewSubAgentTreeSnapshot(
                [
                    Node("a", "root", 1, ReviewSubAgentStatus.Completed),
                    Node("b", "a", 2, ReviewSubAgentStatus.Completed, threadId: "thread-b"),
                ]
            ),
            "status" => new ReviewSubAgentTreeSnapshot(
                [
                    Node("a", "root", 1, ReviewSubAgentStatus.Completed),
                    Node("b", "root", 1, ReviewSubAgentStatus.Error),
                ]
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };

        // baseline (candidate) -> changed (breaks stability, becomes the new candidate) -> baseline again
        // (breaks stability again) -> baseline (finally confirms baseline as stable).
        var source = new ScriptedCompletionSource(baseline, changed, baseline, baseline);
        var barrier = CreateBarrier(source, clock);
        var deadline = clock.GetUtcNow() + TimeSpan.FromMinutes(30);

        var task = barrier.WaitAsync(run, "root", deadline, NoopValidator, CancellationToken.None);
        var result = await PumpUntilSettledAsync(task, clock, TimeSpan.FromSeconds(5));

        result.Nodes.Should().HaveCount(2);
        result.Nodes.Should().Contain(n => n.AgentId == "a" && n.ParentThreadId == "root" && n.Depth == 1);
        result.Nodes.Should().Contain(n => n.AgentId == "b" && n.ParentThreadId == "root" && n.Depth == 1);
        source.CallCount.Should().BeGreaterThanOrEqualTo(4, "the reset must force at least one extra confirmation round-trip");
    }

    [Fact]
    public async Task WaitAsync_PersistedDescendantBehindTerminalAncestor_RemainsPartOfIdentityAndBlocks()
    {
        // Brief bullet 5: a grandchild that is still nonterminal keeps the barrier closed even though its
        // own parent (an intermediate descendant) already reached a terminal status — completeness is
        // evaluated per-node across the whole flattened roster, not rolled up by ancestor.
        var clock = new ObservableFakeClock(DateTimeOffset.UtcNow);
        var run = TestRun();
        var source = new ScriptedCompletionSource(
            new ReviewSubAgentTreeSnapshot(
                [
                    Node("child", "root", 1, ReviewSubAgentStatus.Completed),
                    Node("grandchild", "thread-child", 2, ReviewSubAgentStatus.Running),
                ]
            ),
            new ReviewSubAgentTreeSnapshot(
                [
                    Node("child", "root", 1, ReviewSubAgentStatus.Completed),
                    Node("grandchild", "thread-child", 2, ReviewSubAgentStatus.Completed),
                ]
            ),
            new ReviewSubAgentTreeSnapshot(
                [
                    Node("child", "root", 1, ReviewSubAgentStatus.Completed),
                    Node("grandchild", "thread-child", 2, ReviewSubAgentStatus.Completed),
                ]
            )
        );
        var barrier = CreateBarrier(source, clock);
        var deadline = clock.GetUtcNow() + TimeSpan.FromMinutes(30);

        var task = barrier.WaitAsync(run, "root", deadline, NoopValidator, CancellationToken.None);
        var result = await PumpUntilSettledAsync(task, clock, TimeSpan.FromSeconds(5));

        result.Nodes.Should().HaveCount(2);
        result
            .Nodes.Should()
            .Contain(n => n.AgentId == "grandchild" && n.Depth == 2 && n.Status == ReviewSubAgentStatus.Completed);
    }

    [Fact]
    public async Task WaitAsync_ResumedDeadlineWithFiveMinutesRemaining_ExpiresAtFiveMinutesNotThirty()
    {
        // Brief bullet 6 / "one absolute deadline" decision: WaitAsync obeys ONLY the caller-supplied
        // absolute deadline. A caller resuming after 25 of an original 30-minute budget passes a deadline
        // only 5 minutes out — the barrier must throw at +5 minutes, never fabricate/restore a 30-minute
        // window of its own.
        var clock = new ObservableFakeClock(DateTimeOffset.UtcNow);
        var run = TestRun();
        var source = new ScriptedCompletionSource(
            new ReviewSubAgentTreeSnapshot([Node("a", "root", 1, ReviewSubAgentStatus.Running)])
        );
        var barrier = CreateBarrier(source, clock);
        var deadline = clock.GetUtcNow() + TimeSpan.FromMinutes(5);

        var task = barrier.WaitAsync(run, "root", deadline, NoopValidator, CancellationToken.None);

        // One single big jump proves the barrier's internal backoff/quiet waits are all capped by the
        // remaining time and cascade to the deadline check without overshooting (see design-notes.md §5).
        clock.Advance(TimeSpan.FromMinutes(5));

        await FluentActions
            .Awaiting(() => task)
            .Should()
            .ThrowAsync<ReviewBarrierDeadlineException>();
    }

    [Fact]
    public async Task WaitAsync_LifecycleValidatorFailure_AbortsBeforeBarrierOpens()
    {
        // Brief bullet 7: lifecycle/head validation runs right before a confirmed terminal candidate is
        // accepted. A failing validator means the barrier NEVER opens/returns successfully — the failure
        // propagates instead, even though two identical all-terminal snapshots were observed.
        var clock = new ObservableFakeClock(DateTimeOffset.UtcNow);
        var run = TestRun();
        var source = new ScriptedCompletionSource(
            new ReviewSubAgentTreeSnapshot([Node("a", "root", 1, ReviewSubAgentStatus.Completed)]),
            new ReviewSubAgentTreeSnapshot([Node("a", "root", 1, ReviewSubAgentStatus.Completed)])
        );
        var barrier = CreateBarrier(source, clock);
        var deadline = clock.GetUtcNow() + TimeSpan.FromMinutes(30);
        var validatorCalls = 0;

        Task FailingValidator(CancellationToken ct)
        {
            validatorCalls++;
            throw new InvalidOperationException("PR head changed since collection started.");
        }

        var task = barrier.WaitAsync(run, "root", deadline, FailingValidator, CancellationToken.None);

        var act = () => PumpUntilSettledAsync(task, clock, TimeSpan.FromSeconds(5));
        await act.Should().ThrowAsync<InvalidOperationException>();
        validatorCalls.Should().Be(1, "the validator gates the open exactly once, at the confirmed candidate");
    }

    /// <summary>Test double returning a pre-programmed sequence of snapshots, one per call, holding on the
    /// final entry once exhausted (mirrors a source whose underlying state stopped changing).</summary>
    private sealed class ScriptedCompletionSource : IReviewSubAgentCompletionSource
    {
        private readonly IReadOnlyList<ReviewSubAgentTreeSnapshot> _snapshots;
        private int _index;

        public ScriptedCompletionSource(params ReviewSubAgentTreeSnapshot[] snapshots)
        {
            _snapshots = snapshots;
        }

        public int CallCount { get; private set; }

        public Task<ReviewSubAgentTreeSnapshot> GetSnapshotAsync(ReviewRun run, string parentThreadId, CancellationToken ct)
        {
            CallCount++;
            var next = _snapshots[Math.Min(_index, _snapshots.Count - 1)];
            if (_index < _snapshots.Count - 1)
            {
                _index++;
            }

            return Task.FromResult(next);
        }
    }
}
