using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Agents;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LmMultiTurn.Tests;

public class CodexAgentLoopTests
{
    [Fact]
    public async Task ExecuteRunAsync_MapsCodexEvents_ToMessages()
    {
        var fakeClient = new FakeCodexClient(
        [
            Event("thread.started", """{"type":"thread.started","thread_id":"thread_codex_1"}"""),
            Event("item.started", """
                {
                  "type":"item.started",
                  "item":{"id":"tool_1","type":"mcp_tool_call","server":"sample_tools","tool":"calculate","arguments":{"a":2,"operation":"multiply","b":5},"status":"in_progress"}
                }
                """),
            Event("item.completed", """
                {
                  "type":"item.completed",
                  "item":{"id":"tool_1","type":"mcp_tool_call","server":"sample_tools","tool":"calculate","status":"completed","result":{"content":[{"type":"text","text":"10"}]}}
                }
                """),
            Event("item.updated", """
                {
                  "type":"item.updated",
                  "item":{"id":"reason_1","type":"reasoning","text":"Analyzing request"}
                }
                """),
            Event("item.completed", """
                {
                  "type":"item.completed",
                  "item":{"id":"msg_1","type":"agent_message","text":"The result is 10."}
                }
                """),
            Event("turn.completed", """
                {
                  "type":"turn.completed",
                  "usage":{"input_tokens":12,"cached_input_tokens":3,"output_tokens":7}
                }
                """),
        ]);

        await using var loop = new CodexAgentLoop(
            new CodexSdkOptions(),
            new Dictionary<string, CodexMcpServerConfig>(),
            threadId: "thread-1",
            clientFactory: (_, _) => fakeClient,
            logger: NullLogger<CodexAgentLoop>.Instance);

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var input = new UserInput([
            new TextMessage { Role = Role.User, Text = "calculate 2*5" },
        ]);

        var messages = new List<IMessage>();
        await foreach (var msg in loop.ExecuteRunAsync(input, cts.Token))
        {
            messages.Add(msg);
        }

        messages.OfType<RunAssignmentMessage>().Should().ContainSingle();
        messages.OfType<ToolCallMessage>().Should().ContainSingle(m =>
            m.FunctionName == "calculate" && m.ExecutionTarget == ExecutionTarget.ProviderServer);
        messages.OfType<ToolCallResultMessage>().Should().ContainSingle(m =>
            m.ToolCallId == "tool_1"
            && !m.IsError
            && m.ExecutionTarget == ExecutionTarget.ProviderServer
            && m.Result == "10");
        messages.OfType<ReasoningUpdateMessage>().Should().ContainSingle();
        messages.OfType<TextMessage>().Should().ContainSingle(m => m.Text == "The result is 10.");
        messages.OfType<UsageMessage>().Should().ContainSingle();
        messages.OfType<RunCompletedMessage>().Should().ContainSingle(m => !m.IsError);
        messages
            .Where(m => m is TextMessage or TextUpdateMessage or ReasoningMessage or ReasoningUpdateMessage or ToolCallMessage or ToolCallResultMessage or UsageMessage)
            .Should()
            .OnlyContain(m => m.MessageOrderIdx.HasValue);

        await cts.CancelAsync();
    }

    [Fact]
    public async Task ExecuteRunAsync_EmitsTextUpdates_OnlyFromProviderUpdateEvents()
    {
        var fakeClient = new FakeCodexClient(
        [
            Event("thread.started", """{"type":"thread.started","thread_id":"thread_codex_2"}"""),
            Event("item.updated", """
                {
                  "type":"item.updated",
                  "item":{"id":"msg_1","type":"agent_message","text":"Hel"}
                }
                """),
            Event("item.updated", """
                {
                  "type":"item.updated",
                  "item":{"id":"msg_1","type":"agent_message","text":"Hello"}
                }
                """),
            Event("item.completed", """
                {
                  "type":"item.completed",
                  "item":{"id":"msg_1","type":"agent_message","text":"Hello world"}
                }
                """),
            Event("turn.completed", """
                {
                  "type":"turn.completed",
                  "usage":{"input_tokens":10,"cached_input_tokens":0,"output_tokens":5}
                }
                """),
        ]);

        await using var loop = new CodexAgentLoop(
            new CodexSdkOptions(),
            new Dictionary<string, CodexMcpServerConfig>(),
            threadId: "thread-2",
            clientFactory: (_, _) => fakeClient,
            logger: NullLogger<CodexAgentLoop>.Instance);

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var input = new UserInput([
            new TextMessage { Role = Role.User, Text = "say hello world" },
        ]);

        var messages = new List<IMessage>();
        await foreach (var msg in loop.ExecuteRunAsync(input, cts.Token))
        {
            messages.Add(msg);
        }

        messages.OfType<TextUpdateMessage>().Select(m => m.Text).Should().ContainInOrder("Hel", "lo", " world");
        messages.OfType<TextMessage>().Should().ContainSingle(m => m.Text == "Hello world");
        messages.OfType<RunCompletedMessage>().Should().ContainSingle(m => !m.IsError);
        messages.OfType<TextUpdateMessage>().Should().OnlyContain(m => m.MessageOrderIdx.HasValue);
        messages.OfType<TextMessage>().Should().OnlyContain(m => m.MessageOrderIdx.HasValue);

        await cts.CancelAsync();
    }

    [Fact]
    public async Task ExecuteRunAsync_DoesNotEmitSyntheticTextUpdates_WhenProviderOnlySendsCompletion()
    {
        var fakeClient = new FakeCodexClient(
        [
            Event("thread.started", """{"type":"thread.started","thread_id":"thread_codex_3"}"""),
            Event("item.completed", """
                {
                  "type":"item.completed",
                  "item":{"id":"msg_1","type":"agent_message","text":"Single-shot completion"}
                }
                """),
            Event("turn.completed", """
                {
                  "type":"turn.completed",
                  "usage":{"input_tokens":8,"cached_input_tokens":0,"output_tokens":4}
                }
                """),
        ]);

        await using var loop = new CodexAgentLoop(
            new CodexSdkOptions
            {
                EmitSyntheticMessageUpdates = true,
                SyntheticMessageUpdateChunkChars = 8,
            },
            new Dictionary<string, CodexMcpServerConfig>(),
            threadId: "thread-3",
            clientFactory: (_, _) => fakeClient,
            logger: NullLogger<CodexAgentLoop>.Instance);

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var input = new UserInput([
            new TextMessage { Role = Role.User, Text = "single shot" },
        ]);

        var messages = new List<IMessage>();
        await foreach (var msg in loop.ExecuteRunAsync(input, cts.Token))
        {
            messages.Add(msg);
        }

        messages.OfType<TextUpdateMessage>().Should().BeEmpty();
        messages.OfType<TextMessage>().Should().ContainSingle(m => m.Text == "Single-shot completion");
        messages.OfType<RunCompletedMessage>().Should().ContainSingle(m => !m.IsError);
        messages.OfType<TextMessage>().Should().OnlyContain(m => m.MessageOrderIdx.HasValue);

        await cts.CancelAsync();
    }

    private static CodexTurnEventEnvelope Event(string name, string json)
    {
        var element = JsonDocument.Parse(json).RootElement.Clone();
        return new CodexTurnEventEnvelope
        {
            Type = name,
            Event = element,
            RequestId = Guid.NewGuid().ToString("N"),
            ThreadId = null,
        };
    }

    private sealed class FakeCodexClient : ICodexSdkClient
    {
        private readonly IReadOnlyList<CodexTurnEventEnvelope> _events;

        public FakeCodexClient(IReadOnlyList<CodexTurnEventEnvelope> events)
        {
            _events = events;
        }

        public bool IsRunning { get; private set; }

        public string? CurrentCodexThreadId { get; private set; }

        public string DependencyState => "ready";

        public Task EnsureStartedAsync(CodexBridgeInitOptions options, CancellationToken ct = default)
        {
            IsRunning = true;
            CurrentCodexThreadId = options.ThreadId;
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<CodexTurnEventEnvelope> RunStreamingAsync(
            string input,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var item in _events)
            {
                ct.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }

        public Task ShutdownAsync(TimeSpan? timeout = null, CancellationToken ct = default)
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
