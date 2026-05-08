using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
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

        public int ExecuteCallCount { get; private set; }
        public string? LastRunId { get; private set; }
        public string? LastGenerationId { get; private set; }

        public TestMultiTurnAgent(
            string threadId,
            List<IMessage>? messagesToReturn = null,
            bool shouldFork = false,
            string? systemPrompt = null,
            ILogger? logger = null,
            IConversationStore? store = null)
            : base(threadId, systemPrompt, store: store, logger: logger)
        {
            _messagesToReturn = messagesToReturn ?? [];
            _ = shouldFork; // No longer used but kept for API compatibility
        }

        protected override async Task RunLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                // Wait for at least one input
                if (!await InputReader.WaitToReadAsync(ct))
                {
                    break; // Channel completed
                }

                // Drain all available inputs
                TryDrainInputs(out var batch);
                if (batch.Count == 0)
                {
                    continue;
                }

                // Start run
                var assignment = StartRun(batch);
                ExecuteCallCount++;
                LastRunId = assignment.RunId;
                LastGenerationId = assignment.GenerationId;

                await PublishToAllAsync(new RunAssignmentMessage
                {
                    Assignment = assignment,
                    ThreadId = ThreadId,
                }, ct);

                try
                {
                    // Publish the test messages
                    foreach (var msg in _messagesToReturn)
                    {
                        await PublishToAllAsync(msg, ct);
                    }
                }
                finally
                {
                    await CompleteRunAsync(assignment.RunId, assignment.GenerationId, false, null, 0, ct: ct);
                }
            }
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
        var receipt = await agent.SendAsync(messages, "input-1");

        // Assert - SendAsync now returns SendReceipt (fire-and-forget)
        receipt.Should().NotBeNull();
        receipt.ReceiptId.Should().NotBeNullOrEmpty();
        receipt.InputId.Should().Be("input-1");
        receipt.QueuedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));

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

    #region Fire-and-Forget Behavior Tests

    [Fact]
    public async Task SendAsync_ReturnsImmediately_BeforeProcessingCompletes()
    {
        // Arrange
        var agent = new TestMultiTurnAgent("test-thread");
        using var cts = new CancellationTokenSource();
        var runTask = agent.RunAsync(cts.Token);

        // Act - send multiple messages quickly
        var startTime = DateTimeOffset.UtcNow;
        var receipt1 = await agent.SendAsync([new TextMessage { Text = "Hello 1", Role = Role.User }], "input-1");
        var receipt2 = await agent.SendAsync([new TextMessage { Text = "Hello 2", Role = Role.User }], "input-2");
        var receipt3 = await agent.SendAsync([new TextMessage { Text = "Hello 3", Role = Role.User }], "input-3");
        var endTime = DateTimeOffset.UtcNow;

        // Assert - all receipts should be returned almost immediately (non-blocking)
        (endTime - startTime).Should().BeLessThan(TimeSpan.FromMilliseconds(100),
            "SendAsync should return immediately without waiting for processing");

        receipt1.ReceiptId.Should().NotBe(receipt2.ReceiptId);
        receipt2.ReceiptId.Should().NotBe(receipt3.ReceiptId);

        // Cleanup
        await cts.CancelAsync();
        await agent.DisposeAsync();
    }

    [Fact]
    public async Task SendReceipt_CanBeCorrelatedToRunAssignment_ViaInputIds()
    {
        // Arrange
        var agent = new TestMultiTurnAgent("test-thread");
        var receivedMessages = new List<IMessage>();

        using var cts = new CancellationTokenSource();
        var runTask = agent.RunAsync(cts.Token);

        // Subscribe to output
        var subscribeTask = Task.Run(async () =>
        {
            await foreach (var msg in agent.SubscribeAsync(cts.Token))
            {
                receivedMessages.Add(msg);
            }
        });

        await Task.Delay(100); // Give time for subscription

        // Act
        var receipt = await agent.SendAsync(
            [new TextMessage { Text = "Hello", Role = Role.User }],
            "correlation-test-input");

        // Wait for processing
        await Task.Delay(500);

        // Assert - RunAssignmentMessage should contain our receipt ID
        var runAssignments = receivedMessages.OfType<RunAssignmentMessage>().ToList();
        runAssignments.Should().NotBeEmpty();

        var assignment = runAssignments.First();
        assignment.Assignment.InputIds.Should().NotBeNull();
        assignment.Assignment.InputIds.Should().Contain(receipt.ReceiptId,
            "RunAssignment.InputIds should include the ReceiptId from SendReceipt");

        // Cleanup
        await cts.CancelAsync();
        await agent.DisposeAsync();
    }

    [Fact]
    public async Task MultipleSendsBeforeProcessing_AreBatchedIntoSingleRun()
    {
        // Arrange - Create agent that doesn't start immediately
        var agent = new TestMultiTurnAgent("test-thread");
        var receivedMessages = new List<IMessage>();

        using var cts = new CancellationTokenSource();

        // Queue multiple messages BEFORE starting the loop
        var receipt1 = await agent.SendAsync([new TextMessage { Text = "First", Role = Role.User }], "batch-1");
        var receipt2 = await agent.SendAsync([new TextMessage { Text = "Second", Role = Role.User }], "batch-2");

        // Now subscribe and start
        var subscribeTask = Task.Run(async () =>
        {
            await foreach (var msg in agent.SubscribeAsync(cts.Token))
            {
                receivedMessages.Add(msg);
            }
        });

        await Task.Delay(50);

        // Start the loop - it should batch all queued inputs
        var runTask = agent.RunAsync(cts.Token);

        // Wait for processing
        await Task.Delay(500);

        // Assert - Should have exactly one run with both receipts
        var runAssignments = receivedMessages.OfType<RunAssignmentMessage>().ToList();
        runAssignments.Should().HaveCount(1, "Multiple queued inputs should be batched into a single run");

        var assignment = runAssignments.First();
        assignment.Assignment.InputIds.Should().Contain(receipt1.ReceiptId);
        assignment.Assignment.InputIds.Should().Contain(receipt2.ReceiptId);

        // Cleanup
        await cts.CancelAsync();
        await agent.DisposeAsync();
    }

    [Fact]
    public async Task SendReceipt_InputId_IsPreserved()
    {
        // Arrange
        var agent = new TestMultiTurnAgent("test-thread");

        // Act
        var receipt1 = await agent.SendAsync(
            [new TextMessage { Text = "Test", Role = Role.User }],
            inputId: "my-custom-id");

        var receipt2 = await agent.SendAsync(
            [new TextMessage { Text = "Test", Role = Role.User }],
            inputId: null);

        // Assert
        receipt1.InputId.Should().Be("my-custom-id");
        receipt2.InputId.Should().BeNull();

        // Cleanup
        await agent.DisposeAsync();
    }

    [Fact]
    public async Task SendReceipt_QueuedAt_IsSetCorrectly()
    {
        // Arrange
        var agent = new TestMultiTurnAgent("test-thread");
        var beforeSend = DateTimeOffset.UtcNow;

        // Act
        var receipt = await agent.SendAsync([new TextMessage { Text = "Test", Role = Role.User }]);
        var afterSend = DateTimeOffset.UtcNow;

        // Assert
        receipt.QueuedAt.Should().BeOnOrAfter(beforeSend);
        receipt.QueuedAt.Should().BeOnOrBefore(afterSend);

        // Cleanup
        await agent.DisposeAsync();
    }

    #endregion

    #region Metadata Preservation Tests

    [Fact]
    public async Task UpdateMetadataAsync_PreservesExistingProperties()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var threadId = "test-thread-props";

        // Pre-populate metadata with Properties
        var initialMetadata = new ThreadMetadata
        {
            ThreadId = threadId,
            LatestRunId = "old-run",
            LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Properties = new Dictionary<string, object>
            {
                ["title"] = "My Conversation Title",
                ["preview"] = "First message preview",
            }.ToImmutableDictionary(),
        };
        await store.SaveMetadataAsync(threadId, initialMetadata);

        var agent = new TestMultiTurnAgent(threadId, store: store);

        using var cts = new CancellationTokenSource();
        var runTask = agent.RunAsync(cts.Token);

        // Act - Send a message to trigger run completion and metadata update
        var messages = new List<IMessage>
        {
            new TextMessage { Text = "Hello", Role = Role.User },
        };
        await agent.SendAsync(messages);

        // Wait for processing to complete
        await Task.Delay(500);

        // Assert - Properties should be preserved after the run updates metadata
        var updatedMetadata = await store.LoadMetadataAsync(threadId);
        updatedMetadata.Should().NotBeNull();
        updatedMetadata!.Properties.Should().NotBeNull();
        updatedMetadata.Properties!["title"].Should().Be("My Conversation Title");
        updatedMetadata.Properties["preview"].Should().Be("First message preview");

        // Cleanup
        await cts.CancelAsync();
        await agent.DisposeAsync();
    }

    [Fact]
    public async Task UpdateMetadataAsync_PreservesExistingSessionMappings()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var threadId = "test-thread-sessions";

        // Pre-populate metadata with SessionMappings
        var initialMetadata = new ThreadMetadata
        {
            ThreadId = threadId,
            LatestRunId = "old-run",
            LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SessionMappings = new Dictionary<string, string>
            {
                ["session-1"] = "external-id-1",
                ["session-2"] = "external-id-2",
            },
        };
        await store.SaveMetadataAsync(threadId, initialMetadata);

        var agent = new TestMultiTurnAgent(threadId, store: store);

        using var cts = new CancellationTokenSource();
        var runTask = agent.RunAsync(cts.Token);

        // Act - Send a message to trigger run completion and metadata update
        var messages = new List<IMessage>
        {
            new TextMessage { Text = "Hello", Role = Role.User },
        };
        await agent.SendAsync(messages);

        // Wait for processing to complete
        await Task.Delay(500);

        // Assert - SessionMappings should be preserved after the run updates metadata
        var updatedMetadata = await store.LoadMetadataAsync(threadId);
        updatedMetadata.Should().NotBeNull();
        updatedMetadata!.SessionMappings.Should().NotBeNull();
        updatedMetadata.SessionMappings!["session-1"].Should().Be("external-id-1");
        updatedMetadata.SessionMappings["session-2"].Should().Be("external-id-2");

        // Cleanup
        await cts.CancelAsync();
        await agent.DisposeAsync();
    }

    [Fact]
    public async Task UpdateMetadataAsync_UpdatesLatestRunId_WhilePreservingProperties()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var threadId = "test-thread-run-update";

        // Pre-populate metadata with Properties
        var initialMetadata = new ThreadMetadata
        {
            ThreadId = threadId,
            LatestRunId = "old-run-id",
            LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Properties = new Dictionary<string, object>
            {
                ["title"] = "Preserved Title",
            }.ToImmutableDictionary(),
        };
        await store.SaveMetadataAsync(threadId, initialMetadata);

        var agent = new TestMultiTurnAgent(threadId, store: store);

        using var cts = new CancellationTokenSource();
        var runTask = agent.RunAsync(cts.Token);

        // Act - Send a message to trigger run completion and metadata update
        var messages = new List<IMessage>
        {
            new TextMessage { Text = "Hello", Role = Role.User },
        };
        await agent.SendAsync(messages);

        // Wait for processing to complete
        await Task.Delay(500);

        // Assert - LatestRunId should be updated, but Properties preserved
        var updatedMetadata = await store.LoadMetadataAsync(threadId);
        updatedMetadata.Should().NotBeNull();
        updatedMetadata!.LatestRunId.Should().NotBe("old-run-id", "LatestRunId should be updated");
        updatedMetadata.Properties.Should().NotBeNull();
        updatedMetadata.Properties!["title"].Should().Be("Preserved Title");

        // Cleanup
        await cts.CancelAsync();
        await agent.DisposeAsync();
    }

    #endregion
}
