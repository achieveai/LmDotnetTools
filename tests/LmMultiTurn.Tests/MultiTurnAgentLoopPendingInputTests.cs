using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.ClientTools;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Regression tests for what <see cref="MultiTurnAgentLoop"/> reports in
/// <see cref="RunCompletedMessage.PendingMessageCount"/>.
///
/// The loop drains input that arrived DURING a run into a follow-on run through the same agent and
/// provider, so such a completion is not terminal — and consumers act on that: <c>SubAgentManager</c>
/// disposes a sub-agent's owned provider agent only on a completion with no pending input. The loop
/// used to hardcode 0 here (unlike <c>ClaudeAgentLoop</c>, which has always reported its real queue
/// depth), so a message that landed while a run was finishing produced a "terminal" completion, the
/// provider — and its <c>HttpClient</c> — was disposed, and the follow-on run's very first request
/// threw <c>ObjectDisposedException</c>. Observed live as: <c>No tool calls in turn 11, run
/// complete</c> → <c>Message queued</c> → <c>Run … completed</c> → <c>Starting run … (inputs: 1)</c>
/// → <c>Run … failing (ObjectDisposedException)</c>.
///
/// The completion tests gate the provider mid-generation so the second message lands after the turn loop's
/// pre-turn input poll — i.e. in the exact window that is still queued when the run completes —
/// making the race deterministic rather than timing-dependent.
/// </summary>
public class MultiTurnAgentLoopPendingInputTests
{
    [Fact]
    public async Task Restart_InputTakenBeforeRunAssignment_ConsumesItBeforeLaterQueuedInput()
    {
        // The public run-ledger boundary is after the channel drain and before AddToHistory. Stop
        // there so the first input cannot survive merely by remaining in the channel or in history.
        var store = new AssignmentGateStore(entry => entry.Status == RunStatus.Queued);
        var agent = new GatedStreamingAgent(
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            Released(),
            _ => new TextMessage { Text = "answer", Role = Role.Assistant }
        );
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var loop = new MultiTurnAgentLoop(
            agent,
            new FunctionRegistry(),
            "restart-input-thread",
            store: store,
            persistRunLedger: true
        );
        var running = loop.RunAsync(cts.Token);
        await loop.WaitUntilReadyAsync(cts.Token);
        var first = await loop.SendAsync([NoticeA()], inputId: "input-A");
        var assigning = await store.AssignmentEntered.Task.WaitAsync(cts.Token);
        assigning.InputIds.Should().Equal(first.ReceiptId);
        loop.GetHistorySnapshot().OfType<NotifyMessage>().Should().BeEmpty();
        var second = await loop.SendAsync([MessageB()], inputId: "input-B");
        await loop.StopAsync(TimeSpan.FromSeconds(5));
        await running;

        await RestartUntilMessageBPersistedAsync(loop, store, cts.Token);

        await AssertConsumedOnceInOrderAsync(loop, store, first.ReceiptId, second.ReceiptId, cts.Token);
    }

    [Fact]
    public async Task Restart_InputTakenMidRunBeforeItReachesHistory_ConsumesItBeforeLaterQueuedInput()
    {
        // The pre-turn poll inside a run takes A and records the injection in the run ledger before A
        // reaches history. Stop at that write: A is out of the channel and not yet in history.
        var store = new AssignmentGateStore(entry => entry.Status == RunStatus.InProgress && entry.InputIds.Count > 1);
        var toolEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTool = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new FunctionRegistry().AddFunction(
            new FunctionContract
            {
                Name = "hold",
                Description = "Holds the run between its first and second turn",
                Parameters = [],
            },
            async (_, _, ct) =>
            {
                toolEntered.TrySetResult();
                await releaseTool.Task.WaitAsync(ct);
                return ToolHandlerResult.FromText("held");
            }
        );
        var agent = new GatedStreamingAgent(
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            Released(),
            callIndex =>
                callIndex == 1
                    ? new ToolCallMessage
                    {
                        FunctionName = "hold",
                        FunctionArgs = "{}",
                        ToolCallId = "hold-1",
                        Role = Role.Assistant,
                    }
                    : new TextMessage { Text = "answer", Role = Role.Assistant }
        );
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var loop = new MultiTurnAgentLoop(
            agent,
            registry,
            "restart-midrun-thread",
            store: store,
            persistRunLedger: true
        );
        var running = loop.RunAsync(cts.Token);
        await loop.WaitUntilReadyAsync(cts.Token);
        _ = await loop.SendAsync([new TextMessage { Text = "start", Role = Role.User }], inputId: "input-start");
        await toolEntered.Task.WaitAsync(cts.Token);
        var first = await loop.SendAsync([NoticeA()], inputId: "input-A");
        releaseTool.SetResult();
        var injecting = await store.AssignmentEntered.Task.WaitAsync(cts.Token);
        injecting.InputIds.Should().Contain(first.ReceiptId);
        loop.GetHistorySnapshot().OfType<NotifyMessage>().Should().BeEmpty();
        var second = await loop.SendAsync([MessageB()], inputId: "input-B");
        await loop.StopAsync(TimeSpan.FromSeconds(5));
        await running;

        await RestartUntilMessageBPersistedAsync(loop, store, cts.Token);

        await AssertConsumedOnceInOrderAsync(loop, store, first.ReceiptId, second.ReceiptId, cts.Token);
    }

    [Fact]
    public async Task Restart_AfterTakenInputReachedHistory_DoesNotAppendItAgain()
    {
        // Stop inside the provider call that carries A. A is already in history, so the loop must have
        // released it, and the restart must not append it a second time.
        var store = new AssignmentGateStore(_ => false);
        var generationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new GatedStreamingAgent(
            generationStarted,
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            _ => new TextMessage { Text = "answer", Role = Role.Assistant }
        );
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var loop = new MultiTurnAgentLoop(
            agent,
            new FunctionRegistry(),
            "restart-appended-thread",
            store: store,
            persistRunLedger: true
        );
        var running = loop.RunAsync(cts.Token);
        await loop.WaitUntilReadyAsync(cts.Token);
        var first = await loop.SendAsync([NoticeA()], inputId: "input-A");
        (await generationStarted.Task.WaitAsync(cts.Token)).Should().BeTrue();
        var second = await loop.SendAsync([MessageB()], inputId: "input-B");
        await loop.StopAsync(TimeSpan.FromSeconds(5));
        await running;

        await RestartUntilMessageBPersistedAsync(loop, store, cts.Token);

        await AssertConsumedOnceInOrderAsync(loop, store, first.ReceiptId, second.ReceiptId, cts.Token);
    }

    [Fact]
    public async Task Restart_InputTakenForParkedFoldBeforeItReachesHistory_FoldsItOnce()
    {
        // While the loop is parked on a question, a notice that suppresses spawning is folded into history,
        // and the fold writes the suppression marker first. Stop at that write: the notice is out of the
        // channel and not yet in history.
        string? parkedRunId = null;
        var store = new AssignmentGateStore(
            _ => false,
            metadata => parkedRunId != null && metadata.Properties?.Values.Contains(parkedRunId) == true
        );
        var agent = new GatedStreamingAgent(
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            Released(),
            callIndex => callIndex == 1 ? AskCall() : new TextMessage { Text = "answer", Role = Role.Assistant }
        );
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var loop = new MultiTurnAgentLoop(
            agent,
            new FunctionRegistry(),
            "restart-parked-thread",
            store: store,
            persistRunLedger: true
        );
        var running = loop.RunAsync(cts.Token);
        await loop.WaitUntilReadyAsync(cts.Token);
        _ = await loop.SendAsync([new TextMessage { Text = "which colour?", Role = Role.User }], inputId: "input-ask");
        parkedRunId = await WaitForParkedRunAsync(loop, cts.Token);
        _ = await loop.SendAsync(
            new UserInput([NoticeA()], InputId: "input-A", SuppressSubAgentSpawning: true),
            cts.Token
        );
        await store.MetadataEntered.Task.WaitAsync(cts.Token);
        loop.GetHistorySnapshot()
            .OfType<NotifyMessage>()
            .Where(notice => notice.Detail == "notice-A")
            .Should()
            .BeEmpty();
        await loop.StopAsync(TimeSpan.FromSeconds(5));
        await running;

        running = loop.RunAsync(cts.Token);
        await loop.WaitUntilReadyAsync(cts.Token);
        await store.NoticeAPersisted.Task.WaitAsync(cts.Token);
        await loop.StopAsync(TimeSpan.FromSeconds(5));
        await running;

        loop.GetHistorySnapshot().OfType<NotifyMessage>().Count(notice => notice.Detail == "notice-A").Should().Be(1);
        (await store.LoadMessagesAsync(loop.ThreadId, cts.Token))
            .Count(row => row.GenerationId == "stable-notice-A")
            .Should()
            .Be(1);
    }

    [Fact]
    public async Task DelayedChild_InputHeldForIt_StaysAheadOfInputSentDuringIt()
    {
        // The question is answered just as the loop goes to wait, together with notice A. The drain takes A
        // while the answer's child run is pending, so A is held for after that child. B arrives during the
        // child. The child must not inject B ahead of A, and its completion must still count A as pending.
        var agent = new GatedStreamingAgent(
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            Released(),
            callIndex => callIndex == 1 ? AskCall() : new TextMessage { Text = "answer", Role = Role.Assistant }
        );
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var loop = new MultiTurnAgentLoop(agent, new FunctionRegistry(), "held-for-child-thread");
        var runEvents = Channel.CreateUnbounded<IMessage>();
        _ = CollectRunEventsAsync(loop.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token), runEvents.Writer);
        var answered = 0;
        loop.WaitingForInputForTest = () =>
        {
            if (
                loop.GetDeferredToolCallsAsync().GetAwaiter().GetResult().Count == 0
                || Interlocked.Exchange(ref answered, 1) == 1
            )
            {
                return;
            }

            _ = loop.SendAsync([NoticeA()], inputId: "input-A").AsTask().GetAwaiter().GetResult();
            loop.TryResolveToolCallAsync(AskCallId, "blue")
                .GetAwaiter()
                .GetResult()
                .Should()
                .Be(ResolveToolCallOutcome.Resolved);
        };
        loop.BeforeStartingDelayedRunForTest = async ct =>
            _ = await loop.SendAsync([MessageB()], inputId: "input-B", ct: ct);

        var running = loop.RunAsync(cts.Token);
        await loop.WaitUntilReadyAsync(cts.Token);
        _ = await loop.SendAsync([new TextMessage { Text = "which colour?", Role = Role.User }], inputId: "input-ask");
        string? childRunId = null;
        RunCompletedMessage? child = null;
        while (true)
        {
            var runEvent = await runEvents.Reader.ReadAsync(cts.Token);
            if (
                runEvent is RunAssignmentMessage { Assignment: { InputIds: { } inputIds } assignment }
                && inputIds.Contains($"delayed:{AskCallId}")
            )
            {
                childRunId = assignment.RunId;
            }
            else if (runEvent is RunCompletedMessage completed && completed.CompletedRunId == childRunId)
            {
                child = completed;
            }
            else if (runEvent is RunCompletedMessage { PendingMessageCount: 0 } && child != null)
            {
                break;
            }
        }

        await loop.StopAsync(TimeSpan.FromSeconds(5));
        await running;

        child!.IsError.Should().BeFalse();
        child.PendingMessageCount.Should().Be(2, "A is held for after the child and B is queued behind it");
        loop.GetHistorySnapshot()
            .Where(message =>
                message is NotifyMessage { Detail: "notice-A" } or TextMessage { Role: Role.User, Text: "message-B" }
            )
            .Select(message => message is NotifyMessage ? "A" : "B")
            .Should()
            .Equal(["A", "B"], "input held for the child stays ahead of input that arrived during it");
    }

    private const string AskCallId = "ask-1";

    private static ToolCallMessage AskCall() =>
        new()
        {
            FunctionName = AskUserQuestionToolProvider.ToolName,
            FunctionArgs = JsonSerializer.Serialize(
                new
                {
                    context = "Need a colour.",
                    questions = new[]
                    {
                        new
                        {
                            prompt = "Which colour?",
                            options = new object[] { new { label = "Red" }, new { label = "Blue" } },
                        },
                    },
                }
            ),
            ToolCallId = AskCallId,
            Role = Role.Assistant,
        };

    private static async Task<string> WaitForParkedRunAsync(MultiTurnAgentLoop loop, CancellationToken ct)
    {
        while (true)
        {
            if (await loop.GetDeferredToolCallsAsync(ct) is [{ RunId: { } runId }])
            {
                return runId;
            }

            await Task.Delay(10, ct);
        }
    }

    private static async Task CollectRunEventsAsync(
        IAsyncEnumerator<IMessage> subscription,
        ChannelWriter<IMessage> runEvents
    )
    {
        // Called before any input: the first MoveNextAsync registers the subscriber synchronously.
        await using (subscription)
        {
            while (await subscription.MoveNextAsync())
            {
                if (subscription.Current is RunAssignmentMessage or RunCompletedMessage)
                {
                    _ = runEvents.TryWrite(subscription.Current);
                }
            }
        }
    }

    private static TaskCompletionSource<bool> Released()
    {
        var released = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        released.SetResult(true);
        return released;
    }

    private static NotifyMessage NoticeA() =>
        new()
        {
            NotifyKind = "session-event",
            Detail = "notice-A",
            GenerationId = "stable-notice-A",
        };

    private static TextMessage MessageB() => new() { Text = "message-B", Role = Role.User };

    private static async Task RestartUntilMessageBPersistedAsync(
        MultiTurnAgentLoop loop,
        AssignmentGateStore store,
        CancellationToken ct
    )
    {
        var running = loop.RunAsync(ct);
        await loop.WaitUntilReadyAsync(ct);
        await store.MessageBPersisted.Task.WaitAsync(ct);
        await loop.StopAsync(TimeSpan.FromSeconds(5));
        await running;
    }

    /// <summary>
    /// A then B, once each, in live and persisted history, with the notice's identity intact, and each
    /// input assigned to exactly one run.
    /// </summary>
    private static async Task AssertConsumedOnceInOrderAsync(
        MultiTurnAgentLoop loop,
        AssignmentGateStore store,
        string firstReceiptId,
        string secondReceiptId,
        CancellationToken ct
    )
    {
        var inputs = loop.GetHistorySnapshot()
            .Where(message =>
                message is NotifyMessage { Detail: "notice-A" } or TextMessage { Role: Role.User, Text: "message-B" }
            )
            .Select(message => message is NotifyMessage ? "A" : "B");
        inputs.Should().Equal(["A", "B"], "taken input must stay owned ahead of later input until it enters history");
        var persisted = (await store.LoadMessagesAsync(loop.ThreadId, ct))
            .Where(row =>
                row.GenerationId == "stable-notice-A" || row.MessageJson.Contains("message-B", StringComparison.Ordinal)
            )
            .ToArray();
        persisted.Select(row => row.MessageType).Should().Equal(nameof(NotifyMessage), nameof(TextMessage));
        persisted[0].GenerationId.Should().Be("stable-notice-A");
        var assigned = (await store.ListRunLedgerAsync(loop.ThreadId, ct)).SelectMany(entry => entry.InputIds);
        assigned.Count(id => id == firstReceiptId).Should().Be(1);
        assigned.Count(id => id == secondReceiptId).Should().Be(1);
    }

    [Fact]
    public async Task CompleteRun_InputQueuedWhileRunInFlight_ReportsItAsPending()
    {
        // Arrange: the first generation blocks until the test has queued a second message, so that
        // message is provably still in the input channel when the run completes.
        var generationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGeneration = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new GatedStreamingAgent(
            generationStarted,
            releaseGeneration,
            _ => new TextMessage { Text = "answer", Role = Role.Assistant }
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var loop = new MultiTurnAgentLoop(agent, new FunctionRegistry(), "test-thread");
        _ = loop.RunAsync(cts.Token);

        // ExecuteRunAsync subscribes BEFORE it sends, so the completion cannot be missed.
        var messages = new List<IMessage>();
        var runTask = Task.Run(
            async () =>
            {
                await foreach (
                    var msg in loop.ExecuteRunAsync(
                        new UserInput([new TextMessage { Text = "first", Role = Role.User }], InputId: "input-1"),
                        cts.Token
                    )
                )
                {
                    messages.Add(msg);
                }
            },
            cts.Token
        );

        // Act: queue a second message while the first run is mid-generation, then let it finish.
        (await generationStarted.Task.WaitAsync(TimeSpan.FromSeconds(15)))
            .Should()
            .BeTrue();
        _ = await loop.SendAsync([new TextMessage { Text = "second", Role = Role.User }], inputId: "input-2");
        releaseGeneration.SetResult(true);

        await runTask.WaitAsync(TimeSpan.FromSeconds(15));

        // Assert: the completion advertises the queued input, so a consumer knows a follow-on run is
        // coming and must NOT tear the agent's provider down.
        var completed = messages.OfType<RunCompletedMessage>().Should().ContainSingle().Subject;
        completed.IsError.Should().BeFalse();
        completed
            .PendingMessageCount.Should()
            .Be(1, "the message queued during the run is still unassigned when the run completes");
        completed
            .HasPendingMessages.Should()
            .BeTrue("SubAgentManager disposes a sub-agent's owned provider only on a completion with no pending input");
    }

    [Fact]
    public async Task CompleteRun_FailedRunWithInputQueuedWhileInFlight_ReportsItAsPending()
    {
        // Same window, error path: a run that FAILS is still followed by a run for whatever queued
        // while it ran, so the provider must survive this completion too. The error path used to omit
        // the argument entirely, silently defaulting to 0.
        var generationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGeneration = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new GatedStreamingAgent(
            generationStarted,
            releaseGeneration,
            callIndex =>
                callIndex == 1
                    ? throw new InvalidOperationException("generation failed")
                    : new TextMessage { Text = "answer", Role = Role.Assistant }
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var loop = new MultiTurnAgentLoop(agent, new FunctionRegistry(), "test-thread");
        _ = loop.RunAsync(cts.Token);

        var messages = new List<IMessage>();
        var runTask = Task.Run(
            async () =>
            {
                await foreach (
                    var msg in loop.ExecuteRunAsync(
                        new UserInput([new TextMessage { Text = "first", Role = Role.User }], InputId: "input-1"),
                        cts.Token
                    )
                )
                {
                    messages.Add(msg);
                }
            },
            cts.Token
        );

        (await generationStarted.Task.WaitAsync(TimeSpan.FromSeconds(15))).Should().BeTrue();
        _ = await loop.SendAsync([new TextMessage { Text = "second", Role = Role.User }], inputId: "input-2");
        releaseGeneration.SetResult(true);

        await runTask.WaitAsync(TimeSpan.FromSeconds(15));

        var completed = messages.OfType<RunCompletedMessage>().Should().ContainSingle().Subject;
        completed.IsError.Should().BeTrue("the generation threw");
        completed.PendingMessageCount.Should().Be(1);
        completed
            .HasPendingMessages.Should()
            .BeTrue("a failed run is followed by a run for the queued input, through the same provider");
    }

    /// <summary>
    /// Holds the first run-ledger write that matches <paramref name="gate"/>, and the first metadata write
    /// whose result matches <paramref name="metadataGate"/>, until the loop is stopped. Both are boundaries
    /// after the loop has taken input and before that input reaches history.
    /// </summary>
    private sealed class AssignmentGateStore(
        Func<RunLedgerEntry, bool> gate,
        Func<ThreadMetadata, bool>? metadataGate = null
    ) : IConversationStore, IRunLedgerStore
    {
        private readonly InMemoryConversationStore _inner = new();
        private int _gated;
        private int _metadataGated;

        public TaskCompletionSource<RunLedgerEntry> AssignmentEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource MetadataEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource NoticeAPersisted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource MessageBPersisted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task UpsertRunLedgerAsync(RunLedgerEntry entry, CancellationToken ct = default)
        {
            if (gate(entry) && Interlocked.Exchange(ref _gated, 1) == 0)
            {
                AssignmentEntered.TrySetResult(entry);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            await _inner.UpsertRunLedgerAsync(entry, ct);
        }

        public async Task AppendMessagesAsync(
            string threadId,
            IReadOnlyList<PersistedMessage> messages,
            CancellationToken ct = default
        )
        {
            await _inner.AppendMessagesAsync(threadId, messages, ct);
            if (messages.Any(message => message.MessageJson.Contains("notice-A", StringComparison.Ordinal)))
                NoticeAPersisted.TrySetResult();
            if (messages.Any(message => message.MessageJson.Contains("message-B", StringComparison.Ordinal)))
                MessageBPersisted.TrySetResult();
        }

        public Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(
            string threadId,
            CancellationToken ct = default
        ) => _inner.LoadMessagesAsync(threadId, ct);

        public Task ReplaceMessageAsync(
            string threadId,
            PersistedMessage replacement,
            CancellationToken ct = default
        ) => _inner.ReplaceMessageAsync(threadId, replacement, ct);

        public Task SaveMetadataAsync(string threadId, ThreadMetadata metadata, CancellationToken ct = default) =>
            _inner.SaveMetadataAsync(threadId, metadata, ct);

        public Task<ThreadMetadata?> LoadMetadataAsync(string threadId, CancellationToken ct = default) =>
            _inner.LoadMetadataAsync(threadId, ct);

        public async Task UpdateMetadataAsync(
            string threadId,
            Func<ThreadMetadata?, ThreadMetadata> update,
            CancellationToken ct = default
        )
        {
            if (
                metadataGate != null
                && metadataGate(update(await _inner.LoadMetadataAsync(threadId, ct)))
                && Interlocked.Exchange(ref _metadataGated, 1) == 0
            )
            {
                MetadataEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            await _inner.UpdateMetadataAsync(threadId, update, ct);
        }

        public Task DeleteThreadAsync(string threadId, CancellationToken ct = default) =>
            _inner.DeleteThreadAsync(threadId, ct);

        public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
            int limit = 50,
            int offset = 0,
            ConversationListOptions? options = null,
            CancellationToken ct = default
        ) => _inner.ListThreadsAsync(limit, offset, options, ct);

        public Task<RunLedgerEntry?> LoadRunLedgerAsync(string runId, CancellationToken ct = default) =>
            _inner.LoadRunLedgerAsync(runId, ct);

        public Task<IReadOnlyList<RunLedgerEntry>> ListRunLedgerAsync(
            string threadId,
            CancellationToken ct = default
        ) => _inner.ListRunLedgerAsync(threadId, ct);

        public Task RecordAcceptedInputAsync(
            string threadId,
            string inputId,
            DateTimeOffset acceptedAt,
            CancellationToken ct = default
        ) => _inner.RecordAcceptedInputAsync(threadId, inputId, acceptedAt, ct);

        public Task RemoveAcceptedInputAsync(string threadId, string inputId, CancellationToken ct = default) =>
            _inner.RemoveAcceptedInputAsync(threadId, inputId, ct);

        public Task<IReadOnlySet<string>> ListAcceptedInputIdsAsync(string threadId, CancellationToken ct = default) =>
            _inner.ListAcceptedInputIdsAsync(threadId, ct);
    }

    /// <summary>
    /// Streaming agent whose FIRST generation signals <paramref name="started"/> and then blocks on
    /// <paramref name="release"/> — the seam a test needs to land input inside a run, after the turn
    /// loop's pre-turn input poll. Later generations run straight through so the follow-on run the
    /// loop starts for that input does not wedge the test.
    /// </summary>
    private sealed class GatedStreamingAgent(
        TaskCompletionSource<bool> started,
        TaskCompletionSource<bool> release,
        Func<int, IMessage> reply
    ) : IStreamingAgent
    {
        private int _callCount;

        public async Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            var callIndex = Interlocked.Increment(ref _callCount);
            if (callIndex == 1)
            {
                _ = started.TrySetResult(true);
                _ = await release.Task.WaitAsync(cancellationToken);
            }

            return Single(reply(callIndex), cancellationToken);
        }

        public async Task<IEnumerable<IMessage>> GenerateReplyAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            var stream = await GenerateReplyStreamingAsync(messages, options, cancellationToken);
            var collected = new List<IMessage>();
            await foreach (var msg in stream.WithCancellation(cancellationToken))
            {
                collected.Add(msg);
            }

            return collected;
        }

        private static async IAsyncEnumerable<IMessage> Single(
            IMessage message,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            ct.ThrowIfCancellationRequested();
            yield return message;
            await Task.Yield();
        }
    }
}
