using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
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

    private static ToolCallMessage HoldCall(string id) =>
        new()
        {
            FunctionName = "hold",
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

        public Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 2)
            {
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

        public HoldingTool()
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

                    return ToolHandlerResult.FromText(JsonSerializer.Serialize(new { result = "real" }));
                }
            );
        }

        public FunctionRegistry Registry { get; }

        public CancellationToken Token { get; private set; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();
    }

    /// <summary>Signals when a log line containing <paramref name="fragment"/> is written.</summary>
    private sealed class SignallingLogger(string fragment, TaskCompletionSource signal) : ILogger<MultiTurnAgentLoop>
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
            if (formatter(state, exception).Contains(fragment, StringComparison.Ordinal))
            {
                _ = signal.TrySetResult();
            }
        }
    }
}
