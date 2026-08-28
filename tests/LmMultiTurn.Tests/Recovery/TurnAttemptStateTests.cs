using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Recovery;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Recovery;

/// <summary>
/// Unit tests for the per-attempt bookkeeping recovery decisions are made from.
/// </summary>
public class TurnAttemptStateTests
{
    private const string Generation = "gen-1";

    [Fact]
    public void Observe_Fragments_AreNotCanonicalAndLeaveTheAttemptEmpty()
    {
        var attempt = new TurnAttemptState(Generation);

        attempt.Observe(new TextUpdateMessage { Text = "par", Role = Role.Assistant }).Should().BeFalse();
        attempt.Observe(new ReasoningUpdateMessage { Reasoning = "tia", Role = Role.Assistant }).Should().BeFalse();

        attempt.HasCanonicalMessages.Should().BeFalse("fragments are deltas, not completed work");
        attempt.CompletedMessages.Should().BeEmpty();
    }

    [Fact]
    public void Observe_CompletedContent_IsKeptForHistory()
    {
        var attempt = new TurnAttemptState(Generation);
        attempt.Observe(new TextUpdateMessage { Text = "par", Role = Role.Assistant });

        attempt.Observe(new TextMessage { Text = "partial then whole", Role = Role.Assistant }).Should().BeTrue();

        attempt.HasCanonicalMessages.Should().BeTrue();
        attempt.CompletedMessages.Should().ContainSingle("only the canonical join is kept, never the fragment");
    }

    [Fact]
    public void HasCanonicalMessages_IsFalseForAnAccountingOnlyAttempt()
    {
        var attempt = new TurnAttemptState(Generation);
        attempt.Observe(new TextUpdateMessage { Text = "par", Role = Role.Assistant });

        // Usage is canonical for delivery/history purposes, but it is accounting — it delivered no
        // content and ran no effect. An attempt holding only usage produced nothing worth continuing
        // from, so the retry-versus-continue discriminator must still read "retry".
        attempt.Observe(new UsageMessage { Usage = new Usage() }).Should().BeTrue();

        attempt.CompletedMessages.Should().ContainSingle("usage still belongs in history");
        attempt.HasCanonicalMessages.Should().BeFalse("accounting is not delivered content or a ran effect");
    }

    [Theory]
    [MemberData(nameof(DeliveredContentMessages))]
    public void HasCanonicalMessages_IsTrueForDeliveredContentOrEffects(IMessage delivered)
    {
        var attempt = new TurnAttemptState(Generation);

        attempt.Observe(delivered).Should().BeTrue();

        attempt.HasCanonicalMessages.Should().BeTrue();
    }

    public static TheoryData<IMessage> DeliveredContentMessages() =>
        [
            new TextMessage { Text = "visible", Role = Role.Assistant },
            new ReasoningMessage { Reasoning = "visible", Role = Role.Assistant },
            new ToolCallMessage
            {
                ToolCallId = "call_1",
                FunctionName = "f",
                FunctionArgs = "{}",
                Role = Role.Assistant,
            },
            new ToolCallResultMessage { ToolCallId = "call_1", Result = "ran" },
        ];

    [Fact]
    public async Task SettleToolTasksAsync_AwaitsEveryTrackedTask_ExactlyOnce()
    {
        var attempt = new TurnAttemptState(Generation);
        var gate = new TaskCompletionSource<ToolCallResultMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        attempt.TrackToolTask("call_1", gate.Task);
        attempt.TrackToolTask(
            "call_2",
            Task.Run(() =>
            {
                Interlocked.Increment(ref starts);
                return new ToolCallResultMessage { ToolCallId = "call_2", Result = "ok" };
            })
        );

        var first = attempt.SettleToolTasksAsync();
        var second = attempt.SettleToolTasksAsync();
        second.Should().BeSameAs(first, "a second caller must observe the same wait, not start another");
        first.IsCompleted.Should().BeFalse("the gated tool has not finished");

        gate.SetResult(new ToolCallResultMessage { ToolCallId = "call_1", Result = "ok" });
        await first;

        attempt.HasToolCalls.Should().BeTrue();
        attempt.PendingToolTasks.Should().HaveCount(2);
        starts.Should().Be(1);
    }

    [Fact]
    public async Task SettleToolTasksAsync_SurfacesAToolFailure()
    {
        var attempt = new TurnAttemptState(Generation);
        attempt.TrackToolTask(
            "call_bad",
            Task.FromException<ToolCallResultMessage>(new InvalidOperationException("boom"))
        );

        var settle = async () => await attempt.SettleToolTasksAsync();

        await settle.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
    }
}
