using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// End-to-end tests for the deferred tool execution feature in <see cref="MultiTurnAgentLoop"/>.
/// Each test arranges a mock LLM that emits a tool call, registers a handler that signals
/// deferral, and exercises the full lifecycle: placeholder published, run ends, external
/// resolution arrives, auto-resume fires, LLM sees the resolved value.
/// </summary>
public class DeferredToolExecutionTests
{
    private readonly Mock<IStreamingAgent> _mockAgent = new();
    private readonly Mock<ILogger<MultiTurnAgentLoop>> _loggerMock = new();

    [Fact]
    public async Task DeferredHandler_RecordsPlaceholderAndEndsRun()
    {
        // Mock LLM emits a single tool call. The handler returns Deferred so the loop
        // should record an IsDeferred=true placeholder and end the run after this turn.
        var toolCall = new ToolCallMessage
        {
            FunctionName = "approve_directory_read",
            FunctionArgs = "{\"path\": \"/foo\"}",
            ToolCallId = "tc_1",
            Role = Role.Assistant,
        };

        SetupOneTurnResponse(toolCall);

        var registry = new FunctionRegistry();
        registry.AddFunction(
            BuildContract("approve_directory_read"),
            (_, _, _) => Task.FromResult<ToolHandlerResult>(
                new ToolHandlerResult.Deferred()));

        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            registry,
            "test-thread",
            logger: _loggerMock.Object);

        using var cts = new CancellationTokenSource();
        var runTask = loop.RunAsync(cts.Token);

        var userInput = new UserInput([new TextMessage { Text = "Read /foo", Role = Role.User }]);
        var messages = new List<IMessage>();
        await foreach (var msg in loop.ExecuteRunAsync(userInput, cts.Token))
        {
            messages.Add(msg);
        }

        // Assert: tool call message published, deferred placeholder published, run completed.
        messages.OfType<ToolCallMessage>().Should().ContainSingle(tc => tc.ToolCallId == "tc_1");
        var deferredResult = messages.OfType<ToolCallResultMessage>().Should().ContainSingle().Subject;
        deferredResult.ToolCallId.Should().Be("tc_1");
        deferredResult.IsDeferred.Should().BeTrue();
        deferredResult.Result.Should().Be(string.Empty);
        messages.OfType<RunCompletedMessage>().Should().NotBeEmpty();

        // The mock LLM should have been called only once — no second turn fires while
        // the deferred call is unresolved.
        _mockAgent.Verify(a => a.GenerateReplyStreamingAsync(
            It.IsAny<IEnumerable<IMessage>>(),
            It.IsAny<GenerateReplyOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);

        var pending = await loop.GetDeferredToolCallsAsync();
        pending.Should().ContainSingle(p => p.ToolCallId == "tc_1");

        await cts.CancelAsync();
    }

    [Fact]
    public async Task ResolveToolCallAsync_ReplacesPlaceholderAndAutoResumesRun()
    {
        // Two-call mock LLM: first call returns the tool call, second call (after resume)
        // returns final text. Capture the messages the second call sees so we can assert
        // the resolved value reached the LLM.
        var toolCall = new ToolCallMessage
        {
            FunctionName = "long_running_op",
            FunctionArgs = "{}",
            ToolCallId = "tc_long",
            Role = Role.Assistant,
        };
        var finalAssistantText = new TextMessage
        {
            Text = "Operation completed successfully.",
            Role = Role.Assistant,
        };

        var callCount = 0;
        IEnumerable<IMessage>? secondCallMessages = null;
        _mockAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (msgs, _, _) =>
                {
                    callCount++;
                    if (callCount == 1)
                    {
                        return Task.FromResult(ToAsyncEnumerable([toolCall]));
                    }
                    secondCallMessages = [.. msgs];
                    return Task.FromResult(ToAsyncEnumerable([finalAssistantText]));
                });

        var registry = new FunctionRegistry();
        registry.AddFunction(
            BuildContract("long_running_op"),
            (_, _, _) => Task.FromResult<ToolHandlerResult>(
                new ToolHandlerResult.Deferred()));

        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            registry,
            "test-thread",
            logger: _loggerMock.Object);

        using var cts = new CancellationTokenSource();
        var runTask = loop.RunAsync(cts.Token);

        // Subscribe so we can wait for run completions.
        var subscriberMessages = new List<IMessage>();
        var subscribeTcs = new TaskCompletionSource<bool>();
        var firstRunCompleted = new TaskCompletionSource<bool>();
        var secondRunCompleted = new TaskCompletionSource<bool>();
        var runCompleteCount = 0;
        _ = Task.Run(async () =>
        {
            try
            {
                subscribeTcs.SetResult(true);
                await foreach (var msg in loop.SubscribeAsync(cts.Token))
                {
                    subscriberMessages.Add(msg);
                    if (msg is RunCompletedMessage)
                    {
                        runCompleteCount++;
                        if (runCompleteCount == 1)
                        {
                            firstRunCompleted.TrySetResult(true);
                        }
                        else if (runCompleteCount == 2)
                        {
                            secondRunCompleted.TrySetResult(true);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await subscribeTcs.Task;

        // Send the first user input — kicks off run 1.
        await loop.SendAsync([new TextMessage { Text = "Start the long op", Role = Role.User }]);

        // Wait for run 1 to finish (handler deferred, run ends).
        await firstRunCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Verify the deferred call is registered.
        var pendingBefore = await loop.GetDeferredToolCallsAsync();
        pendingBefore.Should().ContainSingle(p => p.ToolCallId == "tc_long");

        // Resolve the deferred call. This should auto-trigger run 2.
        await loop.ResolveToolCallAsync("tc_long", "{\"status\":\"done\"}");

        // Wait for run 2 to complete (auto-resume).
        await secondRunCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Mock LLM should have been called twice — initial + auto-resumed.
        callCount.Should().Be(2);

        // The second call to the LLM must see the resolved value in its messages.
        // Note: MessageTransformationMiddleware aggregates singular ToolCallResultMessage
        // entries into ToolsCallResultMessage on the upstream path, so we look for the
        // resolved entry inside the plural form too.
        secondCallMessages.Should().NotBeNull();
        var allResults = secondCallMessages!
            .OfType<ToolCallResultMessage>()
            .Concat(
                secondCallMessages!.OfType<ToolsCallResultMessage>()
                    .SelectMany(m => m.ToolCallResults.Select(r => new ToolCallResultMessage
                    {
                        ToolCallId = r.ToolCallId,
                        Result = r.Result,
                        IsError = r.IsError,
                    })))
            .ToList();
        var resolvedInHistory = allResults.Single(m => m.ToolCallId == "tc_long");
        resolvedInHistory.Result.Should().Be("{\"status\":\"done\"}");

        // The deferred set should now be empty.
        var pendingAfter = await loop.GetDeferredToolCallsAsync();
        pendingAfter.Should().BeEmpty();

        await cts.CancelAsync();
    }

    [Fact]
    public async Task ResolveToolCallAsync_IsIdempotent_ForByteEqualDuplicate()
    {
        var toolCall = new ToolCallMessage
        {
            FunctionName = "long_op",
            FunctionArgs = "{}",
            ToolCallId = "tc_idem",
            Role = Role.Assistant,
        };
        var finalText = new TextMessage { Text = "done", Role = Role.Assistant };

        var callCount = 0;
        _mockAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>((_, _, _) =>
            {
                callCount++;
                return callCount == 1
                    ? Task.FromResult(ToAsyncEnumerable([toolCall]))
                    : Task.FromResult(ToAsyncEnumerable([finalText]));
            });

        var registry = new FunctionRegistry();
        registry.AddFunction(
            BuildContract("long_op"),
            (_, _, _) => Task.FromResult<ToolHandlerResult>(new ToolHandlerResult.Deferred()));

        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            registry,
            "test-thread",
            logger: _loggerMock.Object);

        using var cts = new CancellationTokenSource();
        var runTask = loop.RunAsync(cts.Token);

        var firstRunCompleted = new TaskCompletionSource<bool>();
        var secondRunCompleted = new TaskCompletionSource<bool>();
        var completedRuns = 0;
        _ = Task.Run(async () =>
        {
            await foreach (var msg in loop.SubscribeAsync(cts.Token))
            {
                if (msg is RunCompletedMessage)
                {
                    completedRuns++;
                    if (completedRuns == 1)
                    {
                        firstRunCompleted.TrySetResult(true);
                    }
                    else if (completedRuns == 2)
                    {
                        secondRunCompleted.TrySetResult(true);
                    }
                }
            }
        }, cts.Token);

        await loop.SendAsync([new TextMessage { Text = "Go", Role = Role.User }]);
        await firstRunCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Resolve once — triggers auto-resume.
        await loop.ResolveToolCallAsync("tc_idem", "FINAL");
        await secondRunCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Resolve again with the SAME content. Should be a no-op (no exception, no third run).
        await loop.ResolveToolCallAsync("tc_idem", "FINAL");

        // Give any spurious resume a chance to fire — verify it didn't.
        await Task.Delay(150);
        callCount.Should().Be(2, "second resolve with identical content must be a no-op");

        await cts.CancelAsync();
    }

    [Fact]
    public async Task ResolveToolCallAsync_Throws_OnUnknownToolCallId()
    {
        var registry = new FunctionRegistry();
        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            registry,
            "test-thread",
            logger: _loggerMock.Object);

        var act = async () => await loop.ResolveToolCallAsync("nonexistent", "value");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*nonexistent*");
    }

    [Fact]
    public async Task ResolveToolCallAsync_Throws_OnConflictingResolution()
    {
        var toolCall = new ToolCallMessage
        {
            FunctionName = "op",
            FunctionArgs = "{}",
            ToolCallId = "tc_conflict",
            Role = Role.Assistant,
        };
        var finalText = new TextMessage { Text = "done", Role = Role.Assistant };

        var callCount = 0;
        _mockAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>((_, _, _) =>
            {
                callCount++;
                return callCount == 1
                    ? Task.FromResult(ToAsyncEnumerable([toolCall]))
                    : Task.FromResult(ToAsyncEnumerable([finalText]));
            });

        var registry = new FunctionRegistry();
        registry.AddFunction(
            BuildContract("op"),
            (_, _, _) => Task.FromResult<ToolHandlerResult>(new ToolHandlerResult.Deferred()));

        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            registry,
            "test-thread",
            logger: _loggerMock.Object);

        using var cts = new CancellationTokenSource();
        var runTask = loop.RunAsync(cts.Token);

        var firstRunCompleted = new TaskCompletionSource<bool>();
        var completedRuns = 0;
        _ = Task.Run(async () =>
        {
            await foreach (var msg in loop.SubscribeAsync(cts.Token))
            {
                if (msg is RunCompletedMessage)
                {
                    completedRuns++;
                    if (completedRuns == 1)
                    {
                        firstRunCompleted.TrySetResult(true);
                    }
                }
            }
        }, cts.Token);

        await loop.SendAsync([new TextMessage { Text = "Go", Role = Role.User }]);
        await firstRunCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // First resolution succeeds.
        await loop.ResolveToolCallAsync("tc_conflict", "ANSWER_A");

        // Second resolution with different content must throw.
        var act = async () => await loop.ResolveToolCallAsync("tc_conflict", "ANSWER_B");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already been resolved*");

        await cts.CancelAsync();
    }

    [Fact]
    public async Task GetDeferredToolCallsAsync_ExposesFunctionAndArgsMetadata()
    {
        var toolCall = new ToolCallMessage
        {
            FunctionName = "wait_for_human",
            FunctionArgs = "{\"prompt\":\"approve\"}",
            ToolCallId = "tc_meta",
            Role = Role.Assistant,
        };
        SetupOneTurnResponse(toolCall);

        var registry = new FunctionRegistry();
        registry.AddFunction(
            BuildContract("wait_for_human"),
            (_, _, _) => Task.FromResult<ToolHandlerResult>(new ToolHandlerResult.Deferred()));

        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            registry,
            "test-thread",
            logger: _loggerMock.Object);

        using var cts = new CancellationTokenSource();
        var runTask = loop.RunAsync(cts.Token);

        var userInput = new UserInput([new TextMessage { Text = "Go", Role = Role.User }]);
        await foreach (var _ in loop.ExecuteRunAsync(userInput, cts.Token))
        {
            // drain
        }

        var pending = await loop.GetDeferredToolCallsAsync();
        var info = pending.Should().ContainSingle().Subject;
        info.ToolCallId.Should().Be("tc_meta");
        info.FunctionName.Should().Be("wait_for_human");
        info.FunctionArgs.Should().Be("{\"prompt\":\"approve\"}");
        info.DeferredAtUnixMs.Should().BeGreaterThan(0);

        await cts.CancelAsync();
    }

    [Fact]
    public async Task ExecuteTurn_Throws_WhenHistoryHasUnresolvedDeferredResult()
    {
        // Arrange — handler defers on the first call. After the deferral lands in history
        // and the run ends, sending a NEW user input must NOT trigger another LLM call:
        // the precondition guard in ExecuteTurnAsync should refuse to send a request while
        // any deferred tool result is unresolved.
        var toolCall = new ToolCallMessage
        {
            FunctionName = "wait",
            FunctionArgs = "{}",
            ToolCallId = "tc_guard",
            Role = Role.Assistant,
        };
        SetupOneTurnResponse(toolCall);

        var registry = new FunctionRegistry();
        registry.AddFunction(
            BuildContract("wait"),
            (_, _, _) => Task.FromResult<ToolHandlerResult>(new ToolHandlerResult.Deferred()));

        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            registry,
            "test-thread",
            logger: _loggerMock.Object);

        using var cts = new CancellationTokenSource();
        var runTask = loop.RunAsync(cts.Token);

        // First run: tool call defers, run ends.
        var firstUserInput = new UserInput([new TextMessage { Text = "Start", Role = Role.User }]);
        await foreach (var _ in loop.ExecuteRunAsync(firstUserInput, cts.Token))
        {
            // drain
        }

        // Sanity: the deferred entry is in history and unresolved.
        var pending = await loop.GetDeferredToolCallsAsync();
        pending.Should().ContainSingle(p => p.ToolCallId == "tc_guard");

        // Second user input while the deferral is unresolved. The loop's per-run try/catch
        // converts the precondition guard's InvalidOperationException into a RunCompleted
        // message with IsError=true (rather than letting it tear down the loop).
        var followUpInput = new UserInput([new TextMessage { Text = "Hurry up", Role = Role.User }]);
        var followUpMessages = new List<IMessage>();
        await foreach (var msg in loop.ExecuteRunAsync(followUpInput, cts.Token))
        {
            followUpMessages.Add(msg);
        }

        var failedRun = followUpMessages.OfType<RunCompletedMessage>().Should().ContainSingle().Subject;
        failedRun.IsError.Should().BeTrue();
        failedRun.ErrorMessage.Should().Contain("tc_guard").And.Contain("deferred");

        // Mock LLM should still have been called exactly once — the guard fired before
        // any second provider request was sent.
        _mockAgent.Verify(a => a.GenerateReplyStreamingAsync(
            It.IsAny<IEnumerable<IMessage>>(),
            It.IsAny<GenerateReplyOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);

        await cts.CancelAsync();
    }

    [Fact]
    public async Task MultipleDeferred_PartialResolution_AutoResumeOnlyAfterAllResolved()
    {
        // Two tool calls in one turn, both defer. Resolving the first must NOT trigger
        // auto-resume (the second is still pending). Resolving the second MUST trigger
        // auto-resume, and the LLM's next turn must see both resolved values.
        var toolCallA = new ToolCallMessage
        {
            FunctionName = "wait_a",
            FunctionArgs = "{}",
            ToolCallId = "tc_a",
            Role = Role.Assistant,
        };
        var toolCallB = new ToolCallMessage
        {
            FunctionName = "wait_b",
            FunctionArgs = "{}",
            ToolCallId = "tc_b",
            Role = Role.Assistant,
        };
        var finalText = new TextMessage { Text = "all done", Role = Role.Assistant };

        var callCount = 0;
        IEnumerable<IMessage>? secondCallMessages = null;
        _mockAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (msgs, _, _) =>
                {
                    callCount++;
                    if (callCount == 1)
                    {
                        return Task.FromResult(ToAsyncEnumerable(
                            [toolCallA, toolCallB]));
                    }
                    secondCallMessages = [.. msgs];
                    return Task.FromResult(ToAsyncEnumerable([finalText]));
                });

        var registry = new FunctionRegistry();
        registry.AddFunction(BuildContract("wait_a"),
            (_, _, _) => Task.FromResult<ToolHandlerResult>(new ToolHandlerResult.Deferred()));
        registry.AddFunction(BuildContract("wait_b"),
            (_, _, _) => Task.FromResult<ToolHandlerResult>(new ToolHandlerResult.Deferred()));

        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object, registry, "test-thread", logger: _loggerMock.Object);

        using var cts = new CancellationTokenSource();
        var runTask = loop.RunAsync(cts.Token);

        var firstRunCompleted = new TaskCompletionSource<bool>();
        var secondRunCompleted = new TaskCompletionSource<bool>();
        var completedRuns = 0;
        _ = Task.Run(async () =>
        {
            await foreach (var msg in loop.SubscribeAsync(cts.Token))
            {
                if (msg is RunCompletedMessage)
                {
                    completedRuns++;
                    if (completedRuns == 1)
                    {
                        firstRunCompleted.TrySetResult(true);
                    }
                    else if (completedRuns == 2)
                    {
                        secondRunCompleted.TrySetResult(true);
                    }
                }
            }
        }, cts.Token);

        await loop.SendAsync([new TextMessage { Text = "Go", Role = Role.User }]);
        await firstRunCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Both deferrals registered.
        var pending = await loop.GetDeferredToolCallsAsync();
        pending.Should().HaveCount(2);

        // Resolve A. Auto-resume should NOT fire because B is still deferred. We can't
        // wait-for-not-happening, so wait a short moment and verify callCount unchanged.
        await loop.ResolveToolCallAsync("tc_a", "result-a");
        await Task.Delay(150);
        callCount.Should().Be(1, "auto-resume must not fire while any deferral is pending");
        secondRunCompleted.Task.IsCompleted.Should().BeFalse();

        var stillPending = await loop.GetDeferredToolCallsAsync();
        stillPending.Should().ContainSingle(p => p.ToolCallId == "tc_b");

        // Resolve B. Now auto-resume must fire and the second LLM call must see BOTH
        // resolved values.
        await loop.ResolveToolCallAsync("tc_b", "result-b");
        await secondRunCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        callCount.Should().Be(2);

        secondCallMessages.Should().NotBeNull();
        var allResults = secondCallMessages!
            .OfType<ToolCallResultMessage>()
            .Concat(
                secondCallMessages!.OfType<ToolsCallResultMessage>()
                    .SelectMany(m => m.ToolCallResults.Select(r => new ToolCallResultMessage
                    {
                        ToolCallId = r.ToolCallId,
                        Result = r.Result,
                        IsError = r.IsError,
                    })))
            .ToList();
        allResults.Single(m => m.ToolCallId == "tc_a").Result.Should().Be("result-a");
        allResults.Single(m => m.ToolCallId == "tc_b").Result.Should().Be("result-b");

        await cts.CancelAsync();
    }

    [Fact]
    public async Task OnHistoryRestoredAsync_RebuildsDeferredRegistry_FromPersistedHistory()
    {
        // Process restart scenario: a previous process recorded a deferred placeholder, then
        // crashed/exited. New process loads from the store and must repopulate _deferred so
        // GetDeferredToolCallsAsync surfaces the entry and a future ResolveToolCallAsync can
        // complete it.
        var threadId = "test-thread-restore";
        var runId = "run_prev";
        var generationId = "gen_prev";

        var store = new InMemoryConversationStore();

        // Pre-populate the store: one ToolCallMessage (so name/args can be recovered) and the
        // matching ToolCallResultMessage with IsDeferred=true.
        var toolCall = new ToolCallMessage
        {
            ToolCallId = "tc_persisted",
            FunctionName = "wait_for_human",
            FunctionArgs = "{\"prompt\":\"approve\"}",
            Role = Role.Assistant,
            FromAgent = "test",
            GenerationId = generationId,
            RunId = runId,
        };
        var deferredResult = new ToolCallResultMessage
        {
            ToolCallId = "tc_persisted",
            ToolName = "wait_for_human",
            Result = string.Empty,
            IsDeferred = true,
            DeferredAt = 1_700_000_000_000,
            Role = Role.User,
            GenerationId = generationId,
            RunId = runId,
        };

        await store.AppendMessagesAsync(threadId,
        [
            MessagePersistenceConverter.ToPersistedMessage(toolCall, threadId, runId),
            MessagePersistenceConverter.ToPersistedMessage(deferredResult, threadId, runId),
        ]);
        await store.SaveMetadataAsync(threadId, new ThreadMetadata
        {
            ThreadId = threadId,
            LatestRunId = runId,
            LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        // New process: spin up a fresh loop pointed at the same store and recover.
        var registry = new FunctionRegistry();
        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            registry,
            threadId,
            store: store,
            logger: _loggerMock.Object);

        var recovered = await loop.RecoverAsync();
        recovered.Should().BeTrue();

        // The deferred entry must surface from the in-memory _deferred registry — proving
        // OnHistoryRestoredAsync ran and seeded it from the loaded history.
        var pending = await loop.GetDeferredToolCallsAsync();
        var info = pending.Should().ContainSingle().Subject;
        info.ToolCallId.Should().Be("tc_persisted");
        info.FunctionName.Should().Be("wait_for_human");
        info.FunctionArgs.Should().Be("{\"prompt\":\"approve\"}");
        info.RunId.Should().Be(runId);
        info.GenerationId.Should().Be(generationId);
        info.DeferredAtUnixMs.Should().Be(1_700_000_000_000);
    }

    [Fact]
    public async Task ResolveToolCallAsync_ThrowsObjectDisposedException_AfterDispose()
    {
        // Resolutions arrive externally on arbitrary threads (webhook handlers, UI events).
        // After the loop is disposed they must fail fast — silently mutating disposed state
        // would corrupt _deferred / history for any subsequent process that re-opens the
        // same store.
        var registry = new FunctionRegistry();
        var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            registry,
            "test-thread",
            logger: _loggerMock.Object);

        await loop.DisposeAsync();

        var act = async () => await loop.ResolveToolCallAsync("tc_x", "value");
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task GetDeferredToolCallsAsync_ThrowsObjectDisposedException_AfterDispose()
    {
        var registry = new FunctionRegistry();
        var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            registry,
            "test-thread",
            logger: _loggerMock.Object);

        await loop.DisposeAsync();

        var act = async () => await loop.GetDeferredToolCallsAsync();
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task OperationCanceledException_FromHandler_PropagatesAsRunCanceled()
    {
        // ExecuteToolCallAsync's broad catch used to convert OperationCanceledException
        // into LLM-visible error messages — silently swallowing user-initiated cancellation.
        // The fix: rethrow when ct.IsCancellationRequested. The run loop's outer try/catch
        // has `when (ex is not OperationCanceledException)` so cancellation propagates up
        // and the whole RunAsync task winds down (rather than a RunCompleted{IsError=true}).
        var toolCall = new ToolCallMessage
        {
            FunctionName = "slow_op",
            FunctionArgs = "{}",
            ToolCallId = "tc_cancel",
            Role = Role.Assistant,
        };
        SetupOneTurnResponse(toolCall);

        var registry = new FunctionRegistry();
        registry.AddFunction(
            BuildContract("slow_op"),
            (_, _, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                throw new OperationCanceledException(ct);
            });

        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            registry,
            "test-thread",
            logger: _loggerMock.Object);

        using var cts = new CancellationTokenSource();
        var runTask = loop.RunAsync(cts.Token);
        cts.Cancel();

        var userInput = new UserInput([new TextMessage { Text = "Go", Role = Role.User }]);

        // Drain — the run should NOT yield a non-error RunCompleted with the tool error
        // serialized into the message body. With the fix, cancellation propagates and the
        // run-loop-level catch handles it cleanly. Drain may surface no messages or a
        // cancellation; accept either, but verify no IsError=false RunCompleted with the
        // swallowed-cancellation pattern.
        var messages = new List<IMessage>();
        try
        {
            await foreach (var msg in loop.ExecuteRunAsync(userInput, cts.Token))
            {
                messages.Add(msg);
            }
        }
        catch (OperationCanceledException)
        {
            // expected on cancellation
        }

        // The crucial check: no tool result with the swallowed-cancellation error JSON.
        var swallowedCancel = messages
            .OfType<ToolCallResultMessage>()
            .Where(m => m.IsError && m.Result.Contains("OperationCanceled", StringComparison.OrdinalIgnoreCase));
        swallowedCancel.Should().BeEmpty(
            "OperationCanceledException must propagate, not be serialized into an LLM-visible error");
    }

    private void SetupOneTurnResponse(IMessage message)
    {
        _mockAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(ToAsyncEnumerable([message])));
    }

    private static FunctionContract BuildContract(string name) => new()
    {
        Name = name,
        Description = $"Test contract for {name}",
        Parameters = [],
    };

    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
        IEnumerable<IMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var msg in messages)
        {
            ct.ThrowIfCancellationRequested();
            yield return msg;
            await Task.Yield();
        }
    }
}
