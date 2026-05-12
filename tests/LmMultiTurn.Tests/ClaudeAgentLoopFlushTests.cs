using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Verifies the safety-net publish in <c>ClaudeAgentLoop.FlushPendingRunAssignmentsAsync</c>
/// — when the dequeue heuristic misses, the deferred RunAssignmentMessage must still
/// be emitted before run completion so consumers correlating the receipt id can
/// match the run.
/// </summary>
public class ClaudeAgentLoopFlushTests
{
    [Fact]
    public async Task FlushPendingRunAssignmentsAsync_PublishesPendingAssignment_WhenDequeueHeuristicMissed()
    {
        // Arrange: construct a ClaudeAgentLoop without ever starting the run loop.
        // We use the internal-visibility hooks to seed _pendingCliInputs and invoke
        // the flush method directly — this isolates the flush behavior from the
        // CLI process and dequeue heuristic.
        var options = new ClaudeAgentSdkOptions
        {
            Mode = ClaudeAgentSdkMode.OneShot,
        };

        await using var loop = new ClaudeAgentLoop(
            claudeOptions: options,
            mcpServers: null,
            threadId: "flush-test-thread");

        var receiptId = "test-receipt-id";
        var runId = "test-run-id";
        var generationId = "test-gen-id";

        var queuedInput = new QueuedInput(
            new UserInput([new TextMessage { Text = "Hello", Role = Role.User }], InputId: receiptId),
            receiptId,
            DateTimeOffset.UtcNow);
        var assignment = new RunAssignment(runId, generationId, [receiptId]);

        loop._pendingCliInputs.Enqueue((queuedInput, assignment));

        // Subscribe before flush so we can observe the published RunAssignmentMessage.
        var receivedMessages = new List<IMessage>();
        var subscriberCts = new CancellationTokenSource();
        var subscribeTask = Task.Run(async () =>
        {
            await foreach (var msg in loop.SubscribeAsync(subscriberCts.Token))
            {
                receivedMessages.Add(msg);
            }
        });

        // Give the subscriber a moment to register.
        await Task.Delay(50);

        // Act: invoke the flush directly (simulating run-loop completion calling
        // it from the finally block).
        await loop.FlushPendingRunAssignmentsAsync(runId, CancellationToken.None);

        // Allow time for the published message to propagate to the subscriber.
        await Task.Delay(50);
        await subscriberCts.CancelAsync();
        try { await subscribeTask; } catch (OperationCanceledException) { }

        // Assert: a RunAssignmentMessage was published carrying our receipt id,
        // and the pending queue was drained.
        var assignmentMsg = receivedMessages.OfType<RunAssignmentMessage>().SingleOrDefault();
        assignmentMsg.Should().NotBeNull("flush should publish exactly one RunAssignmentMessage for the run");
        assignmentMsg!.Assignment.RunId.Should().Be(runId);
        assignmentMsg.Assignment.InputIds.Should().Contain(receiptId,
            "the published assignment must list the original receipt so consumers can correlate");

        loop._pendingCliInputs.Should().BeEmpty("pending entries for the flushed run must be drained");
    }

    [Fact]
    public async Task FlushPendingRunAssignmentsAsync_DoesNotDrainEntriesForOtherRuns()
    {
        // Arrange: two pending entries for different runs; flush should only
        // drain the matching one.
        var options = new ClaudeAgentSdkOptions { Mode = ClaudeAgentSdkMode.OneShot };
        await using var loop = new ClaudeAgentLoop(
            claudeOptions: options,
            mcpServers: null,
            threadId: "flush-isolation-test");

        var runIdA = "run-a";
        var runIdB = "run-b";

        var inputA = new QueuedInput(
            new UserInput([new TextMessage { Text = "A", Role = Role.User }], InputId: "rcpt-a"),
            "rcpt-a",
            DateTimeOffset.UtcNow);
        var inputB = new QueuedInput(
            new UserInput([new TextMessage { Text = "B", Role = Role.User }], InputId: "rcpt-b"),
            "rcpt-b",
            DateTimeOffset.UtcNow);

        loop._pendingCliInputs.Enqueue((inputA, new RunAssignment(runIdA, "gen-a", ["rcpt-a"])));
        loop._pendingCliInputs.Enqueue((inputB, new RunAssignment(runIdB, "gen-b", ["rcpt-b"])));

        // Act: flush only runIdA. Note runIdA is at the head of the queue; the
        // flush stops at the first entry whose RunId differs.
        await loop.FlushPendingRunAssignmentsAsync(runIdA, CancellationToken.None);

        // Assert: only runIdB's entry remains.
        loop._pendingCliInputs.Should().HaveCount(1);
        loop._pendingCliInputs.TryPeek(out var remaining).Should().BeTrue();
        remaining.Assignment.RunId.Should().Be(runIdB);
    }
}
