using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Agents;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Models;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Agents;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace LmMultiTurn.Tests;

/// <summary>
/// Sibling-loop fork-semantics coverage: <see cref="CodexAgentLoop"/> and
/// <see cref="CopilotAgentLoop"/> must propagate caller-supplied
/// <c>UserInput.ParentRunId</c> exactly like Claude — one happy-path per loop is
/// enough since the rule is centralized in <c>MultiTurnAgentBase.ResolveBatchParent</c>.
/// </summary>
public class CodexAndCopilotForkSemanticsTests : LoggingTestBase
{
    public CodexAndCopilotForkSemanticsTests(ITestOutputHelper output) : base(output) { }

    [Fact]
    public async Task CodexAgentLoop_WithParentRunId_PublishesForkedCompletion()
    {
        var fakeClient = new MinimalCodexClient(
        [
            Event("thread.started", """{"type":"thread.started","thread_id":"thread_codex_fork"}"""),
            Event("item.completed", """
                {"type":"item.completed","item":{"id":"msg_1","type":"agent_message","text":"ok"}}
                """),
            Event("turn.completed", """
                {"type":"turn.completed","usage":{"input_tokens":1,"cached_input_tokens":0,"output_tokens":1}}
                """),
        ]);

        await using var loop = new CodexAgentLoop(
            new CodexSdkOptions(),
            new Dictionary<string, CodexMcpServerConfig>(),
            functionRegistry: null,
            enabledTools: null,
            threadId: "codex-fork-test",
            clientFactory: (_, _) => fakeClient,
            logger: LoggerFactory.CreateLogger<CodexAgentLoop>());

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var input = new UserInput(
            [new TextMessage { Role = Role.User, Text = "hi" }],
            ParentRunId: "codex-parent-1");

        var messages = new List<IMessage>();
        await foreach (var msg in loop.ExecuteRunAsync(input, cts.Token))
        {
            messages.Add(msg);
        }

        var assignment = messages.OfType<RunAssignmentMessage>().Should().ContainSingle().Subject;
        assignment.Assignment.ParentRunId.Should().Be("codex-parent-1");

        var completed = messages.OfType<RunCompletedMessage>().Should().ContainSingle().Subject;
        completed.WasForked.Should().BeTrue();
        completed.ForkedToRunId.Should().Be(completed.CompletedRunId);

        await cts.CancelAsync();
    }

    [Fact]
    public async Task CodexAgentLoop_WithoutParentRunId_NotForked()
    {
        var fakeClient = new MinimalCodexClient(
        [
            Event("thread.started", """{"type":"thread.started","thread_id":"thread_codex_no_fork"}"""),
            Event("item.completed", """
                {"type":"item.completed","item":{"id":"msg_1","type":"agent_message","text":"ok"}}
                """),
            Event("turn.completed", """
                {"type":"turn.completed","usage":{"input_tokens":1,"cached_input_tokens":0,"output_tokens":1}}
                """),
        ]);

        await using var loop = new CodexAgentLoop(
            new CodexSdkOptions(),
            new Dictionary<string, CodexMcpServerConfig>(),
            functionRegistry: null,
            enabledTools: null,
            threadId: "codex-no-fork-test",
            clientFactory: (_, _) => fakeClient,
            logger: LoggerFactory.CreateLogger<CodexAgentLoop>());

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var input = new UserInput([new TextMessage { Role = Role.User, Text = "hi" }]);

        var messages = new List<IMessage>();
        await foreach (var msg in loop.ExecuteRunAsync(input, cts.Token))
        {
            messages.Add(msg);
        }

        var completed = messages.OfType<RunCompletedMessage>().Should().ContainSingle().Subject;
        completed.WasForked.Should().BeFalse();
        completed.ForkedToRunId.Should().BeNull();

        await cts.CancelAsync();
    }

    [Fact]
    public async Task CopilotAgentLoop_WithParentRunId_PublishesForkedCompletion()
    {
        var fakeClient = new MinimalCopilotClient(
            sessionId: "sess_copilot_fork",
            events:
            [
                SessionUpdate("sess_copilot_fork", """
                    {"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"ok"}}
                    """),
                PromptCompleted("""{"usage":{"inputTokens":1,"outputTokens":1,"cachedInputTokens":0}}"""),
            ]);

        await using var loop = new CopilotAgentLoop(
            new CopilotSdkOptions(),
            threadId: "copilot-fork-test",
            clientFactory: (_, _) => fakeClient,
            logger: LoggerFactory.CreateLogger<CopilotAgentLoop>());

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var input = new UserInput(
            [new TextMessage { Role = Role.User, Text = "hi" }],
            ParentRunId: "copilot-parent-1");

        var messages = new List<IMessage>();
        await foreach (var msg in loop.ExecuteRunAsync(input, cts.Token))
        {
            messages.Add(msg);
        }

        var assignment = messages.OfType<RunAssignmentMessage>().Should().ContainSingle().Subject;
        assignment.Assignment.ParentRunId.Should().Be("copilot-parent-1");

        var completed = messages.OfType<RunCompletedMessage>().Should().ContainSingle().Subject;
        completed.WasForked.Should().BeTrue();
        completed.ForkedToRunId.Should().Be(completed.CompletedRunId);

        await cts.CancelAsync();
    }

    // --- Helpers ---

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

    private static CopilotTurnEventEnvelope SessionUpdate(string sessionId, string updateJson)
    {
        using var updateDoc = JsonDocument.Parse(updateJson);
        var updateElement = updateDoc.RootElement;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "session/update");
            writer.WriteString("sessionId", sessionId);
            writer.WritePropertyName("update");
            updateElement.WriteTo(writer);
            writer.WriteEndObject();
        }

        using var envelopeDoc = JsonDocument.Parse(stream.ToArray());
        var element = envelopeDoc.RootElement.Clone();
        return new CopilotTurnEventEnvelope
        {
            Type = "event",
            Event = element,
            RequestId = Guid.NewGuid().ToString("N"),
            SessionId = sessionId,
        };
    }

    private static CopilotTurnEventEnvelope PromptCompleted(string innerJson)
    {
        using var innerDoc = JsonDocument.Parse(innerJson);
        var inner = innerDoc.RootElement;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "session/prompt/completed");
            foreach (var property in inner.EnumerateObject())
            {
                property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        using var envelopeDoc = JsonDocument.Parse(stream.ToArray());
        var element = envelopeDoc.RootElement.Clone();
        return new CopilotTurnEventEnvelope
        {
            Type = "event",
            Event = element,
            RequestId = Guid.NewGuid().ToString("N"),
            SessionId = null,
        };
    }

    private sealed class MinimalCodexClient : ICodexSdkClient
    {
        private readonly IReadOnlyList<CodexTurnEventEnvelope> _events;

        public MinimalCodexClient(IReadOnlyList<CodexTurnEventEnvelope> events) => _events = events;

        public string? CurrentCodexThreadId { get; private set; }

        public string? CurrentTurnId { get; private set; }

        public bool IsRunning { get; private set; }

        public string DependencyState => "ready";

        public void ConfigureDynamicToolExecutor(
            Func<CodexDynamicToolCallRequest, CancellationToken, Task<CodexDynamicToolCallResponse>>? executor)
        {
        }

        public Task StartOrResumeThreadAsync(CodexBridgeInitOptions options, CancellationToken ct = default)
        {
            IsRunning = true;
            CurrentCodexThreadId = options.ThreadId;
            return Task.CompletedTask;
        }

        public Task EnsureStartedAsync(CodexBridgeInitOptions options, CancellationToken ct = default)
            => StartOrResumeThreadAsync(options, ct);

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

        public Task InterruptTurnAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MinimalCopilotClient : ICopilotSdkClient
    {
        private readonly string _sessionId;
        private readonly IReadOnlyList<CopilotTurnEventEnvelope> _events;

        public MinimalCopilotClient(string sessionId, IReadOnlyList<CopilotTurnEventEnvelope> events)
        {
            _sessionId = sessionId;
            _events = events;
        }

        public string? CurrentCopilotSessionId { get; private set; }

        public bool IsRunning { get; private set; }

        public string DependencyState => "ready";

        public void ConfigureDynamicToolExecutor(
            Func<CopilotDynamicToolCallRequest, CancellationToken, Task<CopilotDynamicToolCallResponse>>? executor)
        {
        }

        public Task StartOrResumeSessionAsync(CopilotBridgeInitOptions options, CancellationToken ct = default)
        {
            IsRunning = true;
            CurrentCopilotSessionId = _sessionId;
            return Task.CompletedTask;
        }

        public Task EnsureStartedAsync(CopilotBridgeInitOptions options, CancellationToken ct = default)
            => StartOrResumeSessionAsync(options, ct);

        public async IAsyncEnumerable<CopilotTurnEventEnvelope> RunStreamingAsync(
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

        public Task InterruptTurnAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task ShutdownAsync(TimeSpan? timeout = null, CancellationToken ct = default)
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
