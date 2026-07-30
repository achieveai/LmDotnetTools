using System.Collections.Concurrent;
using AchieveAi.LmDotnetTools.LmCore.Transport;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Transport;

/// <summary>
/// Pins what the dispatcher exists for: the thread that reads a request gets control back before
/// the handler runs, handlers finish in whatever order they finish, capacity is a refusal rather
/// than a queue, and shutdown cancels rather than waits.
/// </summary>
public class BoundedServerRequestDispatcherTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    [Fact]
    public void Constructor_RejectsANonPositiveCapacity()
    {
        // Zero would refuse every request while looking like configuration rather than a bug.
        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => new BoundedServerRequestDispatcher(0)
        );
        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => new BoundedServerRequestDispatcher(-1)
        );
    }

    [Fact]
    public async Task TryDispatch_ReturnsWhileTheHandlerStillOwnsAThread()
    {
        await using var dispatcher = new BoundedServerRequestDispatcher(2);
        using var handlerMayFinish = new ManualResetEventSlim(false);
        using var handlerStarted = new ManualResetEventSlim(false);

        // The handler blocks its thread outright rather than awaiting, which is the case an
        // `await handler(...)` on the read loop could not survive. Dispatching from a separate
        // task turns a regression into a timeout here instead of a hung test run.
        var callerReturned = Task.Run(
            () =>
                dispatcher.TryDispatch(
                    _ =>
                    {
                        handlerStarted.Set();
                        handlerMayFinish.Wait();
                        return Task.CompletedTask;
                    },
                    CancellationToken.None
                )
        );

        Assert.True(await callerReturned.WaitAsync(Generous));
        Assert.True(handlerStarted.Wait(Generous), "the handler never ran");
        Assert.False(handlerMayFinish.IsSet, "the handler finished, so nothing was proven");

        handlerMayFinish.Set();
    }

    [Fact]
    public async Task Handlers_FinishInTheOrderTheyResolve_NotTheOrderTheyArrived()
    {
        await using var dispatcher = new BoundedServerRequestDispatcher(4);
        var gates = new[]
        {
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var finished = new ConcurrentQueue<int>();
        var signals = gates
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();

        for (var i = 0; i < gates.Length; i++)
        {
            var index = i;
            Assert.True(
                dispatcher.TryDispatch(
                    async token =>
                    {
                        await gates[index].Task.WaitAsync(token);
                        finished.Enqueue(index);
                        _ = signals[index].TrySetResult();
                    },
                    CancellationToken.None
                )
            );
        }

        // Resolve the last one first. Anything that handled requests in arrival order — which is
        // what an inline await on the read loop amounts to — could not produce this.
        foreach (var index in new[] { 2, 0, 1 })
        {
            _ = gates[index].TrySetResult();
            await signals[index].Task.WaitAsync(Generous);
        }

        Assert.Equal([2, 0, 1], [.. finished]);
    }

    [Fact]
    public async Task TryDispatch_RefusesAtCapacityAndAdmitsAgainOnceASlotFrees()
    {
        await using var dispatcher = new BoundedServerRequestDispatcher(2);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(dispatcher.TryDispatch(_ => first.Task, CancellationToken.None));
        Assert.True(dispatcher.TryDispatch(_ => second.Task, CancellationToken.None));
        await WaitUntilAsync(() => dispatcher.InFlightCount == 2);

        // Refused rather than queued: a queue would just relocate the unbounded growth.
        Assert.False(
            dispatcher.TryDispatch(_ => Task.CompletedTask, CancellationToken.None),
            "a third request was admitted past a capacity of two"
        );

        _ = first.TrySetResult();
        await WaitUntilAsync(() => dispatcher.InFlightCount == 1);

        Assert.True(dispatcher.TryDispatch(_ => Task.CompletedTask, CancellationToken.None));
        _ = second.TrySetResult();
    }

    [Fact]
    public async Task AHandlerThatThrows_ReleasesItsSlotAndLeavesTheDispatcherUsable()
    {
        await using var dispatcher = new BoundedServerRequestDispatcher(1);

        Assert.True(
            dispatcher.TryDispatch(
                _ => Task.FromException(new InvalidOperationException("handler blew up")),
                CancellationToken.None
            )
        );
        await WaitUntilAsync(() => dispatcher.InFlightCount == 0);

        // Nothing awaits a dispatched handler, so a swallowed failure is the only thing standing
        // between a throwing handler and an unobserved exception charged to unrelated work later.
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(
            dispatcher.TryDispatch(
                token =>
                {
                    _ = ran.TrySetResult();
                    return Task.CompletedTask;
                },
                CancellationToken.None
            )
        );
        await ran.Task.WaitAsync(Generous);
    }

    [Fact]
    public async Task DisposeAsync_CancelsHandlersInsteadOfWaitingForThem()
    {
        var dispatcher = new BoundedServerRequestDispatcher(2);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(
            dispatcher.TryDispatch(
                async token =>
                {
                    _ = started.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.Infinite, token);
                    }
                    catch (OperationCanceledException)
                    {
                        _ = cancelled.TrySetResult();
                        throw;
                    }
                },
                CancellationToken.None
            )
        );
        await started.Task.WaitAsync(Generous);

        // A handler parked on an approval would otherwise hold shutdown open for as long as nobody
        // answers, which is exactly the wait that has no upper bound.
        await dispatcher.DisposeAsync().AsTask().WaitAsync(Generous);
        await cancelled.Task.WaitAsync(Generous);
    }

    [Fact]
    public async Task TryDispatch_RefusesOnceDisposed()
    {
        var dispatcher = new BoundedServerRequestDispatcher(2);
        await dispatcher.DisposeAsync();

        Assert.False(dispatcher.TryDispatch(_ => Task.CompletedTask, CancellationToken.None));
    }

    [Fact]
    public async Task TheSessionToken_CancelsHandlersWithoutDisposingTheDispatcher()
    {
        await using var dispatcher = new BoundedServerRequestDispatcher(2);
        using var session = new CancellationTokenSource();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(
            dispatcher.TryDispatch(
                async token =>
                {
                    _ = started.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.Infinite, token);
                    }
                    catch (OperationCanceledException)
                    {
                        _ = cancelled.TrySetResult();
                        throw;
                    }
                },
                session.Token
            )
        );
        await started.Task.WaitAsync(Generous);

        await session.CancelAsync();

        await cancelled.Task.WaitAsync(Generous);
        await WaitUntilAsync(() => dispatcher.InFlightCount == 0);
    }

    /// <summary>
    /// Waits for a condition the dispatcher reaches on its own thread. Slot release happens after
    /// a handler's continuation runs, so there is no signal the caller can await directly.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Generous);
        while (!condition())
        {
            if (timeout.IsCancellationRequested)
            {
                Assert.Fail($"the dispatcher never reached the expected state within {Generous}");
            }

            await Task.Delay(10, CancellationToken.None);
        }
    }
}
