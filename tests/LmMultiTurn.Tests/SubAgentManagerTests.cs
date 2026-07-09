using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Unit tests for SubAgentManager lifecycle operations: synchronous and background
/// spawning, peeking, completion relay, continuation via SendMessageAsync,
/// concurrency enforcement, and disposal.
/// </summary>
public class SubAgentManagerTests : IAsyncLifetime
{
    private readonly Mock<IMultiTurnAgent> _parentMock = new();
    private readonly Mock<IStreamingAgent> _subAgentMock = new();
    private SubAgentManager? _manager;

    public Task InitializeAsync()
    {
        // Default parent mock: accept any SendAsync call
        _parentMock
            .Setup(p => p.SendAsync(
                It.IsAny<List<IMessage>>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendReceipt("receipt-1", null, DateTimeOffset.UtcNow));

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_manager != null)
        {
            await _manager.DisposeAsync();
        }
    }

    [Fact]
    public async Task SpawnAsync_Synchronous_ReturnsFinalTextWithoutParentRelay()
    {
        // Arrange: sub-agent returns a single text response then the run completes
        SetupSubAgentResponse([
            new TextMessage { Text = "Sub-agent result", Role = Role.Assistant },
        ]);

        _manager = CreateManager();

        // Act: synchronous spawn (default) blocks and returns the final answer directly
        var result = await _manager.SpawnAsync("test-agent", "Do some work");

        // Assert: the tool result is the sub-agent's final text, not a JSON receipt
        result.Should().Be("Sub-agent result");

        // The synchronous path must NOT relay the result to the parent — the result
        // flows back only as this tool result, in the same parent turn.
        _parentMock.Verify(
            p => p.SendAsync(
                It.IsAny<List<IMessage>>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SpawnAsync_Synchronous_Error_ThrowsAndDoesNotRelayToParent()
    {
        // Arrange: sub-agent throws -> MultiTurnAgentLoop completes the run with IsError=true
        _subAgentMock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("API call failed"));

        _manager = CreateManager();

        // Act: synchronous spawn surfaces the failure as a typed exception
        var act = () => _manager.SpawnAsync("test-agent", "error-prone task");

        // Assert
        await act.Should().ThrowAsync<SubAgentExecutionException>()
            .WithMessage("*test-agent*failed*");

        // No parent relay on the synchronous path.
        _parentMock.Verify(
            p => p.SendAsync(
                It.IsAny<List<IMessage>>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SpawnAsync_Background_ReturnsSpawnReceipt()
    {
        // Arrange
        SetupSubAgentResponse([
            new TextMessage { Text = "Sub-agent result", Role = Role.Assistant },
        ]);

        _manager = CreateManager(maxConcurrent: 5);

        // Act: background spawn returns immediately with a JSON receipt
        var resultJson = await _manager.SpawnAsync(
            "test-agent", "Do some work", runInBackground: true);

        // Assert
        using var doc = JsonDocument.Parse(resultJson);
        var root = doc.RootElement;

        root.GetProperty("agent_id").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("template").GetString().Should().Be("test-agent");
        root.GetProperty("status").GetString().Should().Be("spawned");
    }

    [Fact]
    public async Task SpawnAsync_ThrowsOnUnknownTemplate()
    {
        // Arrange
        _manager = CreateManager();

        // Act
        var act = () => _manager.SpawnAsync("non-existent-template", "task");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Unknown template*non-existent-template*");
    }

    [Fact]
    public async Task SpawnAsync_EnforcesConcurrencyLimit()
    {
        // Arrange: sub-agent that never completes (blocks indefinitely)
        var blockingTcs = new TaskCompletionSource<bool>();
        SetupBlockingSubAgent(blockingTcs);

        _manager = CreateManager(maxConcurrent: 1);

        // Act: first (background) spawn acquires the only concurrency slot
        await _manager.SpawnAsync("test-agent", "first task", runInBackground: true);

        // Second spawn should fail because concurrency limit is 1 and the first agent
        // is still running (semaphore wait times out after 5s).
        var act = () => _manager.SpawnAsync(
            "test-agent", "second task", runInBackground: true);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Max concurrent sub-agents*");

        // Cleanup: unblock so dispose doesn't hang
        blockingTcs.SetResult(true);
    }

    [Fact]
    public void Peek_ThrowsOnUnknownAgentId()
    {
        // Arrange
        _manager = CreateManager();

        // Act
        var act = () => _manager.Peek("non-existent-id");

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Unknown agent ID*non-existent-id*");
    }

    [Fact]
    public async Task Peek_ReturnsStatusAndTurns()
    {
        // Arrange: sub-agent returns a text response
        SetupSubAgentResponse([
            new TextMessage { Text = "Working on it...", Role = Role.Assistant },
        ]);

        _manager = CreateManager();
        var resultJson = await _manager.SpawnAsync(
            "test-agent", "Do analysis", runInBackground: true);

        using var spawnDoc = JsonDocument.Parse(resultJson);
        var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        // Poll until monitoring task has processed messages
        await WaitForConditionAsync(
            () =>
            {
                try
                {
                    var json = _manager!.Peek(agentId);
                    return json.Contains("\"status\"");
                }
                catch
                {
                    return false;
                }
            },
            TimeSpan.FromSeconds(10));

        // Act
        var peekJson = _manager.Peek(agentId);

        // Assert
        using var peekDoc = JsonDocument.Parse(peekJson);
        var peekRoot = peekDoc.RootElement;

        peekRoot.GetProperty("agent_id").GetString().Should().Be(agentId);
        peekRoot.GetProperty("template").GetString().Should().Be("test-agent");
        peekRoot.GetProperty("task").GetString().Should().Be("Do analysis");
        peekRoot.TryGetProperty("status", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Completion_Background_SendsWrappedResultToParent()
    {
        // Arrange: sub-agent returns a text response then the run completes
        SetupSubAgentResponse([
            new TextMessage { Text = "Analysis complete: found 3 issues", Role = Role.Assistant },
        ]);

        _manager = CreateManager();
        await _manager.SpawnAsync(
            "test-agent", "Analyze the codebase", runInBackground: true);

        // Poll until the sub-agent completion is relayed to parent
        var parentCalled = false;
        await WaitForConditionAsync(
            () =>
            {
                try
                {
                    _parentMock.Verify(
                        p => p.SendAsync(
                            It.Is<List<IMessage>>(msgs =>
                                msgs.Count == 1
                                && ContainsSubAgentResult(msgs[0], "test-agent", "Analysis complete: found 3 issues")),
                            It.IsAny<string?>(),
                            It.IsAny<string?>(),
                            It.IsAny<CancellationToken>()),
                        Times.AtLeastOnce);
                    parentCalled = true;
                    return true;
                }
                catch
                {
                    return false;
                }
            },
            TimeSpan.FromSeconds(10));

        parentCalled.Should().BeTrue("parent should have received the sub-agent result");

        // Assert: parent's SendAsync was called with the wrapped sub-agent result
        _parentMock.Verify(
            p => p.SendAsync(
                It.Is<List<IMessage>>(msgs =>
                    msgs.Count == 1
                    && ContainsSubAgentResult(msgs[0], "test-agent", "Analysis complete: found 3 issues")),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task DisposeAsync_StopsAllAgents()
    {
        // Arrange: create a background sub-agent with a delayed response
        var blockingTcs = new TaskCompletionSource<bool>();
        SetupBlockingSubAgent(blockingTcs);

        _manager = CreateManager(maxConcurrent: 5);
        await _manager.SpawnAsync(
            "test-agent", "long-running task 1", runInBackground: true);

        // Act & Assert: dispose should not throw even with running agents
        blockingTcs.SetResult(true);
        var act = async () => await _manager.DisposeAsync();
        await act.Should().NotThrowAsync();

        // Prevent double-dispose in DisposeAsync
        _manager = null;
    }

    [Fact]
    public async Task SendMessageAsync_RunningAgent_SendsMessage()
    {
        // Arrange: sub-agent that blocks so it stays in Running state
        var blockingTcs = new TaskCompletionSource<bool>();
        SetupBlockingSubAgent(blockingTcs);

        _manager = CreateManager();
        var spawnJson = await _manager.SpawnAsync(
            "test-agent", "initial task", runInBackground: true);

        using var spawnDoc = JsonDocument.Parse(spawnJson);
        var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        // Act: continue with a new message (background) while the agent is running
        var resumeJson = await _manager.SendMessageAsync(
            agentId, "follow-up message", runInBackground: true);

        // Assert
        using var resumeDoc = JsonDocument.Parse(resumeJson);
        resumeDoc.RootElement.GetProperty("status").GetString()
            .Should().Be("message_sent");

        // Cleanup
        blockingTcs.SetResult(true);
    }

    [Fact]
    public async Task SendMessageAsync_CompletedAgent_RestartsRun()
    {
        // Arrange: sub-agent completes quickly
        SetupSubAgentResponse([
            new TextMessage { Text = "First result", Role = Role.Assistant },
        ]);

        _manager = CreateManager();
        var spawnJson = await _manager.SpawnAsync(
            "test-agent", "initial task", runInBackground: true);

        using var spawnDoc = JsonDocument.Parse(spawnJson);
        var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        // Poll until the sub-agent completes
        await WaitForConditionAsync(
            () =>
            {
                try
                {
                    var json = _manager!.Peek(agentId);
                    return json.Contains("\"completed\"");
                }
                catch
                {
                    return false;
                }
            },
            TimeSpan.FromSeconds(10));

        // Verify it completed
        var peekJson = _manager.Peek(agentId);
        using var peekDoc = JsonDocument.Parse(peekJson);
        peekDoc.RootElement.GetProperty("status").GetString()
            .Should().Be("completed");

        // Act: continue the completed agent - this restarts a new run.
        // Set up the mock to respond again for the restart.
        SetupSubAgentResponse([
            new TextMessage { Text = "Second result", Role = Role.Assistant },
        ]);

        var resumeJson = await _manager.SendMessageAsync(
            agentId, "continue work", runInBackground: true);

        // Assert
        using var resumeDoc = JsonDocument.Parse(resumeJson);
        resumeDoc.RootElement.GetProperty("status").GetString()
            .Should().Be("resumed");
    }

    [Fact]
    public async Task SendMessageAsync_ResolvesAgentByName()
    {
        // Arrange: blocking agent stays Running so it can receive a follow-up message
        var blockingTcs = new TaskCompletionSource<bool>();
        SetupBlockingSubAgent(blockingTcs);

        _manager = CreateManager();

        // Spawn with a caller-supplied name in the background
        await _manager.SpawnAsync(
            "test-agent", "initial task", name: "researcher", runInBackground: true);

        // Act: address the agent by its name instead of its generated id
        var resumeJson = await _manager.SendMessageAsync(
            "researcher", "follow-up message", runInBackground: true);

        // Assert
        using var resumeDoc = JsonDocument.Parse(resumeJson);
        var root = resumeDoc.RootElement;
        root.GetProperty("name").GetString().Should().Be("researcher");
        root.GetProperty("status").GetString().Should().Be("message_sent");

        // Cleanup
        blockingTcs.SetResult(true);
    }

    [Fact]
    public async Task SendMessageAsync_InjectIntoRunningBackgroundAgent_DoesNotOverReleaseConcurrencyGate()
    {
        // Regression guard for the gate single-release invariant. A background sub-agent
        // continued in place via SendMessage (while still Running) feeds a SECOND run
        // under the SAME monitor, so that one monitor observes two RunCompletedMessages.
        // The concurrency slot is acquired once (at spawn), so it must be released exactly
        // once. Releasing per completion (the original bug) over-releases the SemaphoreSlim,
        // which throws SemaphoreFullException inside the monitor and flips the agent to
        // Error status. The fix releases once per monitor, so the agent settles 'completed'.
        var entered = new TaskCompletionSource<bool>();
        var release = new TaskCompletionSource<bool>();
        _subAgentMock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                async (_, _, ct) =>
                {
                    // Signal the first turn is in-flight, then block so the task input is
                    // already consumed before the follow-up is injected (forcing two runs).
                    _ = entered.TrySetResult(true);
                    await release.Task.WaitAsync(ct);
                    return ToAsyncEnumerable([
                        new TextMessage { Text = "done", Role = Role.Assistant },
                    ]);
                });

        _manager = CreateManager(maxConcurrent: 1);

        var spawnJson = await _manager.SpawnAsync(
            "test-agent", "initial task", runInBackground: true);
        using var spawnDoc = JsonDocument.Parse(spawnJson);
        var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        // Wait until the first run has consumed the task and is blocked, so the follow-up
        // becomes a distinct second run rather than collapsing into the first batch.
        (await entered.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();

        // Inject the follow-up while the first run is still Running -> same-monitor path.
        var resumeJson = await _manager.SendMessageAsync(
            agentId, "follow-up", runInBackground: true);
        using var resumeDoc = JsonDocument.Parse(resumeJson);
        resumeDoc.RootElement.GetProperty("status").GetString()
            .Should().Be("message_sent");

        // Release the block: the first run completes, then the queued follow-up drives a
        // second run — both completions are observed by the one monitor.
        release.SetResult(true);

        // The agent must settle in 'completed', NOT 'error'. Under the over-release bug the
        // monitor faults on the second completion (SemaphoreFullException) -> Error status.
        await WaitForConditionAsync(
            () =>
            {
                try
                {
                    return _manager!.Peek(agentId).Contains("\"completed\"");
                }
                catch
                {
                    return false;
                }
            },
            TimeSpan.FromSeconds(10));

        var peekJson = _manager.Peek(agentId);
        using var peekDoc = JsonDocument.Parse(peekJson);
        peekDoc.RootElement.GetProperty("status").GetString()
            .Should().Be(
                "completed",
                "the monitor must release the concurrency slot exactly once across both runs");
    }

    [Fact]
    public async Task SendMessageAsync_UnknownTarget_Throws()
    {
        // Arrange
        _manager = CreateManager();

        // Act
        var act = () => _manager.SendMessageAsync("non-existent-id", "some message");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Unknown sub-agent*non-existent-id*");
    }

    [Fact]
    public async Task Completion_Background_Error_SendsWrappedErrorToParent()
    {
        // Arrange: sub-agent throws on first call to trigger error run completion.
        // MultiTurnAgentLoop catches this and calls CompleteRunAsync(isError: true),
        // which produces RunCompletedMessage with IsError=true.
        _subAgentMock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("API call failed"));

        _manager = CreateManager();
        await _manager.SpawnAsync(
            "test-agent", "error-prone task", runInBackground: true);

        // Poll until parent receives error notification
        await WaitForConditionAsync(
            () =>
            {
                try
                {
                    _parentMock.Verify(
                        p => p.SendAsync(
                            It.Is<List<IMessage>>(msgs =>
                                msgs.Count == 1
                                && ContainsSubAgentError(msgs[0], "test-agent")),
                            It.IsAny<string?>(),
                            It.IsAny<string?>(),
                            It.IsAny<CancellationToken>()),
                        Times.AtLeastOnce);
                    return true;
                }
                catch
                {
                    return false;
                }
            },
            TimeSpan.FromSeconds(10));

        // Assert: parent received error notification specifically (not [Completed])
        _parentMock.Verify(
            p => p.SendAsync(
                It.Is<List<IMessage>>(msgs =>
                    msgs.Count == 1
                    && ContainsSubAgentError(msgs[0], "test-agent")),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Theory]
    [InlineData(null, null, null, null)]
    [InlineData(new[] { "tool1", "tool2" }, null, null, new[] { "tool1", "tool2" })]
    [InlineData(new[] { "tool1" }, new[] { "tool2" }, null, new[] { "tool1", "tool2" })]
    [InlineData(new[] { "tool1", "tool2" }, null, new[] { "tool2" }, new[] { "tool1" })]
    [InlineData(null, new[] { "tool1" }, null, new[] { "tool1" })]
    [InlineData(null, new[] { "tool1", "tool2" }, new[] { "tool1" }, new[] { "tool2" })]
    public void BuildEnabledToolSet_FiltersToolsCorrectly(
        string[]? templateTools,
        string[]? addTools,
        string[]? removeTools,
        string[]? expectedTools)
    {
        // Act
        var result = SubAgentManager.BuildEnabledToolSet(
            templateTools?.ToList(), addTools, removeTools);

        // Assert
        if (expectedTools == null)
        {
            result.Should().BeNull();
        }
        else
        {
            result.Should().NotBeNull();
            result.Should().BeEquivalentTo(expectedTools);
        }
    }

    [Fact]
    public void BuildEnabledToolSet_RemoveWithoutBaseSet_Throws()
    {
        // Act
        var act = () => SubAgentManager.BuildEnabledToolSet(
            templateEnabledTools: null,
            addTools: null,
            removeTools: ["tool1"]);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Cannot specify removeTools without enabledTools or addTools*");
    }

    #region Helpers

    private static async Task WaitForConditionAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
    }

    /// <summary>
    /// Checks if a message is a sub-agent-completion NotifyMessage containing the completion markers.
    /// Extracted as a static method to avoid pattern matching in Moq expression trees.
    /// </summary>
    private static bool ContainsSubAgentResult(
        IMessage message,
        string templateName,
        string expectedResultText)
    {
        if (message is not NotifyMessage { NotifyKind: NotifyKinds.SubAgentCompletion } nm)
        {
            return false;
        }

        var text = nm.GetText() ?? string.Empty;
        return text.Contains($"<sub-agent name=\"{templateName}\"")
            && text.Contains("</sub-agent>")
            && text.Contains(expectedResultText);
    }

    /// <summary>
    /// Checks if a message is a sub-agent-completion NotifyMessage containing the error markers.
    /// Verifies the [Error] tag specifically to distinguish from [Completed].
    /// </summary>
    private static bool ContainsSubAgentError(
        IMessage message,
        string templateName)
    {
        if (message is not NotifyMessage { NotifyKind: NotifyKinds.SubAgentCompletion } nm)
        {
            return false;
        }

        var text = nm.GetText() ?? string.Empty;
        return text.Contains($"<sub-agent name=\"{templateName}\"")
            && text.Contains("</sub-agent>")
            && text.Contains("[Error]");
    }

    /// <summary>
    /// Creates a SubAgentManager with the test's mock sub-agent and parent.
    /// </summary>
    private SubAgentManager CreateManager(int maxConcurrent = 5)
    {
        var options = CreateOptions(maxConcurrent);
        return new SubAgentManager(
            parentAgent: _parentMock.Object,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: options,
            source: new MutableSubAgentTemplateSource(options.Templates));
    }

    /// <summary>
    /// Creates SubAgentOptions with a single "test-agent" template
    /// backed by the mock sub-agent.
    /// </summary>
    private SubAgentOptions CreateOptions(int maxConcurrent = 5)
    {
        var template = new SubAgentTemplate
        {
            SystemPrompt = "You are a test agent.",
            AgentFactory = () => _subAgentMock.Object,
        };

        return new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["test-agent"] = template,
            },
            MaxConcurrentSubAgents = maxConcurrent,
        };
    }

    /// <summary>
    /// Configures the mock sub-agent to return the given messages from
    /// GenerateReplyStreamingAsync on every call.
    /// </summary>
    private void SetupSubAgentResponse(List<IMessage> messages)
    {
        _subAgentMock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(ToAsyncEnumerable(messages)));
    }

    /// <summary>
    /// Configures the mock sub-agent to block until <paramref name="release"/> is
    /// completed (or the run is cancelled), keeping it in the Running state. Used by
    /// concurrency, disposal, and continuation tests that need a long-running agent.
    /// </summary>
    private void SetupBlockingSubAgent(TaskCompletionSource<bool> release)
    {
        _subAgentMock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                async (_, _, ct) =>
                {
                    await release.Task.WaitAsync(ct);
                    return ToAsyncEnumerable([
                        new TextMessage { Text = "done", Role = Role.Assistant },
                    ]);
                });
    }

    /// <summary>
    /// Converts a list of messages to an IAsyncEnumerable for mock setup.
    /// </summary>
    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
        List<IMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var msg in messages)
        {
            ct.ThrowIfCancellationRequested();
            yield return msg;
            await Task.Yield();
        }
    }

    #endregion
}
