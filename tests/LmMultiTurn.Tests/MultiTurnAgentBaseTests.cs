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
        private readonly bool _stripReceiptIdsFromAssignment;
        private readonly TimeSpan _fallbackGracePeriod;

        public int ExecuteCallCount { get; private set; }
        public string? LastRunId { get; private set; }
        public string? LastGenerationId { get; private set; }

        /// <summary>Test-only window into the protected conversation history, used to assert recovery.</summary>
        public IReadOnlyList<IMessage> SnapshotHistoryForTest() => GetHistorySnapshot();

        public TestMultiTurnAgent(
            string threadId,
            List<IMessage>? messagesToReturn = null,
            bool shouldFork = false,
            string? systemPrompt = null,
            ILogger? logger = null,
            IConversationStore? store = null,
            bool stripReceiptIdsFromAssignment = false,
            TimeSpan? fallbackGracePeriod = null)
            : base(threadId, systemPrompt, store: store, logger: logger)
        {
            _messagesToReturn = messagesToReturn ?? [];
            _stripReceiptIdsFromAssignment = stripReceiptIdsFromAssignment;
            _fallbackGracePeriod = fallbackGracePeriod ?? TimeSpan.FromMilliseconds(100);
            _ = shouldFork; // No longer used but kept for API compatibility
        }

        protected override TimeSpan FallbackGracePeriod => _fallbackGracePeriod;

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

                // Optionally strip InputIds to model implementations (e.g.,
                // ClaudeAgentLoop's dequeue-deferred publisher) that may publish a
                // RunAssignmentMessage that doesn't list the caller's receipt.
                var publishedAssignment = _stripReceiptIdsFromAssignment
                    ? assignment with { InputIds = [] }
                    : assignment;

                await PublishToAllAsync(new RunAssignmentMessage
                {
                    Assignment = publishedAssignment,
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
    public async Task ExecuteRunAsync_TerminatesViaFallback_WhenAssignmentMissesReceiptId()
    {
        // Arrange: an agent that simulates an implementation publishing a
        // RunAssignmentMessage with empty InputIds (e.g., ClaudeAgentLoop's
        // dequeue-deferred publish path missing the dequeue signal).
        var testMessage = new TextMessage { Text = "Response", Role = Role.Assistant };
        var agent = new TestMultiTurnAgent(
            "test-thread",
            messagesToReturn: [testMessage],
            stripReceiptIdsFromAssignment: true,
            fallbackGracePeriod: TimeSpan.FromMilliseconds(100));

        using var cts = new CancellationTokenSource();
        var runTask = agent.RunAsync(cts.Token);

        var userInput = new UserInput(
            [new TextMessage { Text = "Hello", Role = Role.User }],
            InputId: "fallback-test-input");

        // Act: enumerate ExecuteRunAsync. Wraps in WaitAsync so a hang fails the
        // test cleanly with TimeoutException instead of bleeding into xUnit's
        // outer timeout.
        var receivedMessages = new List<IMessage>();
        var executeTask = Task.Run(async () =>
        {
            await foreach (var msg in agent.ExecuteRunAsync(userInput, cts.Token))
            {
                receivedMessages.Add(msg);
            }
        });

        await executeTask.WaitAsync(TimeSpan.FromSeconds(5));

        receivedMessages.OfType<RunAssignmentMessage>().Should().NotBeEmpty();
        receivedMessages.OfType<RunCompletedMessage>().Should().NotBeEmpty();

        // Cleanup
        await cts.CancelAsync();
        await agent.StopAsync();
        await agent.DisposeAsync();
    }

    [Fact]
    public async Task ExecuteRunAsync_DoesNotTerminateOnPriorRunCompletion_WhenOurRunStillPending()
    {
        // Regression: the deferred fallback must NOT trip on a prior in-flight
        // run's completion before our run has even started. The agent here
        // strips receipt ids on the FIRST run only — modeling: a prior run was
        // already in flight when we subscribed (its RunAssignmentMessage came
        // through but didn't list our receipt because we hadn't sent yet), then
        // our run starts and is correctly receipt-correlated. Without the
        // deferred-fallback grace logic, the immediate fallback would fire on
        // run #1's completion and yield-break before run #2 (our run) ran.
        var firstResponse = new TextMessage { Text = "First run response", Role = Role.Assistant };
        var secondResponse = new TextMessage { Text = "Second run response", Role = Role.Assistant };
        var agent = new TwoRunTestAgent(
            "test-thread",
            firstRunMessages: [firstResponse],
            secondRunMessages: [secondResponse],
            stripReceiptOnFirstRun: true,
            fallbackGracePeriod: TimeSpan.FromMilliseconds(200));

        using var cts = new CancellationTokenSource();
        var runTask = agent.RunAsync(cts.Token);

        // Pre-queue an input that will become run #1 BEFORE we subscribe via
        // ExecuteRunAsync. This input belongs to a different caller (us, here,
        // simulating concurrent callers).
        await agent.SendAsync(
            [new TextMessage { Text = "First", Role = Role.User }],
            inputId: "prior-input");

        // Brief delay so run #1 starts and its assignment is published.
        await Task.Delay(50);

        var userInput = new UserInput(
            [new TextMessage { Text = "Second", Role = Role.User }],
            InputId: "our-input");

        var receivedMessages = new List<IMessage>();
        var executeTask = Task.Run(async () =>
        {
            await foreach (var msg in agent.ExecuteRunAsync(userInput, cts.Token))
            {
                receivedMessages.Add(msg);
            }
        });

        await executeTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Must have observed BOTH responses — the iterator should not have
        // terminated on run #1's completion.
        receivedMessages.OfType<TextMessage>().Should().Contain(m => m.Text == "Second run response",
            "ExecuteRunAsync must wait for our actual run, not yield-break on a prior run's completion");

        await cts.CancelAsync();
        await agent.StopAsync();
        await agent.DisposeAsync();
    }

    /// <summary>
    /// Test agent that distinguishes the first run from later runs so we can
    /// model a prior in-flight run that did not include the caller's receipt.
    /// </summary>
    private sealed class TwoRunTestAgent : MultiTurnAgentBase
    {
        private readonly List<IMessage> _firstRunMessages;
        private readonly List<IMessage> _secondRunMessages;
        private readonly bool _stripReceiptOnFirstRun;
        private readonly TimeSpan _fallbackGracePeriod;
        private int _runIndex;

        public TwoRunTestAgent(
            string threadId,
            List<IMessage> firstRunMessages,
            List<IMessage> secondRunMessages,
            bool stripReceiptOnFirstRun,
            TimeSpan fallbackGracePeriod)
            : base(threadId)
        {
            _firstRunMessages = firstRunMessages;
            _secondRunMessages = secondRunMessages;
            _stripReceiptOnFirstRun = stripReceiptOnFirstRun;
            _fallbackGracePeriod = fallbackGracePeriod;
        }

        protected override TimeSpan FallbackGracePeriod => _fallbackGracePeriod;

        protected override async Task RunLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                if (!await InputReader.WaitToReadAsync(ct))
                {
                    break;
                }

                TryDrainInputs(out var batch);
                if (batch.Count == 0)
                {
                    continue;
                }

                _runIndex++;
                var assignment = StartRun(batch);
                var stripReceipts = _stripReceiptOnFirstRun && _runIndex == 1;
                var publishedAssignment = stripReceipts
                    ? assignment with { InputIds = [] }
                    : assignment;

                await PublishToAllAsync(new RunAssignmentMessage
                {
                    Assignment = publishedAssignment,
                    ThreadId = ThreadId,
                }, ct);

                var messagesForThisRun = _runIndex == 1 ? _firstRunMessages : _secondRunMessages;
                try
                {
                    foreach (var msg in messagesForThisRun)
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

    #region History Recovery Tests

    [Fact]
    public async Task RunAsync_RecoversPersistedHistory_FromStore_WithoutExplicitRecoverCall()
    {
        // Regression: the agent pool builds a loop and starts it via RunAsync — it never calls
        // RecoverAsync explicitly. After an app restart the in-memory history is empty, so unless
        // RunAsync rehydrates the persisted conversation, the LLM loses ALL prior context even
        // though every message is still on disk (symptom: "the model doesn't have older messages").
        var store = new InMemoryConversationStore();
        var threadId = "test-thread-history-recovery";
        const string runId = "prior-run";

        var priorMessages = new List<IMessage>
        {
            new TextMessage { Text = "My name is Alice.", Role = Role.User, GenerationId = "g1", RunId = runId },
            new TextMessage
            {
                Text = "Nice to meet you, Alice.",
                Role = Role.Assistant,
                GenerationId = "g2",
                RunId = runId,
            },
        };
        await store.AppendMessagesAsync(
            threadId,
            MessagePersistenceConverter.ToPersistedMessages(priorMessages, threadId, runId));
        await store.SaveMetadataAsync(
            threadId,
            new ThreadMetadata
            {
                ThreadId = threadId,
                LatestRunId = runId,
                LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });

        var agent = new TestMultiTurnAgent(threadId, store: store);
        using var cts = new CancellationTokenSource();

        // Act: start the loop exactly as the pool does — RunAsync, NOT an explicit RecoverAsync.
        _ = agent.RunAsync(cts.Token);

        // Recovery runs at loop startup; poll the working history until it rehydrates (or time out).
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (agent.SnapshotHistoryForTest().Count < priorMessages.Count && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        // Assert: the prior conversation is back in the loop's history, so the next turn resends
        // it to the LLM.
        var history = agent.SnapshotHistoryForTest();
        history.OfType<TextMessage>().Select(m => m.Text)
            .Should()
            .Contain("My name is Alice.")
            .And.Contain("Nice to meet you, Alice.");

        await cts.CancelAsync();
        await agent.DisposeAsync();
    }

    [Fact]
    public async Task RecoverAsync_TransientLoadFailure_DoesNotPoisonRecovery_AndRetrySucceeds()
    {
        // Regression: _historyRecovered must NOT be set when LoadMessagesAsync/LoadMetadataAsync
        // throws (transient store/IO failure). If the flag were set up front, the failed
        // RecoverAsync would permanently block both RunAsync's startup recovery and any later
        // explicit RecoverAsync, leaving the agent stuck with empty history even after the store
        // recovers. Here the store throws on the FIRST LoadMessagesAsync call then succeeds; the
        // first RecoverAsync must throw without marking recovered, and a SECOND RecoverAsync must
        // restore history.
        var inner = new InMemoryConversationStore();
        var threadId = "test-thread-transient-recovery";
        const string runId = "prior-run";

        var priorMessages = new List<IMessage>
        {
            new TextMessage { Text = "My name is Bob.", Role = Role.User, GenerationId = "g1", RunId = runId },
            new TextMessage
            {
                Text = "Hello, Bob.",
                Role = Role.Assistant,
                GenerationId = "g2",
                RunId = runId,
            },
        };
        await inner.AppendMessagesAsync(
            threadId,
            MessagePersistenceConverter.ToPersistedMessages(priorMessages, threadId, runId));
        await inner.SaveMetadataAsync(
            threadId,
            new ThreadMetadata
            {
                ThreadId = threadId,
                LatestRunId = runId,
                LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });

        var store = new FlakyLoadMessagesStore(inner, failuresBeforeSuccess: 1);
        var agent = new TestMultiTurnAgent(threadId, store: store);

        // Act 1: first RecoverAsync hits the transient failure and throws.
        var firstRecover = async () => await agent.RecoverAsync();
        await firstRecover.Should().ThrowAsync<IOException>();

        // The flag must NOT be stuck — history is still empty and a retry is allowed.
        agent.SnapshotHistoryForTest().Should().BeEmpty(
            "a failed recovery must not leave partially-restored history");

        // Act 2: a later RunAsync (as the agent pool does — no explicit RecoverAsync) must still
        // perform startup recovery, because the failed attempt must not have set _historyRecovered.
        // The store now succeeds on its second LoadMessagesAsync call.
        using var cts = new CancellationTokenSource();
        _ = agent.RunAsync(cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (agent.SnapshotHistoryForTest().Count < priorMessages.Count && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        // Assert: the transient failure did not poison recovery — RunAsync rehydrated the history.
        agent.SnapshotHistoryForTest().OfType<TextMessage>().Select(m => m.Text)
            .Should()
            .Contain("My name is Bob.")
            .And.Contain("Hello, Bob.");

        await cts.CancelAsync();
        await agent.DisposeAsync();
    }

    /// <summary>
    /// Decorator store that throws on the first N <see cref="LoadMessagesAsync"/> calls (modeling a
    /// transient store/IO failure) then delegates to the inner store. Used to prove that a failed
    /// recovery does not permanently poison <c>_historyRecovered</c>.
    /// </summary>
    private sealed class FlakyLoadMessagesStore : IConversationStore
    {
        private readonly IConversationStore _inner;
        private int _remainingFailures;

        public FlakyLoadMessagesStore(IConversationStore inner, int failuresBeforeSuccess)
        {
            _inner = inner;
            _remainingFailures = failuresBeforeSuccess;
        }

        public Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(string threadId, CancellationToken ct = default)
        {
            if (_remainingFailures > 0)
            {
                _remainingFailures--;
                throw new IOException("Transient store failure (simulated).");
            }

            return _inner.LoadMessagesAsync(threadId, ct);
        }

        public Task AppendMessagesAsync(string threadId, IReadOnlyList<PersistedMessage> messages, CancellationToken ct = default)
            => _inner.AppendMessagesAsync(threadId, messages, ct);

        public Task ReplaceMessageAsync(string threadId, PersistedMessage replacement, CancellationToken ct = default)
            => _inner.ReplaceMessageAsync(threadId, replacement, ct);

        public Task SaveMetadataAsync(string threadId, ThreadMetadata metadata, CancellationToken ct = default)
            => _inner.SaveMetadataAsync(threadId, metadata, ct);

        public Task<ThreadMetadata?> LoadMetadataAsync(string threadId, CancellationToken ct = default)
            => _inner.LoadMetadataAsync(threadId, ct);

        public Task DeleteThreadAsync(string threadId, CancellationToken ct = default)
            => _inner.DeleteThreadAsync(threadId, ct);

        public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(int limit = 50, int offset = 0, CancellationToken ct = default)
            => _inner.ListThreadsAsync(limit, offset, ct);
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
