using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.TestDoubles;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Covers the accept path that reaches a pooled agent from the composition root (#418): an async
/// workflow's completion, re-injected into the conversation that started it.
/// </summary>
/// <remarks>
/// It is the path least likely to be looked for. A workflow finishes long after the turn that started
/// it, so the conversation is normally idle when the notice arrives - which is exactly the state a
/// grantee handoff or a sandbox session refresh reads as "nothing in hand". Unrecorded, the agent is
/// disposed with the workflow's result still queued and nobody is ever told the work was done.
/// </remarks>
public class WorkflowCompletionNotifierTests
{
    private const string ThreadId = "thread-workflow";

    [Fact]
    public async Task ACompletionNotice_CountsAsWorkInHand_UntilARunTakesIt()
    {
        await using var pool = CreatePool();
        var agent = CreateAgent(pool);

        // The conversation is idle - no run id, not running - which is the whole difficulty.
        pool.TryGetHandoffState(ThreadId, out var before).Should().BeTrue();
        before.IsBusy.Should().BeFalse("the turn that started the workflow finished long ago");

        await WorkflowCompletionNotifier.DeliverAsync(
            pool,
            ThreadId,
            agent,
            new TextMessage { Role = Role.User, Text = "workflow finished" },
            CancellationToken.None);

        agent.SentMessages.Should().ContainSingle("the notice really was queued");

        pool.TryGetHandoffState(ThreadId, out var after).Should().BeTrue();
        after.IsBusy.Should().BeTrue("a queued completion notice is work in hand like any other input");
        (await pool.TryReleaseIdleAgentAsync(ThreadId, after))
            .Should().Be(MultiTurnAgentPool.AgentReleaseOutcome.Busy);
    }

    [Fact]
    public async Task AFailedDelivery_DoesNotLeaveTheThreadWedgedBusy()
    {
        // The partner of announcing an accept BEFORE the input is taken. A throw means nothing was
        // queued, so no run can ever name that id - and an entry left standing would make the
        // conversation refuse every handoff until the grace expired, thirty seconds bought for a turn
        // that does not exist. Since #442 the notifier no longer records or withdraws anything
        // itself; the net ledger effect it must not produce is the same either way.
        await using var pool = CreatePool();
        var agent = CreateAgent(pool);
        agent.ThrowOnSend = true;

        var deliver = async () => await WorkflowCompletionNotifier.DeliverAsync(
            pool,
            ThreadId,
            agent,
            new TextMessage { Role = Role.User, Text = "workflow finished" },
            CancellationToken.None);

        await deliver.Should().ThrowAsync<InvalidOperationException>(
            "a failed delivery must stay visible to WorkflowManager's own handling");

        pool.TryGetHandoffState(ThreadId, out var after).Should().BeTrue();
        after.IsBusy.Should().BeFalse("nothing was queued, so nothing is owed");
        (await pool.TryReleaseIdleAgentAsync(ThreadId, after))
            .Should().Be(MultiTurnAgentPool.AgentReleaseOutcome.Released);
    }

    private static RecordingMultiTurnAgent CreateAgent(MultiTurnAgentPool pool)
    {
        var agent = (RecordingMultiTurnAgent)pool.GetOrCreateAgent(
            ThreadId,
            SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);
        agent.CurrentRunId = null;
        agent.IsRunning = false;
        return agent;
    }

    private static MultiTurnAgentPool CreatePool() =>
        new(
            (threadId, _, _) => new MultiTurnAgentPool.AgentCreationResult(
                new RecordingMultiTurnAgent(threadId)),
            NullLogger<MultiTurnAgentPool>.Instance);
}
