using System.Diagnostics;
using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmTestUtils;

namespace LmTestUtils.Tests;

/// <summary>
/// Covers the contract that makes <see cref="Wait"/> worth consolidating: a wait that never
/// completes has to fail the test rather than return quietly. Every private poll helper this class
/// replaces got that backwards.
/// </summary>
public class WaitTests
{
    /// <summary>
    /// Budget for the cases that MUST time out. Short, because the test pays it in full every run,
    /// and safe to keep short: a condition hard-coded to false cannot be rescued by a longer wait.
    /// </summary>
    private static readonly TimeSpan Brief = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Budget for the cases that must SUCCEED. Deliberately far larger than the work needs. Those
    /// conditions become true on their own after a fixed number of evaluations, so the happy path
    /// never spends this — but under solution-wide parallel load a <c>Task.Delay(5)</c> can land
    /// hundreds of milliseconds late (#343), and a budget sized to the ideal schedule turns that
    /// into the helper-under-test reporting itself broken.
    /// </summary>
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(5);

    [Fact]
    public async Task A_condition_that_never_holds_throws_naming_what_it_waited_for()
    {
        var thrown = await Assert.ThrowsAsync<TimeoutException>(() =>
            Wait.UntilAsync(() => false, "the widget was flushed", Brief, Tick)
        );

        // The description and the call site are the whole point: the failure has to say which wait
        // stalled and what it wanted, because a bare timeout is what made these bugs expensive.
        Assert.Contains("the widget was flushed", thrown.Message, StringComparison.Ordinal);
        Assert.Contains(
            nameof(A_condition_that_never_holds_throws_naming_what_it_waited_for),
            thrown.Message,
            StringComparison.Ordinal
        );
        Assert.Contains("WaitTests.cs", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_condition_already_true_returns_without_polling()
    {
        var evaluations = 0;
        var elapsed = Stopwatch.StartNew();

        await Wait.UntilAsync(
            () =>
            {
                evaluations++;
                return true;
            },
            "the condition already holds",
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(10)
        );

        // One evaluation, and crucially no sleep: the poll interval must not become a floor on how
        // long an already-satisfied wait takes, or every such wait taxes the suite.
        Assert.Equal(1, evaluations);
        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(5),
            $"an already-true condition must not sleep, but the wait took {elapsed.Elapsed}"
        );
    }

    [Fact]
    public async Task A_condition_that_becomes_true_partway_through_completes()
    {
        var remaining = 3;

        await Wait.UntilAsync(() => --remaining <= 0, "the countdown reached zero", Generous, Tick);

        Assert.True(remaining <= 0);
    }

    [Fact]
    public async Task TryUntilAsync_reports_the_timeout_instead_of_throwing()
    {
        // The escape hatch returns bool precisely so a caller cannot ignore the outcome by accident.
        Assert.False(await Wait.TryUntilAsync(() => false, Brief, Tick));
        Assert.True(await Wait.TryUntilAsync(() => true, Generous, Tick));
    }

    [Fact]
    public async Task An_asynchronous_condition_is_polled_the_same_way()
    {
        var remaining = 3;

        await Wait.UntilAsync(
            () => Task.FromResult(--remaining <= 0),
            "the async countdown reached zero",
            Generous,
            Tick
        );

        Assert.True(remaining <= 0);

        var thrown = await Assert.ThrowsAsync<TimeoutException>(() =>
            Wait.UntilAsync(() => Task.FromResult(false), "the async widget flushed", Brief, Tick)
        );
        Assert.Contains("the async widget flushed", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_zero_timeout_still_evaluates_the_condition_once()
    {
        // Otherwise a caller passing an elapsed budget gets a failure that never looked, which reads
        // in the log exactly like a condition that was checked and found false.
        var evaluations = 0;

        await Wait.UntilAsync(
            () =>
            {
                evaluations++;
                return true;
            },
            "an already-satisfied condition under a zero budget",
            TimeSpan.Zero,
            Tick
        );

        Assert.Equal(1, evaluations);
    }

    [Fact]
    public async Task An_exception_from_the_condition_propagates_rather_than_being_swallowed()
    {
        // Swallowing belongs at the call site, where the decision to tolerate a transient read is
        // visible, not hidden inside the shared helper where nobody sees it.
        // Explicitly typed: a bare `() => throw ...` is convertible to both the sync and async
        // condition overloads, so it needs to say which one it means.
        Func<bool> throws = () => throw new InvalidOperationException("boom");

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Wait.UntilAsync(throws, "a condition that throws", Brief, Tick)
        );
    }

    [Fact]
    public async Task A_timeout_with_an_observed_supplier_appends_the_last_observed_state()
    {
        // #358: without a way to report what the wait actually saw, a timeout only proves the
        // condition never held -- it says nothing about how close it got, so the next reader has
        // to reproduce the failure just to find out what the state looked like when it gave up.
        var thrown = await Assert.ThrowsAsync<TimeoutException>(() =>
            Wait.UntilAsync(() => false, "the widget was flushed", Brief, Tick, observed: () => "status=pending")
        );

        Assert.Contains("Last observed: status=pending.", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_timeout_with_no_observed_supplier_omits_the_last_observed_clause()
    {
        // The existing callers that do not pass observed must see byte-for-byte the same message as
        // before -- a dangling "Last observed:" clause with nothing after it would be worse than
        // saying nothing.
        var thrown = await Assert.ThrowsAsync<TimeoutException>(() =>
            Wait.UntilAsync(() => false, "the widget was flushed", Brief, Tick)
        );

        Assert.DoesNotContain("Last observed", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_observed_supplier_is_never_invoked_when_the_condition_succeeds()
    {
        var invoked = false;

        await Wait.UntilAsync(
            () => true,
            "the condition already holds",
            Generous,
            Tick,
            observed: () =>
            {
                invoked = true;
                return "should never run";
            }
        );

        Assert.False(invoked, "observed must only be paid for on the failure path, not the happy one");
    }

    [Fact]
    public async Task A_throwing_observed_supplier_yields_a_TimeoutException_not_the_suppliers_own_exception()
    {
        // The sync (Func<bool>) overload. Before this guard, observed() ran unguarded inside the
        // TimeoutException's message interpolation, so a throwing supplier replaced the timeout
        // entirely: the caller saw the supplier's own exception, with no TimeoutException, no
        // mention of the wait's name, and no file/line pointing at the stalled condition.
        var thrown = await Assert.ThrowsAsync<TimeoutException>(() =>
            Wait.UntilAsync(
                () => false,
                "the widget was flushed",
                Brief,
                Tick,
                observed: () => throw new InvalidOperationException("boom")
            )
        );

        Assert.Contains("Last observed: <observed supplier threw: boom>.", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_throwing_observed_supplier_on_the_async_overload_also_yields_a_TimeoutException()
    {
        // The async (Func<Task<bool>>) overload has its own, separately-written throw site, so the
        // same guard has to be applied there independently -- fixing only the sync overload would
        // leave this one still letting a throwing supplier's exception escape raw.
        var thrown = await Assert.ThrowsAsync<TimeoutException>(() =>
            Wait.UntilAsync(
                () => Task.FromResult(false),
                "the async widget flushed",
                Brief,
                Tick,
                observed: () => throw new InvalidOperationException("boom")
            )
        );

        Assert.Contains("Last observed: <observed supplier threw: boom>.", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_timeout_on_the_async_overload_with_an_observed_supplier_appends_the_last_observed_state()
    {
        // Pins the "Last observed:" append specifically for the async (Func<Task<bool>>) overload.
        // It has its own, separately-written throw site from the sync overload, so a mutation that
        // drops the append from only this overload is otherwise invisible: the sync-overload test
        // above (A_timeout_with_an_observed_supplier_appends_the_last_observed_state) exercises a
        // different code path and cannot catch a regression here.
        var thrown = await Assert.ThrowsAsync<TimeoutException>(() =>
            Wait.UntilAsync(
                () => Task.FromResult(false),
                "the async widget flushed",
                Brief,
                Tick,
                observed: () => "status=pending"
            )
        );

        Assert.Contains("Last observed: status=pending.", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_token_exits_the_wait_with_OperationCanceledException_not_a_timeout()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // ThrowsAnyAsync, not ThrowsAsync: Task.Delay raises the TaskCanceledException subtype, not the
        // base OperationCanceledException exactly -- the contract is "cancellation surfaces as some
        // OperationCanceledException, not a TimeoutException", not the precise concrete type.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Wait.UntilAsync(() => false, "a wait that gets cancelled", Generous, Tick, cancellationToken: cts.Token)
        );
    }

    #region ForTeardownAsync

    /// <summary>
    /// Marks the fault raised by the abandoned teardown below, so the assertion cannot be satisfied
    /// or defeated by an unobserved exception from some OTHER test sharing this process.
    /// </summary>
    private const string AbandonedTeardownFault = "abandoned-teardown-fault-8f2c1d";

    [Fact]
    public async Task A_teardown_that_never_returns_throws_naming_what_was_being_torn_down()
    {
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var thrown = await Assert.ThrowsAsync<TimeoutException>(() =>
            Wait.ForTeardownAsync(() => new ValueTask(never.Task), "the widget host", Brief)
        );

        Assert.Contains("the widget host", thrown.Message, StringComparison.Ordinal);
        Assert.Contains(
            nameof(A_teardown_that_never_returns_throws_naming_what_was_being_torn_down),
            thrown.Message,
            StringComparison.Ordinal
        );
        Assert.Contains("WaitTests.cs", thrown.Message, StringComparison.Ordinal);

        never.SetResult();
    }

    [Fact]
    public async Task A_teardown_that_itself_times_out_surfaces_its_own_failure_not_the_ceiling()
    {
        // Task.WaitAsync reports a ceiling breach with the SAME exception type it propagates when the
        // awaited work throws one itself, so a catch keyed on type alone reports every internally
        // timing-out disposal as "the ceiling elapsed" — losing the real message and stack, and
        // naming a deadline that never fired. Nothing here looks at elapsed time (#343): the claim is
        // purely which exception comes out.
        const string ownFailure = "the subject's own disposal deadline";

        var thrown = await Assert.ThrowsAsync<TimeoutException>(() =>
            Wait.ForTeardownAsync(
                () =>
                    new ValueTask(
                        Task.Run(async () =>
                        {
                            await Task.Yield();
                            throw new TimeoutException(ownFailure);
                        })
                    ),
                "a subject whose disposal times out internally",
                Generous
            )
        );

        Assert.Equal(ownFailure, thrown.Message);
        Assert.DoesNotContain("tearing down", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "a subject whose disposal times out internally",
            thrown.Message,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task A_teardown_that_faults_before_the_ceiling_surfaces_that_fault_unchanged()
    {
        // The same discriminator, for the ordinary case: a non-TimeoutException fault must not be
        // touched either. Generous budget, already-failing work — the ceiling is never in play.
        const string boom = "disposal-blew-up";

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Wait.ForTeardownAsync(
                () => new ValueTask(Task.FromException(new InvalidOperationException(boom))),
                "a subject whose disposal fails outright",
                Generous
            )
        );

        Assert.Equal(boom, thrown.Message);
    }

    [Fact]
    public async Task A_teardown_abandoned_at_the_ceiling_has_its_later_fault_observed()
    {
        // The bound does not cancel the teardown, it abandons it — so the teardown can still fault
        // AFTER nobody is awaiting it. Left unobserved, that fault surfaces from the finalizer
        // thread as an UnobservedTaskException with no connection to the test that caused it, which
        // in a parallel run gets attributed to whatever happened to be executing. Task.WaitAsync does
        // NOT mark the source task's fault observed, so the continuation in ForTeardownAsync is the
        // only thing that does.
        var unobserved = new List<string>();
        void Collect(object? _, UnobservedTaskExceptionEventArgs e)
        {
            lock (unobserved)
            {
                unobserved.AddRange(e.Exception.Flatten().InnerExceptions.Select(x => x.Message));
            }
        }

        TaskScheduler.UnobservedTaskException += Collect;
        try
        {
            await AbandonThenFaultAsync();

            // Nothing above still references the abandoned task, so collecting it runs the finalizer
            // that would raise the event if its fault were still unobserved.
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            GC.Collect();

            lock (unobserved)
            {
                Assert.DoesNotContain(AbandonedTeardownFault, unobserved);
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Collect;
        }
    }

    /// <summary>
    /// Times out a teardown and only THEN faults it, letting every reference to that task go out of
    /// scope on return. Separated so the abandoned task is unreachable while the caller collects —
    /// a live local (or a retained <see cref="TaskCompletionSource"/>) would keep it alive and the
    /// finalizer would never run, making the assertion above pass for the wrong reason.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task AbandonThenFaultAsync()
    {
        var abandoned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = await Assert.ThrowsAsync<TimeoutException>(() =>
            Wait.ForTeardownAsync(() => new ValueTask(abandoned.Task), "a teardown that outlives its ceiling", Brief)
        );

        abandoned.SetException(new InvalidOperationException(AbandonedTeardownFault));
    }

    [Fact]
    public async Task A_teardown_with_no_named_ceiling_uses_the_teardown_default_not_the_poll_default()
    {
        // Costs its full 30s every run, deliberately: the ONLY way to observe which constant the
        // ceiling resolved to is to let it elapse and read the budget back out of the message.
        // Nothing here is timing-sensitive despite that — the teardown never completes, so there is
        // no race for a starved runner (#343) to lose; a slow machine only makes it slower.
        Assert.NotEqual(Wait.DefaultTimeout, Wait.DefaultTeardownTimeout);

        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var thrown = await Assert.ThrowsAsync<TimeoutException>(() =>
            Wait.ForTeardownAsync(() => new ValueTask(never.Task), "an unbudgeted teardown")
        );

        Assert.Contains(
            $"Timed out after {Wait.DefaultTeardownTimeout.TotalSeconds:0.###}s",
            thrown.Message,
            StringComparison.Ordinal
        );

        never.SetResult();
    }

    [Fact]
    public async Task A_null_teardown_is_rejected_by_name_rather_than_NullReferenced()
    {
        var thrown = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            Wait.ForTeardownAsync((Func<ValueTask>)null!, "a teardown that was never supplied")
        );

        Assert.Equal("teardown", thrown.ParamName);
    }

    [Fact]
    public async Task A_null_subject_is_rejected_by_name_rather_than_NullReferenced()
    {
        var thrown = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            Wait.ForTeardownAsync((IAsyncDisposable)null!, "a subject that was never supplied")
        );

        Assert.Equal("subject", thrown.ParamName);
    }

    [Fact]
    public async Task A_disposable_subject_is_bounded_the_same_way_as_a_teardown_delegate()
    {
        var subject = new NeverReturningDisposable();

        var thrown = await Assert.ThrowsAsync<TimeoutException>(() =>
            Wait.ForTeardownAsync(subject, "the subject under test", Brief)
        );

        Assert.Contains("the subject under test", thrown.Message, StringComparison.Ordinal);
        Assert.Contains(
            nameof(A_disposable_subject_is_bounded_the_same_way_as_a_teardown_delegate),
            thrown.Message,
            StringComparison.Ordinal
        );

        subject.Release();
    }

    /// <summary>An <see cref="IAsyncDisposable"/> whose disposal blocks until released.</summary>
    private sealed class NeverReturningDisposable : IAsyncDisposable
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Release() => _release.TrySetResult();

        public async ValueTask DisposeAsync() => await _release.Task;
    }

    #endregion
}
