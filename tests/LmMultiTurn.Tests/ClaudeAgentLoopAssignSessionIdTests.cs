using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Agents;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Validates the host-chosen <c>AssignSessionId</c> path added by issue #55:
/// the CLI is invoked with <c>--session-id &lt;guid&gt;</c> on the very first run
/// (creating a new on-disk session under the caller's id), and subsequent runs
/// flip to <c>--resume &lt;id&gt;</c> once the SDK reports it back via
/// <see cref="SystemInitMessage"/>. Mutually exclusive with the
/// <c>initialSessionId</c> constructor parameter.
/// </summary>
public class ClaudeAgentLoopAssignSessionIdTests
{
    private const string AssignedId = "00000000-0000-4000-8000-00000000abcd";
    private const string SeedId = "sess-pre-existing-001";

    [Fact]
    public async Task Construction_WithBothInitialSessionIdAndAssignSessionId_Throws()
    {
        var options = new ClaudeAgentSdkOptions
        {
            Mode = ClaudeAgentSdkMode.OneShot,
            MaxTurnsPerRun = 5,
            AssignSessionId = AssignedId,
        };

        var mockClient = new CapturingClient(
            scriptedMessages: [new ResultEventMessage { IsError = false }],
            liveSessionId: null);

        var act = async () =>
        {
            await using var loop = new ClaudeAgentLoop(
                claudeOptions: options,
                mcpServers: null,
                threadId: "ctor-mutex",
                clientFactory: (_, _) => mockClient,
                initialSessionId: SeedId);
        };

        await act.Should().ThrowAsync<ArgumentException>()
            .Where(ex => ex.ParamName == "initialSessionId");
    }

    [Fact]
    public async Task BuildRequest_FirstRun_EmitsAssignedSessionIdOnly()
    {
        var options = new ClaudeAgentSdkOptions
        {
            Mode = ClaudeAgentSdkMode.OneShot,
            MaxTurnsPerRun = 5,
            AssignSessionId = AssignedId,
        };

        var mockClient = new CapturingClient(
            scriptedMessages: [new ResultEventMessage { IsError = false }],
            liveSessionId: null);

        await using var loop = new ClaudeAgentLoop(
            claudeOptions: options,
            mcpServers: null,
            threadId: "first-run-assign",
            clientFactory: (_, _) => mockClient);

        var request = loop.BuildClaudeAgentSdkRequest();

        request.AssignedSessionId.Should().Be(AssignedId,
            "first run must carry the host-chosen id as --session-id");
        request.SessionId.Should().BeNullOrEmpty(
            "no captured/seeded session id exists yet, so --resume must not be emitted");
    }

    [Fact]
    public async Task BuildRequest_NoAssignedId_LeavesBothEmpty()
    {
        var options = new ClaudeAgentSdkOptions
        {
            Mode = ClaudeAgentSdkMode.OneShot,
            MaxTurnsPerRun = 5,
        };

        var mockClient = new CapturingClient(
            scriptedMessages: [new ResultEventMessage { IsError = false }],
            liveSessionId: null);

        await using var loop = new ClaudeAgentLoop(
            claudeOptions: options,
            mcpServers: null,
            threadId: "no-assign",
            clientFactory: (_, _) => mockClient);

        var request = loop.BuildClaudeAgentSdkRequest();

        request.AssignedSessionId.Should().BeNullOrEmpty();
        request.SessionId.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task SubsequentRun_AfterSdkReportsSessionId_FlipsToResume()
    {
        // First run advertises --session-id; the SDK echoes a SystemInitMessage
        // carrying the same id. From the second run onward, the loop must emit
        // --resume <id> instead — never both at the same time.
        var options = new ClaudeAgentSdkOptions
        {
            Mode = ClaudeAgentSdkMode.Interactive,
            MaxTurnsPerRun = 5,
            AssignSessionId = AssignedId,
        };

        var mockClient = new CapturingClient(
            scriptedMessages:
            [
                new SystemInitMessage { SessionId = AssignedId, Model = "claude-sonnet-4-6" },
                new TextMessage { Text = "ok", Role = Role.Assistant },
                new ResultEventMessage { IsError = false },
            ],
            liveSessionId: AssignedId);

        await using var loop = new ClaudeAgentLoop(
            claudeOptions: options,
            mcpServers: null,
            threadId: "assign-then-resume",
            clientFactory: (_, _) => mockClient);

        // Pre-run: --session-id path
        var firstRequest = loop.BuildClaudeAgentSdkRequest();
        firstRequest.AssignedSessionId.Should().Be(AssignedId);
        firstRequest.SessionId.Should().BeNullOrEmpty();

        await DriveOneRunAsync(loop);

        loop.CurrentSessionId.Should().Be(AssignedId,
            "the SDK's SystemInitMessage must populate CurrentSessionId after the first run");

        var secondRequest = loop.BuildClaudeAgentSdkRequest();
        secondRequest.SessionId.Should().Be(AssignedId,
            "after capture, subsequent runs must emit --resume against the captured id");
        secondRequest.AssignedSessionId.Should().BeNullOrEmpty(
            "--session-id and --resume are mutually exclusive; resume wins once an id is known");
    }

    [Fact]
    public async Task BuildRequest_WithSeededSessionId_PrefersResumeOverAssign()
    {
        // Defence-in-depth: even though the constructor blocks both being set,
        // a captured seed must dominate any stale AssignSessionId in BuildRequest.
        var options = new ClaudeAgentSdkOptions
        {
            Mode = ClaudeAgentSdkMode.OneShot,
            MaxTurnsPerRun = 5,
            // NOTE: AssignSessionId is left null here to satisfy the ctor mutex;
            // the test verifies resume wins when CurrentSessionId is populated.
        };

        var mockClient = new CapturingClient(
            scriptedMessages: [new ResultEventMessage { IsError = false }],
            liveSessionId: null);

        await using var loop = new ClaudeAgentLoop(
            claudeOptions: options,
            mcpServers: null,
            threadId: "seed-vs-assign",
            clientFactory: (_, _) => mockClient,
            initialSessionId: SeedId);

        var request = loop.BuildClaudeAgentSdkRequest();
        request.SessionId.Should().Be(SeedId);
        request.AssignedSessionId.Should().BeNullOrEmpty();
    }

    private static async Task<List<IMessage>> DriveOneRunAsync(ClaudeAgentLoop loop)
    {
        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var userInput = new UserInput(
            [new TextMessage { Text = "hi", Role = Role.User }],
            InputId: "test-input");

        var received = new List<IMessage>();
        var executeTask = Task.Run(async () =>
        {
            await foreach (var msg in loop.ExecuteRunAsync(userInput, cts.Token))
            {
                received.Add(msg);
            }
        });

        await executeTask.WaitAsync(TimeSpan.FromSeconds(10));

        await cts.CancelAsync();
        await loop.StopAsync();
        return received;
    }

    private sealed class CapturingClient : IClaudeAgentSdkClient
    {
        private readonly IReadOnlyList<IMessage> _messages;
        private readonly string? _liveSessionId;

        public CapturingClient(IReadOnlyList<IMessage> scriptedMessages, string? liveSessionId)
        {
            _messages = scriptedMessages;
            _liveSessionId = liveSessionId;
        }

        public bool IsRunning { get; private set; }

        public SessionInfo? CurrentSession { get; private set; }

        public ClaudeAgentSdkRequest? LastRequest { get; private set; }

        public Task StartAsync(ClaudeAgentSdkRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            IsRunning = true;
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<IMessage> SendMessagesAsync(
            IEnumerable<IMessage> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (_liveSessionId != null)
            {
                CurrentSession = new SessionInfo
                {
                    SessionId = _liveSessionId,
                    CreatedAt = DateTime.UtcNow,
                    ProjectRoot = "test",
                };
            }

            foreach (var msg in _messages)
            {
                await Task.Delay(1, cancellationToken);
                yield return msg;
            }

            IsRunning = false;
        }

        public async IAsyncEnumerable<IMessage> SubscribeToMessagesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (_liveSessionId != null)
            {
                CurrentSession = new SessionInfo
                {
                    SessionId = _liveSessionId,
                    CreatedAt = DateTime.UtcNow,
                    ProjectRoot = "test",
                };
            }

            foreach (var msg in _messages)
            {
                await Task.Delay(5, cancellationToken);
                yield return msg;
            }
        }

        public Task SendAsync(IEnumerable<IMessage> messages, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> SendExitCommandAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task ShutdownAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsRunning = false;
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            IsRunning = false;
        }
    }
}
