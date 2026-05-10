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
/// Regression for Bug 3: SystemInitMessage (carrying SessionId/Model) was being
/// dropped by ClaudeAgentLoop when handling the dequeue heuristic in Interactive
/// mode, blocking consumers (e.g. session-resume continuity) from observing the
/// session id assigned to a run.
/// </summary>
public class ClaudeAgentLoopSystemInitRelayTests
{
    [Fact]
    public async Task ExecuteRunAsync_RelaysSystemInitMessage_WithSessionId()
    {
        // Arrange: an in-process mock SDK client that yields a SystemInitMessage
        // with a known SessionId followed by a ResultEventMessage to terminate
        // the Interactive subscription.
        const string expectedSessionId = "sess-test-123";
        const string expectedModel = "claude-sonnet-4-6";

        var scriptedMessages = new List<IMessage>
        {
            new SystemInitMessage { SessionId = expectedSessionId, Model = expectedModel },
            new ResultEventMessage { IsError = false },
        };

        var mockClient = new ScriptedClaudeAgentSdkClient(scriptedMessages);

        var options = new ClaudeAgentSdkOptions
        {
            Mode = ClaudeAgentSdkMode.Interactive,
            MaxTurnsPerRun = 5,
        };

        await using var loop = new ClaudeAgentLoop(
            claudeOptions: options,
            mcpServers: null,
            threadId: "system-init-relay-test",
            clientFactory: (_, _) => mockClient);

        using var cts = new CancellationTokenSource();
        var runTask = loop.RunAsync(cts.Token);

        var userInput = new UserInput(
            [new TextMessage { Text = "Hello", Role = Role.User }],
            InputId: "test-input");

        // Act: enumerate ExecuteRunAsync until completion (RunCompletedMessage
        // ends the iteration). Use WaitAsync so a hang fails cleanly.
        var receivedMessages = new List<IMessage>();
        var executeTask = Task.Run(async () =>
        {
            await foreach (var msg in loop.ExecuteRunAsync(userInput, cts.Token))
            {
                receivedMessages.Add(msg);
            }
        });

        await executeTask.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert: SystemInitMessage with our SessionId reached the consumer.
        var initMessages = receivedMessages.OfType<SystemInitMessage>().ToList();
        initMessages.Should().NotBeEmpty(
            "ClaudeAgentLoop must relay SystemInitMessage so consumers can capture SessionId for --resume");
        initMessages[0].SessionId.Should().Be(expectedSessionId);
        initMessages[0].Model.Should().Be(expectedModel);

        // Cleanup
        await cts.CancelAsync();
        await loop.StopAsync();
    }

    /// <summary>
    /// Minimal in-process mock that replays a scripted message stream from
    /// SubscribeToMessagesAsync and no-ops everything else. Stays alive across
    /// Start/Send so ClaudeAgentLoop's Interactive code path can drive it.
    /// </summary>
    private sealed class ScriptedClaudeAgentSdkClient : IClaudeAgentSdkClient
    {
        private readonly IReadOnlyList<IMessage> _messages;

        public ScriptedClaudeAgentSdkClient(IReadOnlyList<IMessage> messages)
        {
            _messages = messages;
        }

        public bool IsRunning { get; private set; }

        public SessionInfo? CurrentSession { get; private set; }

        public ClaudeAgentSdkRequest? LastRequest { get; private set; }

        public Task StartAsync(ClaudeAgentSdkRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            IsRunning = true;
            CurrentSession = new SessionInfo
            {
                SessionId = request.SessionId ?? "scripted-session",
                CreatedAt = DateTime.UtcNow,
                ProjectRoot = "test",
            };
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<IMessage> SendMessagesAsync(
            IEnumerable<IMessage> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var msg in _messages)
            {
                await Task.Delay(1, cancellationToken);
                yield return msg;
            }
        }

        public async IAsyncEnumerable<IMessage> SubscribeToMessagesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
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
