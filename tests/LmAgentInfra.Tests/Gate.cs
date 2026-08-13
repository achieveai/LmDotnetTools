using System.Runtime.CompilerServices;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests;

/// <summary>
/// An edge-triggered condition wait. The condition is captured <em>after</em> the wake-up signal is
/// captured, so a state change that lands between the two still wakes the waiter instead of
/// stranding it until the next one.
/// </summary>
/// <remarks>
/// This is what lets these suites keep their "nothing sleeps" contract while still waiting on work
/// that happens on another thread. A polling loop with a delay would pass on a fast machine and
/// flake on a loaded one; a bare <c>TaskCompletionSource</c> would miss the signal that arrives
/// while the waiter is between checks.
/// </remarks>
internal sealed class Gate(TimeSpan? waitCeiling = null)
{
    /// <summary>
    /// Ceiling on a single wait. Nothing here is supposed to take wall-clock time at all, so this is
    /// never reached by a healthy test — it exists because the failure mode of an UNBOUNDED wait is
    /// far worse than a late one. A condition that never holds (its producer signalled before the
    /// waiter arrived at a state the waiter can no longer observe, or was never going to hold at all)
    /// wedges the testhost indefinitely; `dotnet test`'s inactivity blame-dump then aborts the WHOLE
    /// run, so every assembly queued behind this one never executes and the console reports a crash
    /// rather than a test. Bounded, the same situation is one red test naming the waiter that stalled.
    /// Overridable only so the bound itself can be tested without a 30s test.
    /// </summary>
    private readonly TimeSpan _waitCeiling = waitCeiling ?? TimeSpan.FromSeconds(30);

    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Wakes every current waiter so each can re-evaluate its condition.</summary>
    internal void Signal() =>
        Interlocked
            .Exchange(
                ref _signal,
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            )
            .TrySetResult();

    /// <summary>Completes once <paramref name="condition"/> holds, or throws after the wait
    /// ceiling.</summary>
    internal async Task WaitAsync(
        Func<bool> condition,
        [CallerMemberName] string? waiter = null,
        [CallerFilePath] string? file = null,
        [CallerLineNumber] int line = 0
    )
    {
        using var ceiling = new CancellationTokenSource(_waitCeiling);
        while (true)
        {
            var signal = Volatile.Read(ref _signal).Task;
            if (condition())
            {
                return;
            }

            try
            {
                await signal.WaitAsync(ceiling.Token);
            }
            catch (OperationCanceledException) when (ceiling.IsCancellationRequested)
            {
                // Re-check before failing: the condition may have become true in the same instant the
                // ceiling elapsed, and reporting a timeout for a satisfied wait would be its own flake.
                if (condition())
                {
                    return;
                }

                throw new TimeoutException(
                    $"Gate.WaitAsync timed out after {_waitCeiling.TotalSeconds:0.###}s waiting for the "
                        + $"condition in {waiter} ({Path.GetFileName(file)}:{line}). The condition never "
                        + "held, which previously wedged the testhost until dotnet test aborted the "
                        + "entire run."
                );
            }
        }
    }
}
