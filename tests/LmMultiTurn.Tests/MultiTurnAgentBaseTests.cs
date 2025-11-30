using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Tests for the MultiTurnAgentBase abstract class using a test implementation.
/// </summary>
public class MultiTurnAgentBaseTests
{
    private readonly Mock<ILogger<TestMultiTurnAgent>> _loggerMock = new();

    /// <summary>
    /// Test implementation of MultiTurnAgentBase for testing purposes.
    /// </summary>
    private class TestMultiTurnAgent : MultiTurnAgentBase
    {
        private readonly List<IMessage> _messagesToReturn;
        private readonly bool _shouldFork;

        public int ExecuteCallCount { get; private set; }
        public string? LastRunId { get; private set; }
        public string? LastGenerationId { get; private set; }

        public TestMultiTurnAgent(
            string threadId,
            List<IMessage>? messagesToReturn = null,
            bool shouldFork = false,
            string? systemPrompt = null,
            ILogger? logger = null)
            : base(threadId, systemPrompt, logger: logger)
        {
            _messagesToReturn = messagesToReturn ?? [];
            _shouldFork = shouldFork;
        }

        protected override Task<bool> ExecuteAgenticLoopAsync(
            string runId,
            string generationId,
            CancellationToken ct)
        {
            ExecuteCallCount++;
            LastRunId = runId;
            LastGenerationId = generationId;

            // Publish the test messages
            foreach (var msg in _messagesToReturn)
            {
                PublishToAllAsync(msg, ct).AsTask().Wait(ct);
            }

            return Task.FromResult(_shouldFork);
        }
    }

    [Fact]
    public async Task Constructor_SetsProperties()
    {
        // Arrange & Act
        var threadId = "test-thread-123";
        await using var agent = new TestMultiTurnAgent(threadId);

        // Assert
        agent.ThreadId.Should().Be(threadId);
        agent.CurrentRunId.Should().BeNull();
        agent.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task SendAsync_WhenNotRunning_QueuesMessage()
    {
        // Arrange
        var agent = new TestMultiTurnAgent("test-thread");
        var messages = new List<IMessage>
        {
            new TextMessage { Text = "Hello", Role = Role.User },
        };

        // Start the loop so it can process
        using var cts = new CancellationTokenSource();
        var runTask = agent.RunAsync(cts.Token);

        // Act
        var assignment = await agent.SendAsync(messages, "input-1");

        // Assert
        assignment.Should().NotBeNull();
        assignment.RunId.Should().NotBeNullOrEmpty();
        assignment.GenerationId.Should().NotBeNullOrEmpty();
        assignment.InputId.Should().Be("input-1");
        assignment.WasInjected.Should().BeFalse();

        // Cleanup
        await cts.CancelAsync();
        await agent.StopAsync();
        await agent.DisposeAsync();
    }

    [Fact]
    public async Task SubscribeAsync_ReceivesMessages()
    {
        // Arrange
        var testMessage = new TextMessage { Text = "Test response", Role = Role.Assistant };
        var agent = new TestMultiTurnAgent(
            "test-thread",
            messagesToReturn: [testMessage]);

        var receivedMessages = new List<IMessage>();

        // Start the loop
        using var cts = new CancellationTokenSource();
        var runTask = agent.RunAsync(cts.Token);

        // Subscribe
        var subscribeTask = Task.Run(async () =>
        {
            await foreach (var msg in agent.SubscribeAsync(cts.Token))
            {
                receivedMessages.Add(msg);
            }
        });

        // Give time for subscription to be registered
        await Task.Delay(100);

        // Act - send a message to trigger processing
        var messages = new List<IMessage>
        {
            new TextMessage { Text = "Hello", Role = Role.User },
        };
        await agent.SendAsync(messages);

        // Give time for processing
        await Task.Delay(500);

        // Assert
        receivedMessages.OfType<RunAssignmentMessage>().Should().NotBeEmpty();
        receivedMessages.OfType<TextMessage>().Should().Contain(m => m.Text == "Test response");
        receivedMessages.OfType<RunCompletedMessage>().Should().NotBeEmpty();

        // Cleanup
        await cts.CancelAsync();
        await agent.StopAsync();
        await agent.DisposeAsync();
    }

    [Fact]
    public async Task ExecuteRunAsync_ReturnsMessagesForRun()
    {
        // Arrange
        var testMessage = new TextMessage { Text = "Response", Role = Role.Assistant };
        var agent = new TestMultiTurnAgent(
            "test-thread",
            messagesToReturn: [testMessage]);

        using var cts = new CancellationTokenSource();
        var runTask = agent.RunAsync(cts.Token);

        // Act
        var userInput = new UserInput(
            [new TextMessage { Text = "Hello", Role = Role.User }],
            InputId: "test-input");

        var receivedMessages = new List<IMessage>();
        await foreach (var msg in agent.ExecuteRunAsync(userInput, cts.Token))
        {
            receivedMessages.Add(msg);
        }

        // Assert
        receivedMessages.Should().NotBeEmpty();
        receivedMessages.OfType<RunAssignmentMessage>().Should().NotBeEmpty();
        receivedMessages.OfType<TextMessage>().Should().Contain(m => m.Text == "Response");
        receivedMessages.OfType<RunCompletedMessage>().Should().NotBeEmpty();

        // Cleanup
        await cts.CancelAsync();
        await agent.StopAsync();
        await agent.DisposeAsync();
    }

    [Fact]
    public async Task StopAsync_StopsRunningLoop()
    {
        // Arrange
        var agent = new TestMultiTurnAgent("test-thread");
        using var cts = new CancellationTokenSource();
        var runTask = agent.RunAsync(cts.Token);

        agent.IsRunning.Should().BeTrue();

        // Act
        await agent.StopAsync();

        // Assert
        agent.IsRunning.Should().BeFalse();

        // Cleanup
        await agent.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_CleansUpResources()
    {
        // Arrange
        var agent = new TestMultiTurnAgent("test-thread");
        using var cts = new CancellationTokenSource();
        _ = agent.RunAsync(cts.Token);

        // Act
        await agent.DisposeAsync();

        // Assert
        agent.IsRunning.Should().BeFalse();

        // Calling dispose again should not throw
        await agent.DisposeAsync();
    }

    [Fact]
    public async Task RunAsync_ThrowsWhenAlreadyRunning()
    {
        // Arrange
        var agent = new TestMultiTurnAgent("test-thread");
        using var cts = new CancellationTokenSource();
        _ = agent.RunAsync(cts.Token);

        // Act & Assert
        var act = () => agent.RunAsync(cts.Token);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already running*");

        // Cleanup
        await cts.CancelAsync();
        await agent.DisposeAsync();
    }

    [Fact]
    public async Task ThreadId_ReturnsConfiguredValue()
    {
        // Arrange
        var expectedThreadId = "my-unique-thread-id";
        await using var agent = new TestMultiTurnAgent(expectedThreadId);

        // Act & Assert
        agent.ThreadId.Should().Be(expectedThreadId);
    }

    [Fact]
    public async Task CurrentRunId_UpdatesDuringExecution()
    {
        // Arrange
        var agent = new TestMultiTurnAgent("test-thread");
        using var cts = new CancellationTokenSource();
        var runTask = agent.RunAsync(cts.Token);

        // Initially null
        agent.CurrentRunId.Should().BeNull();

        // Act
        var messages = new List<IMessage>
        {
            new TextMessage { Text = "Hello", Role = Role.User },
        };
        await agent.SendAsync(messages);

        // Give time for processing to start and complete
        await Task.Delay(500);

        // After completion, should be null again
        agent.CurrentRunId.Should().BeNull();

        // Cleanup
        await cts.CancelAsync();
        await agent.DisposeAsync();
    }
}
