using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Agents;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Models;
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

public class CodexAgentLoopTests : LoggingTestBase
{
    public CodexAgentLoopTests(ITestOutputHelper output) : base(output) { }
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
            functionRegistry: null,
            enabledTools: null,
            threadId: "thread-1",
            clientFactory: (_, _) => fakeClient,
            logger: LoggerFactory.CreateLogger<CodexAgentLoop>());

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
    public async Task ExecuteRunAsync_MapsInternalWebSearchItems_ToToolMessages_ByDefault()
    {
        var fakeClient = new FakeCodexClient(
        [
            Event("thread.started", """{"type":"thread.started","thread_id":"thread_codex_internal_1"}"""),
            Event("item.started", """
                {
                  "type":"item.started",
                  "item":{"id":"ws_1","type":"webSearch","query":"latest dotnet sdk","action":"search","status":"inProgress"}
                }
                """),
            Event("item.completed", """
                {
                  "type":"item.completed",
                  "item":{"id":"ws_1","type":"webSearch","query":"latest dotnet sdk","action":"search","status":"completed","results":[{"title":"Docs"}]}
                }
                """),
            Event("turn.completed", """
                {
                  "type":"turn.completed",
                  "usage":{"input_tokens":9,"cached_input_tokens":0,"output_tokens":3}
                }
                """),
        ]);

        await using var loop = new CodexAgentLoop(
            new CodexSdkOptions(),
            new Dictionary<string, CodexMcpServerConfig>(),
            functionRegistry: null,
            enabledTools: null,
            threadId: "thread-internal-1",
            clientFactory: (_, _) => fakeClient,
            logger: LoggerFactory.CreateLogger<CodexAgentLoop>());

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var input = new UserInput([
            new TextMessage { Role = Role.User, Text = "search for latest dotnet sdk" },
        ]);

        var messages = new List<IMessage>();
        await foreach (var msg in loop.ExecuteRunAsync(input, cts.Token))
        {
            messages.Add(msg);
        }

        messages.OfType<ToolCallMessage>().Should().ContainSingle(m =>
            m.ToolCallId == "ws_1"
            && m.FunctionName == "web_search"
            && m.ExecutionTarget == ExecutionTarget.ProviderServer
            && m.FunctionArgs != null
            && m.FunctionArgs.Contains("\"query\":\"latest dotnet sdk\"", StringComparison.Ordinal));
        messages.OfType<ToolCallResultMessage>().Should().ContainSingle(m =>
            m.ToolCallId == "ws_1"
            && m.ToolName == "web_search"
            && !m.IsError
            && m.Result.Contains("\"status\":\"success\"", StringComparison.Ordinal));
        messages.OfType<ReasoningMessage>()
            .Should()
            .NotContain(m => m.Reasoning != null && m.Reasoning.Contains("Web search completed", StringComparison.Ordinal));

        await cts.CancelAsync();
    }

    [Fact]
    public async Task ExecuteRunAsync_UsesLegacyReasoningSummary_WhenInternalToolExposureDisabled()
    {
        var fakeClient = new FakeCodexClient(
        [
            Event("thread.started", """{"type":"thread.started","thread_id":"thread_codex_internal_2"}"""),
            Event("item.completed", """
                {
                  "type":"item.completed",
                  "item":{"id":"ws_2","type":"webSearch","query":"weather in seattle","status":"completed"}
                }
                """),
            Event("turn.completed", """
                {
                  "type":"turn.completed",
                  "usage":{"input_tokens":6,"cached_input_tokens":0,"output_tokens":2}
                }
                """),
        ]);

        await using var loop = new CodexAgentLoop(
            new CodexSdkOptions
            {
                ExposeCodexInternalToolsAsToolMessages = false,
            },
            new Dictionary<string, CodexMcpServerConfig>(),
            functionRegistry: null,
            enabledTools: null,
            threadId: "thread-internal-2",
            clientFactory: (_, _) => fakeClient,
            logger: LoggerFactory.CreateLogger<CodexAgentLoop>());

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var input = new UserInput([
            new TextMessage { Role = Role.User, Text = "weather in seattle" },
        ]);

        var messages = new List<IMessage>();
        await foreach (var msg in loop.ExecuteRunAsync(input, cts.Token))
        {
            messages.Add(msg);
        }

        messages.OfType<ToolCallMessage>().Should().BeEmpty();
        messages.OfType<ToolCallResultMessage>().Should().BeEmpty();
        messages.OfType<ReasoningMessage>().Should().ContainSingle(m =>
            m.Reasoning != null && m.Reasoning.Contains("Web search completed: weather in seattle", StringComparison.Ordinal));

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
            functionRegistry: null,
            enabledTools: null,
            threadId: "thread-2",
            clientFactory: (_, _) => fakeClient,
            logger: LoggerFactory.CreateLogger<CodexAgentLoop>());

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
            functionRegistry: null,
            enabledTools: null,
            threadId: "thread-3",
            clientFactory: (_, _) => fakeClient,
            logger: LoggerFactory.CreateLogger<CodexAgentLoop>());

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

    [Fact]
    public async Task ExecuteRunAsync_UsesDeveloperInstructions_AndDoesNotPrependSystemPromptToPromptInput()
    {
        var fakeClient = new FakeCodexClient(
        [
            Event("thread.started", """{"type":"thread.started","thread_id":"thread_codex_4"}"""),
            Event("item.completed", """
                {
                  "type":"item.completed",
                  "item":{"id":"msg_1","type":"agent_message","text":"Ack"}
                }
                """),
            Event("turn.completed", """
                {
                  "type":"turn.completed",
                  "usage":{"input_tokens":3,"cached_input_tokens":0,"output_tokens":1}
                }
                """),
        ]);

        await using var loop = new CodexAgentLoop(
            new CodexSdkOptions(),
            new Dictionary<string, CodexMcpServerConfig>(),
            functionRegistry: null,
            enabledTools: null,
            threadId: "thread-4",
            systemPrompt: "System instructions should go to developerInstructions",
            clientFactory: (_, _) => fakeClient,
            logger: LoggerFactory.CreateLogger<CodexAgentLoop>());

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var input = new UserInput([
            new TextMessage { Role = Role.User, Text = "user question" },
        ]);

        await foreach (var _ in loop.ExecuteRunAsync(input, cts.Token))
        {
        }

        fakeClient.LastStartOptions.Should().NotBeNull();
        fakeClient.LastStartOptions!.DeveloperInstructions.Should().Be("System instructions should go to developerInstructions");
        fakeClient.LastRunInput.Should().Be("user question");

        await cts.CancelAsync();
    }

    [Fact]
    public async Task ExecuteRunAsync_UsesModelInstructionsFile_WhenSystemPromptIsLarge()
    {
        var fakeClient = new FakeCodexClient(
        [
            Event("thread.started", """{"type":"thread.started","thread_id":"thread_codex_5"}"""),
            Event("turn.completed", """
                {
                  "type":"turn.completed",
                  "usage":{"input_tokens":1,"cached_input_tokens":0,"output_tokens":1}
                }
                """),
        ]);

        string? generatedPath;
        await using (var loop = new CodexAgentLoop(
                         new CodexSdkOptions
                         {
                             UseModelInstructionsFileThresholdChars = 10,
                         },
                         new Dictionary<string, CodexMcpServerConfig>(),
                         functionRegistry: null,
                         enabledTools: null,
                         threadId: "thread-5",
                         systemPrompt: "this is a long system prompt exceeding threshold",
                         clientFactory: (_, _) => fakeClient,
                         logger: LoggerFactory.CreateLogger<CodexAgentLoop>()))
        {
            using var cts = new CancellationTokenSource();
            _ = loop.RunAsync(cts.Token);

            var input = new UserInput([
                new TextMessage { Role = Role.User, Text = "hello" },
            ]);

            await foreach (var _ in loop.ExecuteRunAsync(input, cts.Token))
            {
            }

            fakeClient.LastStartOptions.Should().NotBeNull();
            fakeClient.LastStartOptions!.DeveloperInstructions.Should().BeNull();
            fakeClient.LastStartOptions.ModelInstructionsFile.Should().NotBeNullOrWhiteSpace();
            generatedPath = fakeClient.LastStartOptions.ModelInstructionsFile;
            File.Exists(generatedPath!).Should().BeTrue();

            await cts.CancelAsync();
        }

        generatedPath.Should().NotBeNullOrWhiteSpace();
        File.Exists(generatedPath!).Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteRunAsync_MapsAppServerDeltaNotifications_ToIncrementalMessages()
    {
        var fakeClient = new FakeCodexClient(
        [
            Event("thread/started", """
                {
                  "type":"thread/started",
                  "thread":{"id":"thread_app_1"}
                }
                """),
            Event("item/agentMessage/delta", """
                {
                  "type":"item/agentMessage/delta",
                  "itemId":"msg_1",
                  "delta":"Hel"
                }
                """),
            Event("item/agentMessage/delta", """
                {
                  "type":"item/agentMessage/delta",
                  "itemId":"msg_1",
                  "delta":"lo"
                }
                """),
            Event("item/reasoning/summaryTextDelta", """
                {
                  "type":"item/reasoning/summaryTextDelta",
                  "itemId":"reason_1",
                  "delta":"Thinking..."
                }
                """),
            Event("item/completed", """
                {
                  "type":"item/completed",
                  "item":{"id":"reason_1","type":"reasoning","summary":["Thinking complete"],"content":[]}
                }
                """),
            Event("item/completed", """
                {
                  "type":"item/completed",
                  "item":{"id":"msg_1","type":"agentMessage","text":"Hello"}
                }
                """),
            Event("thread/tokenUsage/updated", """
                {
                  "type":"thread/tokenUsage/updated",
                  "threadId":"thread_app_1",
                  "turnId":"turn_1",
                  "tokenUsage":{"last":{"inputTokens":4,"cachedInputTokens":1,"outputTokens":2,"reasoningOutputTokens":1}}
                }
                """),
            Event("turn/completed", """
                {
                  "type":"turn/completed",
                  "threadId":"thread_app_1",
                  "turn":{"id":"turn_1","status":"completed"}
                }
                """),
        ]);

        await using var loop = new CodexAgentLoop(
            new CodexSdkOptions(),
            new Dictionary<string, CodexMcpServerConfig>(),
            functionRegistry: null,
            enabledTools: null,
            threadId: "thread-6",
            clientFactory: (_, _) => fakeClient,
            logger: LoggerFactory.CreateLogger<CodexAgentLoop>());

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

        messages.OfType<TextUpdateMessage>().Select(m => m.Text).Should().ContainInOrder("Hel", "lo");
        messages.OfType<ReasoningUpdateMessage>().Select(m => m.Reasoning).Should().Contain("Thinking...");
        messages.OfType<TextMessage>().Should().ContainSingle(m => m.Text == "Hello");
        messages.OfType<ReasoningMessage>().Should().ContainSingle(m => m.Reasoning == "Thinking complete");
        messages.OfType<UsageMessage>().Should().ContainSingle(m =>
            m.Usage.PromptTokens == 4
            && m.Usage.CompletionTokens == 2
            && m.Usage.InputTokenDetails!.CachedTokens == 1
            && m.Usage.OutputTokenDetails!.ReasoningTokens == 1);

        await cts.CancelAsync();
    }

    [Fact]
    public async Task ExecuteRunAsync_PersistsLatestRunId_AndCodexThreadId()
    {
        var fakeClient = new FakeCodexClient(
        [
            Event("thread.started", """{"type":"thread.started","thread_id":"codex_thread_persist_1"}"""),
            Event("turn.completed", """
                {
                  "type":"turn.completed",
                  "usage":{"input_tokens":2,"cached_input_tokens":0,"output_tokens":1}
                }
                """),
        ]);

        var store = new InMemoryConversationStore();
        await using var loop = new CodexAgentLoop(
            new CodexSdkOptions(),
            new Dictionary<string, CodexMcpServerConfig>(),
            functionRegistry: null,
            enabledTools: null,
            threadId: "thread-persist-1",
            store: store,
            clientFactory: (_, _) => fakeClient,
            logger: LoggerFactory.CreateLogger<CodexAgentLoop>());

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

        var runCompleted = messages.OfType<RunCompletedMessage>().Should().ContainSingle().Subject;
        var metadata = await store.LoadMetadataAsync("thread-persist-1", CancellationToken.None);
        metadata.Should().NotBeNull();
        metadata!.CurrentRunId.Should().BeNull();
        metadata.LatestRunId.Should().Be(runCompleted.CompletedRunId);
        metadata.Properties.Should().NotBeNull();
        metadata.Properties!.TryGetValue("codex_thread_id", out var codexThreadId).Should().BeTrue();
        codexThreadId?.ToString().Should().Be("codex_thread_persist_1");

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

        public CodexBridgeInitOptions? LastStartOptions { get; private set; }

        public string? LastRunInput { get; private set; }

        public bool IsRunning { get; private set; }

        public string? CurrentCodexThreadId { get; private set; }

        public string? CurrentTurnId { get; private set; }

        public string DependencyState => "ready";

        public void ConfigureDynamicToolExecutor(
            Func<CodexDynamicToolCallRequest, CancellationToken, Task<CodexDynamicToolCallResponse>>? executor)
        {
        }

        public Task StartOrResumeThreadAsync(CodexBridgeInitOptions options, CancellationToken ct = default)
        {
            IsRunning = true;
            CurrentCodexThreadId = options.ThreadId;
            LastStartOptions = options;
            return Task.CompletedTask;
        }

        public Task EnsureStartedAsync(CodexBridgeInitOptions options, CancellationToken ct = default)
            => StartOrResumeThreadAsync(options, ct);

        public async IAsyncEnumerable<CodexTurnEventEnvelope> RunStreamingAsync(
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

        public Task ShutdownAsync(TimeSpan? timeout = null, CancellationToken ct = default)
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public Task InterruptTurnAsync(CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
