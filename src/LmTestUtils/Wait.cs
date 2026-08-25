using System.Runtime.CompilerServices;

namespace AchieveAi.LmDotnetTools.LmTestUtils;

/// <summary>
/// Bounded polling for a condition another thread is expected to satisfy.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the alternative kept being re-invented: private per-file copies of the same
/// deadline loop, most of which returned <em>silently</em> when the deadline passed with the
/// condition still false. A silent poll is only safe when every caller re-asserts the condition
/// afterwards, and that is not a property a reader can check locally — it has to hold at each call
/// site, forever. Where it failed to hold, the test passed whether or not the behaviour it named
/// ever happened.
/// </para>
/// <para>
/// So the loud form is the default and the only one with a convenient name. <c>UntilAsync</c>
/// throws a <see cref="TimeoutException"/> that quotes the caller's own description of what it was
/// waiting for, plus the file and line — turning a wait that never completes into a named failing
/// test rather than a green one.
/// </para>
/// <para>
/// <c>TryUntilAsync</c> is the deliberate escape hatch, for the one shape the loud form
/// cannot express: a claim that something must <em>not</em> have happened yet, where the polled
/// condition is the other side's progress rather than the outcome under assertion. It returns
/// <see cref="bool"/> rather than <see cref="Task"/> so the caller has to do something with the
/// answer; ignoring it is at least visible in the diff.
/// </para>
/// <para>
/// There is deliberately no silent <c>void</c>/<c>Task</c> variant. That signature is the defect.
/// </para>
/// </remarks>
public static class Wait
{
    /// <summary>Deadline used when a caller does not name one.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Ceiling for a teardown that is not allowed to wedge the run.</summary>
    /// <remarks>
    /// Deliberately far larger than <see cref="DefaultTimeout"/>: a healthy teardown that stops
    /// several agents and drains their background tasks can legitimately take seconds, so this is
    /// never reached by a run that is merely slow. It exists because the failure mode of an
    /// <em>unbounded</em> teardown is categorically worse than a late one.
    /// </remarks>
    public static readonly TimeSpan DefaultTeardownTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Gap between condition evaluations.</summary>
    /// <remarks>
    /// Short enough that a satisfied condition is noticed promptly, long enough that a cheap
    /// condition does not spin a core. Nothing here should be sensitive to the exact value: a test
    /// that depends on the poll interval is measuring the poll, not the behaviour.
    /// </remarks>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Polls until <paramref name="condition"/> holds, or throws once the deadline passes.
    /// </summary>
    /// <param name="condition">
    /// Evaluated repeatedly. Exceptions propagate — if a condition can legitimately throw while the
    /// system is still settling (reading state that does not exist yet, say), catch it in the lambda
    /// and return <see langword="false"/>, so that the swallowing is visible where it is decided.
    /// </param>
    /// <param name="because">
    /// What the caller is waiting for, phrased so it reads as the reason a failure is a failure.
    /// Required: an un-described timeout tells the next reader nothing that the stack trace did not.
    /// </param>
    /// <param name="timeout">Deadline for the whole wait. Defaults to <see cref="DefaultTimeout"/>.</param>
    /// <param name="pollInterval">Gap between evaluations. Defaults to <see cref="DefaultPollInterval"/>.</param>
    /// <param name="cancellationToken">Abandons the wait; surfaces as an <see cref="OperationCanceledException"/>.</param>
    /// <param name="waiter">Supplied by the compiler. Names the calling member in the failure message.</param>
    /// <param name="file">Supplied by the compiler. Names the calling file in the failure message.</param>
    /// <param name="line">Supplied by the compiler. Names the calling line in the failure message.</param>
    public static async Task UntilAsync(
        Func<bool> condition,
        string because,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default,
        [CallerMemberName] string? waiter = null,
        [CallerFilePath] string? file = null,
        [CallerLineNumber] int line = 0
    )
    {
        ArgumentNullException.ThrowIfNull(condition);

        var budget = timeout ?? DefaultTimeout;
        if (await TryUntilAsync(condition, budget, pollInterval, cancellationToken))
        {
            return;
        }

        throw new TimeoutException(
            $"Timed out after {budget.TotalSeconds:0.###}s waiting until {because}. "
                + $"Waiter: {waiter} ({Path.GetFileName(file)}:{line}). The condition never held, so "
                + "whatever this wait was a precondition for was never actually reached."
        );
    }

    /// <inheritdoc cref="UntilAsync(Func{bool}, string, TimeSpan?, TimeSpan?, CancellationToken, string?, string?, int)" />
    /// <param name="condition">Asynchronous form; otherwise as above.</param>
    /// <param name="because">As above.</param>
    /// <param name="timeout">Deadline for the whole wait. Defaults to <see cref="DefaultTimeout"/>.</param>
    /// <param name="pollInterval">Gap between evaluations. Defaults to <see cref="DefaultPollInterval"/>.</param>
    /// <param name="cancellationToken">Abandons the wait; surfaces as an <see cref="OperationCanceledException"/>.</param>
    /// <param name="waiter">Supplied by the compiler. Names the calling member in the failure message.</param>
    /// <param name="file">Supplied by the compiler. Names the calling file in the failure message.</param>
    /// <param name="line">Supplied by the compiler. Names the calling line in the failure message.</param>
    public static async Task UntilAsync(
        Func<Task<bool>> condition,
        string because,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default,
        [CallerMemberName] string? waiter = null,
        [CallerFilePath] string? file = null,
        [CallerLineNumber] int line = 0
    )
    {
        ArgumentNullException.ThrowIfNull(condition);

        var budget = timeout ?? DefaultTimeout;
        if (await TryUntilAsync(condition, budget, pollInterval, cancellationToken))
        {
            return;
        }

        throw new TimeoutException(
            $"Timed out after {budget.TotalSeconds:0.###}s waiting until {because}. "
                + $"Waiter: {waiter} ({Path.GetFileName(file)}:{line}). The condition never held, so "
                + "whatever this wait was a precondition for was never actually reached."
        );
    }

    /// <summary>
    /// Awaits a teardown that must return, or throws once the ceiling passes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The counterpart to <see cref="UntilAsync(Func{bool}, string, TimeSpan?, TimeSpan?, CancellationToken, string?, string?, int)"/>
    /// for the other unbounded shape a test suite keeps re-inventing: a fixture's
    /// <c>IAsyncLifetime.DisposeAsync</c> that simply <c>await</c>s the subject's own disposal. When
    /// a test body throws before releasing something that disposal is blocked on, that bare await
    /// never returns — and an unbounded teardown does not fail the one test that stalled, it wedges
    /// the testhost until <c>dotnet test</c>'s inactivity blame-dump aborts the WHOLE run. Every
    /// assembly queued behind it never executes, the console reports a crash rather than a test, and
    /// the assertion that actually failed is never reported at all.
    /// </para>
    /// <para>
    /// Bounded, the same situation is one red test naming the teardown that stalled. The abandoned
    /// disposal keeps running in the background; that is deliberate, since the alternative — waiting
    /// on it — is precisely the defect.
    /// </para>
    /// </remarks>
    /// <param name="teardown">Invoked once; its returned <see cref="ValueTask"/> is what gets bounded.</param>
    /// <param name="because">
    /// What is being torn down, phrased so it reads as the reason a failure is a failure. Required:
    /// a fixture teardown has no assertion of its own to name the subject.
    /// </param>
    /// <param name="timeout">Ceiling for the whole teardown. Defaults to <see cref="DefaultTeardownTimeout"/>.</param>
    /// <param name="waiter">Supplied by the compiler. Names the calling member in the failure message.</param>
    /// <param name="file">Supplied by the compiler. Names the calling file in the failure message.</param>
    /// <param name="line">Supplied by the compiler. Names the calling line in the failure message.</param>
    public static async Task ForTeardownAsync(
        Func<ValueTask> teardown,
        string because,
        TimeSpan? timeout = null,
        [CallerMemberName] string? waiter = null,
        [CallerFilePath] string? file = null,
        [CallerLineNumber] int line = 0
    )
    {
        ArgumentNullException.ThrowIfNull(teardown);

        var budget = timeout ?? DefaultTeardownTimeout;
        var work = teardown().AsTask();
        try
        {
            await work.WaitAsync(budget);
        }
        catch (TimeoutException)
        {
            // The abandoned teardown outlives this await. Observe whatever it eventually does so a
            // later fault cannot surface as an unobserved-task exception on the finalizer thread,
            // attributed to some unrelated test.
            _ = work.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );

            throw new TimeoutException(
                $"Timed out after {budget.TotalSeconds:0.###}s tearing down {because}. "
                    + $"Waiter: {waiter} ({Path.GetFileName(file)}:{line}). The teardown never "
                    + "returned — something it awaits is still blocked, most likely because a test "
                    + "body threw before releasing it. Unbounded, this wedged the testhost until "
                    + "dotnet test aborted the entire run instead of reporting the failure."
            );
        }
    }

    /// <summary>
    /// Polls until <paramref name="condition"/> holds, reporting whether it did rather than throwing.
    /// </summary>
    /// <remarks>
    /// Prefer <see cref="UntilAsync(Func{bool}, string, TimeSpan?, TimeSpan?, CancellationToken, string?, string?, int)"/>.
    /// Reach for this only to wait on the <em>other</em> side's progress before asserting that
    /// something has NOT happened, and pass a condition that also becomes true on the regression, so
    /// a broken implementation fails fast instead of burning the whole deadline first.
    /// </remarks>
    /// <param name="condition">Evaluated repeatedly, as above.</param>
    /// <param name="timeout">Deadline for the whole wait. Defaults to <see cref="DefaultTimeout"/>.</param>
    /// <param name="pollInterval">Gap between evaluations. Defaults to <see cref="DefaultPollInterval"/>.</param>
    /// <param name="cancellationToken">Abandons the wait; surfaces as an <see cref="OperationCanceledException"/>.</param>
    /// <returns><see langword="true"/> if the condition held before the deadline.</returns>
    public static Task<bool> TryUntilAsync(
        Func<bool> condition,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(condition);
        return TryUntilAsync(
            () => Task.FromResult(condition()),
            timeout,
            pollInterval,
            cancellationToken
        );
    }

    /// <inheritdoc cref="TryUntilAsync(Func{bool}, TimeSpan?, TimeSpan?, CancellationToken)" />
    /// <param name="condition">Asynchronous form; otherwise as above.</param>
    /// <param name="timeout">Deadline for the whole wait. Defaults to <see cref="DefaultTimeout"/>.</param>
    /// <param name="pollInterval">Gap between evaluations. Defaults to <see cref="DefaultPollInterval"/>.</param>
    /// <param name="cancellationToken">Abandons the wait; surfaces as an <see cref="OperationCanceledException"/>.</param>
    /// <returns><see langword="true"/> if the condition held before the deadline.</returns>
    public static async Task<bool> TryUntilAsync(
        Func<Task<bool>> condition,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(condition);

        var interval = pollInterval ?? DefaultPollInterval;
        // Monotonic, so a system clock adjustment mid-test cannot shorten or extend the budget.
        var deadline = Environment.TickCount64 + (long)(timeout ?? DefaultTimeout).TotalMilliseconds;

        while (true)
        {
            if (await condition())
            {
                return true;
            }

            // Checked AFTER evaluating, so a condition that is already true never sleeps, and a
            // zero timeout still gets exactly one honest evaluation rather than none.
            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            await Task.Delay(interval, cancellationToken);
        }
    }
}
