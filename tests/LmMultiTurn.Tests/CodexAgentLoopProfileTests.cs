using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Agents;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.AgentRuntime;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace LmMultiTurn.Tests;

public class CodexAgentLoopProfileTests : LoggingTestBase
{
    public CodexAgentLoopProfileTests(ITestOutputHelper output) : base(output) { }

    [Fact]
    public async Task Profile_SystemPrompt_OverridesDeveloperInstructions()
    {
        var fakeClient = new RecordingCodexClient(SuccessfulTurnEvents());

        await using var loop = new CodexAgentLoop(
            new CodexSdkOptions
            {
                DeveloperInstructions = "host-level",
                Profile = new AgentRuntimeProfile { SystemPrompt = "profile-level" },
            },
            new Dictionary<string, CodexMcpServerConfig>(),
            functionRegistry: null,
            enabledTools: null,
            threadId: "thread-profile-1",
            clientFactory: (_, _) => fakeClient,
            logger: LoggerFactory.CreateLogger<CodexAgentLoop>());

        await RunOneTurnAsync(loop);

        fakeClient.LastStartOptions.Should().NotBeNull();
        fakeClient.LastStartOptions!.DeveloperInstructions.Should().Be("profile-level");
    }

    [Fact]
    public async Task Profile_McpServers_MergeWithHostMcp_ProfileWinsOnCollision()
    {
        var fakeClient = new RecordingCodexClient(SuccessfulTurnEvents());

        var hostMcp = new Dictionary<string, CodexMcpServerConfig>
        {
            ["alpha"] = new() { Command = "host-alpha" },
            ["shared"] = new() { Command = "host-shared" },
        };

        var profileMcp = new Dictionary<string, McpServerConfig>
        {
            ["beta"] = McpServerConfig.CreateStdio("profile-beta", ["x"]),
            ["shared"] = McpServerConfig.CreateStdio("profile-shared", ["y"]),
        };

        await using var loop = new CodexAgentLoop(
            new CodexSdkOptions
            {
                Profile = new AgentRuntimeProfile { McpServers = profileMcp },
            },
            hostMcp,
            functionRegistry: null,
            enabledTools: null,
            threadId: "thread-profile-2",
            clientFactory: (_, _) => fakeClient,
            logger: LoggerFactory.CreateLogger<CodexAgentLoop>());

        await RunOneTurnAsync(loop);

        var sent = fakeClient.LastStartOptions!.McpServers!;
        sent.Should().ContainKey("alpha").WhoseValue.Command.Should().Be("host-alpha");
        sent.Should().ContainKey("beta").WhoseValue.Command.Should().Be("profile-beta");
        sent.Should().ContainKey("shared").WhoseValue.Command.Should().Be("profile-shared");
    }

    [Fact]
    public async Task Profile_ProfileSystemPrompt_OverridesConstructorSystemPrompt()
    {
        var fakeClient = new RecordingCodexClient(SuccessfulTurnEvents());

        await using var loop = new CodexAgentLoop(
            new CodexSdkOptions
            {
                Profile = new AgentRuntimeProfile { SystemPrompt = "profile-wins" },
            },
            new Dictionary<string, CodexMcpServerConfig>(),
            functionRegistry: null,
            enabledTools: null,
            threadId: "thread-profile-precedence-1",
            systemPrompt: "ctor-prompt",
            clientFactory: (_, _) => fakeClient,
            logger: LoggerFactory.CreateLogger<CodexAgentLoop>());

        await RunOneTurnAsync(loop);

        fakeClient.LastStartOptions!.DeveloperInstructions.Should().Be("profile-wins");
    }

    [Fact]
    public async Task NoProfile_ConstructorSystemPrompt_WinsOverDeveloperInstructions()
    {
        var fakeClient = new RecordingCodexClient(SuccessfulTurnEvents());

        await using var loop = new CodexAgentLoop(
            new CodexSdkOptions { DeveloperInstructions = "host-level" },
            new Dictionary<string, CodexMcpServerConfig>(),
            functionRegistry: null,
            enabledTools: null,
            threadId: "thread-profile-precedence-2",
            systemPrompt: "ctor-prompt",
            clientFactory: (_, _) => fakeClient,
            logger: LoggerFactory.CreateLogger<CodexAgentLoop>());

        await RunOneTurnAsync(loop);

        fakeClient.LastStartOptions!.DeveloperInstructions.Should().Be("ctor-prompt");
    }

    [Fact]
    public async Task Profile_WithSkillsOrSubAgents_WarningLoggedOnce_AcrossMultipleTurns()
    {
        var fakeClient = new RecordingCodexClient([.. SuccessfulTurnEvents(), .. SuccessfulTurnEvents()]);
        var capture = new CapturingLogger<CodexAgentLoop>();

        await using var loop = new CodexAgentLoop(
            new CodexSdkOptions
            {
                Profile = new AgentRuntimeProfile
                {
                    Skills = [AgentSkill.Inline("s", "body")],
                },
            },
            new Dictionary<string, CodexMcpServerConfig>(),
            functionRegistry: null,
            enabledTools: null,
            threadId: "thread-codex-warning-once",
            clientFactory: (_, _) => fakeClient,
            logger: capture);

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);
        var input = new UserInput([new TextMessage { Role = Role.User, Text = "hi" }]);
        await foreach (var _ in loop.ExecuteRunAsync(input, cts.Token)) { }
        await foreach (var _ in loop.ExecuteRunAsync(input, cts.Token)) { }
        await cts.CancelAsync();

        capture.WarningCount("codex.profile.unsupported").Should().Be(1);
    }

    [Fact]
    public async Task Profile_WithSkillsOrSubAgents_DoesNotAlterMcp_ButRuns()
    {
        var fakeClient = new RecordingCodexClient(SuccessfulTurnEvents());

        await using var loop = new CodexAgentLoop(
            new CodexSdkOptions
            {
                Profile = new AgentRuntimeProfile
                {
                    Skills = [AgentSkill.Inline("s", "body")],
                    SubAgents = [SubAgentDefinition.Inline("a", "body")],
                },
            },
            new Dictionary<string, CodexMcpServerConfig>(),
            functionRegistry: null,
            enabledTools: null,
            threadId: "thread-profile-3",
            clientFactory: (_, _) => fakeClient,
            logger: LoggerFactory.CreateLogger<CodexAgentLoop>());

        await RunOneTurnAsync(loop);

        fakeClient.LastStartOptions.Should().NotBeNull();
        fakeClient.LastStartOptions!.McpServers.Should().BeEmpty();
    }

    private static async Task RunOneTurnAsync(CodexAgentLoop loop)
    {
        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);
        var input = new UserInput([new TextMessage { Role = Role.User, Text = "hi" }]);
        await foreach (var _ in loop.ExecuteRunAsync(input, cts.Token))
        {
        }
        await cts.CancelAsync();
    }

    private static IReadOnlyList<CodexTurnEventEnvelope> SuccessfulTurnEvents()
    {
        return
        [
            Event("thread.started", """{"type":"thread.started","thread_id":"t1"}"""),
            Event("turn.completed", """{"type":"turn.completed","usage":{"input_tokens":1,"cached_input_tokens":0,"output_tokens":1}}"""),
        ];
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

    private sealed class RecordingCodexClient : ICodexSdkClient
    {
        private readonly IReadOnlyList<CodexTurnEventEnvelope> _events;

        public RecordingCodexClient(IReadOnlyList<CodexTurnEventEnvelope> events)
        {
            _events = events;
        }

        public CodexBridgeInitOptions? LastStartOptions { get; private set; }

        public bool IsRunning { get; private set; }

        public string? CurrentCodexThreadId { get; private set; }

        public string? CurrentTurnId => null;

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
}
