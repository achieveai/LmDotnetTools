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
    /// Builds a barrier over any completion source with the unknown-node quiescence allowance configured,
    /// and with a logger the test can read back — the timeout path's only observable output is what it logs.
    /// </summary>
    private static ReviewSubAgentCompletionBarrier CreateBarrier(
        IReviewSubAgentCompletionSource source,
        FakeTimeProvider clock,
        TimeSpan unknownQuiescence,
        CapturingLogger<ReviewSubAgentCompletionBarrier> logger) =>
        new(source, QuietPeriod, logger, clock, unknownQuiescence);

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
        string threadId = "",
        DateTimeOffset? lastActivityUtc = null) =>
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
        int steps)
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
                    $"The barrier neither settled nor registered another wait within {hangGuard}.");
            }

            clock.Advance(step);
        }

        task.IsCompleted.Should()
            .BeFalse(
                "the barrier must still be closed after {0} of polling, with its deadline not yet reached",
                step * steps);
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
        var roster = new ReviewSubAgentTreeSnapshot(
            [
                Node("agent-real", "root", 1, ReviewSubAgentStatus.Completed),
                Node("agent-ghost", "root", 1, ReviewSubAgentStatus.Unknown,
                    lastActivityUtc: start - TimeSpan.FromMinutes(20)),
            ]
        );
        var logger = new CapturingLogger<ReviewSubAgentCompletionBarrier>();
        var barrier = CreateBarrier(new ScriptedCompletionSource(roster), clock, Quiescence, logger);
        var deadline = clock.GetUtcNow() + TimeSpan.FromMinutes(30);

        var task = barrier.WaitAsync(run, "root", deadline, NoopValidator, CancellationToken.None);
        var result = await PumpUntilSettledAsync(task, clock, TimeSpan.FromSeconds(5));

        result.Nodes.Should().HaveCount(2, "the unresolved node is admitted, not dropped from the roster");
        logger
            .CountAtLevel(LogLevel.Warning, "agent-ghost")
            .Should()
            .Be(1, "opening over an unresolved node is a weaker guarantee than the headline contract and must never be silent");
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
            new ReviewSubAgentTreeSnapshot(
                [
                    // Activity tracks the clock: however far time is advanced, this node was busy a moment ago.
                    Node("agent-busy", "root", 1, ReviewSubAgentStatus.Unknown,
                        lastActivityUtc: clock.GetUtcNow()),
                ]
            ));
        var barrier = CreateBarrier(
            source, clock, Quiescence, new CapturingLogger<ReviewSubAgentCompletionBarrier>());
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
        var roster = new ReviewSubAgentTreeSnapshot(
            [
                Node("agent-quiet", "root", 1, ReviewSubAgentStatus.Running,
                    lastActivityUtc: start - TimeSpan.FromHours(2)),
            ]
        );
        var barrier = CreateBarrier(
            new ScriptedCompletionSource(roster), clock, Quiescence,
            new CapturingLogger<ReviewSubAgentCompletionBarrier>());
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
        var roster = new ReviewSubAgentTreeSnapshot(
            [Node("agent-unstamped", "root", 1, ReviewSubAgentStatus.Unknown)]
        );
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
    public async Task WaitAsync_QuiescenceDisabled_RestoresStrictTerminalOnlySettlement()
    {
        // The allowance is configuration, and switching it off must restore the original contract exactly —
        // an operator who decides the inference is wrong for their host needs the strict behaviour back
        // without a code change. Same long-inactive unknown node as the run-277 test; only the window differs.
        var start = DateTimeOffset.UtcNow;
        var clock = new ObservableFakeClock(start);
        var run = TestRun();
        var roster = new ReviewSubAgentTreeSnapshot(
            [
                Node("agent-ghost", "root", 1, ReviewSubAgentStatus.Unknown,
                    lastActivityUtc: start - TimeSpan.FromHours(12)),
            ]
        );
        var barrier = CreateBarrier(
            new ScriptedCompletionSource(roster), clock, TimeSpan.Zero,
            new CapturingLogger<ReviewSubAgentCompletionBarrier>());
        var deadline = clock.GetUtcNow() + TimeSpan.FromMinutes(30);

        var task = barrier.WaitAsync(run, "root", deadline, NoopValidator, CancellationToken.None);

        await PumpAndStayClosedAsync(task, clock, TimeSpan.FromMinutes(1), steps: 10);

        clock.Advance(TimeSpan.FromMinutes(30));
        await FluentActions.Awaiting(() => task).Should().ThrowAsync<ReviewBarrierDeadlineException>();
    }

    /// <summary>Test double that rebuilds its snapshot on every call, so a node's reported state can track
    /// the test clock — the only way to script a child that is still genuinely working.</summary>
    private sealed class LiveCompletionSource(Func<ReviewSubAgentTreeSnapshot> build)
        : IReviewSubAgentCompletionSource
    {
        public Task<ReviewSubAgentTreeSnapshot> GetSnapshotAsync(
            ReviewRun run,
            string parentThreadId,
            CancellationToken ct) => Task.FromResult(build());
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
