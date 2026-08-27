using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Identity;
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
        private readonly Task? _startGate;

        public int ExecuteCallCount { get; private set; }
        public string? LastRunId { get; private set; }
        public string? LastGenerationId { get; private set; }

        /// <summary>Test-only window into the protected conversation history, used to assert recovery.</summary>
        public IReadOnlyList<IMessage> SnapshotHistoryForTest() => GetHistorySnapshot();

        /// <summary>
        /// Test-only door onto the protected fan-out, so the publish path can be driven directly
        /// instead of through a run. Publishing IS the code under test in the subscriber-race test.
        /// </summary>
        public ValueTask PublishForTest(IMessage message, CancellationToken ct) =>
            PublishToAllAsync(message, ct);

        /// <summary>Test-only window into the protected pending-input count, used to prove inputs are
        /// queued (not yet drained) while the run loop is deterministically stalled.</summary>
        public int PendingInputCountForTest => PendingInputCount;

        public TestMultiTurnAgent(
            string threadId,
            List<IMessage>? messagesToReturn = null,
            bool shouldFork = false,
            string? systemPrompt = null,
            ILogger? logger = null,
            IConversationStore? store = null,
            bool stripReceiptIdsFromAssignment = false,
            TimeSpan? fallbackGracePeriod = null,
            Task? startGate = null)
            : base(threadId, systemPrompt, store: store, logger: logger)
        {
            _messagesToReturn = messagesToReturn ?? [];
            _stripReceiptIdsFromAssignment = stripReceiptIdsFromAssignment;
            _fallbackGracePeriod = fallbackGracePeriod ?? TimeSpan.FromMilliseconds(100);
            _ = shouldFork; // No longer used but kept for API compatibility
            _startGate = startGate;
        }

        protected override TimeSpan FallbackGracePeriod => _fallbackGracePeriod;

        protected override async Task RunLoopAsync(CancellationToken ct)
        {
            // Test-only deterministic stall: when set, the loop cannot make ANY progress
            // (not even reading the input channel) until the gate is released or ct fires.
            // This lets a test prove a caller-facing method returns without waiting on
            // run-loop processing, with no reliance on wall-clock timing.
            if (_startGate != null)
            {
                await _startGate.WaitAsync(ct);
            }

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
                var assignment = await StartRunAsync(batch, ct: ct);
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

    /// <summary>
    /// Task 5 (fix round 3) — <see cref="SendReceipt.SpawningSuppressed"/> is an ENFORCEMENT statement, not an
    /// echo. An agent that does not override <c>EnforcesSpawnSuppression</c> has no spawn machinery to police,
    /// so accepting a flagged input must leave the receipt false: a host relays that field to its caller as a
    /// guarantee, and echoing the request would manufacture one nothing is keeping.
    /// </summary>
    [Fact]
    public async Task TrySendAsync_DoesNotClaimSpawnSuppression_WhenTheAgentCannotEnforceIt()
    {
        await using var agent = new TestMultiTurnAgent("thread-enforcement");

        var receipt = await agent.TrySendAsync(new UserInput(
            [new TextMessage { Text = "synthesize", Role = Role.User }],
            SuppressSubAgentSpawning: true));

        receipt.Should().NotBeNull();
        receipt!.SpawningSuppressed.Should().BeFalse(
            "this agent accepts the flag but has nothing that will act on it");
    }

    /// <summary>
    /// Task 5 (fix round 4) — the same invariant on the blocking send. Both overloads hand back a
    /// <see cref="SendReceipt"/> a host relays as a guarantee, so a caller must not be able to get an honest
    /// answer from one path and a silently-unstamped one from the other by picking a different method.
    /// </summary>
    [Fact]
    public async Task SendAsync_MakesTheSameSpawnSuppressionStatementAsTrySendAsync()
    {
        await using var agent = new TestMultiTurnAgent("thread-enforcement-send");

        var receipt = await agent.SendAsync(new UserInput(
            [new TextMessage { Text = "synthesize", Role = Role.User }],
            SuppressSubAgentSpawning: true));

        receipt.SpawningSuppressed.Should().BeFalse(
            "SendAsync must state enforcement exactly as TrySendAsync does");
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
                var assignment = await StartRunAsync(batch, ct: ct);
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
    public async Task PublishToAll_WhileSubscribersLeave_NeverWritesToATornSnapshot()
    {
        // The publisher copies its subscriber map before fanning out, and copying a
        // ConcurrentDictionary is not one atomic step: a List/collection-expression copy reads
        // ICollection.Count under the dictionary's locks and then calls CopyTo under them AGAIN.
        // A subscriber that unsubscribes between the two makes CopyTo fill fewer slots than the
        // length already committed to, so the copy ends in default(KeyValuePair) — a NULL channel
        // that the fan-out immediately dereferences.
        //
        // That is not theoretical: it surfaced as an intermittent NullReferenceException thrown out
        // of an UNRELATED test's DisposeAsync (a delayed child run publishing while the loop shut
        // down), i.e. as noise in someone else's failure, which is how a real race hides. Removals
        // are the common case here, not an exotic one — every client disconnect is one, and so is
        // the slow-subscriber drop the publisher itself performs.
        //
        // There is no seam to force that interleaving, so it is reproduced by volume: subscribers
        // that keep leaving while a publisher never stops. Nothing sleeps — the work is the wait.
        const int Drainers = 16;
        const int Publishes = 20_000;

        await using var agent = new TestMultiTurnAgent("publish-race");

        // A ceiling, not a delay: if the fan-out ever wedges, the test fails instead of hanging.
        using var life = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Long-lived readers, so every snapshot has real entries to copy. They re-subscribe after a
        // drop because the publisher is entitled to evict a slow one, and an empty map races nothing.
        using var drainerLife = CancellationTokenSource.CreateLinkedTokenSource(life.Token);
        var drainers = Enumerable.Range(0, Drainers)
            .Select(_ => Task.Run(() => DrainUntilCancelledAsync(agent, drainerLife.Token)))
            .ToArray();

        // The churn: a subscriber joining and leaving continuously, so a removal is always in
        // flight against the publisher's copy.
        using var churnLife = CancellationTokenSource.CreateLinkedTokenSource(life.Token);
        var churn = Task.Run(async () =>
        {
            while (!churnLife.IsCancellationRequested)
            {
                using var solo = CancellationTokenSource.CreateLinkedTokenSource(churnLife.Token);
                var reader = Task.Run(() => DrainUntilCancelledAsync(agent, solo.Token));

                await Task.Yield();
                await solo.CancelAsync();
                await reader;
            }
        });

        var message = new TextMessage { Text = "fan-out", Role = Role.Assistant };
        var publish = async () =>
        {
            for (var i = 0; i < Publishes; i++)
            {
                await agent.PublishForTest(message, life.Token);
            }
        };

        await publish.Should().NotThrowAsync(
            "a subscriber leaving mid-copy must never leave a null channel in the publisher's snapshot");

        await churnLife.CancelAsync();
        await churn;
        await drainerLife.CancelAsync();
        await Task.WhenAll(drainers);
    }

    /// <summary>
    /// Reads until cancelled, re-subscribing if the publisher drops the subscriber for being slow.
    /// Consuming is all that matters — the values are the test's noise, the churn is its signal.
    /// </summary>
    private static async Task DrainUntilCancelledAsync(
        TestMultiTurnAgent agent, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await foreach (var _ in agent.SubscribeAsync(ct))
                {
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Leaving is the point of this helper, not a failure.
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
        // Arrange - a gate that deterministically stalls the run loop before it can make
        // ANY progress (it will not even read the input channel) until released. This
        // proves SendAsync returns without waiting on run-loop processing without relying
        // on a wall-clock threshold, which is brittle under system load (see history: this
        // test previously asserted elapsed time < 100ms and flaked at 152ms during a full,
        // heavily-loaded solution test run even though SendAsync performs no blocking I/O).
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new TestMultiTurnAgent("test-thread", startGate: startGate.Task);
        using var cts = new CancellationTokenSource();
        var runTask = agent.RunAsync(cts.Token);

        // Act - send multiple messages while the run loop is provably stalled
        var receipt1 = await agent.SendAsync([new TextMessage { Text = "Hello 1", Role = Role.User }], "input-1");
        var receipt2 = await agent.SendAsync([new TextMessage { Text = "Hello 2", Role = Role.User }], "input-2");
        var receipt3 = await agent.SendAsync([new TextMessage { Text = "Hello 3", Role = Role.User }], "input-3");

        // Assert - deterministic proof: all three sends completed while the run loop has
        // made zero progress (it never even started draining the channel), so SendAsync
        // cannot have waited on any processing to complete.
        agent.ExecuteCallCount.Should().Be(0,
            "SendAsync must return before the run loop even begins processing, not merely quickly");
        agent.PendingInputCountForTest.Should().Be(3,
            "all three inputs must be sitting in the channel, unread, while the run loop is stalled");

        receipt1.ReceiptId.Should().NotBe(receipt2.ReceiptId);
        receipt2.ReceiptId.Should().NotBe(receipt3.ReceiptId);

        // Cleanup - release the gate so the loop can drain and finish, then stop it
        startGate.SetResult();
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
    public async Task RunAsync_DoesNotReRecover_AfterExplicitRecoverAsync()
    {
        // Regression for the idempotency guard (_historyRecovered): RestoreHistory APPENDS, so if
        // RunAsync re-ran recovery after a caller had already called RecoverAsync explicitly, the
        // persisted history would be duplicated (2N messages instead of N). The guard must skip the
        // second recovery so history stays at N.
        var store = new InMemoryConversationStore();
        var threadId = "test-thread-no-double-recover";
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

        // Recover explicitly first (as a caller that pre-warms history would).
        var recovered = await agent.RecoverAsync(cts.Token);
        recovered.Should().BeTrue();
        agent.SnapshotHistoryForTest().Count.Should().Be(priorMessages.Count);

        // Act: start the loop. RunAsync's startup recovery must observe the guard and skip, so it
        // does NOT append the same history a second time.
        _ = agent.RunAsync(cts.Token);

        // Give RunAsync's startup path a chance to (incorrectly) re-recover: poll until the loop is
        // running, then confirm the count is still N (never 2N). Await-don't-sleep on the deadline.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!agent.IsRunning && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        // Assert: history was NOT duplicated.
        var history = agent.SnapshotHistoryForTest();
        history.Count.Should().Be(priorMessages.Count);
        history.OfType<TextMessage>().Count(m => m.Text == "My name is Alice.").Should().Be(1);
        history.OfType<TextMessage>().Count(m => m.Text == "Nice to meet you, Alice.").Should().Be(1);

        await cts.CancelAsync();
        await agent.DisposeAsync();
    }

    [Fact]
    public async Task RecoverAsync_SkipsACorruptRecord_AndRestoresHealthySiblings()
    {
        // #489: one undeserializable persisted record must not abort recovery of the healthy records
        // around it. RecoverAsync degrades per-record — it skips the corrupt row (logging its id) and
        // restores every sibling. Because SubAgentManager.RestartRunAsync recovers through this SAME
        // method, a single bad row can no longer abort a sub-agent restart either (the two restore
        // sites now agree). Red-first: before the fix the whole conversion aborts on the first bad
        // row, so RecoverAsync throws JsonException and NO sibling is restored.
        var store = new InMemoryConversationStore();
        var threadId = "test-thread-corrupt-record";
        const string runId = "prior-run";

        // A corrupt row deliberately BETWEEN two healthy ones: a whole-list abort (the pre-fix
        // behavior) would lose the sibling after it, so restoring both proves per-record resilience.
        var healthyBefore =
            MessagePersistenceConverter.ToPersistedMessage(
                new TextMessage
                {
                    Text = "before corrupt",
                    Role = Role.User,
                    GenerationId = "g1",
                    RunId = runId,
                },
                threadId,
                runId
            ) with
            {
                Timestamp = 1,
            };
        var corrupt = healthyBefore with
        {
            Id = "corrupt-record-1",
            Timestamp = 2,
            MessageJson = "{ this is not valid message json",
        };
        var healthyAfter =
            MessagePersistenceConverter.ToPersistedMessage(
                new TextMessage
                {
                    Text = "after corrupt",
                    Role = Role.Assistant,
                    GenerationId = "g2",
                    RunId = runId,
                },
                threadId,
                runId
            ) with
            {
                Timestamp = 3,
            };

        await store.AppendMessagesAsync(threadId, [healthyBefore, corrupt, healthyAfter]);
        await store.SaveMetadataAsync(
            threadId,
            new ThreadMetadata
            {
                ThreadId = threadId,
                LatestRunId = runId,
                LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });

        var agent = new TestMultiTurnAgent(threadId, store: store);

        var recovered = await agent.RecoverAsync();

        recovered.Should().BeTrue();
        var texts = agent.SnapshotHistoryForTest().OfType<TextMessage>().Select(m => m.Text).ToList();
        texts.Should().Contain("before corrupt").And.Contain("after corrupt");
        // Exactly the two healthy siblings — the corrupt row contributes nothing (not a placeholder).
        agent.SnapshotHistoryForTest().Count.Should().Be(2);

        await agent.DisposeAsync();
    }

    [Theory]
    // corruptTheCall: which half of the pair is the damaged row.
    // pluralCallShape: false = the SINGULAR ToolCallMessage production actually persists
    // (MessageTransformationMiddleware splits plural into one-per-call upstream of the turn body);
    // true = the plural ToolsCallMessage, which still reaches the store via the middleware's unsplit
    // passthrough and via the sibling Claude/Codex/Copilot loops. A sweep keyed on only one of the
    // two shapes passes half these cases and no-ops against the other half's stores.
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task RecoverAsync_DropsBothHalvesOfAToolCallPair_WhenEitherHalfIsCorrupt(
        bool corruptTheCall,
        bool pluralCallShape)
    {
        // A tool call and its result are TWO separate persisted rows (MessagePersistenceConverter is
        // strictly 1:1). Skipping one row per #489 therefore ORPHANS its partner, and nothing
        // downstream repairs that: RestoreHistory is a bare AddRange, GetMessagesWithSystemPrompt
        // returns the history unfiltered, and MessageTransformationMiddleware passes an unpaired half
        // through verbatim. Providers reject both shapes (Anthropic: "tool_use ids were found without
        // tool_result blocks", "tool_call_id is not found"), and because the same row fails to
        // deserialize on EVERY recovery the thread would stay wedged across restarts. So recovery
        // enforces the same pairing invariant the write path maintains: neither half survives alone.
        // The healthy TextMessage still comes back — the per-record win of #489 is not regressed.
        var store = new InMemoryConversationStore();
        var threadId =
            $"test-thread-orphan-{(corruptTheCall ? "call" : "result")}-{(pluralCallShape ? "plural" : "singular")}";
        const string runId = "prior-run";
        const string toolCallId = "call-1";

        var healthyText =
            MessagePersistenceConverter.ToPersistedMessage(
                new TextMessage { Text = "healthy text", Role = Role.User, RunId = runId },
                threadId,
                runId
            ) with
            {
                Timestamp = 1,
            };
        IMessage callMessage = pluralCallShape
            ? new ToolsCallMessage
            {
                Role = Role.Assistant,
                RunId = runId,
                ToolCalls =
                [
                    new ToolCall
                    {
                        ToolCallId = toolCallId,
                        FunctionName = "do_thing",
                        FunctionArgs = "{}",
                    },
                ],
            }
            : new ToolCallMessage
            {
                Role = Role.Assistant,
                RunId = runId,
                ToolCallId = toolCallId,
                FunctionName = "do_thing",
                FunctionArgs = "{}",
            };
        var callRow =
            MessagePersistenceConverter.ToPersistedMessage(callMessage, threadId, runId) with
            {
                Timestamp = 2,
            };
        var resultRow =
            MessagePersistenceConverter.ToPersistedMessage(
                new ToolCallResultMessage
                {
                    ToolCallId = toolCallId,
                    ToolName = "do_thing",
                    Result = "ok",
                    RunId = runId,
                },
                threadId,
                runId
            ) with
            {
                Timestamp = 3,
            };

        // Only MessageJson is damaged — MessageType/Id survive, exactly as a bit-rotted row would.
        const string CorruptJson = "{ this is not valid message json";
        if (corruptTheCall)
        {
            callRow = callRow with { MessageJson = CorruptJson };
        }
        else
        {
            resultRow = resultRow with { MessageJson = CorruptJson };
        }

        await store.AppendMessagesAsync(threadId, [healthyText, callRow, resultRow]);
        await store.SaveMetadataAsync(
            threadId,
            new ThreadMetadata
            {
                ThreadId = threadId,
                LatestRunId = runId,
                LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });

        var agent = new TestMultiTurnAgent(threadId, store: store);

        var recovered = await agent.RecoverAsync();

        recovered.Should().BeTrue();
        var history = agent.SnapshotHistoryForTest();

        // NEITHER half may survive — never exactly one.
        history.OfType<ToolCallMessage>().Should().BeEmpty(
            "a tool call whose result was skipped must not reach the provider");
        history.OfType<ToolsCallMessage>().Should().BeEmpty(
            "a tool call whose result was skipped must not reach the provider");
        history.OfType<ToolCallResultMessage>().Should().BeEmpty(
            "a tool result whose call was skipped must not reach the provider");

        // ...and the unrelated healthy row is still restored (#489's per-record win is intact).
        history.OfType<TextMessage>().Select(m => m.Text).Should().ContainSingle()
            .Which.Should().Be("healthy text");
        history.Count.Should().Be(1);

        await agent.DisposeAsync();
    }

    [Theory]
    [InlineData(true)] // the RESULT row was never written — a dangling tool_use
    [InlineData(false)] // the CALL row was never written — a dangling tool_result
    public async Task RecoverAsync_DropsAnUnpairedToolMessage_WhenItsPartnerRowIsSimplyAbsent(
        bool resultRowAbsent)
    {
        // ORIGIN B, with no corruption anywhere. PersistMessageAsync appends one row at a time and
        // swallows an append failure (`catch (Exception ex) { Logger.LogWarning(ex, "Failed to persist
        // message"); }`), so a lost append leaves a permanently half-written tool exchange in the
        // store — a shape that pre-dates the per-record skip entirely. The sweep is blind to WHY a
        // partner is missing, so the same mechanism repairs this route too; a sweep gated on "a record
        // was skipped" would leave this, the older and likelier route, unrepaired.
        var store = new InMemoryConversationStore();
        var threadId = $"test-thread-absent-{(resultRowAbsent ? "result" : "call")}";
        const string runId = "prior-run";
        const string toolCallId = "append-was-lost";

        var text =
            MessagePersistenceConverter.ToPersistedMessage(
                new TextMessage { Text = "healthy text", Role = Role.User, RunId = runId },
                threadId,
                runId
            ) with
            {
                Timestamp = 1,
            };
        IMessage survivingHalf = resultRowAbsent
            ? new ToolCallMessage
            {
                Role = Role.Assistant,
                RunId = runId,
                ToolCallId = toolCallId,
                FunctionName = "do_thing",
                FunctionArgs = "{}",
            }
            : new ToolCallResultMessage
            {
                ToolCallId = toolCallId,
                ToolName = "do_thing",
                Result = "ok",
                RunId = runId,
            };
        var survivingRow =
            MessagePersistenceConverter.ToPersistedMessage(survivingHalf, threadId, runId) with
            {
                Timestamp = 2,
            };

        // The partner row is never appended at all — every row in this store deserializes cleanly.
        await store.AppendMessagesAsync(threadId, [text, survivingRow]);
        await store.SaveMetadataAsync(
            threadId,
            new ThreadMetadata
            {
                ThreadId = threadId,
                LatestRunId = runId,
                LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });

        var agent = new TestMultiTurnAgent(threadId, store: store);

        var recovered = await agent.RecoverAsync();

        recovered.Should().BeTrue();
        var history = agent.SnapshotHistoryForTest();
        history.OfType<ToolCallMessage>().Should().BeEmpty();
        history.OfType<ToolCallResultMessage>().Should().BeEmpty();
        history.OfType<TextMessage>().Select(m => m.Text).Should().ContainSingle()
            .Which.Should().Be("healthy text");

        await agent.DisposeAsync();
    }

    [Fact]
    public async Task RecoverAsync_CascadesTheDrop_WhenLosingOneCallOrphansAnAnsweredSibling()
    {
        // One message may carry SEVERAL calls, so a single pass is not enough. Here call B's answer is
        // missing, which condemns the whole call message — and that in turn orphans call A's result,
        // which was perfectly well paired until the message holding A was dropped. Only a sweep that
        // iterates to a fixed point removes A's result too; a one-pass sweep leaves a dangling
        // tool_result behind and the provider rejects the replay exactly as before.
        var store = new InMemoryConversationStore();
        var threadId = "test-thread-cascading-drop";
        const string runId = "prior-run";

        var text =
            MessagePersistenceConverter.ToPersistedMessage(
                new TextMessage { Text = "healthy text", Role = Role.User, RunId = runId },
                threadId,
                runId
            ) with
            {
                Timestamp = 1,
            };
        var twoCallRow =
            MessagePersistenceConverter.ToPersistedMessage(
                new ToolsCallMessage
                {
                    Role = Role.Assistant,
                    RunId = runId,
                    ToolCalls =
                    [
                        new ToolCall
                        {
                            ToolCallId = "call-a",
                            FunctionName = "do_a",
                            FunctionArgs = "{}",
                        },
                        new ToolCall
                        {
                            ToolCallId = "call-b",
                            FunctionName = "do_b",
                            FunctionArgs = "{}",
                        },
                    ],
                },
                threadId,
                runId
            ) with
            {
                Timestamp = 2,
            };
        var resultForA =
            MessagePersistenceConverter.ToPersistedMessage(
                new ToolCallResultMessage
                {
                    ToolCallId = "call-a",
                    ToolName = "do_a",
                    Result = "ok",
                    RunId = runId,
                },
                threadId,
                runId
            ) with
            {
                Timestamp = 3,
            };

        // call-b's result row is never written; call-a's is healthy and readable.
        await store.AppendMessagesAsync(threadId, [text, twoCallRow, resultForA]);
        await store.SaveMetadataAsync(
            threadId,
            new ThreadMetadata
            {
                ThreadId = threadId,
                LatestRunId = runId,
                LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });

        var agent = new TestMultiTurnAgent(threadId, store: store);

        await agent.RecoverAsync();

        var history = agent.SnapshotHistoryForTest();
        history.OfType<ToolsCallMessage>().Should().BeEmpty("call-b was never answered");
        history.OfType<ToolCallResultMessage>().Should().BeEmpty(
            "call-a's result is orphaned by the removal of the message that requested it");
        history.Should().ContainSingle();

        await agent.DisposeAsync();
    }

    [Fact]
    public async Task RecoverAsync_TreatsAPluralResultMessage_AsAnswering_ItsToolCalls()
    {
        // A plural ToolsCallResultMessage answers its calls just as the singular one does. Reading
        // only the singular shape would leave those calls looking unanswered and delete them the
        // moment any unrelated row was skipped.
        var store = new InMemoryConversationStore();
        var threadId = "test-thread-plural-result";
        const string runId = "prior-run";
        const string toolCallId = "call-plural";

        var corrupt =
            MessagePersistenceConverter.ToPersistedMessage(
                new TextMessage { Text = "irrelevant", Role = Role.User, RunId = runId },
                threadId,
                runId
            ) with
            {
                Id = "corrupt-unrelated",
                Timestamp = 1,
                MessageJson = "{ not json",
            };
        var callRow =
            MessagePersistenceConverter.ToPersistedMessage(
                new ToolCallMessage
                {
                    Role = Role.Assistant,
                    RunId = runId,
                    ToolCallId = toolCallId,
                    FunctionName = "do_thing",
                    FunctionArgs = "{}",
                },
                threadId,
                runId
            ) with
            {
                Timestamp = 2,
            };
        var pluralResultRow =
            MessagePersistenceConverter.ToPersistedMessage(
                new ToolsCallResultMessage
                {
                    RunId = runId,
                    ToolCallResults = [new ToolCallResult(toolCallId, "ok")],
                },
                threadId,
                runId
            ) with
            {
                Timestamp = 3,
            };

        await store.AppendMessagesAsync(threadId, [corrupt, callRow, pluralResultRow]);
        await store.SaveMetadataAsync(
            threadId,
            new ThreadMetadata
            {
                ThreadId = threadId,
                LatestRunId = runId,
                LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });

        var agent = new TestMultiTurnAgent(threadId, store: store);

        await agent.RecoverAsync();

        // The sweep DID run (a row was skipped) and still kept the pair intact.
        var history = agent.SnapshotHistoryForTest();
        history.OfType<ToolCallMessage>().Should().ContainSingle(
            "its answer is present, just in the plural message shape");
        history.OfType<ToolsCallResultMessage>().Should().ContainSingle();

        await agent.DisposeAsync();
    }

    [Fact]
    public async Task RecoverAsync_ReturnsFalse_WhenEveryPersistedRecordIsCorrupt()
    {
        // "Nothing was restored" must have ONE answer. The zero-row branch already returns false, so
        // an all-corrupt load — the identical observable outcome — must not return true.
        var store = new InMemoryConversationStore();
        var threadId = "test-thread-all-corrupt";
        const string runId = "prior-run";

        var template = MessagePersistenceConverter.ToPersistedMessage(
            new TextMessage { Text = "irrelevant", Role = Role.User, RunId = runId },
            threadId,
            runId);
        var corruptA = template with
        {
            Id = "corrupt-a",
            Timestamp = 1,
            MessageJson = "{ not json",
        };
        var corruptB = template with
        {
            Id = "corrupt-b",
            Timestamp = 2,
            MessageJson = "also not json",
        };

        await store.AppendMessagesAsync(threadId, [corruptA, corruptB]);
        await store.SaveMetadataAsync(
            threadId,
            new ThreadMetadata
            {
                ThreadId = threadId,
                LatestRunId = runId,
                LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });

        var agent = new TestMultiTurnAgent(threadId, store: store);

        var recovered = await agent.RecoverAsync();

        recovered.Should().BeFalse("zero messages were restored, which is what false means here");
        agent.SnapshotHistoryForTest().Should().BeEmpty();

        await agent.DisposeAsync();
    }

    [Fact]
    public async Task RecoverAsync_ReportsAttemptedAndSkippedCounts_AtWarning_WhenRecordsAreDropped()
    {
        // An operator scanning at Information must not read "Recovered 1 messages" and miss that the
        // other rows were lost. The summary states what was attempted and what was dropped, and is
        // raised to Warning whenever anything was dropped — including rows dropped by the pairing
        // sweep, not just rows that failed to deserialize.
        var logger = new CapturingLogger();
        var store = new InMemoryConversationStore();
        var threadId = "test-thread-recovery-counts";
        const string runId = "prior-run";
        const string toolCallId = "call-9";

        var healthyText =
            MessagePersistenceConverter.ToPersistedMessage(
                new TextMessage { Text = "healthy text", Role = Role.User, RunId = runId },
                threadId,
                runId
            ) with
            {
                Timestamp = 1,
            };
        var callRow =
            MessagePersistenceConverter.ToPersistedMessage(
                new ToolCallMessage
                {
                    Role = Role.Assistant,
                    RunId = runId,
                    ToolCallId = toolCallId,
                    FunctionName = "do_thing",
                    FunctionArgs = "{}",
                },
                threadId,
                runId
            ) with
            {
                Timestamp = 2,
            };
        var resultRow =
            MessagePersistenceConverter.ToPersistedMessage(
                new ToolCallResultMessage
                {
                    ToolCallId = toolCallId,
                    ToolName = "do_thing",
                    Result = "ok",
                    RunId = runId,
                },
                threadId,
                runId
            ) with
            {
                Timestamp = 3,
                MessageJson = "{ this is not valid message json",
            };

        // 3 attempted: 1 unreadable (the result), 1 dropped for pairing (the call), 1 restored.
        await store.AppendMessagesAsync(threadId, [healthyText, callRow, resultRow]);
        await store.SaveMetadataAsync(
            threadId,
            new ThreadMetadata
            {
                ThreadId = threadId,
                LatestRunId = runId,
                LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });

        var agent = new TestMultiTurnAgent(threadId, store: store, logger: logger);

        await agent.RecoverAsync();

        var summary = logger.Entries.Should()
            .ContainSingle(e => e.Message.Contains("Recovered 1 of 3 persisted records"))
            .Which;
        summary.Level.Should().Be(
            LogLevel.Warning,
            "records were dropped, so the summary must not sit at Information");
        summary.Message.Should().Contain("1 unreadable").And.Contain("1 unpaired");

        await agent.DisposeAsync();
    }

    [Fact]
    public async Task RecoverAsync_DistinguishesAnUnknownTypeDiscriminatorFromACorruptRecord()
    {
        // A $type written by a NEWER binary is not bit rot: during a rollback window every such row
        // is dropped, and an operator must be able to tell that apart from a damaged record. The
        // outcome is unchanged (the row is still skipped, siblings still restored) — only the log
        // distinguishes the two.
        var logger = new CapturingLogger();
        var store = new InMemoryConversationStore();
        var threadId = "test-thread-unknown-type";
        const string runId = "prior-run";

        var healthy =
            MessagePersistenceConverter.ToPersistedMessage(
                new TextMessage { Text = "healthy text", Role = Role.User, RunId = runId },
                threadId,
                runId
            ) with
            {
                Timestamp = 1,
            };
        var fromNewerBinary = healthy with
        {
            Id = "from-newer-binary",
            Timestamp = 2,
            MessageJson = """{"$type":"some_future_message","text":"hi","role":"user"}""",
        };

        await store.AppendMessagesAsync(threadId, [healthy, fromNewerBinary]);
        await store.SaveMetadataAsync(
            threadId,
            new ThreadMetadata
            {
                ThreadId = threadId,
                LatestRunId = runId,
                LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });

        var agent = new TestMultiTurnAgent(threadId, store: store, logger: logger);

        var recovered = await agent.RecoverAsync();

        recovered.Should().BeTrue();
        agent.SnapshotHistoryForTest().Should().ContainSingle();

        logger.Entries.Should().Contain(
            e => e.Message.Contains("unknown message type")
                && e.Message.Contains("some_future_message")
                && e.Message.Contains("from-newer-binary"),
            "a schema the running binary does not know must not read as a corrupt record");
        logger.Entries.Should().NotContain(
            e => e.Message.Contains("Skipping corrupt persisted record"),
            "the unknown-type row must not also be reported as corruption");

        await agent.DisposeAsync();
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose() { }
        }
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
    public async Task UpdateMetadataAsync_PreservesOwnershipStampedAtCreation()
    {
        // Ownership is stamped ONCE, at creation, and never repaired later (spec 8.3). That design
        // only holds if nothing downstream overwrites it - and this method is downstream of every
        // conversation, because it runs after every completed run.
        //
        // It rebuilds ThreadMetadata from scratch and carries Properties and SessionMappings across
        // by hand. The four owner columns were never added to that list, and SaveMetadataAsync
        // upserts all four unconditionally (`SET tenant_id = excluded.tenant_id, ...`). So the
        // first completed turn on a freshly provisioned conversation nulls its tenant and its
        // owner. Under Identity:Enforce=true a null-tenant row is treated as absent, which means
        // the conversation 404s for the person who just created it.
        var store = new InMemoryConversationStore();
        var threadId = "test-thread-ownership";

        await store.SaveMetadataAsync(
            threadId,
            new ThreadMetadata
            {
                ThreadId = threadId,
                LatestRunId = "old-run",
                LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                TenantId = "tnt_acme",
                OwnerUserId = "entra-tid:owner-oid",
                OwnerAppId = "codereview-daemon",
                Visibility = Visibility.Shared,
            });

        var agent = new TestMultiTurnAgent(threadId, store: store);

        using var cts = new CancellationTokenSource();
        var runTask = agent.RunAsync(cts.Token);

        await agent.SendAsync([new TextMessage { Text = "Hello", Role = Role.User }]);
        await Task.Delay(500);

        var updated = await store.LoadMetadataAsync(threadId);

        _ = updated.Should().NotBeNull();
        _ = updated!.TenantId.Should().Be("tnt_acme");
        _ = updated.OwnerUserId.Should().Be("entra-tid:owner-oid");
        _ = updated.OwnerAppId.Should().Be("codereview-daemon");
        _ = updated.Visibility.Should().Be(Visibility.Shared);

        // Non-vacuity: the run really did write metadata, so the assertions above are reading a
        // row this method rewrote rather than the untouched row the fixture seeded.
        _ = updated.LatestRunId.Should().NotBe("old-run");

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
