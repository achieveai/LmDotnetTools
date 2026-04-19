using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Agents;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace LmMultiTurn.Tests;

public class CopilotAgentLoopTests : LoggingTestBase
{
    public CopilotAgentLoopTests(ITestOutputHelper output) : base(output)
    {
    }

    /// <summary>
    /// Emits an incremental text chunk, a thought chunk, a tool_call + tool_call_update,
    /// then the synthetic session/prompt/completed terminator. Ensures the event
    /// translator produces TextUpdate -> TextMessage, reasoning deltas, and tool
    /// messages in the expected order.
    /// </summary>
    [Fact]
    public async Task ExecuteRunAsync_MapsAcpSessionUpdates_ToMessages()
    {
        var fakeClient = new FakeCopilotClient(
            sessionId: "sess_copilot_1",
            events:
            [
                SessionUpdate("sess_copilot_1", """
                    {"sessionUpdate":"agent_thought_chunk","content":{"type":"text","text":"Thinking..."}}
                    """),
                SessionUpdate("sess_copilot_1", """
                    {"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Hel"}}
                    """),
                SessionUpdate("sess_copilot_1", """
                    {"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"lo"}}
                    """),
                SessionUpdate("sess_copilot_1", """
                    {"sessionUpdate":"tool_call","toolCallId":"tool_1","title":"calculate","rawInput":{"a":2,"b":5}}
                    """),
                SessionUpdate("sess_copilot_1", """
                    {"sessionUpdate":"tool_call_update","toolCallId":"tool_1","status":"completed","content":[{"type":"text","text":"10"}]}
                    """),
                PromptCompleted("""{"usage":{"inputTokens":4,"outputTokens":2,"cachedInputTokens":1}}"""),
            ]);

        await using var loop = new CopilotAgentLoop(
            new CopilotSdkOptions(),
            threadId: "thread-copilot-1",
            clientFactory: (_, _) => fakeClient,
            logger: LoggerFactory.CreateLogger<CopilotAgentLoop>());

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var input = new UserInput([
            new TextMessage { Role = Role.User, Text = "say hello" },
        ]);

        var messages = new List<IMessage>();
        await foreach (var msg in loop.ExecuteRunAsync(input, cts.Token))
        {
            messages.Add(msg);
        }

        messages.OfType<RunAssignmentMessage>().Should().ContainSingle();

        messages.OfType<TextUpdateMessage>().Select(m => m.Text).Should().ContainInOrder("Hel", "lo");
        messages.OfType<TextMessage>().Should().ContainSingle(m => m.Text == "Hello");

        messages.OfType<ReasoningUpdateMessage>().Should().ContainSingle(m =>
            m.Reasoning == "Thinking..."
            && m.Visibility == ReasoningVisibility.Summary);
        messages.OfType<ReasoningMessage>().Should().ContainSingle(m => m.Reasoning == "Thinking...");

        messages.OfType<ToolCallMessage>().Should().ContainSingle(m =>
            m.ToolCallId == "tool_1"
            && m.FunctionName == "calculate"
            && m.ExecutionTarget == ExecutionTarget.ProviderServer);
        messages.OfType<ToolCallResultMessage>().Should().ContainSingle(m =>
            m.ToolCallId == "tool_1"
            && !m.IsError
            && m.Result.Contains("10", StringComparison.Ordinal));

        messages.OfType<UsageMessage>().Should().ContainSingle(m =>
            m.Usage.PromptTokens == 4
            && m.Usage.CompletionTokens == 2
            && m.Usage.InputTokenDetails!.CachedTokens == 1);

        messages.OfType<RunCompletedMessage>().Should().ContainSingle(m => !m.IsError);

        messages
            .Where(m => m is TextMessage or TextUpdateMessage or ReasoningMessage or ReasoningUpdateMessage or ToolCallMessage or ToolCallResultMessage or UsageMessage)
            .Should()
            .OnlyContain(m => m.MessageOrderIdx.HasValue);

        await cts.CancelAsync();
    }

    /// <summary>
    /// tool_call_update with status=failed must surface as an errored ToolCallResultMessage
    /// carrying the copilot_tool_failed error code.
    /// </summary>
    [Fact]
    public async Task ExecuteRunAsync_ToolCallFailure_ProducesErrorResult()
    {
        var fakeClient = new FakeCopilotClient(
            sessionId: "sess_tool_fail",
            events:
            [
                SessionUpdate("sess_tool_fail", """
                    {"sessionUpdate":"tool_call","toolCallId":"t1","title":"broken","rawInput":{}}
                    """),
                SessionUpdate("sess_tool_fail", """
                    {"sessionUpdate":"tool_call_update","toolCallId":"t1","status":"failed","content":[{"type":"text","text":"kaboom"}]}
                    """),
                PromptCompleted("""{"usage":{"inputTokens":1,"outputTokens":0}}"""),
            ]);

        await using var loop = new CopilotAgentLoop(
            new CopilotSdkOptions(),
            threadId: "thread-copilot-tool-fail",
            clientFactory: (_, _) => fakeClient,
            logger: LoggerFactory.CreateLogger<CopilotAgentLoop>());

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var input = new UserInput([
            new TextMessage { Role = Role.User, Text = "break something" },
        ]);

        var messages = new List<IMessage>();
        await foreach (var msg in loop.ExecuteRunAsync(input, cts.Token))
        {
            messages.Add(msg);
        }

        messages.OfType<ToolCallResultMessage>().Should().ContainSingle(m =>
            m.ToolCallId == "t1"
            && m.IsError
            && m.ErrorCode == "copilot_tool_failed");
        messages.OfType<RunCompletedMessage>().Should().ContainSingle();

        await cts.CancelAsync();
    }

    /// <summary>
    /// Verifies that system prompt is forwarded as developerInstructions on the
    /// bridge init options, and the raw user prompt is sent via session/prompt.
    /// </summary>
    [Fact]
    public async Task ExecuteRunAsync_UsesDeveloperInstructions_AndSendsRawPromptToCopilot()
    {
        var fakeClient = new FakeCopilotClient(
            sessionId: "sess_prompt_check",
            events: [PromptCompleted("""{"usage":{"inputTokens":1,"outputTokens":1}}""")]);

        await using var loop = new CopilotAgentLoop(
            new CopilotSdkOptions(),
            threadId: "thread-copilot-prompt",
            systemPrompt: "developer prompt goes here",
            clientFactory: (_, _) => fakeClient,
            logger: LoggerFactory.CreateLogger<CopilotAgentLoop>());

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var input = new UserInput([
            new TextMessage { Role = Role.User, Text = "user question" },
        ]);

        await foreach (var _ in loop.ExecuteRunAsync(input, cts.Token))
        {
        }

        fakeClient.LastStartOptions.Should().NotBeNull();
        fakeClient.LastStartOptions!.DeveloperInstructions.Should().Be("developer prompt goes here");
        fakeClient.LastRunInput.Should().Be("user question");

        await cts.CancelAsync();
    }

    /// <summary>
    /// When the underlying client extracts a Copilot session id, it must be persisted
    /// under the copilot_session_id property on the thread metadata.
    /// </summary>
    [Fact]
    public async Task ExecuteRunAsync_PersistsCopilotSessionId_ToThreadMetadata()
    {
        var fakeClient = new FakeCopilotClient(
            sessionId: "sess_persist_1",
            events:
            [
                SessionUpdate("sess_persist_1", """
                    {"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Ack"}}
                    """),
                PromptCompleted("""{"usage":{"inputTokens":1,"outputTokens":1}}"""),
            ]);

        var store = new InMemoryConversationStore();
        await using var loop = new CopilotAgentLoop(
            new CopilotSdkOptions(),
            threadId: "thread-copilot-persist-1",
            store: store,
            clientFactory: (_, _) => fakeClient,
            logger: LoggerFactory.CreateLogger<CopilotAgentLoop>());

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var input = new UserInput([
            new TextMessage { Role = Role.User, Text = "hello" },
        ]);

        var messages = new List<IMessage>();
        await foreach (var msg in loop.ExecuteRunAsync(input, cts.Token))
        {
            messages.Add(msg);
        }

        // Allow async metadata persistence to complete after RunCompletedMessage
        await Task.Delay(500);

        messages.OfType<RunCompletedMessage>().Should().ContainSingle(m => !m.IsError);
        var metadata = await store.LoadMetadataAsync("thread-copilot-persist-1", CancellationToken.None);
        metadata.Should().NotBeNull();
        metadata!.Properties.Should().NotBeNull();
        metadata.Properties!.TryGetValue("copilot_session_id", out var copilotSessionId).Should().BeTrue();
        copilotSessionId?.ToString().Should().Be("sess_persist_1");

        await cts.CancelAsync();
    }

    // --- Helpers ---

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

    private sealed class FakeCopilotClient : ICopilotSdkClient
    {
        private readonly string _sessionId;
        private readonly IReadOnlyList<CopilotTurnEventEnvelope> _events;

        public FakeCopilotClient(string sessionId, IReadOnlyList<CopilotTurnEventEnvelope> events)
        {
            _sessionId = sessionId;
            _events = events;
        }

        public CopilotBridgeInitOptions? LastStartOptions { get; private set; }

        public string? LastRunInput { get; private set; }

        public bool IsRunning { get; private set; }

        public string? CurrentCopilotSessionId { get; private set; }

        public string DependencyState => "ready";

        public void ConfigureDynamicToolExecutor(
            Func<CopilotDynamicToolCallRequest, CancellationToken, Task<CopilotDynamicToolCallResponse>>? executor)
        {
        }

        public Task StartOrResumeSessionAsync(CopilotBridgeInitOptions options, CancellationToken ct = default)
        {
            IsRunning = true;
            LastStartOptions = options;
            CurrentCopilotSessionId = _sessionId;
            return Task.CompletedTask;
        }

        public Task EnsureStartedAsync(CopilotBridgeInitOptions options, CancellationToken ct = default)
            => StartOrResumeSessionAsync(options, ct);

        public async IAsyncEnumerable<CopilotTurnEventEnvelope> RunStreamingAsync(
            string input,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            LastRunInput = input;
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
