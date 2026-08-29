using System.Globalization;
using System.Net;
using System.Text;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
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
        TimeSpan? quietPeriod = null
    ) => new(source, quietPeriod ?? QuietPeriod, LoggerFactory.CreateLogger<ReviewSubAgentCompletionBarrier>(), clock);

    /// <summary>
    /// Builds a barrier over any completion source with the unknown-node quiescence allowance configured,
    /// and with a logger the test can read back — the timeout path's only observable output is what it logs.
    /// </summary>
    private static ReviewSubAgentCompletionBarrier CreateBarrier(
        IReviewSubAgentCompletionSource source,
        FakeTimeProvider clock,
        TimeSpan unknownQuiescence,
        CapturingLogger<ReviewSubAgentCompletionBarrier> logger
    ) => new(source, QuietPeriod, logger, clock, unknownQuiescence);

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

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
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
        string threadId = "",
        DateTimeOffset? lastActivityUtc = null
    ) =>
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
            LastActivityUtc = lastActivityUtc,
            FailureCode = null,
        };

    private static Task NoopValidator(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Drives the clock forward one wait step at a time until the awaited task settles, or fails
    /// loudly once it has taken more steps than the scripted source could possibly need.</summary>
    private static async Task<T> PumpUntilSettledAsync<T>(
        Task<T> task,
        ObservableFakeClock clock,
        TimeSpan step,
        int maxSteps = 50
    )
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
                    $"The barrier neither settled nor registered another wait within {hangGuard}."
                );
            }

            clock.Advance(step);
        }

        if (!task.IsCompleted)
        {
            throw new InvalidOperationException(
                $"The barrier never settled within {maxSteps} wait steps of {step} — it is not re-polling as expected."
            );
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
            new ReviewSubAgentTreeSnapshot([
                Node("a", "root", 1, ReviewSubAgentStatus.Running),
                Node("b", "root", 1, ReviewSubAgentStatus.Running),
                Node("c", "root", 1, ReviewSubAgentStatus.Running),
            ]),
            new ReviewSubAgentTreeSnapshot([
                Node("a", "root", 1, ReviewSubAgentStatus.Completed),
                Node("b", "root", 1, ReviewSubAgentStatus.Running),
                Node("c", "root", 1, ReviewSubAgentStatus.Running),
            ]),
            new ReviewSubAgentTreeSnapshot([
                Node("a", "root", 1, ReviewSubAgentStatus.Completed),
                Node("b", "root", 1, ReviewSubAgentStatus.Completed),
                Node("c", "root", 1, ReviewSubAgentStatus.Running),
            ]),
            new ReviewSubAgentTreeSnapshot([
                Node("a", "root", 1, ReviewSubAgentStatus.Completed),
                Node("b", "root", 1, ReviewSubAgentStatus.Completed),
                Node("c", "root", 1, ReviewSubAgentStatus.Completed),
            ]),
            // Second identical all-terminal snapshot (stability confirmation) — same shape as the previous one.
            new ReviewSubAgentTreeSnapshot([
                Node("a", "root", 1, ReviewSubAgentStatus.Completed),
                Node("b", "root", 1, ReviewSubAgentStatus.Completed),
                Node("c", "root", 1, ReviewSubAgentStatus.Completed),
            ])
        );
        var barrier = CreateBarrier(source, clock);
        var deadline = clock.GetUtcNow() + TimeSpan.FromMinutes(30);

        var task = barrier.WaitAsync(run, "root", deadline, NoopValidator, CancellationToken.None);
        var result = await PumpUntilSettledAsync(task, clock, TimeSpan.FromSeconds(5));

        result.Nodes.Should().HaveCount(3);
        result.Nodes.Should().OnlyContain(n => n.Status == ReviewSubAgentStatus.Completed);
        source
            .CallCount.Should()
            .BeGreaterThanOrEqualTo(5, "the barrier must keep re-polling while any child is still running");
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
            new ReviewSubAgentTreeSnapshot([
                Node("fg", "root", 1, ReviewSubAgentStatus.Error),
                Node("bg", "root", 1, ReviewSubAgentStatus.Running),
            ]),
            // Background settles to Stopped (also terminal); Unknown still blocks.
            new ReviewSubAgentTreeSnapshot([
                Node("fg", "root", 1, ReviewSubAgentStatus.Error),
                Node("bg", "root", 1, ReviewSubAgentStatus.Unknown),
            ]),
            new ReviewSubAgentTreeSnapshot([
                Node("fg", "root", 1, ReviewSubAgentStatus.Error),
                Node("bg", "root", 1, ReviewSubAgentStatus.Stopped),
            ]),
            new ReviewSubAgentTreeSnapshot([
                Node("fg", "root", 1, ReviewSubAgentStatus.Error),
                Node("bg", "root", 1, ReviewSubAgentStatus.Stopped),
            ])
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

        var baseline = new ReviewSubAgentTreeSnapshot([
            Node("a", "root", 1, ReviewSubAgentStatus.Completed),
            Node("b", "root", 1, ReviewSubAgentStatus.Completed),
        ]);
        var changed = change switch
        {
            "add" => new ReviewSubAgentTreeSnapshot([
                Node("a", "root", 1, ReviewSubAgentStatus.Completed),
                Node("b", "root", 1, ReviewSubAgentStatus.Completed),
                Node("c", "root", 1, ReviewSubAgentStatus.Completed),
            ]),
            "remove" => new ReviewSubAgentTreeSnapshot([Node("a", "root", 1, ReviewSubAgentStatus.Completed)]),
            "reparent" => new ReviewSubAgentTreeSnapshot([
                Node("a", "root", 1, ReviewSubAgentStatus.Completed),
                Node("b", "a", 2, ReviewSubAgentStatus.Completed, threadId: "thread-b"),
            ]),
            "status" => new ReviewSubAgentTreeSnapshot([
                Node("a", "root", 1, ReviewSubAgentStatus.Completed),
                Node("b", "root", 1, ReviewSubAgentStatus.Error),
            ]),
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
        source
            .CallCount.Should()
            .BeGreaterThanOrEqualTo(4, "the reset must force at least one extra confirmation round-trip");
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
            new ReviewSubAgentTreeSnapshot([
                Node("child", "root", 1, ReviewSubAgentStatus.Completed),
                Node("grandchild", "thread-child", 2, ReviewSubAgentStatus.Running),
            ]),
            new ReviewSubAgentTreeSnapshot([
                Node("child", "root", 1, ReviewSubAgentStatus.Completed),
                Node("grandchild", "thread-child", 2, ReviewSubAgentStatus.Completed),
            ]),
            new ReviewSubAgentTreeSnapshot([
                Node("child", "root", 1, ReviewSubAgentStatus.Completed),
                Node("grandchild", "thread-child", 2, ReviewSubAgentStatus.Completed),
            ])
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

        await FluentActions.Awaiting(() => task).Should().ThrowAsync<ReviewBarrierDeadlineException>();
    }

    [Fact]
    public async Task WaitAsync_SnapshotCallOutlastsTheDeadline_ThrowsInsteadOfOpeningOnIt()
    {
        // The deadline is checked at the TOP of the loop, but the snapshot call that decides the iteration
        // happens after it — and that call is a network round trip to the review host, which can take longer
        // than whatever budget was left. The clock reading taken before it was then reused to judge what came
        // back, so a tree confirmed after the deadline had passed was still accepted and returned, and the
        // overrun was bounded only by how long the source took to answer. The barrier's own contract is a
        // single ABSOLUTE deadline, so the only correct answer once it has passed is the timeout.
        var clock = new ObservableFakeClock(DateTimeOffset.UtcNow);
        var run = TestRun();
        var deadline = clock.GetUtcNow() + TimeSpan.FromMinutes(30);
        var settled = new ReviewSubAgentTreeSnapshot([Node("a", "root", 1, ReviewSubAgentStatus.Completed)]);

        // The roster is all-terminal and IDENTICAL across both observations, so every other condition for
        // opening the barrier is met on the second call. The deadline is the only thing standing in the way,
        // which is what makes this a test of the deadline and not of the settling rule.
        var source = new SlowCompletionSource(
            settled,
            onCall: call =>
            {
                if (call == 2)
                {
                    clock.Advance(TimeSpan.FromMinutes(31));
                }
            }
        );
        var barrier = CreateBarrier(
            source,
            clock,
            TimeSpan.Zero,
            new CapturingLogger<ReviewSubAgentCompletionBarrier>()
        );

        var task = barrier.WaitAsync(run, "root", deadline, NoopValidator, CancellationToken.None);

        var act = () => PumpUntilSettledAsync(task, clock, TimeSpan.FromSeconds(5));
        _ = await act.Should().ThrowAsync<ReviewBarrierDeadlineException>();
        source.CallCount.Should().Be(2, "the overrun is detected on the call that caused it, not a poll later");
    }

    /// <summary>
    /// A completion source that runs <paramref name="onCall"/> before answering, so a test can make the call
    /// itself consume time — the one thing a source returning an already-built snapshot cannot otherwise do.
    /// </summary>
    private sealed class SlowCompletionSource(ReviewSubAgentTreeSnapshot snapshot, Action<int> onCall)
        : IReviewSubAgentCompletionSource
    {
        public int CallCount { get; private set; }

        public Task<ReviewSubAgentTreeSnapshot> GetSnapshotAsync(
            ReviewRun run,
            string parentThreadId,
            CancellationToken ct
        )
        {
            CallCount++;
            onCall(CallCount);
            return Task.FromResult(snapshot);
        }
    }

    [Fact]
    public async Task WaitAsync_SnapshotCallBlocksButHonorsCancellation_ThrowsAtDeadlineNotOneTransportTimeoutLater()
    {
        // #280: the snapshot round trip used to be awaited on the stage token alone, so its duration was
        // bounded only by whatever timeout the source's transport happened to carry — a framework default,
        // or nothing at all for a long-poll client. A call that outlives the barrier's own absolute deadline
        // must be cut at the deadline. Here the source blocks but DOES observe its token, so the barrier's
        // deadline-linked token is what must end it — then the top-of-loop check throws.
        var clock = new ObservableFakeClock(DateTimeOffset.UtcNow);
        var run = TestRun();
        var deadline = clock.GetUtcNow() + TimeSpan.FromMinutes(30);
        var source = new BlockingCompletionSource(honorsCancellation: true);
        var barrier = CreateBarrier(
            source,
            clock,
            TimeSpan.Zero,
            new CapturingLogger<ReviewSubAgentCompletionBarrier>()
        );

        var task = barrier.WaitAsync(run, "root", deadline, NoopValidator, CancellationToken.None);

        // A single step larger than the whole budget: if the call were bounded only by an incidental
        // transport timeout (or unbounded), crossing the deadline mid-call would not end it and the barrier
        // would never throw.
        var act = () => PumpUntilSettledAsync(task, clock, TimeSpan.FromMinutes(31));
        _ = await act.Should().ThrowAsync<ReviewBarrierDeadlineException>();
        source.CallCount.Should().Be(1, "the single blocked call is cut at the deadline, not retried past it");

        // The deadline-linked token — not just WaitAsync abandoning the await — is what ends the call:
        // the source observed ITS OWN token cancel, which is the "cancellation reaches the transport"
        // property #280 asks for (a WaitAsync-only bound would leave this request running, un-cancelled).
        (await source.ObservedCancellation.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should()
            .BeTrue(
                "the barrier's deadline-linked token must cancel the in-flight snapshot call, not merely abandon it"
            );
    }

    [Fact]
    public async Task WaitAsync_SnapshotCallIgnoresCancellation_StillThrowsAtDeadlineRatherThanHangingForever()
    {
        // #280, the harder half: the bound must not DEPEND on the source honoring cancellation. A transport
        // that ignores its token (or a client configured with Timeout.InfiniteTimeSpan) would leave a
        // deadline-linked token powerless — WaitAsync is what still guarantees the barrier abandons the call
        // at its budget and throws, instead of hanging behind a round trip that never returns.
        var clock = new ObservableFakeClock(DateTimeOffset.UtcNow);
        var run = TestRun();
        var deadline = clock.GetUtcNow() + TimeSpan.FromMinutes(30);
        var source = new BlockingCompletionSource(honorsCancellation: false);
        var barrier = CreateBarrier(
            source,
            clock,
            TimeSpan.Zero,
            new CapturingLogger<ReviewSubAgentCompletionBarrier>()
        );

        var task = barrier.WaitAsync(run, "root", deadline, NoopValidator, CancellationToken.None);

        var act = () => PumpUntilSettledAsync(task, clock, TimeSpan.FromMinutes(31));
        _ = await act.Should().ThrowAsync<ReviewBarrierDeadlineException>();
        source
            .CallCount.Should()
            .Be(1, "the single token-ignoring call is abandoned at the deadline, not retried past it");
    }

    /// <summary>
    /// A completion source whose <see cref="GetSnapshotAsync"/> never returns a snapshot, so the ONLY thing
    /// that can bound the call is the barrier itself. <paramref name="honorsCancellation"/> chooses whether
    /// it parks on its token (a well-behaved transport the deadline-linked token can cancel) or ignores it
    /// entirely (a transport only WaitAsync can bound).
    /// </summary>
    private sealed class BlockingCompletionSource(bool honorsCancellation) : IReviewSubAgentCompletionSource
    {
        private readonly TaskCompletionSource<bool> _observedCancellation = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public int CallCount { get; private set; }

        /// <summary>Completes with <c>true</c> once <see cref="GetSnapshotAsync"/> has seen ITS OWN token
        /// cancel — the evidence that the barrier's deadline-linked token reached the transport, rather
        /// than the call merely being abandoned by WaitAsync.</summary>
        public Task<bool> ObservedCancellation => _observedCancellation.Task;

        public async Task<ReviewSubAgentTreeSnapshot> GetSnapshotAsync(
            ReviewRun run,
            string parentThreadId,
            CancellationToken ct
        )
        {
            CallCount++;
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, honorsCancellation ? ct : CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                _ = _observedCancellation.TrySetResult(true);
                throw;
            }

            throw new InvalidOperationException("A blocking completion source never returns a snapshot.");
        }
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

    private static readonly TimeSpan Quiescence = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Advances the clock through <paramref name="steps"/> polls WITHOUT requiring the barrier to settle,
    /// and asserts it is still closed at the end.
    /// </summary>
    /// <remarks>
    /// A test that starts the barrier and immediately jumps the clock past the deadline proves nothing about
    /// blocking: the deadline check runs at the top of the loop, so the throw happens whether the roster
    /// blocked or not, and a rule that wrongly admitted every node would pass just the same. (It did — every
    /// pin below was green against a deliberately over-broad rule until this helper replaced that idiom.)
    /// Staying strictly inside the deadline is what makes "still waiting" the only explanation for an
    /// incomplete task.
    /// </remarks>
    private static async Task PumpAndStayClosedAsync<T>(
        Task<T> task,
        ObservableFakeClock clock,
        TimeSpan step,
        int steps
    )
    {
        var hangGuard = TimeSpan.FromSeconds(30);

        for (var i = 0; i < steps; i++)
        {
            var parked = clock.WaitForNextWaitAsync(hangGuard);
            if (ReferenceEquals(await Task.WhenAny(task, parked), task))
            {
                break;
            }

            if (!await parked)
            {
                throw new InvalidOperationException(
                    $"The barrier neither settled nor registered another wait within {hangGuard}."
                );
            }

            clock.Advance(step);
        }

        task.IsCompleted.Should()
            .BeFalse(
                "the barrier must still be closed after {0} of polling, with its deadline not yet reached",
                step * steps
            );
    }

    [Fact]
    public async Task WaitAsync_UnknownNodeInactiveBeyondQuiescence_OpensInsteadOfBurningTheDeadline()
    {
        // The live defect (mcqdb run 277, PR #11256, thirteen consecutive cycles). The host could not
        // resolve some children's identity, so their status was never stamped and the roster reported them
        // as "unknown". Unknown is not terminal, so the barrier waited on nodes that had no terminal
        // transition left to make: it burned all 30 minutes, threw, and the completed review was discarded.
        // The retry produced the same roster, so no number of retries could ever converge.
        //
        // The node here is exactly that shape: unknown, and last active well before the quiescence window.
        var start = DateTimeOffset.UtcNow;
        var clock = new ObservableFakeClock(start);
        var run = TestRun();
        var roster = new ReviewSubAgentTreeSnapshot([
            Node("agent-real", "root", 1, ReviewSubAgentStatus.Completed),
            Node(
                "agent-ghost",
                "root",
                1,
                ReviewSubAgentStatus.Unknown,
                lastActivityUtc: start - TimeSpan.FromMinutes(20)
            ),
        ]);
        var logger = new CapturingLogger<ReviewSubAgentCompletionBarrier>();
        var barrier = CreateBarrier(new ScriptedCompletionSource(roster), clock, Quiescence, logger);
        var deadline = clock.GetUtcNow() + TimeSpan.FromMinutes(30);

        var task = barrier.WaitAsync(run, "root", deadline, NoopValidator, CancellationToken.None);
        var result = await PumpUntilSettledAsync(task, clock, TimeSpan.FromSeconds(5));

        result.Nodes.Should().HaveCount(2, "the unresolved node is admitted, not dropped from the roster");
        logger
            .CountAtLevel(LogLevel.Warning, "agent-ghost")
            .Should()
            .Be(
                1,
                "opening over an unresolved node is a weaker guarantee than the headline contract and must never be silent"
            );
    }

    [Fact]
    public async Task WaitAsync_UnknownNodeStillAdvancingActivity_KeepsBlockingUntilTheDeadline()
    {
        // The pin that keeps the allowance honest: inactivity is the ONLY thing that admits an unknown
        // node. A child whose identity was never stamped but which is demonstrably still working keeps
        // advancing its last-activity instant, and must go on blocking however long the barrier waits.
        // Without this, the fix would trade a hang for the far worse failure of synthesizing a review from
        // reviewers that had not finished.
        var clock = new ObservableFakeClock(DateTimeOffset.UtcNow);
        var run = TestRun();
        var source = new LiveCompletionSource(() =>
            new ReviewSubAgentTreeSnapshot([
                // Activity tracks the clock: however far time is advanced, this node was busy a moment ago.
                Node("agent-busy", "root", 1, ReviewSubAgentStatus.Unknown, lastActivityUtc: clock.GetUtcNow()),
            ])
        );
        var barrier = CreateBarrier(source, clock, Quiescence, new CapturingLogger<ReviewSubAgentCompletionBarrier>());
        var deadline = clock.GetUtcNow() + TimeSpan.FromMinutes(30);

        var task = barrier.WaitAsync(run, "root", deadline, NoopValidator, CancellationToken.None);

        // Ten minutes of polling — twice the quiescence window, a third of the deadline.
        await PumpAndStayClosedAsync(task, clock, TimeSpan.FromMinutes(1), steps: 10);

        clock.Advance(TimeSpan.FromMinutes(30));
        await FluentActions.Awaiting(() => task).Should().ThrowAsync<ReviewBarrierDeadlineException>();
    }

    [Fact]
    public async Task WaitAsync_RunningNodeInactiveBeyondQuiescence_KeepsBlockingAndIsNeverQuiesced()
    {
        // The allowance is scoped to Unknown and must not leak onto Running. Running is a positive
        // assertion that the source KNOWS the child is alive; silence does not overturn it — a reviewer
        // thinking between tool calls looks identical to one that stopped. Only Unknown, which asserts
        // nothing at all and therefore has no terminal transition to wait for, may be settled by silence.
        var start = DateTimeOffset.UtcNow;
        var clock = new ObservableFakeClock(start);
        var run = TestRun();
        var roster = new ReviewSubAgentTreeSnapshot([
            Node(
                "agent-quiet",
                "root",
                1,
                ReviewSubAgentStatus.Running,
                lastActivityUtc: start - TimeSpan.FromHours(2)
            ),
        ]);
        var barrier = CreateBarrier(
            new ScriptedCompletionSource(roster),
            clock,
            Quiescence,
            new CapturingLogger<ReviewSubAgentCompletionBarrier>()
        );
        var deadline = clock.GetUtcNow() + TimeSpan.FromMinutes(30);

        var task = barrier.WaitAsync(run, "root", deadline, NoopValidator, CancellationToken.None);

        await PumpAndStayClosedAsync(task, clock, TimeSpan.FromMinutes(1), steps: 10);

        clock.Advance(TimeSpan.FromMinutes(30));
        await FluentActions.Awaiting(() => task).Should().ThrowAsync<ReviewBarrierDeadlineException>();
    }

    [Fact]
    public async Task WaitAsync_UnknownNodeWithNoActivityTimestamp_KeepsBlockingAndTimeoutNamesIt()
    {
        // Absence of a timestamp is not evidence of inactivity. A source that simply does not report
        // last-activity would otherwise have every one of its unknown nodes admitted the instant the
        // allowance was switched on — the field would act as a kill switch for the barrier rather than as
        // evidence. It must fail closed instead.
        //
        // This also pins the other half of the fix: a barrier that times out has to say which node held it
        // open. Run 277's timeout logged nothing at all, which is why naming the culprit needed a database.
        var clock = new ObservableFakeClock(DateTimeOffset.UtcNow);
        var run = TestRun();
        var roster = new ReviewSubAgentTreeSnapshot([Node("agent-unstamped", "root", 1, ReviewSubAgentStatus.Unknown)]);
        var logger = new CapturingLogger<ReviewSubAgentCompletionBarrier>();
        var barrier = CreateBarrier(new ScriptedCompletionSource(roster), clock, Quiescence, logger);
        var deadline = clock.GetUtcNow() + TimeSpan.FromMinutes(30);

        var task = barrier.WaitAsync(run, "root", deadline, NoopValidator, CancellationToken.None);

        await PumpAndStayClosedAsync(task, clock, TimeSpan.FromMinutes(1), steps: 10);

        clock.Advance(TimeSpan.FromMinutes(30));
        await FluentActions.Awaiting(() => task).Should().ThrowAsync<ReviewBarrierDeadlineException>();
        logger
            .CountAtLevel(LogLevel.Error, "agent-unstamped")
            .Should()
            .BeGreaterThan(0, "the timeout must name the node that held the barrier open");
    }

    [Fact]
    public async Task WaitAsync_UnknownNodeThatWakesUpBetweenTheTwoObservations_DoesNotOpenTheBarrier()
    {
        // One of the two windows where the quiescence allowance could be turned against the barrier. An
        // unknown node that was quiet at the CANDIDATE observation and demonstrably working again at the
        // CONFIRMATION one must re-block. This is the half that ORDERING decides: the node wakes up INTO the
        // window, so settlement is re-evaluated against the confirmation snapshot and the current instant,
        // finds it no longer quiesced, and discards the pending candidate before the identity comparison is
        // reached at all. That ordering is the whole guarantee for this shape, which is why it is pinned here
        // rather than left to be re-derived from the two checks sitting near each other.
        //
        // A node that wakes to an instant still OUTSIDE the window never reaches this path — it stays
        // quiesced — and is pinned by the test below.
        var start = DateTimeOffset.UtcNow;
        var clock = new ObservableFakeClock(start);
        var run = TestRun();
        var polls = 0;
        var source = new LiveCompletionSource(() =>
        {
            // Quiet for the first poll only — the candidate — then busy on every poll after it.
            var lastActivity = polls++ == 0 ? start - TimeSpan.FromMinutes(20) : clock.GetUtcNow();
            return new ReviewSubAgentTreeSnapshot([
                Node("agent-ghost", "root", 1, ReviewSubAgentStatus.Unknown, lastActivityUtc: lastActivity),
            ]);
        });
        var barrier = CreateBarrier(source, clock, Quiescence, new CapturingLogger<ReviewSubAgentCompletionBarrier>());
        var deadline = clock.GetUtcNow() + TimeSpan.FromMinutes(30);

        var task = barrier.WaitAsync(run, "root", deadline, NoopValidator, CancellationToken.None);

        await PumpAndStayClosedAsync(task, clock, TimeSpan.FromMinutes(1), steps: 10);

        clock.Advance(TimeSpan.FromMinutes(30));
        await FluentActions.Awaiting(() => task).Should().ThrowAsync<ReviewBarrierDeadlineException>();
    }

    [Fact]
    public async Task WaitAsync_UnknownNodeWhoseActivityAdvancesButStaysOutsideTheWindow_DoesNotOpenTheBarrier()
    {
        // The other half, and the one ordering does NOT cover. This node's activity moves forward on every
        // poll — it is working — but every instant it reports is older than the quiescence window, so it
        // stays settled and the roster stays all-settled. Re-evaluating settlement against the confirmation
        // snapshot therefore lets it straight through, and the candidate/confirmation comparison is the only
        // thing left that can see the movement. A source reporting activity in arrears is enough to produce
        // this shape: batched or lagging events hold a live child's last-activity permanently behind the
        // window while still advancing it.
        var clock = new ObservableFakeClock(DateTimeOffset.UtcNow);
        var run = TestRun();
        var source = new LiveCompletionSource(() =>
            new ReviewSubAgentTreeSnapshot([
                // Always twice the window in arrears: quiesced at every observation, identical at none.
                Node(
                    "agent-lagging",
                    "root",
                    1,
                    ReviewSubAgentStatus.Unknown,
                    lastActivityUtc: clock.GetUtcNow() - (Quiescence * 2)
                ),
            ])
        );
        var barrier = CreateBarrier(source, clock, Quiescence, new CapturingLogger<ReviewSubAgentCompletionBarrier>());
        var deadline = clock.GetUtcNow() + TimeSpan.FromMinutes(30);

        var task = barrier.WaitAsync(run, "root", deadline, NoopValidator, CancellationToken.None);

        await PumpAndStayClosedAsync(task, clock, TimeSpan.FromMinutes(1), steps: 10);

        clock.Advance(TimeSpan.FromMinutes(30));
        await FluentActions.Awaiting(() => task).Should().ThrowAsync<ReviewBarrierDeadlineException>();
    }

    [Fact]
    public async Task WaitAsync_TerminalNodeStillBeingHeartbeated_OpensBecauseActivityIsNotComparedThere()
    {
        // The bound on the test above. Comparing last-activity is scoped to non-terminal nodes, and this is
        // what the scope is for: a source that goes on re-stamping activity on a child it has ALREADY
        // reported as finished — a heartbeat, a clock rounding to a coarser tick — would otherwise reset
        // stability on every poll and hang the barrier for the full deadline, which is precisely the run-277
        // failure the quiescence allowance exists to remove. A terminal node's settlement does not rest on
        // the timestamp, so movement there is noise and is ignored.
        var clock = new ObservableFakeClock(DateTimeOffset.UtcNow);
        var run = TestRun();
        var source = new LiveCompletionSource(() =>
            new ReviewSubAgentTreeSnapshot([
                // Finished, and still being stamped: a different instant at every observation.
                Node("agent-done", "root", 1, ReviewSubAgentStatus.Completed, lastActivityUtc: clock.GetUtcNow()),
            ])
        );
        var barrier = CreateBarrier(source, clock, Quiescence, new CapturingLogger<ReviewSubAgentCompletionBarrier>());
        var deadline = clock.GetUtcNow() + TimeSpan.FromMinutes(30);

        var task = barrier.WaitAsync(run, "root", deadline, NoopValidator, CancellationToken.None);
        var result = await PumpUntilSettledAsync(task, clock, TimeSpan.FromSeconds(5));

        result.Nodes.Should().HaveCount(1, "the barrier opens on a terminal roster however often it is re-stamped");
    }

    [Fact]
    public async Task WaitAsync_QuiescenceDisabled_RestoresStrictTerminalOnlySettlement()
    {
        // The allowance is configuration, and switching it off must restore the original contract exactly —
        // an operator who decides the inference is wrong for their host needs the strict behaviour back
        // without a code change. Same long-inactive unknown node as the run-277 test; only the window differs.
        var start = DateTimeOffset.UtcNow;
        var clock = new ObservableFakeClock(start);
        var run = TestRun();
        var roster = new ReviewSubAgentTreeSnapshot([
            Node(
                "agent-ghost",
                "root",
                1,
                ReviewSubAgentStatus.Unknown,
                lastActivityUtc: start - TimeSpan.FromHours(12)
            ),
        ]);
        var barrier = CreateBarrier(
            new ScriptedCompletionSource(roster),
            clock,
            TimeSpan.Zero,
            new CapturingLogger<ReviewSubAgentCompletionBarrier>()
        );
        var deadline = clock.GetUtcNow() + TimeSpan.FromMinutes(30);

        var task = barrier.WaitAsync(run, "root", deadline, NoopValidator, CancellationToken.None);

        await PumpAndStayClosedAsync(task, clock, TimeSpan.FromMinutes(1), steps: 10);

        clock.Advance(TimeSpan.FromMinutes(30));
        await FluentActions.Awaiting(() => task).Should().ThrowAsync<ReviewBarrierDeadlineException>();
    }

    /// <summary>
    /// The instant the wire fixtures below are built around. Fixed rather than <c>UtcNow</c> so the
    /// timestamp the test asserts is the same one it serialised, tick for tick, through the JSON round trip.
    /// </summary>
    private static readonly DateTimeOffset WireStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static HttpClient NewS2SHttp(FakeHttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("http://localhost:5051/") };

    /// <summary>Wraps the real client and adapter around <paramref name="handler"/>, so the barrier polls
    /// through exactly the code path a live daemon uses.</summary>
    private static IReviewSubAgentCompletionSource S2SSourceOver(FakeHttpMessageHandler handler) =>
        new S2SReviewSubAgentCompletionSource(new LmStreamingS2SClient(NewS2SHttp(handler), "s", "id", "key"));

    private static string GhostNodeBody(DateTimeOffset lastActivity) =>
        "{\"schemaVersion\":1,\"nodes\":[{\"agentId\":\"agent-ghost\",\"threadId\":\"thread-ghost\","
        + "\"parentThreadId\":\"root\",\"depth\":1,\"template\":\"reviewer\",\"status\":\"who-knows\","
        + $"\"lastActivityUtc\":\"{lastActivity.ToString("O", CultureInfo.InvariantCulture)}\"}}]}}";

    [Fact]
    public async Task WaitAsync_OverTheRealS2SWire_OpensOnAnUnknownNodeWhoseOnlyEvidenceIsTheParsedTimestamp()
    {
        // Every other quiescence test here builds its roster with the Node() helper, so the barrier has
        // never been shown one that came off the wire. That left the single field the whole allowance rests
        // on — lastActivityUtc, mapped in LmStreamingS2SClient.ParseNode — asserted by nobody in between:
        // delete that assignment and all of them stay green while the barrier silently sees null, refuses
        // to settle any unresolved node, and burns the full deadline on every review that has one. This
        // drives the real handler -> real client -> real adapter -> real barrier.
        var clock = new ObservableFakeClock(WireStart);
        var stale = WireStart - TimeSpan.FromHours(12);
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "api/conversations/root/subagents?recursive=true",
            GhostNodeBody(stale)
        );
        var barrier = CreateBarrier(
            S2SSourceOver(handler),
            clock,
            Quiescence,
            new CapturingLogger<ReviewSubAgentCompletionBarrier>()
        );
        var deadline = clock.GetUtcNow() + TimeSpan.FromMinutes(30);

        var task = barrier.WaitAsync(TestRun(), "root", deadline, NoopValidator, CancellationToken.None);
        var result = await PumpUntilSettledAsync(task, clock, TimeSpan.FromSeconds(5));

        var node = result.Nodes.Should().ContainSingle().Subject;
        node.Status.Should()
            .Be(ReviewSubAgentStatus.Unknown, "an unrecognised wire status must never read as terminal");
        node.LastActivityUtc.Should().Be(stale, "the parsed instant is the whole of what settled this node");
    }

    [Fact]
    public async Task WaitAsync_OverTheRealS2SWire_StaysClosedWhileTheHostKeepsAdvancingTheTimestamp()
    {
        // The bound on the test above, and what makes its pass mean something: the barrier does not open
        // over an unknown node merely because one arrived off the wire. Same body, same status, same code
        // path — only the instant differs, and the host re-stamps it to "now" on every poll, so the node is
        // never quiesced and the barrier burns its deadline instead.
        var clock = new ObservableFakeClock(WireStart);
        var handler = new FakeHttpMessageHandler().On(
            req => req.RequestUri!.ToString().Contains("subagents?recursive=true", StringComparison.Ordinal),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(GhostNodeBody(clock.GetUtcNow()), Encoding.UTF8, "application/json"),
            }
        );
        var barrier = CreateBarrier(
            S2SSourceOver(handler),
            clock,
            Quiescence,
            new CapturingLogger<ReviewSubAgentCompletionBarrier>()
        );
        var deadline = clock.GetUtcNow() + TimeSpan.FromMinutes(30);

        var task = barrier.WaitAsync(TestRun(), "root", deadline, NoopValidator, CancellationToken.None);

        await PumpAndStayClosedAsync(task, clock, TimeSpan.FromMinutes(1), steps: 10);

        clock.Advance(TimeSpan.FromMinutes(30));
        await FluentActions.Awaiting(() => task).Should().ThrowAsync<ReviewBarrierDeadlineException>();
        handler.CountRequests("subagents").Should().BeGreaterThan(1, "the barrier really did keep polling");
    }

    /// <summary>Test double that rebuilds its snapshot on every call, so a node's reported state can track
    /// the test clock — the only way to script a child that is still genuinely working.</summary>
    private sealed class LiveCompletionSource(Func<ReviewSubAgentTreeSnapshot> build) : IReviewSubAgentCompletionSource
    {
        public Task<ReviewSubAgentTreeSnapshot> GetSnapshotAsync(
            ReviewRun run,
            string parentThreadId,
            CancellationToken ct
        ) => Task.FromResult(build());
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

        public Task<ReviewSubAgentTreeSnapshot> GetSnapshotAsync(
            ReviewRun run,
            string parentThreadId,
            CancellationToken ct
        )
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
