using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Tests for expected-run foreground cancellation (WI #194, tasks 1-4):
/// <see cref="IMultiTurnAgent.CancelCurrentRunAsync"/> cancels exactly one matching run via a
/// per-run <see cref="CancellationTokenSource"/> that is linked to, but independently
/// cancellable from, the background loop's own lifetime token; a matching cancellation and a
/// natural/error completion race through one exactly-once terminal arbiter in
/// <c>MultiTurnAgentBase.CompleteRunAsync</c>; a stale <c>expectedRunId</c> cannot cancel a
/// newer run; and the loop keeps accepting/completing subsequent input after a cancellation.
/// </summary>
public class RunCancellationTests
{
    #region MultiTurnAgentLoop integration — matching cancellation

    [Fact]
    public async Task CancelCurrentRunAsync_MatchingExpectedRunId_CancelsRun_EmitsSingleCancelledTerminal_AndLoopContinues()
    {
        var mockAgent = new Mock<IStreamingAgent>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverReleased = new TaskCompletionSource().Task;
        SetupHangingThenNormalResponses(mockAgent, started, neverReleased);

        var store = new InMemoryConversationStore();
        var registry = new FunctionRegistry();
        await using var loop = new MultiTurnAgentLoop(
            mockAgent.Object,
            registry,
            "cancel-thread-match",
            store: store,
            persistRunLedger: true);

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var input1 = new UserInput([new TextMessage { Text = "Hi", Role = Role.User }], InputId: "in-1");
        var (runId1, run1Completion) = await StartRunAndCaptureAssignmentAsync(loop, input1, cts.Token);

        // Confirm the turn is actually mid-flight before cancelling, so the cancellation races
        // real in-flight work rather than a run that has not started its turn yet.
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var outcome = await loop.CancelCurrentRunAsync(runId1);
        outcome.Should().Be(RunCancellationResult.Accepted);

        var messages1 = await run1Completion.WaitAsync(TimeSpan.FromSeconds(5));

        // Exactly one terminal for this run, and it is the Cancelled one.
        var completions1 = messages1.OfType<RunCompletedMessage>().ToList();
        completions1.Should().HaveCount(1);
        completions1[0].CompletedRunId.Should().Be(runId1);
        completions1[0].IsCancelled.Should().BeTrue();
        completions1[0].IsError.Should().BeFalse();

        // Durable ledger status matches the published terminal outcome.
        var ledgerEntry = await store.LoadRunLedgerAsync(runId1);
        ledgerEntry.Should().NotBeNull();
        ledgerEntry!.Status.Should().Be(RunStatus.Cancelled);

        // Loop liveness: the outer background loop must still accept and complete new input —
        // a matching per-run cancellation must never propagate to the outer loop's own
        // cancellation handler and tear the loop down.
        var input2 = new UserInput([new TextMessage { Text = "Again", Role = Role.User }], InputId: "in-2");
        var messages2 = new List<IMessage>();
        await foreach (var msg in loop.ExecuteRunAsync(input2, cts.Token))
        {
            messages2.Add(msg);
        }

        messages2.OfType<RunCompletedMessage>().Should()
            .ContainSingle(m => !m.IsCancelled && !m.IsError);

        await cts.CancelAsync();
    }

    #endregion

    #region MultiTurnAgentLoop integration — stale expected run

    [Fact]
    public async Task CancelCurrentRunAsync_StaleExpectedRunId_ReturnsStaleRun_AndDoesNotAffectNewerRun()
    {
        var mockAgent = new Mock<IStreamingAgent>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SetupFirstNormalThenGatedResponses(mockAgent, started, releaseGate.Task);

        var registry = new FunctionRegistry();
        await using var loop = new MultiTurnAgentLoop(mockAgent.Object, registry, "cancel-thread-stale");

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        // Run A1 completes fast and naturally.
        var input1 = new UserInput([new TextMessage { Text = "Hi", Role = Role.User }], InputId: "stale-in-1");
        string? runId1 = null;
        await foreach (var msg in loop.ExecuteRunAsync(input1, cts.Token))
        {
            if (msg is RunAssignmentMessage ram)
            {
                runId1 = ram.Assignment.RunId;
            }
        }

        runId1.Should().NotBeNull();

        // Run A2 starts and is mid-flight (gated, not yet released) when the delayed Stop for
        // A1 arrives.
        var input2 = new UserInput([new TextMessage { Text = "Again", Role = Role.User }], InputId: "stale-in-2");
        var (runId2, run2Completion) = await StartRunAndCaptureAssignmentAsync(loop, input2, cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        runId2.Should().NotBe(runId1);

        // A delayed Stop naming the now-superseded A1 must be rejected as stale...
        var staleOutcome = await loop.CancelCurrentRunAsync(runId1!);
        staleOutcome.Should().Be(RunCancellationResult.StaleRun);

        // ...and must NOT have touched A2's own cancellation token: releasing A2's gate lets it
        // complete NORMALLY (not via cancellation), proving the stale call never reached it.
        releaseGate.TrySetResult();
        var messages2 = await run2Completion.WaitAsync(TimeSpan.FromSeconds(5));

        var completions2 = messages2.OfType<RunCompletedMessage>().ToList();
        completions2.Should().HaveCount(1);
        completions2[0].CompletedRunId.Should().Be(runId2);
        completions2[0].IsCancelled.Should().BeFalse();
        completions2[0].IsError.Should().BeFalse();

        await cts.CancelAsync();
    }

    #endregion

    #region MultiTurnAgentLoop integration — no active run

    [Fact]
    public async Task CancelCurrentRunAsync_NoActiveRun_ReturnsNoActiveRun()
    {
        var mockAgent = new Mock<IStreamingAgent>();
        var registry = new FunctionRegistry();
        await using var loop = new MultiTurnAgentLoop(mockAgent.Object, registry, "cancel-thread-idle");

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var outcome = await loop.CancelCurrentRunAsync("no-such-run");

        outcome.Should().Be(RunCancellationResult.NoActiveRun);

        await cts.CancelAsync();
    }

    #endregion

    #region MultiTurnAgentLoop integration — primary/child parity

    [Fact]
    public async Task CancelCurrentRunAsync_AppliesUniformly_ToPrimaryAndChildLikeAgents()
    {
        // Two independent IMultiTurnAgent instances model a primary agent and a
        // SubAgentManager-owned child (also a MultiTurnAgentLoop implementing the same
        // IMultiTurnAgent contract). CancelCurrentRunAsync must behave identically for both,
        // without cross-talk between the two agents' run state.
        var mockPrimary = new Mock<IStreamingAgent>();
        var mockChild = new Mock<IStreamingAgent>();
        var startedPrimary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedChild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverReleased = new TaskCompletionSource().Task;
        SetupHangingThenNormalResponses(mockPrimary, startedPrimary, neverReleased);
        SetupHangingThenNormalResponses(mockChild, startedChild, neverReleased);

        var registryPrimary = new FunctionRegistry();
        var registryChild = new FunctionRegistry();
        await using var primary = new MultiTurnAgentLoop(mockPrimary.Object, registryPrimary, "primary-thread");
        await using var child = new MultiTurnAgentLoop(mockChild.Object, registryChild, "subagent-child-thread");

        using var ctsPrimary = new CancellationTokenSource();
        using var ctsChild = new CancellationTokenSource();
        _ = primary.RunAsync(ctsPrimary.Token);
        _ = child.RunAsync(ctsChild.Token);

        var inputPrimary = new UserInput([new TextMessage { Text = "Hi", Role = Role.User }], InputId: "primary-in-1");
        var inputChild = new UserInput([new TextMessage { Text = "Hi", Role = Role.User }], InputId: "child-in-1");

        var (runIdPrimary, primaryCompletion) =
            await StartRunAndCaptureAssignmentAsync(primary, inputPrimary, ctsPrimary.Token);
        var (runIdChild, childCompletion) =
            await StartRunAndCaptureAssignmentAsync(child, inputChild, ctsChild.Token);

        await startedPrimary.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await startedChild.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var outcomePrimary = await primary.CancelCurrentRunAsync(runIdPrimary);
        var outcomeChild = await child.CancelCurrentRunAsync(runIdChild);

        outcomePrimary.Should().Be(RunCancellationResult.Accepted);
        outcomeChild.Should().Be(RunCancellationResult.Accepted);

        var messagesPrimary = await primaryCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        var messagesChild = await childCompletion.WaitAsync(TimeSpan.FromSeconds(5));

        messagesPrimary.OfType<RunCompletedMessage>().Should()
            .ContainSingle(m => m.CompletedRunId == runIdPrimary && m.IsCancelled);
        messagesChild.OfType<RunCompletedMessage>().Should()
            .ContainSingle(m => m.CompletedRunId == runIdChild && m.IsCancelled);

        await ctsPrimary.CancelAsync();
        await ctsChild.CancelAsync();
    }

    #endregion

    #region MultiTurnAgentBase — exactly-once terminal arbiter (cancellation vs. natural completion)

    [Fact]
    public async Task CompleteRunAsync_CancellationRacingNaturalCompletion_EmitsExactlyOneTerminal()
    {
        // Models the race explicitly rather than relying on real OS-thread timing: the run's
        // own per-run wrapper always calls CompleteRunAsync sequentially at most twice for the
        // same runId (natural completion, immediately "raced" by a late matching-cancellation
        // completion attempt) — exactly the shape a genuine concurrent CancelCurrentRunAsync
        // call arriving just after natural completion would produce. The exactly-once arbiter
        // in CompleteRunAsync must let only the FIRST call win.
        var store = new InMemoryConversationStore();
        const string threadId = "race-thread";

        await using var agent = new ArbiterTestAgent(threadId, store, persistRunLedger: true);
        using var cts = new CancellationTokenSource();
        _ = agent.RunAsync(cts.Token);

        // ExecuteRunAsync subscribes BEFORE sending (avoiding a SubscribeAsync-then-SendAsync
        // registration race) and terminates as soon as it sees the run's terminal message — so
        // if the arbiter let the second (losing) CompleteRunAsync call through too, this would
        // either observe two RunCompletedMessage entries or hang past the first one.
        var input = new UserInput([new TextMessage { Text = "Hi", Role = Role.User }], InputId: "race-in-1");
        var messages = new List<IMessage>();
        await foreach (var msg in agent.ExecuteRunAsync(input, cts.Token))
        {
            messages.Add(msg);
        }

        var completions = messages.OfType<RunCompletedMessage>().ToList();
        completions.Should().HaveCount(1, "the exactly-once arbiter must suppress the second (raced) completion attempt");
        completions[0].IsCancelled.Should().BeFalse("the first (natural) completion must win the race");
        completions[0].IsError.Should().BeFalse();

        var ledgerEntries = await store.ListRunLedgerAsync(threadId);
        var entry = ledgerEntries.Should().ContainSingle(e => e.InputIds.Contains("race-in-1")).Subject;
        entry.Status.Should().Be(RunStatus.Completed, "the durable ledger status must not be overwritten by the losing call");

        await cts.CancelAsync();
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Runs <paramref name="input"/> through <see cref="IMultiTurnAgent.ExecuteRunAsync"/> on a
    /// background task, waits for its <see cref="RunAssignmentMessage"/> to arrive, and returns
    /// the assigned run id plus a task that completes with every message the run produced once
    /// <c>ExecuteRunAsync</c> itself completes (i.e. once the run reaches a terminal outcome).
    /// </summary>
    private static async Task<(string RunId, Task<List<IMessage>> Completion)> StartRunAndCaptureAssignmentAsync(
        IMultiTurnAgent agent,
        UserInput input,
        CancellationToken ct)
    {
        var assignmentTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var messages = new List<IMessage>();

        var completion = Task.Run(async () =>
        {
            await foreach (var msg in agent.ExecuteRunAsync(input, ct))
            {
                messages.Add(msg);
                if (msg is RunAssignmentMessage ram && !assignmentTcs.Task.IsCompleted)
                {
                    assignmentTcs.TrySetResult(ram.Assignment.RunId);
                }
            }

            return messages;
        });

        var runId = await assignmentTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        return (runId, completion);
    }

    /// <summary>
    /// First call hangs (yielding no message) until the run's own cancellation token is
    /// cancelled; signals <paramref name="started"/> once the hang begins so a test can synchronize on
    /// "the turn is now mid-flight" without sleeping. Subsequent calls return one normal
    /// text message immediately.
    /// </summary>
    private static void SetupHangingThenNormalResponses(
        Mock<IStreamingAgent> mockAgent,
        TaskCompletionSource started,
        Task neverReleasedGate)
    {
        var callCount = 0;
        mockAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>((_, _, _) =>
            {
                var isFirstCall = Interlocked.Increment(ref callCount) == 1;
                return isFirstCall
                    ? Task.FromResult(GatedAsyncEnumerable(
                        started,
                        neverReleasedGate,
                        new TextMessage { Text = "unreachable", Role = Role.Assistant }))
                    : Task.FromResult(ToAsyncEnumerable(
                        [new TextMessage { Text = "second", Role = Role.Assistant }]));
            });
    }

    /// <summary>
    /// First call returns one normal text message immediately. Second call gates on
    /// <paramref name="releaseGate"/>, signalling <paramref name="started"/> once it begins
    /// waiting, so a test can synchronize on "the second turn is now mid-flight" and later
    /// release it explicitly to prove its cancellation token was never touched.
    /// </summary>
    private static void SetupFirstNormalThenGatedResponses(
        Mock<IStreamingAgent> mockAgent,
        TaskCompletionSource started,
        Task releaseGate)
    {
        var callCount = 0;
        mockAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>((_, _, _) =>
            {
                var isFirstCall = Interlocked.Increment(ref callCount) == 1;
                return isFirstCall
                    ? Task.FromResult(ToAsyncEnumerable(
                        [new TextMessage { Text = "first", Role = Role.Assistant }]))
                    : Task.FromResult(GatedAsyncEnumerable(
                        started,
                        releaseGate,
                        new TextMessage { Text = "second", Role = Role.Assistant }));
            });
    }

    private static async IAsyncEnumerable<IMessage> GatedAsyncEnumerable(
        TaskCompletionSource started,
        Task gate,
        IMessage message,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        started.TrySetResult();
        await gate.WaitAsync(ct);
        yield return message;
    }

    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
        List<IMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var msg in messages)
        {
            ct.ThrowIfCancellationRequested();
            yield return msg;
            await Task.Yield();
        }
    }

    #endregion

    #region Test doubles

    /// <summary>
    /// Minimal concrete <see cref="MultiTurnAgentBase"/> whose loop starts one run per drained
    /// batch and then calls the protected <c>CompleteRunAsync</c> TWICE in a row for that same
    /// run — modelling a natural completion immediately raced by a late matching-cancellation
    /// completion attempt (see <see cref="CompleteRunAsync_CancellationRacingNaturalCompletion_EmitsExactlyOneTerminal"/>).
    /// </summary>
    private sealed class ArbiterTestAgent : MultiTurnAgentBase
    {
        public ArbiterTestAgent(string threadId, IConversationStore? store, bool persistRunLedger)
            : base(threadId, store: store, persistRunLedger: persistRunLedger)
        {
        }

        protected override async Task RunLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                if (!await InputReader.WaitToReadAsync(ct))
                {
                    break;
                }

                if (!TryDrainInputs(out var batch) || batch.Count == 0)
                {
                    continue;
                }

                var assignment = await StartRunAsync(batch, ct: ct);
                await PublishToAllAsync(
                    new RunAssignmentMessage { Assignment = assignment, ThreadId = ThreadId },
                    ct);

                // "Natural" completion — this call must win the race.
                await CompleteRunAsync(assignment.RunId, assignment.GenerationId, ct: ct);

                // "Late cancel" completion attempt for the SAME run — must be a no-op.
                await CompleteRunAsync(
                    assignment.RunId,
                    assignment.GenerationId,
                    isCancelled: true,
                    ct: ct);
            }
        }
    }

    #endregion
}
