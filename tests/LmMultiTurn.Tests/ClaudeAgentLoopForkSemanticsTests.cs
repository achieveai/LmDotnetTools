using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Agents;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.ClientTools;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Validates that <see cref="ClaudeAgentLoop"/> emits <c>WasForked = true</c> /
/// <c>ForkedToRunId = run.RunId</c> on <see cref="RunCompletedMessage"/> whenever
/// a caller threads an explicit <c>parentRunId</c> via <see cref="MultiTurnAgentBase.SendAsync(System.Collections.Generic.List{AchieveAi.LmDotnetTools.LmCore.Messages.IMessage},string,string,System.Threading.CancellationToken)"/>.
/// </summary>
public class ClaudeAgentLoopForkSemanticsTests
{
    [Fact]
    public async Task ExecuteRunAsync_WithParentRunId_PublishesForkedCompletion()
    {
        var scriptedMessages = new List<IMessage>
        {
            new TextMessage { Text = "hi back", Role = Role.Assistant },
            new ResultEventMessage { IsError = false },
        };

        var mockClient = new ScriptedClaudeClient(scriptedMessages, sessionId: "sess-fork-1");
        var options = new ClaudeAgentSdkOptions { Mode = ClaudeAgentSdkMode.Interactive, MaxTurnsPerRun = 5 };

        await using var loop = new ClaudeAgentLoop(
            claudeOptions: options,
            mcpServers: null,
            threadId: "claude-fork-test",
            clientFactory: (_, _) => mockClient
        );

        var (received, _) = await DriveOneRunAsync(loop, parentRunId: "parent-run-abc");

        var assignment = received.OfType<RunAssignmentMessage>().Should().ContainSingle().Subject;
        assignment
            .Assignment.ParentRunId.Should()
            .Be("parent-run-abc", "caller-supplied UserInput.ParentRunId must be threaded into RunAssignment");

        var completed = received.OfType<RunCompletedMessage>().Should().ContainSingle().Subject;
        completed.WasForked.Should().BeTrue("the run originated from an explicit caller fork");
        completed
            .ForkedToRunId.Should()
            .Be(completed.CompletedRunId, "ForkedToRunId must point to the new run when WasForked is true");
    }

    [Fact]
    public async Task ExecuteRunAsync_WithNullParentRunId_PublishesUnforkedCompletion()
    {
        var scriptedMessages = new List<IMessage>
        {
            new TextMessage { Text = "hi back", Role = Role.Assistant },
            new ResultEventMessage { IsError = false },
        };

        var mockClient = new ScriptedClaudeClient(scriptedMessages, sessionId: "sess-no-fork");
        var options = new ClaudeAgentSdkOptions { Mode = ClaudeAgentSdkMode.Interactive, MaxTurnsPerRun = 5 };

        await using var loop = new ClaudeAgentLoop(
            claudeOptions: options,
            mcpServers: null,
            threadId: "claude-no-fork-test",
            clientFactory: (_, _) => mockClient
        );

        var (received, _) = await DriveOneRunAsync(loop, parentRunId: null);

        var completed = received.OfType<RunCompletedMessage>().Should().ContainSingle().Subject;
        completed.WasForked.Should().BeFalse("no caller-supplied parent → not an explicit fork");
        completed.ForkedToRunId.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteRunAsync_WithEmptyParentRunId_TreatedAsNull()
    {
        var scriptedMessages = new List<IMessage>
        {
            new TextMessage { Text = "hi back", Role = Role.Assistant },
            new ResultEventMessage { IsError = false },
        };

        var mockClient = new ScriptedClaudeClient(scriptedMessages, sessionId: "sess-empty-parent");
        var options = new ClaudeAgentSdkOptions { Mode = ClaudeAgentSdkMode.Interactive, MaxTurnsPerRun = 5 };

        await using var loop = new ClaudeAgentLoop(
            claudeOptions: options,
            mcpServers: null,
            threadId: "claude-empty-parent-test",
            clientFactory: (_, _) => mockClient
        );

        var (received, _) = await DriveOneRunAsync(loop, parentRunId: string.Empty);

        var completed = received.OfType<RunCompletedMessage>().Should().ContainSingle().Subject;
        completed.WasForked.Should().BeFalse();
        completed.ForkedToRunId.Should().BeNull();
    }

    /// <summary>
    /// Regression guard for issue #246: the client-facing AskUserQuestion/NotifyClient tools are
    /// registered ONLY by <see cref="MultiTurnAgentLoop"/>'s own constructor (unconditionally, via
    /// <see cref="AchieveAi.LmDotnetTools.LmCore.Middleware.FunctionRegistry"/>). Unlike Codex/Copilot,
    /// <see cref="ClaudeAgentLoop"/> has no FunctionRegistry concept at all — its only tool surface
    /// is the CLI's built-in <c>--tools</c> allow-list and whatever MCP servers a caller configures.
    /// This locks down that neither of those ever mentions the two client-only tool names, guarding
    /// against a future change that tries to expose them via a hardcoded MCP server or an addition
    /// to the default allow-list (which would be meaningless here since ClaudeAgentLoop has no
    /// mechanism to answer such a call back through this loop).
    /// </summary>
    [Fact]
    public async Task ExecuteRunAsync_DoesNotAdvertiseAskUserQuestionOrNotifyClient()
    {
        var scriptedMessages = new List<IMessage>
        {
            new TextMessage { Text = "hi back", Role = Role.Assistant },
            new ResultEventMessage { IsError = false },
        };

        var mockClient = new ScriptedClaudeClient(scriptedMessages, sessionId: "sess-no-client-tools");
        var options = new ClaudeAgentSdkOptions { Mode = ClaudeAgentSdkMode.Interactive, MaxTurnsPerRun = 5 };

        await using var loop = new ClaudeAgentLoop(
            claudeOptions: options,
            mcpServers: null,
            threadId: "claude-no-client-tools-test",
            clientFactory: (_, _) => mockClient
        );

        _ = await DriveOneRunAsync(loop, parentRunId: null);

        mockClient.LastRequest.Should().NotBeNull();
        var allowedTools = mockClient.LastRequest!.AllowedTools ?? string.Empty;
        allowedTools
            .Should()
            .NotContain(
                AskUserQuestionToolProvider.ToolName,
                "ClaudeAgentLoop's built-in --tools allow-list must never advertise the client-only AskUserQuestion tool"
            );
        allowedTools
            .Should()
            .NotContain(
                NotifyClientToolProvider.ToolName,
                "ClaudeAgentLoop's built-in --tools allow-list must never advertise the client-only NotifyClient tool"
            );

        var mcpServerNames = mockClient.LastRequest.McpServers?.Keys ?? Enumerable.Empty<string>();
        mcpServerNames.Should().NotContain(AskUserQuestionToolProvider.ToolName);
        mcpServerNames.Should().NotContain(NotifyClientToolProvider.ToolName);
    }

    /// <summary>
    /// Drives a single run via <c>ExecuteRunAsync</c>, optionally with a caller-supplied
    /// <paramref name="parentRunId"/>. Returns the messages observed.
    /// </summary>
    private static async Task<(List<IMessage> Messages, Task Run)> DriveOneRunAsync(
        ClaudeAgentLoop loop,
        string? parentRunId
    )
    {
        using var cts = new CancellationTokenSource();
        var runTask = loop.RunAsync(cts.Token);

        var userInput = new UserInput(
            [new TextMessage { Text = "hi", Role = Role.User }],
            InputId: "fork-input",
            ParentRunId: parentRunId
        );

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
    /// Minimal in-process Claude SDK mock. Mirrors the shape used in
    /// <c>ClaudeAgentLoopSystemInitRelayTests</c> — replays a scripted stream from both
    /// <c>SendMessagesAsync</c> (OneShot) and <c>SubscribeToMessagesAsync</c> (Interactive).
    /// </summary>
    private sealed class ScriptedClaudeClient : IClaudeAgentSdkClient
    {
        private readonly IReadOnlyList<IMessage> _messages;
        private readonly string _sessionId;

        public ScriptedClaudeClient(IReadOnlyList<IMessage> messages, string sessionId)
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
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<IMessage> SendMessagesAsync(
            IEnumerable<IMessage> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
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
            IsRunning = false;
        }

        public async IAsyncEnumerable<IMessage> SubscribeToMessagesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
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

        public Task SendAsync(IEnumerable<IMessage> messages, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> SendExitCommandAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

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
