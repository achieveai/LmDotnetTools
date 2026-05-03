using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
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
                new ToolHandlerResult.Deferred("PENDING approval for /foo")));

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
        deferredResult.Result.Should().Be("PENDING approval for /foo");
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
                    secondCallMessages = msgs.ToList();
                    return Task.FromResult(ToAsyncEnumerable([finalAssistantText]));
                });

        var registry = new FunctionRegistry();
        registry.AddFunction(
            BuildContract("long_running_op"),
            (_, _, _) => Task.FromResult<ToolHandlerResult>(
                new ToolHandlerResult.Deferred("PENDING long op")));

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
                    ? Task.FromResult(ToAsyncEnumerable([(IMessage)toolCall]))
                    : Task.FromResult(ToAsyncEnumerable([(IMessage)finalText]));
            });

        var registry = new FunctionRegistry();
        registry.AddFunction(
            BuildContract("long_op"),
            (_, _, _) => Task.FromResult<ToolHandlerResult>(new ToolHandlerResult.Deferred("PENDING")));

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
                    if (completedRuns == 1) firstRunCompleted.TrySetResult(true);
                    else if (completedRuns == 2) secondRunCompleted.TrySetResult(true);
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
                    ? Task.FromResult(ToAsyncEnumerable([(IMessage)toolCall]))
                    : Task.FromResult(ToAsyncEnumerable([(IMessage)finalText]));
            });

        var registry = new FunctionRegistry();
        registry.AddFunction(
            BuildContract("op"),
            (_, _, _) => Task.FromResult<ToolHandlerResult>(new ToolHandlerResult.Deferred("PENDING")));

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
                    if (completedRuns == 1) firstRunCompleted.TrySetResult(true);
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
            (_, _, _) => Task.FromResult<ToolHandlerResult>(
                new ToolHandlerResult.Deferred("WAIT", System.Collections.Immutable.ImmutableDictionary
                    .CreateRange(new Dictionary<string, string> { ["ticket"] = "T-42" }))));

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
        info.Placeholder.Should().Be("WAIT");
        info.Metadata.Should().NotBeNull();
        info.Metadata!["ticket"].Should().Be("T-42");

        await cts.CancelAsync();
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
