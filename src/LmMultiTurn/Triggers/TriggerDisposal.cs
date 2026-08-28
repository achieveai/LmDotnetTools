namespace AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;

/// <summary>
/// Disposal helpers shared by <see cref="IArmedTrigger"/> implementations.
/// </summary>
public static class TriggerDisposal
{
    /// <summary>
    /// Disposes <paramref name="cancellationTokenSource"/> once <paramref name="backgroundTask"/>
    /// settles, without awaiting that task.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An armed trigger's <c>DisposeAsync</c> must NOT await its own background task. Disposal is
    /// typically invoked from inside the runtime's fire-handling callback — that is, from a
    /// continuation of the very task being awaited — so awaiting it there deadlocks. Cancelling and
    /// then deferring the CTS dispose to a continuation is the way out, and every trigger source
    /// needs the same one.
    /// </para>
    /// <para>
    /// The continuation swallows <see cref="ObjectDisposedException"/> because two disposals can
    /// race to the same CTS and the second has nothing left to do. It runs with
    /// <see cref="TaskContinuationOptions.ExecuteSynchronously"/> on
    /// <see cref="TaskScheduler.Default"/> and an uncancellable token: the continuation must run
    /// exactly once, whatever the task's outcome and whatever ambient scheduler the caller happens
    /// to be on.
    /// </para>
    /// </remarks>
    /// <param name="backgroundTask">The trigger's watch/poll task. Not awaited.</param>
    /// <param name="cancellationTokenSource">The CTS to dispose once that task settles.</param>
    public static void DisposeAfter(Task backgroundTask, CancellationTokenSource cancellationTokenSource)
    {
        ArgumentNullException.ThrowIfNull(backgroundTask);
        ArgumentNullException.ThrowIfNull(cancellationTokenSource);

        _ = backgroundTask.ContinueWith(
            _ =>
            {
                try
                {
                    cancellationTokenSource.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // Already disposed — nothing to do.
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }
}
