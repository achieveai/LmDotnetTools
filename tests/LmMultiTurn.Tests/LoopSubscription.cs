using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;

namespace LmMultiTurn.Tests;

/// <summary>
/// Helpers for tests that observe a loop's published output on a background task.
/// </summary>
internal static class LoopSubscription
{
    /// <summary>
    /// Subscribes to <paramref name="agent"/> and drains it on a background task, returning only once the
    /// subscription is REGISTERED with the agent.
    /// <para>
    /// <c>SubscribeAsync</c> is an async iterator, so its body — including registering the subscriber —
    /// does not run until the first <c>MoveNextAsync</c>. Starting a background task that subscribes
    /// (even one that signals a "subscribed" TaskCompletionSource before its <c>await foreach</c>) only
    /// proves the task started, not that it is registered: under load the run the test triggers next can
    /// publish AND complete first, and the joining subscriber then gets nothing — the replay buffer only
    /// covers a run that is still in flight. The test then waits for a message nobody will ever deliver.
    /// Pumping the first <c>MoveNextAsync</c> here, on the calling thread, makes registration a
    /// happens-before of this method returning.
    /// </para>
    /// </summary>
    /// <param name="agent">The agent to observe.</param>
    /// <param name="onMessage">Invoked on the background task for each published message.</param>
    /// <param name="ct">Cancels the subscription; cancellation ends the drain silently.</param>
    /// <returns>A handle whose <see cref="Drain.WaitAsync"/> waits without letting a broken drain
    /// masquerade as a timeout.</returns>
    public static Drain StartDraining(
        IMultiTurnAgent agent,
        Action<IMessage> onMessage,
        CancellationToken ct)
    {
        var enumerator = agent.SubscribeAsync(ct).GetAsyncEnumerator(ct);
        var first = enumerator.MoveNextAsync();

        return new Drain(Task.Run(async () =>
        {
            try
            {
                for (var hasMessage = await first; hasMessage; hasMessage = await enumerator.MoveNextAsync())
                {
                    onMessage(enumerator.Current);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                await enumerator.DisposeAsync();
            }
        }));
    }

    /// <summary>
    /// Drains <paramref name="agent"/> and signals the first <paramref name="expectedCount"/>
    /// <see cref="RunCompletedMessage"/>s in order, so a test can wait for "run N finished" by index.
    /// </summary>
    public static RunCompletions SubscribeForRunCompletions(
        IMultiTurnAgent agent,
        CancellationToken ct,
        int expectedCount)
    {
        var sources = Enumerable.Range(0, expectedCount)
            .Select(_ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToList();
        var completed = 0;

        var drain = StartDraining(
            agent,
            msg =>
            {
                if (msg is RunCompletedMessage)
                {
                    var idx = completed;
                    completed++;
                    if (idx < sources.Count)
                    {
                        _ = sources[idx].TrySetResult(true);
                    }
                }
            },
            ct);

        return new RunCompletions(drain, sources);
    }
}

/// <summary>Run-completion signals fed by a <see cref="Drain"/>, waited on by run index.</summary>
internal sealed class RunCompletions(Drain drain, IReadOnlyList<TaskCompletionSource<bool>> sources)
{
    /// <summary>Waits for the <paramref name="index"/>-th run completion (0-based).</summary>
    public Task WaitAsync(int index) =>
        drain.WaitAsync(sources[index].Task, TimeSpan.FromSeconds(5));
}

/// <summary>
/// A running subscription drain. Waiting through <see cref="WaitAsync"/> rather than on the expectation
/// alone is what keeps a broken subscription from presenting as a mysterious timeout: a drain that throws
/// stops delivering messages, so every later wait would sit out its full timeout and then report only that
/// nothing arrived — never the exception that caused it.
/// </summary>
internal sealed class Drain(Task drainTask)
{
    /// <summary>Stands in for "the drain is still healthy", which is not an outcome to act on — only a
    /// faulted drain is. Shared because it never completes and therefore carries no per-wait state.</summary>
    private static readonly Task NeverCompletes =
        new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously).Task;

    /// <summary>
    /// Awaits <paramref name="expectation"/> for at most <paramref name="timeout"/>, failing early with the
    /// drain's own exception if the drain broke first.
    /// </summary>
    public Task WaitAsync(Task expectation, TimeSpan timeout) =>
        Task.WhenAny(expectation, FaultAsync()).Unwrap().WaitAsync(timeout);

    private async Task FaultAsync()
    {
        try
        {
            await drainTask;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "The subscription drain failed, so no further published messages were delivered.", ex);
        }

        // A drain that ended cleanly (subscription closed) is not a failure — what it already delivered may
        // well have satisfied the caller — so leave the outcome to the expectation or the timeout.
        await NeverCompletes;
    }
}
