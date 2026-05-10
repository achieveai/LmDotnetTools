using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Agents;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Validates how <see cref="ClaudeAgentLoop"/> surfaces the Claude SDK session id
/// without leaking <see cref="SystemInitMessage"/> into the public message stream.
///
/// Contract:
///   * <c>SystemInitMessage</c> is SDK metadata and must NOT appear in messages
///     observed by consumers via <c>ExecuteRunAsync</c> or <c>SubscribeAsync</c>.
///   * The session id assigned by the SDK MUST be observable via
///     <see cref="ClaudeAgentLoop.CurrentSessionId"/> in BOTH Interactive
///     (where the SDK emits <c>SystemInitMessage</c>) and OneShot mode (where
///     it does not — only <see cref="IClaudeAgentSdkClient.CurrentSession"/> is
///     updated).
///   * When a persistence store is configured, the latest session id is written
///     into <see cref="ThreadMetadata.SessionMappings"/> under the
///     <c>"claude-sdk:{sessionId}"</c> key, mapped to the <c>RunId</c>.
/// </summary>
public class ClaudeAgentLoopSystemInitRelayTests
{
    [Fact]
    public async Task Interactive_ExposesSessionId_AndDoesNotLeakSystemInitMessage()
    {
        const string expectedSessionId = "sess-interactive-123";
        const string expectedModel = "claude-sonnet-4-6";

        var scriptedMessages = new List<IMessage>
        {
            new SystemInitMessage { SessionId = expectedSessionId, Model = expectedModel },
            new TextMessage { Text = "hello", Role = Role.Assistant },
            new ResultEventMessage { IsError = false },
        };

        var mockClient = new ScriptedClaudeAgentSdkClient(scriptedMessages, expectedSessionId);

        var options = new ClaudeAgentSdkOptions
        {
            Mode = ClaudeAgentSdkMode.Interactive,
            MaxTurnsPerRun = 5,
        };

        await using var loop = new ClaudeAgentLoop(
            claudeOptions: options,
            mcpServers: null,
            threadId: "interactive-session-test",
            clientFactory: (_, _) => mockClient);

        var (received, _) = await DriveOneRunAsync(loop);

        received.OfType<SystemInitMessage>().Should().BeEmpty(
            "SystemInitMessage is SDK metadata and must not be published to consumers");
        received.OfType<TextMessage>().Should().Contain(t => t.Text == "hello");

        loop.CurrentSessionId.Should().Be(expectedSessionId,
            "Interactive mode must capture SessionId from SystemInitMessage");
    }

    [Fact]
    public async Task OneShot_ExposesSessionId_FromClientCurrentSession()
    {
        // Critical: OneShot mode's SendMessagesAsync intentionally suppresses
        // SystemInitMessage (emitSystemInit:false in ClaudeAgentSdkClient.cs).
        // The loop must instead poll the underlying client's CurrentSession.
        const string expectedSessionId = "sess-oneshot-456";

        // No SystemInitMessage in scripted stream — mirrors real OneShot behavior.
        var scriptedMessages = new List<IMessage>
        {
            new TextMessage { Text = "hello", Role = Role.Assistant },
            new ResultEventMessage { IsError = false },
        };

        var mockClient = new ScriptedClaudeAgentSdkClient(scriptedMessages, expectedSessionId);

        var options = new ClaudeAgentSdkOptions
        {
            Mode = ClaudeAgentSdkMode.OneShot,
            MaxTurnsPerRun = 5,
        };

        await using var loop = new ClaudeAgentLoop(
            claudeOptions: options,
            mcpServers: null,
            threadId: "oneshot-session-test",
            clientFactory: (_, _) => mockClient);

        var (received, _) = await DriveOneRunAsync(loop);

        received.OfType<SystemInitMessage>().Should().BeEmpty(
            "OneShot path must not publish SystemInitMessage either");

        loop.CurrentSessionId.Should().Be(expectedSessionId,
            "OneShot mode must capture SessionId from IClaudeAgentSdkClient.CurrentSession " +
            "since the SDK does not surface SystemInitMessage in this path");
    }

    [Fact]
    public async Task OneShot_PersistsSessionId_InThreadMetadataSessionMappings()
    {
        const string expectedSessionId = "sess-oneshot-persist-789";

        var scriptedMessages = new List<IMessage>
        {
            new TextMessage { Text = "hello", Role = Role.Assistant },
            new ResultEventMessage { IsError = false },
        };

        var mockClient = new ScriptedClaudeAgentSdkClient(scriptedMessages, expectedSessionId);
        var store = new InMemoryConversationStore();
        var threadId = "oneshot-persist-test";

        var options = new ClaudeAgentSdkOptions
        {
            Mode = ClaudeAgentSdkMode.OneShot,
            MaxTurnsPerRun = 5,
        };

        await using (var loop = new ClaudeAgentLoop(
            claudeOptions: options,
            mcpServers: null,
            threadId: threadId,
            store: store,
            clientFactory: (_, _) => mockClient))
        {
            await DriveOneRunAsync(loop);
        }

        var metadata = await store.LoadMetadataAsync(threadId);
        metadata.Should().NotBeNull();
        metadata!.SessionMappings.Should().NotBeNull();
        metadata.SessionMappings!.Should().ContainKey($"claude-sdk:{expectedSessionId}",
            "ClaudeAgentLoop must persist the SDK session id into ThreadMetadata.SessionMappings " +
            "so a later process can resume the session");
        metadata.LatestRunId.Should().NotBeNullOrEmpty();
        metadata.SessionMappings![$"claude-sdk:{expectedSessionId}"]
            .Should().Be(metadata.LatestRunId,
                "the mapping value must be the run id the session id belongs to");
    }

    [Fact]
    public async Task OneShot_CapturesSessionId_EvenWhenClientIsDisposedBetweenRuns()
    {
        // OneShot disposes the client at the end of each run via
        // OnAfterRunAsync->DisposeClientResourcesAsync. Make sure the session id
        // is snapshotted before disposal so it is still available afterwards.
        const string expectedSessionId = "sess-oneshot-survives-dispose";

        var scriptedMessages = new List<IMessage>
        {
            new TextMessage { Text = "hello", Role = Role.Assistant },
            new ResultEventMessage { IsError = false },
        };

        var mockClient = new ScriptedClaudeAgentSdkClient(scriptedMessages, expectedSessionId);

        var options = new ClaudeAgentSdkOptions
        {
            Mode = ClaudeAgentSdkMode.OneShot,
            MaxTurnsPerRun = 5,
        };

        await using var loop = new ClaudeAgentLoop(
            claudeOptions: options,
            mcpServers: null,
            threadId: "oneshot-dispose-test",
            clientFactory: (_, _) => mockClient);

        await DriveOneRunAsync(loop);

        // After OneShot completes, OnAfterRunAsync has already disposed the
        // client. The session id must still be readable from the loop.
        loop.CurrentSessionId.Should().Be(expectedSessionId);
    }

    /// <summary>
    /// Drives the loop through exactly one run via ExecuteRunAsync and returns
    /// the messages observed plus the run task.
    /// </summary>
    private static async Task<(List<IMessage> Messages, Task Run)> DriveOneRunAsync(ClaudeAgentLoop loop)
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
        return (received, runTask);
    }

    /// <summary>
    /// Minimal in-process mock that:
    ///   * Replays a scripted message stream from BOTH SubscribeToMessagesAsync
    ///     (Interactive) and SendMessagesAsync (OneShot).
    ///   * Reports the supplied session id via <see cref="CurrentSession"/> so
    ///     the OneShot capture path can poll it.
    /// </summary>
    private sealed class ScriptedClaudeAgentSdkClient : IClaudeAgentSdkClient
    {
        private readonly IReadOnlyList<IMessage> _messages;
        private readonly string _sessionId;

        public ScriptedClaudeAgentSdkClient(IReadOnlyList<IMessage> messages, string sessionId)
        {
            _messages = messages;
            _sessionId = sessionId;
        }

        public bool IsRunning { get; private set; }

        public SessionInfo? CurrentSession { get; private set; }

        public ClaudeAgentSdkRequest? LastRequest { get; private set; }

        public Task StartAsync(ClaudeAgentSdkRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            IsRunning = true;
            // Real client populates CurrentSession only AFTER the SystemInitEvent
            // arrives. Mirror that by leaving CurrentSession null until the first
            // message is yielded below.
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<IMessage> SendMessagesAsync(
            IEnumerable<IMessage> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // Simulate the SDK populating CurrentSession when it parses
            // SystemInitEvent — happens before the first message is yielded
            // back to the caller.
            CurrentSession = new SessionInfo
            {
                SessionId = _sessionId,
                CreatedAt = DateTime.UtcNow,
                ProjectRoot = "test",
            };

            foreach (var msg in _messages)
            {
                await Task.Delay(1, cancellationToken);
                yield return msg;
            }

            // OneShot: process exits at end of stream.
            IsRunning = false;
        }

        public async IAsyncEnumerable<IMessage> SubscribeToMessagesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CurrentSession = new SessionInfo
            {
                SessionId = _sessionId,
                CreatedAt = DateTime.UtcNow,
                ProjectRoot = "test",
            };

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
