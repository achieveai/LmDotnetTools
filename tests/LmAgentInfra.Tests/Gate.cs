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
internal sealed class Gate
{
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Wakes every current waiter so each can re-evaluate its condition.</summary>
    internal void Signal() =>
        Interlocked
            .Exchange(
                ref _signal,
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            )
            .TrySetResult();

    /// <summary>Completes once <paramref name="condition"/> holds.</summary>
    internal async Task WaitAsync(Func<bool> condition)
    {
        while (true)
        {
            var signal = Volatile.Read(ref _signal).Task;
            if (condition())
            {
                return;
            }

            await signal;
        }
    }
}
