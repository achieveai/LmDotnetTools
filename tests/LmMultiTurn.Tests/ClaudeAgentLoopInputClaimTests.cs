using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Agents;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// The accepted-input invariant where <see cref="ClaudeAgentLoop"/> meets
/// <c>MultiTurnAgentBase</c>'s drained-input claim.
///
/// <para>
/// <c>HasUnassignedInput</c> exists so a host never disposes an agent that has acknowledged an
/// input no run owns yet. Interactive mode stresses it in two ways the base alone does not:
/// </para>
/// <list type="number">
/// <item><description>
/// the input watcher PARKS drained batches in a local queue while a run is in flight, so a claim
/// has to survive later drains — and <c>_inputsInHand</c> is settled by ASSIGNMENT, so it cannot;
/// </description></item>
/// <item><description>
/// the watcher is a SECOND reader of the input channel, so it has to be gone — not merely
/// cancelled — before interactive execution returns and the run loop drains that channel again.
/// </description></item>
/// </list>
/// </summary>
public class ClaudeAgentLoopInputClaimTests
{
    /// <summary>
    /// Park two acknowledged batches, then drain a third that carries no work at all.
    ///
    /// <para>
    /// The third drain is the discriminator. <c>TryDrainInputs</c> settles the in-hand claim by
    /// assignment — <c>Volatile.Write(ref _inputsInHand, work)</c> — so a batch that carries no
    /// work settles it to ZERO. With the parked batches relying on that field, the agent reports
    /// itself idle while holding two inputs it has already handed a receipt for, which is exactly
    /// the state a host reads immediately before disposing it. Retention has to be additive and
    /// per-batch for the claim to mean anything once a batch is parked.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ParkedBatches_KeepHasUnassignedInputTrue_WhenALaterDrainCarriesNoWork()
    {
        var client = new GatedClaudeAgentSdkClient();

        await using var loop = new ClaudeAgentLoop(
            claudeOptions: new ClaudeAgentSdkOptions { Mode = ClaudeAgentSdkMode.Interactive, MaxTurnsPerRun = 5 },
            mcpServers: null,
            threadId: "parked-input-claim",
            clientFactory: (_, _) => client
        );

        using var cts = new CancellationTokenSource();
        var runTask = loop.RunAsync(cts.Token);

        try
        {
            // Run 1 is in flight the moment the loop has pushed its initial messages at the CLI.
            // From here _runInProgress is true, so every further input the watcher drains is parked.
            _ = await loop.SendAsync(UserText("first", "i1"));
            (await WaitForAsync(() => client.SendCallCount >= 1))
                .Should()
                .BeTrue("the loop must start run 1 before anything can be parked");

            _ = await loop.SendAsync(UserText("second", "i2"));
            (await WaitForAsync(() => loop.ParkedInputBatchCountForTest >= 1))
                .Should()
                .BeTrue("the watcher parks input while a run is in progress");

            _ = await loop.SendAsync(UserText("third", "i3"));
            (await WaitForAsync(() => loop.ParkedInputBatchCountForTest >= 2))
                .Should()
                .BeTrue("a second batch parks behind the first");

            loop.HasUnassignedInput.Should()
                .BeTrue("two acknowledged inputs are parked and no run owns either of them");

            // A batch the base itself classifies as carrying no work: Resume is null but Messages is
            // empty, so CarriesUnassignedWork is false for every item in it. Draining it settles the
            // in-hand claim to zero.
            _ = await loop.SendAsync(new UserInput([], InputId: "no-work"));
            (await WaitForAsync(() => loop.ParkedInputBatchCountForTest >= 3))
                .Should()
                .BeTrue("the work-free batch is drained and parked like any other");

            loop.HasUnassignedInput.Should()
                .BeTrue(
                    "a drain that carries no work must not cancel the claim on the two work-carrying "
                        + "batches still parked ahead of it — they are acknowledged, and no run owns them"
                );

            // Let run 1 finish. The loop then merges every parked batch into one run, which is the
            // point the claim is finally allowed to drop: CurrentRunId now names these inputs.
            await client.EmitAsync(new ResultEventMessage { IsError = false });
            (await WaitForAsync(() => client.SendCallCount >= 2)).Should().BeTrue("the merged run must actually start");

            (await WaitForAsync(() => !loop.HasUnassignedInput))
                .Should()
                .BeTrue(
                    "once a run owns every parked batch the retained claim must be given back — "
                        + "an un-released retention would strand the agent as permanently busy"
                );
        }
        finally
        {
            await client.EmitAsync(new ResultEventMessage { IsError = false });
            client.Complete();
            await cts.CancelAsync();
            await SettleAsync(loop, runTask);
        }
    }

    /// <summary>
    /// Interactive execution must not return while its input watcher is still alive.
    ///
    /// <para>
    /// Cancelling the watcher only ASKS it to stop. If the caller returns on the request rather than
    /// on the fact, the run loop's next iteration drains the input channel while the previous run's
    /// watcher is still draining it too: two readers splitting one acknowledged batch, each settling
    /// the shared in-hand claim by assignment, and one of them writing into a local queue the other
    /// is dequeuing from.
    /// </para>
    /// <para>
    /// The watcher is held inside its exit path by a gate the test owns, so "still alive" is a fact
    /// and not a race. Both directions are asserted against the same probe — no second run while the
    /// watcher is held, a second run once it is released — so a probe that simply never fires cannot
    /// pass this test.
    /// </para>
    /// </summary>
    [Fact]
    public async Task InteractiveExecution_WaitsForTheInputWatcher_BeforeTheRunLoopReadsTheChannelAgain()
    {
        var client = new GatedClaudeAgentSdkClient();
        var watcherEnteredExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWatcher = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var loop = new ClaudeAgentLoop(
            claudeOptions: new ClaudeAgentSdkOptions { Mode = ClaudeAgentSdkMode.Interactive, MaxTurnsPerRun = 5 },
            mcpServers: null,
            threadId: "watcher-shutdown",
            clientFactory: (_, _) => client
        );

        loop.InputWatcherExitHookForTest = () =>
        {
            _ = watcherEnteredExit.TrySetResult();
            return releaseWatcher.Task;
        };

        using var cts = new CancellationTokenSource();
        var runTask = loop.RunAsync(cts.Token);

        try
        {
            _ = await loop.SendAsync(UserText("first", "i1"));
            (await WaitForAsync(() => client.SendCallCount >= 1))
                .Should()
                .BeTrue("run 1 must be in flight before it can be completed");

            // Complete run 1. The watcher is cancelled and walks into the gate, where it stays.
            await client.EmitAsync(new ResultEventMessage { IsError = false });
            await watcherEnteredExit.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // Only now is a second input introduced — the watcher is provably past its own drain
            // loop, so whoever picks this up is the run loop.
            _ = await loop.SendAsync(UserText("second", "i2"));

            var advancedWhileWatcherAlive = await WaitForAsync(
                () => client.SendCallCount >= 2,
                TimeSpan.FromSeconds(2)
            );

            advancedWhileWatcherAlive
                .Should()
                .BeFalse(
                    "the run loop must not start a second run — and so must not drain the input channel "
                        + "again — while the previous run's input watcher is still running"
                );

            _ = releaseWatcher.TrySetResult();

            var advancedAfterRelease = await WaitForAsync(() => client.SendCallCount >= 2);
            advancedAfterRelease
                .Should()
                .BeTrue(
                    "once the watcher is gone the run loop must pick the input up; a probe that could "
                        + "never fire would prove nothing about the assertion above"
                );
        }
        finally
        {
            _ = releaseWatcher.TrySetResult();
            await client.EmitAsync(new ResultEventMessage { IsError = false });
            client.Complete();
            await cts.CancelAsync();
            await SettleAsync(loop, runTask);
        }
    }

    private static UserInput UserText(string text, string inputId) =>
        new([new TextMessage { Text = text, Role = Role.User }], InputId: inputId);

    private static async Task SettleAsync(ClaudeAgentLoop loop, Task runTask)
    {
        try
        {
            await loop.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (OperationCanceledException)
        {
            // Stopping a cancelled loop is the expected exit.
        }

        try
        {
            await runTask.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (OperationCanceledException)
        {
            // Same.
        }
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }

    /// <summary>
    /// A Claude SDK client whose message stream the test drives item by item, so every point at
    /// which the loop changes state is reached on purpose rather than after a sleep.
    /// </summary>
    private sealed class GatedClaudeAgentSdkClient : IClaudeAgentSdkClient
    {
        private readonly Channel<IMessage> _stream = Channel.CreateUnbounded<IMessage>();
        private int _sendCallCount;

        public int SendCallCount => Volatile.Read(ref _sendCallCount);

        public bool IsRunning { get; private set; }

        public SessionInfo? CurrentSession { get; private set; }

        public ClaudeAgentSdkRequest? LastRequest { get; private set; }

        public ValueTask EmitAsync(IMessage message) => _stream.Writer.WriteAsync(message);

        public void Complete() => _stream.Writer.TryComplete();

        public Task StartAsync(ClaudeAgentSdkRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            IsRunning = true;
            CurrentSession = new SessionInfo
            {
                SessionId = "gated-session",
                CreatedAt = DateTime.UtcNow,
                ProjectRoot = "test",
            };
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<IMessage> SendMessagesAsync(
            IEnumerable<IMessage> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            await foreach (var msg in _stream.Reader.ReadAllAsync(cancellationToken))
            {
                yield return msg;
            }
        }

        public async IAsyncEnumerable<IMessage> SubscribeToMessagesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            await foreach (var msg in _stream.Reader.ReadAllAsync(cancellationToken))
            {
                yield return msg;
            }
        }

        public Task SendAsync(IEnumerable<IMessage> messages, CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _sendCallCount);
            return Task.CompletedTask;
        }

        public Task<bool> SendExitCommandAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task ShutdownAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            IsRunning = false;
            Complete();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsRunning = false;
            Complete();
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            IsRunning = false;
            Complete();
        }
    }
}
