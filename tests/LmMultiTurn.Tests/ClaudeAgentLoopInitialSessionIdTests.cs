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
/// Validates that <see cref="ClaudeAgentLoop"/> accepts an <c>initialSessionId</c>
/// constructor argument so callers can drive <c>--resume &lt;SessionId&gt;</c>
/// on the very first underlying SDK run (issue #47).
/// </summary>
public class ClaudeAgentLoopInitialSessionIdTests
{
    [Fact]
    public async Task Construction_WithInitialSessionId_PopulatesCurrentSessionIdBeforeFirstRun()
    {
        const string seededId = "sess-seeded-001";

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
            threadId: "ctor-seed-pre-run",
            clientFactory: (_, _) => mockClient,
            initialSessionId: seededId);

        loop.CurrentSessionId.Should().Be(seededId,
            "constructor seed must populate CurrentSessionId synchronously, " +
            "before any run starts");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Construction_WithNullOrEmptyInitialSessionId_LeavesCurrentSessionIdNull(
        string? seed)
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
            threadId: "ctor-seed-null-empty",
            clientFactory: (_, _) => mockClient,
            initialSessionId: seed);

        loop.CurrentSessionId.Should().BeNull(
            "null and empty seeds must not propagate as a real session id");
    }

    [Fact]
    public async Task Construction_WithInitialSessionId_EmitsResumeFlagViaRequest()
    {
        // BuildClaudeAgentSdkRequest is internal and synchronous; verify directly
        // that the seeded id is what the first request would carry as --resume.
        const string seededId = "sess-seeded-resume-002";

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
            threadId: "ctor-seed-request-built",
            clientFactory: (_, _) => mockClient,
            initialSessionId: seededId);

        var request = loop.BuildClaudeAgentSdkRequest();
        request.SessionId.Should().Be(seededId,
            "the constructor seed must flow into the SDK request as --resume " +
            "on the very first run");
    }

    [Fact]
    public async Task LiveSystemInitMessage_OverridesSeededValue()
    {
        const string seededId = "sess-seeded-pre";
        const string liveId = "sess-live-actual";

        var options = new ClaudeAgentSdkOptions
        {
            Mode = ClaudeAgentSdkMode.Interactive,
            MaxTurnsPerRun = 5,
        };

        var mockClient = new CapturingClient(
            scriptedMessages:
            [
                new SystemInitMessage { SessionId = liveId, Model = "claude-sonnet-4-6" },
                new TextMessage { Text = "hello", Role = Role.Assistant },
                new ResultEventMessage { IsError = false },
            ],
            liveSessionId: liveId);

        await using var loop = new ClaudeAgentLoop(
            claudeOptions: options,
            mcpServers: null,
            threadId: "ctor-seed-overridden",
            clientFactory: (_, _) => mockClient,
            initialSessionId: seededId);

        loop.CurrentSessionId.Should().Be(seededId,
            "sanity check: seed must be visible before the run starts");

        await DriveOneRunAsync(loop);

        loop.CurrentSessionId.Should().Be(liveId,
            "a live SystemInitMessage must replace the caller-seeded value " +
            "so the loop continues with the SDK's actual session id");
    }

    [Fact]
    public async Task OneShot_SeededSessionId_SurvivesAfterRunDispose()
    {
        // OneShot disposes the client at the end of each run. A seeded id that
        // never gets contradicted by the SDK (because no live id was reported)
        // must still be observable on the loop.
        const string seededId = "sess-seeded-survives-oneshot";

        var options = new ClaudeAgentSdkOptions
        {
            Mode = ClaudeAgentSdkMode.OneShot,
            MaxTurnsPerRun = 5,
        };

        // No SystemInitMessage; liveSessionId == null so CurrentSession stays
        // null in the mock - mirrors a real-world resume where the SDK accepts
        // the supplied --resume id without reporting a different one.
        var mockClient = new CapturingClient(
            scriptedMessages:
            [
                new TextMessage { Text = "hello", Role = Role.Assistant },
                new ResultEventMessage { IsError = false },
            ],
            liveSessionId: null);

        await using var loop = new ClaudeAgentLoop(
            claudeOptions: options,
            mcpServers: null,
            threadId: "ctor-seed-oneshot-survives-dispose",
            clientFactory: (_, _) => mockClient,
            initialSessionId: seededId);

        await DriveOneRunAsync(loop);

        loop.CurrentSessionId.Should().Be(seededId,
            "when the SDK never reports a different session id, OneShot's per-run " +
            "client dispose must not drop the seeded value");
    }

    private static async Task<List<IMessage>> DriveOneRunAsync(ClaudeAgentLoop loop)
    {
        using var cts = new CancellationTokenSource();
        var runTask = loop.RunAsync(cts.Token);

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

    /// <summary>
    /// Replays a scripted message stream from both subscribe/send paths and
    /// optionally reports a live session id via <see cref="CurrentSession"/>.
    /// </summary>
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
