using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Agents;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Models;
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

public class CopilotAgentLoopProfileTests : LoggingTestBase
{
    public CopilotAgentLoopProfileTests(ITestOutputHelper output) : base(output) { }

    [Fact]
    public async Task Profile_SystemPrompt_OverridesDeveloperInstructions()
    {
        var fakeClient = new RecordingCopilotClient([PromptCompleted()]);

        await using var loop = new CopilotAgentLoop(
            new CopilotSdkOptions
            {
                DeveloperInstructions = "host-level",
                Profile = new AgentRuntimeProfile { SystemPrompt = "profile-level" },
            },
            threadId: "thread-copilot-profile-1",
            clientFactory: (_, _) => fakeClient,
            logger: LoggerFactory.CreateLogger<CopilotAgentLoop>());

        await RunOneTurnAsync(loop);

        fakeClient.LastStartOptions.Should().NotBeNull();
        fakeClient.LastStartOptions!.DeveloperInstructions.Should().Be("profile-level");
    }

    [Fact]
    public async Task Profile_ProfileSystemPrompt_OverridesConstructorSystemPrompt()
    {
        var fakeClient = new RecordingCopilotClient([PromptCompleted()]);

        await using var loop = new CopilotAgentLoop(
            new CopilotSdkOptions
            {
                Profile = new AgentRuntimeProfile { SystemPrompt = "profile-wins" },
            },
            threadId: "thread-copilot-profile-2",
            systemPrompt: "ctor-prompt",
            clientFactory: (_, _) => fakeClient,
            logger: LoggerFactory.CreateLogger<CopilotAgentLoop>());

        await RunOneTurnAsync(loop);

        fakeClient.LastStartOptions!.DeveloperInstructions.Should().Be("profile-wins");
    }

    [Fact]
    public async Task Profile_WithUnsupportedInputs_WarningLoggedOnce_AcrossMultipleTurns()
    {
        var fakeClient = new RecordingCopilotClient([PromptCompleted(), PromptCompleted()]);
        var capture = new CapturingLogger<CopilotAgentLoop>();

        await using var loop = new CopilotAgentLoop(
            new CopilotSdkOptions
            {
                Profile = new AgentRuntimeProfile
                {
                    McpServers = new Dictionary<string, McpServerConfig>
                    {
                        ["m"] = McpServerConfig.CreateStdio("node", ["a.js"]),
                    },
                },
            },
            threadId: "thread-copilot-warning-once",
            clientFactory: (_, _) => fakeClient,
            logger: capture);

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);
        var input = new UserInput([new TextMessage { Role = Role.User, Text = "hi" }]);
        await foreach (var _ in loop.ExecuteRunAsync(input, cts.Token)) { }
        await foreach (var _ in loop.ExecuteRunAsync(input, cts.Token)) { }
        await cts.CancelAsync();

        capture.WarningCount("copilot.profile.unsupported").Should().Be(1);
    }

    [Fact]
    public async Task Profile_WithSkillsAndSubAgents_TriggersWarningOnce()
    {
        var fakeClient = new RecordingCopilotClient([PromptCompleted(), PromptCompleted()]);
        var capture = new CapturingLogger<CopilotAgentLoop>();

        await using var loop = new CopilotAgentLoop(
            new CopilotSdkOptions
            {
                Profile = new AgentRuntimeProfile
                {
                    Skills = [AgentSkill.Inline("s", "body")],
                    SubAgents = [SubAgentDefinition.Inline("a", "body")],
                },
            },
            threadId: "thread-copilot-warning-skills-subagents",
            clientFactory: (_, _) => fakeClient,
            logger: capture);

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);
        var input = new UserInput([new TextMessage { Role = Role.User, Text = "hi" }]);
        await foreach (var _ in loop.ExecuteRunAsync(input, cts.Token)) { }
        await foreach (var _ in loop.ExecuteRunAsync(input, cts.Token)) { }
        await cts.CancelAsync();

        capture.WarningCount("copilot.profile.unsupported").Should().Be(1);
    }

    [Fact]
    public async Task Profile_WithUnsupportedInputs_DoesNotCrash()
    {
        var fakeClient = new RecordingCopilotClient([PromptCompleted()]);

        await using var loop = new CopilotAgentLoop(
            new CopilotSdkOptions
            {
                Profile = new AgentRuntimeProfile
                {
                    Skills = [AgentSkill.Inline("s", "body")],
                    SubAgents = [SubAgentDefinition.Inline("a", "body")],
                    McpServers = new Dictionary<string, McpServerConfig>
                    {
                        ["ignored"] = McpServerConfig.CreateStdio("node", ["a.js"]),
                    },
                },
            },
            threadId: "thread-copilot-profile-3",
            clientFactory: (_, _) => fakeClient,
            logger: LoggerFactory.CreateLogger<CopilotAgentLoop>());

        await RunOneTurnAsync(loop);

        fakeClient.LastStartOptions.Should().NotBeNull();
    }

    private static async Task RunOneTurnAsync(CopilotAgentLoop loop)
    {
        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);
        var input = new UserInput([new TextMessage { Role = Role.User, Text = "hi" }]);
        await foreach (var _ in loop.ExecuteRunAsync(input, cts.Token))
        {
        }
        await cts.CancelAsync();
    }

    private static CopilotTurnEventEnvelope PromptCompleted()
    {
        const string json = """{"type":"session/prompt/completed","usage":{"inputTokens":1,"outputTokens":1}}""";
        var element = JsonDocument.Parse(json).RootElement.Clone();
        return new CopilotTurnEventEnvelope
        {
            Type = "event",
            Event = element,
            RequestId = Guid.NewGuid().ToString("N"),
            SessionId = null,
        };
    }

    private sealed class RecordingCopilotClient : ICopilotSdkClient
    {
        private readonly IReadOnlyList<CopilotTurnEventEnvelope> _events;

        public RecordingCopilotClient(IReadOnlyList<CopilotTurnEventEnvelope> events)
        {
            _events = events;
        }

        public CopilotBridgeInitOptions? LastStartOptions { get; private set; }

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
            CurrentCopilotSessionId = "session-mock";
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
