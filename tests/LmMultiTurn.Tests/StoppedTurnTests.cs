using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Approval;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.ClientTools;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// #930 item 11: Stop ends the turn where it is. What the user already saw stays in history, marked
/// interrupted. No tool answer goes back to the model, and tools already running are not killed.
/// </summary>
public class StoppedTurnTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    private static TextUpdateMessage Delta(string text) => new() { Text = text, Role = Role.Assistant };

    [Fact]
    public async Task StopMidText_KeepsWhatTheUserSaw_MarkedInterrupted_InHistoryAndStore()
    {
        var provider = new ScriptedProvider(_ => [Delta("Hel"), Delta("lo")], blockFirstTurn: true);
        var store = new InMemoryConversationStore();
        using var cts = new CancellationTokenSource(Generous);
        await using var loop = new MultiTurnAgentLoop(provider, new FunctionRegistry(), "stop-text", store: store);
        var running = loop.RunAsync(cts.Token);
        await loop.WaitUntilReadyAsync(cts.Token);
        _ = await loop.SendAsync([new TextMessage { Text = "go", Role = Role.User }], inputId: "input-1");
        await provider.FirstTurnBlocked.Task.WaitAsync(cts.Token);

        await loop.StopAsync(Generous);
        await running;

        var partial = AssistantTexts(loop.GetHistorySnapshot()).Should().ContainSingle().Subject;
        partial.Text.Should().Be("Hello");
        partial.IsInterrupted.Should().BeTrue();

        var persisted = MessagePersistenceConverter
            .FromPersistedMessages(await store.LoadMessagesAsync(loop.ThreadId, cts.Token))
            .ToList();
        var stored = AssistantTexts(persisted).Should().ContainSingle().Subject;
        stored.Text.Should().Be("Hello");
        stored.IsInterrupted.Should().BeTrue("the mark has to survive a reload");
    }

    [Fact]
    public async Task StopAfterAFinishedTextBlock_DoesNotDuplicateIt()
    {
        // A tool-call fragment closes the text block, so the joiner has already handed the finished text over.
        var provider = new ScriptedProvider(
            _ =>
                [
                    Delta("done"),
                    new ToolCallUpdateMessage
                    {
                        ToolCallId = "half",
                        FunctionName = "x",
                        Role = Role.Assistant,
                    },
                ],
            blockFirstTurn: true
        );
        using var cts = new CancellationTokenSource(Generous);
        await using var loop = new MultiTurnAgentLoop(provider, new FunctionRegistry(), "stop-finished");
        var running = loop.RunAsync(cts.Token);
        await loop.WaitUntilReadyAsync(cts.Token);
        _ = await loop.SendAsync([new TextMessage { Text = "go", Role = Role.User }], inputId: "input-1");
        await provider.FirstTurnBlocked.Task.WaitAsync(cts.Token);

        await loop.StopAsync(Generous);
        await running;

        var text = AssistantTexts(loop.GetHistorySnapshot()).Should().ContainSingle().Subject;
        text.Text.Should().Be("done");
        text.IsInterrupted.Should().BeFalse("this block was complete before the Stop");
    }

    [Fact]
    public async Task StopBeforeAnyText_AddsNoAssistantRow()
    {
        var provider = new ScriptedProvider(_ => [], blockFirstTurn: true);
        using var cts = new CancellationTokenSource(Generous);
        await using var loop = new MultiTurnAgentLoop(provider, new FunctionRegistry(), "stop-empty");
        var running = loop.RunAsync(cts.Token);
        await loop.WaitUntilReadyAsync(cts.Token);
        _ = await loop.SendAsync([new TextMessage { Text = "go", Role = Role.User }], inputId: "input-1");
        await provider.FirstTurnBlocked.Task.WaitAsync(cts.Token);

        await loop.StopAsync(Generous);
        await running;

        AssistantTexts(loop.GetHistorySnapshot()).Should().BeEmpty();
    }

    [Fact]
    public async Task StopWhileAToolRuns_LeavesItRunning_AndAnswersTheCallOnce()
    {
        var tool = new HoldingTool();
        var lateResult = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider(call =>
            call == 1 ? [HoldCall("hold-1")] : [new TextMessage { Text = "next answer", Role = Role.Assistant }]
        );
        using var cts = new CancellationTokenSource(Generous);
        await using var loop = new MultiTurnAgentLoop(
            provider,
            tool.Registry,
            "stop-tool",
            logger: new SignallingLogger("arrived after the Stop", lateResult)
        );
        var running = loop.RunAsync(cts.Token);
        await loop.WaitUntilReadyAsync(cts.Token);
        _ = await loop.SendAsync([new TextMessage { Text = "go", Role = Role.User }], inputId: "input-1");
        await tool.Entered.Task.WaitAsync(cts.Token);

        await loop.StopAsync(Generous);
        await running;

        tool.Token.IsCancellationRequested.Should().BeFalse("Stop does not kill a running tool");
        tool.Finished.Task.IsCompleted.Should().BeFalse("the tool has not been told to finish yet");
        var stopped = ResultsFor(loop, "hold-1").Should().ContainSingle().Subject;
        stopped.IsError.Should().BeTrue();
        stopped.Result.Should().Contain("stopped");
        provider.CallCount.Should().Be(1, "no tool answer goes back to the model after a Stop");

        tool.Release();
        await tool.Finished.Task.WaitAsync(cts.Token);
        await lateResult.Task.WaitAsync(cts.Token);

        ResultsFor(loop, "hold-1").Should().ContainSingle().Which.Result.Should().Contain("stopped");

        var restarted = loop.RunAsync(cts.Token);
        await loop.WaitUntilReadyAsync(cts.Token);
        _ = await loop.SendAsync([new TextMessage { Text = "again", Role = Role.User }], inputId: "input-2");
        await provider.SecondRequest.Task.WaitAsync(cts.Token);
        await loop.StopAsync(Generous);
        await restarted;

        var history = loop.GetHistorySnapshot().ToList();
        var callIndex = history.FindIndex(m => m is ToolCallMessage { ToolCallId: "hold-1" });
        history[callIndex + 1]
            .Should()
            .BeOfType<ToolCallResultMessage>("a tool call must be directly followed by its result")
            .Which.ToolCallId.Should()
            .Be("hold-1");
    }

    [Fact]
    public async Task Dispose_StillCancelsARunningTool()
    {
        var tool = new HoldingTool();
        var provider = new ScriptedProvider(_ => [HoldCall("hold-2")]);
        using var cts = new CancellationTokenSource(Generous);
        var loop = new MultiTurnAgentLoop(provider, tool.Registry, "dispose-tool");
        var running = loop.RunAsync(cts.Token);
        await loop.WaitUntilReadyAsync(cts.Token);
        _ = await loop.SendAsync([new TextMessage { Text = "go", Role = Role.User }], inputId: "input-1");
        await tool.Entered.Task.WaitAsync(cts.Token);

        await loop.DisposeAsync();

        await tool.Finished.Task.WaitAsync(cts.Token);
        tool.Token.IsCancellationRequested.Should().BeTrue("disposal is what ends a running tool");
        await running;
    }

    [Fact]
    public async Task ToolsCallWithoutGenerationId_IsStillExecuted()
    {
        // #502: the plural message used to pass straight through, so it was saved but never ran.
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new FunctionRegistry().AddFunction(
            new FunctionContract
            {
                Name = "probe",
                Description = "Records that it ran",
                Parameters = [],
            },
            (_, _, _) =>
            {
                ran.TrySetResult();
                return Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("ok"));
            }
        );
        var provider = new ScriptedProvider(
            call =>
                call == 1
                    ?
                    [
                        new ToolsCallMessage
                        {
                            Role = Role.Assistant,
                            ToolCalls =
                            [
                                new ToolCall
                                {
                                    FunctionName = "probe",
                                    FunctionArgs = "{}",
                                    ToolCallId = "plural-1",
                                },
                            ],
                        },
                    ]
                    : [new TextMessage { Text = "done", Role = Role.Assistant }],
            stampGeneration: false
        );
        using var cts = new CancellationTokenSource(Generous);
        await using var loop = new MultiTurnAgentLoop(provider, registry, "missing-generation");
        var running = loop.RunAsync(cts.Token);
        await loop.WaitUntilReadyAsync(cts.Token);
        _ = await loop.SendAsync([new TextMessage { Text = "go", Role = Role.User }], inputId: "input-1");

        await ran.Task.WaitAsync(cts.Token);
        await provider.SecondRequest.Task.WaitAsync(cts.Token);
        await loop.StopAsync(Generous);
        await running;

        ResultsFor(loop, "plural-1").Should().ContainSingle();
    }

    [Fact]
    public async Task Stop_WaitsForAClaimedAnswer_ThatTakesLongerThanTwoSecondsToSave()
    {
        var saving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishSaving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new HookedStore(async id =>
        {
            if (id == "ask-1")
            {
                _ = saving.TrySetResult();
                await finishSaving.Task;
            }
        });
        var provider = new ScriptedProvider(call =>
            call == 1 ? [Call("ask-1", AskUserQuestionToolProvider.ToolName)] : [Answer]
        );
        using var cts = new CancellationTokenSource(Generous);
        await using var loop = new MultiTurnAgentLoop(provider, DeferredQuestion(), "stop-slow-save", store: store);
        var running = loop.RunAsync(cts.Token);
        await loop.WaitUntilReadyAsync(cts.Token);
        _ = await loop.SendAsync([new TextMessage { Text = "go", Role = Role.User }], inputId: "input-1");

        Task stop;
        try
        {
            await saving.Task.WaitAsync(cts.Token);
            stop = loop.StopAsync(Generous);
            (await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(2.2), cts.Token)))
                .Should()
                .NotBeSameAs(stop, "the next request must not go out before this answer is saved");
        }
        finally
        {
            finishSaving.TrySetResult();
        }

        await stop.WaitAsync(cts.Token);
        await running.WaitAsync(cts.Token);

        var request = await NextRequestAsync(loop, provider, cts.Token);
        ShouldAnswerEachCallOnce(request, "ask-1");
    }

    [Fact]
    public async Task Stop_WhenAClaimedAnswerFailsToSave_AnswersTheCallItself()
    {
        var saving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failSaving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var store = new HookedStore(async id =>
        {
            if (id == "ask-1" && Interlocked.Increment(ref attempts) == 1)
            {
                _ = saving.TrySetResult();
                await failSaving.Task;
                throw new IOException("store down");
            }
        });
        var provider = new ScriptedProvider(call =>
            call == 1 ? [Call("ask-1", AskUserQuestionToolProvider.ToolName)] : [Answer]
        );
        using var cts = new CancellationTokenSource(Generous);
        await using var loop = new MultiTurnAgentLoop(provider, DeferredQuestion(), "stop-failed-save", store: store);
        var running = loop.RunAsync(cts.Token);
        await loop.WaitUntilReadyAsync(cts.Token);
        _ = await loop.SendAsync([new TextMessage { Text = "go", Role = Role.User }], inputId: "input-1");

        Task stop;
        try
        {
            await saving.Task.WaitAsync(cts.Token);
            stop = loop.StopAsync(Generous);
        }
        finally
        {
            failSaving.TrySetResult();
        }

        await stop.WaitAsync(cts.Token);
        await running.WaitAsync(cts.Token);

        ResultsFor(loop, "ask-1").Should().ContainSingle().Which.IsError.Should().BeTrue();
        var request = await NextRequestAsync(loop, provider, cts.Token);
        ShouldAnswerEachCallOnce(request, "ask-1");
    }

    [Fact]
    public async Task Stop_WhenItsOwnAnswerCannotBeSaved_StillAnswersTheCall()
    {
        var tool = new HoldingTool();
        var store = new HookedStore(id =>
            id == "hold-1" ? Task.FromException(new IOException("store down")) : Task.CompletedTask
        );
        var provider = new ScriptedProvider(call => call == 1 ? [HoldCall("hold-1")] : [Answer]);
        using var cts = new CancellationTokenSource(Generous);
        await using var loop = new MultiTurnAgentLoop(provider, tool.Registry, "stop-row-fails", store: store);
        var running = loop.RunAsync(cts.Token);
        await loop.WaitUntilReadyAsync(cts.Token);
        _ = await loop.SendAsync([new TextMessage { Text = "go", Role = Role.User }], inputId: "input-1");
        await tool.Entered.Task.WaitAsync(cts.Token);

        await loop.StopAsync(Generous).WaitAsync(cts.Token);
        await running.WaitAsync(cts.Token);

        ResultsFor(loop, "hold-1").Should().ContainSingle().Which.IsError.Should().BeTrue();
        var request = await NextRequestAsync(loop, provider, cts.Token);
        ShouldAnswerEachCallOnce(request, "hold-1");
    }

    [Fact]
    public async Task Stop_WithAFinishedAWritingAndARunningTool_AnswersEachCallOnce()
    {
        var tool = new HoldingTool();
        var registry = tool
            .Registry.AddFunction(
                new FunctionContract
                {
                    Name = "finish",
                    Description = "Finishes at once",
                    Parameters = [],
                },
                (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("finished"))
            )
            .AddFunction(QuestionContract, DeferQuestion);
        var finishedSaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishWriting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new HookedStore(async id =>
        {
            if (id == "finished-1")
            {
                _ = finishedSaved.TrySetResult();
            }
            else if (id == "ask-1")
            {
                _ = writing.TrySetResult();
                await finishWriting.Task;
            }
        });
        var lateResult = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider(call =>
            call == 1
                ?
                [
                    Call("finished-1", "finish"),
                    Call("ask-1", AskUserQuestionToolProvider.ToolName),
                    HoldCall("hold-1"),
                ]
                : [Answer]
        );
        using var cts = new CancellationTokenSource(Generous);
        await using var loop = new MultiTurnAgentLoop(
            provider,
            registry,
            "stop-mixed",
            store: store,
            logger: new SignallingLogger("arrived after the Stop", lateResult)
        );
        var running = loop.RunAsync(cts.Token);
        await loop.WaitUntilReadyAsync(cts.Token);
        _ = await loop.SendAsync([new TextMessage { Text = "go", Role = Role.User }], inputId: "input-1");

        Task stop;
        try
        {
            await Task.WhenAll(finishedSaved.Task, writing.Task, tool.Entered.Task).WaitAsync(cts.Token);
            stop = loop.StopAsync(Generous);
        }
        finally
        {
            finishWriting.TrySetResult();
        }

        await stop.WaitAsync(cts.Token);
        await running.WaitAsync(cts.Token);
        tool.Release();
        await lateResult.Task.WaitAsync(cts.Token);

        var request = await NextRequestAsync(loop, provider, cts.Token);
        ShouldAnswerEachCallOnce(request, "finished-1", "ask-1", "hold-1");
    }

    [Fact]
    public async Task Stop_WhileAToolWaitsToStart_TheToolNeverRuns()
    {
        // A host policy that ignores its token: an approver gate is already refused after a Stop by
        // ToolInvocationPreparer, but nothing there re-checks the token after a policy allows the call.
        var tool = new HoldingTool();
        var policy = new HoldingPolicy();
        var abandoned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider(_ => [HoldCall("hold-1")]);
        using var cts = new CancellationTokenSource(Generous);
        await using var loop = new MultiTurnAgentLoop(
            provider,
            tool.Registry,
            "stop-queued",
            logger: new SignallingLogger("ended without a result", abandoned),
            lifecycleServices: new MultiTurnLifecycleServices
            {
                Approval = new ToolInvocationPreparer(new ToolApprovalOptions { HostPolicy = policy }),
            }
        );
        var running = loop.RunAsync(cts.Token);
        await loop.WaitUntilReadyAsync(cts.Token);
        _ = await loop.SendAsync([new TextMessage { Text = "go", Role = Role.User }], inputId: "input-1");
        await policy.Entered.Task.WaitAsync(cts.Token);

        await loop.StopAsync(Generous).WaitAsync(cts.Token);
        await running.WaitAsync(cts.Token);
        ResultsFor(loop, "hold-1").Should().ContainSingle().Which.IsError.Should().BeTrue();

        policy.Release();

        (await Task.WhenAny(abandoned.Task, tool.Entered.Task).WaitAsync(cts.Token))
            .Should()
            .BeSameAs(abandoned.Task, "the Stop already answered this call, so its handler must not start");
        tool.Entered.Task.IsCompleted.Should().BeFalse();
    }

    private static readonly TextMessage Answer = new() { Text = "next answer", Role = Role.Assistant };

    private static readonly FunctionContract QuestionContract = new()
    {
        Name = AskUserQuestionToolProvider.ToolName,
        Description = "Defers until someone answers",
        Parameters = [],
    };

    private static Task<ToolHandlerResult> DeferQuestion(string args, ToolCallContext context, CancellationToken ct) =>
        Task.FromResult<ToolHandlerResult>(new ToolHandlerResult.Deferred());

    private static FunctionRegistry DeferredQuestion() =>
        new FunctionRegistry().AddFunction(QuestionContract, DeferQuestion);

    /// <summary>Restarts a stopped loop, sends new input and returns the request the provider then got.</summary>
    [Fact]
    public async Task StopDuringTheOutputSave_KeepsTheFinishedResult_AndAnswersTheCallOnce()
    {
        var saved = new HoldingOutputStore();
        var provider = new ScriptedProvider(call => call == 1 ? [Call("big-1", "big")] : [Answer]);
        using var cts = new CancellationTokenSource(Generous);
        await using var loop = new MultiTurnAgentLoop(provider, BigOutputRegistry(), "stop-save")
        {
            ToolOutputSpill = new ToolOutputSpillOptions { Store = saved, MaxInlineBytes = 2_000 },
        };
        var running = loop.RunAsync(cts.Token);
        await loop.WaitUntilReadyAsync(cts.Token);
        _ = await loop.SendAsync([new TextMessage { Text = "go", Role = Role.User }], inputId: "input-1");
        await saved.Entered.Task.WaitAsync(cts.Token);

        // Released only once the Stop has reached the run token. A save bound to that token has been
        // cancelled by then: the provider's callback was registered first, so it runs last.
        var stopping = loop.StopAsync(Generous);
        await provider.RunTokenCancelled.Task.WaitAsync(cts.Token);
        saved.Release();
        await stopping.WaitAsync(cts.Token);
        await running.WaitAsync(cts.Token);

        var result = ResultsFor(loop, "big-1").Should().ContainSingle().Subject;
        result.IsError.Should().BeFalse("the tool finished before the Stop");
        result.Result.Should().Contain(HoldingOutputStore.Path);
        var request = await NextRequestAsync(loop, provider, cts.Token);
        ShouldAnswerEachCallOnce(request, "big-1");
    }

    [Fact]
    public async Task DisposeDuringTheOutputSave_KeepsTheFinishedResult()
    {
        var saved = new HoldingOutputStore();
        var provider = new ScriptedProvider(call => call == 1 ? [Call("big-1", "big")] : [Answer]);
        using var cts = new CancellationTokenSource(Generous);
        var store = new InMemoryConversationStore();

        // Lifecycle on: its tool-completed write also runs on the disposed loop, and must not stop history.
        var loop = new MultiTurnAgentLoop(
            provider,
            BigOutputRegistry(),
            "dispose-save",
            store: store,
            lifecycleServices: new MultiTurnLifecycleServices
            {
                Publisher = NullLifecyclePublisher.Instance,
                LifecycleStore = store,
            }
        )
        {
            ToolOutputSpill = new ToolOutputSpillOptions { Store = saved, MaxInlineBytes = 2_000 },
        };
        var running = loop.RunAsync(cts.Token);
        await loop.WaitUntilReadyAsync(cts.Token);
        _ = await loop.SendAsync([new TextMessage { Text = "go", Role = Role.User }], inputId: "input-1");
        await saved.Entered.Task.WaitAsync(cts.Token);

        await loop.DisposeAsync().AsTask().WaitAsync(cts.Token);
        await running.WaitAsync(cts.Token);

        var result = ResultsFor(loop, "big-1").Should().ContainSingle().Subject;
        result.IsError.Should().BeFalse("the tool finished before the loop was disposed");
        result.Result.Should().StartWith("xxx").And.Contain("could not be saved");
    }

    [Fact]
    public async Task ALargeResultArrivingAfterTheStop_SavesNothing_AndIsLoggedAsDropped()
    {
        var tool = new HoldingTool(new string('x', 50_000));
        _ = tool.Registry.AddFunction(
            ReaderContract,
            (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("unused"))
        );
        var saved = new HoldingOutputStore();
        saved.Release();
        var lateResult = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider(_ => [HoldCall("hold-1")]);
        using var cts = new CancellationTokenSource(Generous);
        await using var loop = new MultiTurnAgentLoop(
            provider,
            tool.Registry,
            "stop-late-large",
            logger: new SignallingLogger("arrived after the Stop", lateResult, LogLevel.Information)
        )
        {
            ToolOutputSpill = new ToolOutputSpillOptions { Store = saved, MaxInlineBytes = 2_000 },
        };
        var running = loop.RunAsync(cts.Token);
        await loop.WaitUntilReadyAsync(cts.Token);
        _ = await loop.SendAsync([new TextMessage { Text = "go", Role = Role.User }], inputId: "input-1");
        await tool.Entered.Task.WaitAsync(cts.Token);

        await loop.StopAsync(Generous).WaitAsync(cts.Token);
        await running.WaitAsync(cts.Token);
        tool.Release();
        await lateResult.Task.WaitAsync(cts.Token);

        saved.Saved.Should().BeEmpty("nothing will ever read this result");
    }

    private static readonly FunctionContract ReaderContract = new()
    {
        Name = "Read",
        Description = "Stands in for the tool that opens a saved file",
        Parameters = [],
    };

    private static FunctionRegistry BigOutputRegistry() =>
        new FunctionRegistry()
            .AddFunction(
                new FunctionContract
                {
                    Name = "big",
                    Description = "Returns more than the inline limit at once",
                    Parameters = [],
                },
                (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText(new string('x', 50_000)))
            )
            .AddFunction(
                ReaderContract,
                (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("unused"))
            );

    private static async Task<IReadOnlyList<IMessage>> NextRequestAsync(
        MultiTurnAgentLoop loop,
        ScriptedProvider provider,
        CancellationToken ct
    )
    {
        var restarted = loop.RunAsync(ct);
        await loop.WaitUntilReadyAsync(ct);
        _ = await loop.SendAsync([new TextMessage { Text = "again", Role = Role.User }], inputId: "input-2");
        await provider.SecondRequest.Task.WaitAsync(ct);
        await loop.StopAsync(Generous).WaitAsync(ct);
        await restarted.WaitAsync(ct);
        return provider.SecondRequestMessages;
    }

    private static void ShouldAnswerEachCallOnce(IReadOnlyList<IMessage> request, params string[] toolCallIds)
    {
        var calls = request
            .SelectMany(m =>
                m switch
                {
                    ToolCallMessage call => [call.ToolCallId],
                    ToolsCallMessage plural => plural.ToolCalls.Select(c => c.ToolCallId),
                    ToolsCallAggregateMessage both => both.ToolsCallMessage.ToolCalls.Select(c => c.ToolCallId),
                    _ => [],
                }
            )
            .ToList();
        var answers = request
            .SelectMany(m =>
                m switch
                {
                    ToolCallResultMessage result => [result.ToolCallId],
                    ToolsCallResultMessage plural => plural.ToolCallResults.Select(r => r.ToolCallId),
                    ToolsCallAggregateMessage both => both.ToolsCallResult.ToolCallResults.Select(r => r.ToolCallId),
                    _ => [],
                }
            )
            .ToList();

        foreach (var id in toolCallIds)
        {
            calls.Where(c => c == id).Should().ContainSingle($"{id} must be in the next request once");
            answers.Where(a => a == id).Should().ContainSingle($"{id} must have exactly one answer");
        }
    }

    private static ToolCallMessage HoldCall(string id) => Call(id, "hold");

    private static ToolCallMessage Call(string id, string functionName) =>
        new()
        {
            FunctionName = functionName,
            FunctionArgs = "{}",
            ToolCallId = id,
            Role = Role.Assistant,
        };

    private static IEnumerable<TextMessage> AssistantTexts(IEnumerable<IMessage> messages) =>
        messages.OfType<TextMessage>().Where(m => m.Role == Role.Assistant);

    private static IReadOnlyList<ToolCallResultMessage> ResultsFor(MultiTurnAgentLoop loop, string toolCallId) =>
        [.. loop.GetHistorySnapshot().OfType<ToolCallResultMessage>().Where(m => m.ToolCallId == toolCallId)];

    /// <summary>
    /// Streams a scripted reply stamped the way real providers stamp it. <c>blockFirstTurn</c> then parks the
    /// first call on its token, which is the moment every delta has already been published.
    /// </summary>
    private sealed class ScriptedProvider(
        Func<int, IReadOnlyList<IMessage>> script,
        bool blockFirstTurn = false,
        bool stampGeneration = true
    ) : IStreamingAgent
    {
        private int _calls;

        public int CallCount => Volatile.Read(ref _calls);

        public TaskCompletionSource FirstTurnBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource RunTokenCancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<IMessage> SecondRequestMessages { get; private set; } = [];

        public Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                _ = cancellationToken.Register(() => RunTokenCancelled.TrySetResult());
            }

            if (call == 2)
            {
                SecondRequestMessages = [.. messages];
                _ = SecondRequest.TrySetResult();
            }

            return Task.FromResult(Stream(call, options, cancellationToken));
        }

        public Task<IEnumerable<IMessage>> GenerateReplyAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException("Streaming only");

        private async IAsyncEnumerable<IMessage> Stream(
            int call,
            GenerateReplyOptions? options,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            await Task.Yield();
            foreach (var message in script(call))
            {
                yield return stampGeneration ? message.WithIds(options) : message;
            }

            if (blockFirstTurn && call == 1)
            {
                _ = FirstTurnBlocked.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
        }
    }

    /// <summary>A tool that stays running until released and records the token it was given.</summary>
    private sealed class HoldingTool
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HoldingTool(string? output = null)
        {
            Registry = new FunctionRegistry().AddFunction(
                new FunctionContract
                {
                    Name = "hold",
                    Description = "Runs until the test releases it",
                    Parameters = [],
                },
                async (_, _, ct) =>
                {
                    Token = ct;
                    _ = Entered.TrySetResult();
                    try
                    {
                        await _release.Task.WaitAsync(ct);
                    }
                    finally
                    {
                        _ = Finished.TrySetResult();
                    }

                    return ToolHandlerResult.FromText(output ?? JsonSerializer.Serialize(new { result = "real" }));
                }
            );
        }

        public FunctionRegistry Registry { get; }

        public CancellationToken Token { get; private set; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();
    }

    /// <summary>A store that holds a save until released, and fails it if its token was cancelled by then.</summary>
    private sealed class HoldingOutputStore : IToolOutputStore
    {
        public const string Path = "/workspace/.tool-output/abc/output.txt";

        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Saved { get; } = [];

        public void Release() => _release.TrySetResult();

        public async Task<string?> SaveAsync(string content, CancellationToken ct = default)
        {
            Saved.Add(content);
            _ = Entered.TrySetResult();
            await _release.Task.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            return Path;
        }
    }

    /// <summary>A host policy that holds a call until released, ignoring its token on purpose.</summary>
    private sealed class HoldingPolicy : IToolExecutionPolicy
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public async ValueTask<ToolApprovalVerdict> EvaluateAsync(
            ToolApprovalContext context,
            CancellationToken cancellationToken
        )
        {
            _ = Entered.TrySetResult();
            await _release.Task.WaitAsync(Generous, CancellationToken.None);
            return ToolApprovalVerdict.Allow();
        }
    }

    /// <summary>A store that runs a hook before saving a tool result, so a test can hold or fail that save.</summary>
    private sealed class HookedStore(Func<string, Task> beforeResultSaved) : IConversationStore
    {
        private readonly InMemoryConversationStore _inner = new();

        public async Task AppendMessagesAsync(
            string threadId,
            IReadOnlyList<PersistedMessage> messages,
            CancellationToken ct = default
        )
        {
            foreach (
                var result in MessagePersistenceConverter
                    .FromPersistedMessages(messages)
                    .OfType<ToolCallResultMessage>()
            )
            {
                await beforeResultSaved(result.ToolCallId!);
            }

            await _inner.AppendMessagesAsync(threadId, messages, ct);
        }

        public Task ReplaceMessageAsync(
            string threadId,
            PersistedMessage replacement,
            CancellationToken ct = default
        ) => _inner.ReplaceMessageAsync(threadId, replacement, ct);

        public Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(
            string threadId,
            CancellationToken ct = default
        ) => _inner.LoadMessagesAsync(threadId, ct);

        public Task SaveMetadataAsync(string threadId, ThreadMetadata metadata, CancellationToken ct = default) =>
            _inner.SaveMetadataAsync(threadId, metadata, ct);

        public Task<ThreadMetadata?> LoadMetadataAsync(string threadId, CancellationToken ct = default) =>
            _inner.LoadMetadataAsync(threadId, ct);

        public Task UpdateMetadataAsync(
            string threadId,
            Func<ThreadMetadata?, ThreadMetadata> update,
            CancellationToken ct = default
        ) => _inner.UpdateMetadataAsync(threadId, update, ct);

        public Task DeleteThreadAsync(string threadId, CancellationToken ct = default) =>
            _inner.DeleteThreadAsync(threadId, ct);

        public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
            int limit = 50,
            int offset = 0,
            ConversationListOptions? options = null,
            CancellationToken ct = default
        ) => _inner.ListThreadsAsync(limit, offset, options, ct);
    }

    /// <summary>Signals when a log line containing <paramref name="fragment"/> is written.</summary>
    private sealed class SignallingLogger(
        string fragment,
        TaskCompletionSource signal,
        LogLevel minimumLevel = LogLevel.Trace
    ) : ILogger<MultiTurnAgentLoop>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => NullLogger<MultiTurnAgentLoop>.Instance.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (logLevel >= minimumLevel && formatter(state, exception).Contains(fragment, StringComparison.Ordinal))
            {
                _ = signal.TrySetResult();
            }
        }
    }
}
