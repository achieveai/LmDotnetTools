using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Unit tests for SubAgentManager lifecycle operations:
/// spawning, peeking, completion relay, concurrency enforcement, and disposal.
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
    public async Task SpawnAsync_CreatesAgentAndReturnsId()
    {
        // Arrange
        SetupSubAgentResponse([
            new TextMessage { Text = "Sub-agent result", Role = Role.Assistant },
        ]);

        _manager = CreateManager(maxConcurrent: 5);

        // Act
        var resultJson = await _manager.SpawnAsync("test-agent", "Do some work");

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
        _subAgentMock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                async (_, _, ct) =>
                {
                    // Wait until cancelled or test completes
                    await blockingTcs.Task.WaitAsync(ct);
                    return ToAsyncEnumerable([
                        new TextMessage { Text = "done", Role = Role.Assistant },
                    ]);
                });

        _manager = CreateManager(maxConcurrent: 1);

        // Act: first spawn should succeed
        await _manager.SpawnAsync("test-agent", "first task");

        // Second spawn should fail because concurrency limit is 1
        // and first agent is still running (semaphore wait times out after 5s)
        var act = () => _manager.SpawnAsync("test-agent", "second task");

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Max concurrent sub-agents*");

        // Cleanup: unblock so dispose doesn't hang
        blockingTcs.SetResult(true);
    }

    [Fact]
    public async Task Peek_ThrowsOnUnknownAgentId()
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
        var resultJson = await _manager.SpawnAsync("test-agent", "Do analysis");

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
    public async Task Completion_SendsWrappedResultToParent()
    {
        // Arrange: sub-agent returns a text response then the run completes
        SetupSubAgentResponse([
            new TextMessage { Text = "Analysis complete: found 3 issues", Role = Role.Assistant },
        ]);

        _manager = CreateManager();
        await _manager.SpawnAsync("test-agent", "Analyze the codebase");

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
        // Arrange: create a sub-agent with a delayed response
        var blockingTcs = new TaskCompletionSource<bool>();
        _subAgentMock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                async (_, _, ct) =>
                {
                    await blockingTcs.Task.WaitAsync(ct);
                    return ToAsyncEnumerable([
                        new TextMessage { Text = "done", Role = Role.Assistant },
                    ]);
                });

        _manager = CreateManager(maxConcurrent: 5);
        await _manager.SpawnAsync("test-agent", "long-running task 1");

        // Act & Assert: dispose should not throw even with running agents
        blockingTcs.SetResult(true);
        var act = async () => await _manager.DisposeAsync();
        await act.Should().NotThrowAsync();

        // Prevent double-dispose in DisposeAsync
        _manager = null;
    }

    [Fact]
    public async Task ResumeAsync_RunningAgent_SendsMessage()
    {
        // Arrange: sub-agent that blocks so it stays in Running state
        var blockingTcs = new TaskCompletionSource<bool>();
        _subAgentMock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                async (_, _, ct) =>
                {
                    await blockingTcs.Task.WaitAsync(ct);
                    return ToAsyncEnumerable([
                        new TextMessage { Text = "done", Role = Role.Assistant },
                    ]);
                });

        _manager = CreateManager();
        var spawnJson = await _manager.SpawnAsync("test-agent", "initial task");

        using var spawnDoc = JsonDocument.Parse(spawnJson);
        var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        // Act: resume with a new message while agent is running
        var resumeJson = await _manager.ResumeAsync(agentId, "follow-up message");

        // Assert
        using var resumeDoc = JsonDocument.Parse(resumeJson);
        resumeDoc.RootElement.GetProperty("status").GetString()
            .Should().Be("message_sent");

        // Cleanup
        blockingTcs.SetResult(true);
    }

    [Fact]
    public async Task ResumeAsync_CompletedAgent_RestartsRun()
    {
        // Arrange: sub-agent completes quickly
        SetupSubAgentResponse([
            new TextMessage { Text = "First result", Role = Role.Assistant },
        ]);

        _manager = CreateManager();
        var spawnJson = await _manager.SpawnAsync("test-agent", "initial task");

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

        // Act: resume the completed agent - this should restart a new run
        // Need to set up the mock to respond again for the restart
        SetupSubAgentResponse([
            new TextMessage { Text = "Second result", Role = Role.Assistant },
        ]);

        var resumeJson = await _manager.ResumeAsync(agentId, "continue work");

        // Assert
        using var resumeDoc = JsonDocument.Parse(resumeJson);
        resumeDoc.RootElement.GetProperty("status").GetString()
            .Should().Be("resumed");
    }

    [Fact]
    public async Task ResumeAsync_UnknownAgentId_Throws()
    {
        // Arrange
        _manager = CreateManager();

        // Act
        var act = () => _manager.ResumeAsync("non-existent-id", "some message");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Unknown agent ID*non-existent-id*");
    }

    [Fact]
    public async Task Completion_Error_SendsWrappedErrorToParent()
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
        await _manager.SpawnAsync("test-agent", "error-prone task");

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
    /// Checks if a message is a TextMessage containing sub-agent completion markers.
    /// Extracted as a static method to avoid pattern matching in Moq expression trees.
    /// </summary>
    private static bool ContainsSubAgentResult(
        IMessage message,
        string templateName,
        string expectedResultText)
    {
        if (message is not TextMessage tm)
        {
            return false;
        }

        return tm.Text.Contains($"<sub-agent name=\"{templateName}\"")
            && tm.Text.Contains("</sub-agent>")
            && tm.Text.Contains(expectedResultText);
    }

    /// <summary>
    /// Checks if a message is a TextMessage containing sub-agent XML tags.
    /// Extracted as a static method to avoid pattern matching in Moq expression trees.
    /// </summary>
    private static bool ContainsSubAgentTag(
        IMessage message,
        string templateName)
    {
        if (message is not TextMessage tm)
        {
            return false;
        }

        return tm.Text.Contains($"<sub-agent name=\"{templateName}\"")
            && tm.Text.Contains("</sub-agent>");
    }

    /// <summary>
    /// Checks if a message is a TextMessage containing sub-agent error markers.
    /// Verifies the [Error] tag specifically to distinguish from [Completed].
    /// </summary>
    private static bool ContainsSubAgentError(
        IMessage message,
        string templateName)
    {
        if (message is not TextMessage tm)
        {
            return false;
        }

        return tm.Text.Contains($"<sub-agent name=\"{templateName}\"")
            && tm.Text.Contains("</sub-agent>")
            && tm.Text.Contains("[Error]");
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
            parentHandlers: new Dictionary<string, Func<string, Task<string>>>(),
            parentMultiModalHandlers: null,
            options: options);
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
