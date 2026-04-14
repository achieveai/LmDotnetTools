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

        // Give time for the monitoring task to process messages
        await Task.Delay(500);

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

        // Wait for the sub-agent to complete and relay result to parent
        await Task.Delay(1500);

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

    #region Helpers

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
            Name = "test-agent",
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
