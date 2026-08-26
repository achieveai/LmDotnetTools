using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.Extensions.Time.Testing;

namespace LmStreaming.Sample.Tests.Agents;

/// <summary>
/// Covers issue #418: the grantee handoff decided whether to release a pooled agent by asking the
/// pool a question and then acting on the answer from outside it.
/// </summary>
/// <remarks>
/// <para>
/// Two defects of one shape. The <b>window</b>: nothing held the entry between
/// <c>IsRunInProgress</c> and <c>RemoveAgentAsync</c>, and the removal disposed unconditionally, so
/// a run that started in between was aborted anyway - which is the exact outcome the check exists to
/// prevent. The <b>queued turn</b>: in-progress is derived from a current run id plus a live run
/// task, and an input that has been accepted but not yet started has neither, so it read as idle and
/// was discarded with the agent after its sender had already been given a receipt.
/// </para>
/// <para>
/// The replacement is a compare-and-remove: one locked look returns every fact the decision needs
/// (<see cref="MultiTurnAgentPool.TryGetHandoffState"/>), and the release re-validates that same
/// entry under the same per-thread lock before it removes anything
/// (<see cref="MultiTurnAgentPool.TryReleaseIdleAgentAsync"/>). "Idle" gained the accepted-input
/// ledger, without which the queued-turn case survives the fix.
/// </para>
/// </remarks>
[Collection("EnvironmentVariables")]
public class MultiTurnAgentPoolHandoffTests
{
    private const string Alice = "dir-a:alice";
    private const string Bob = "dir-a:bob";

    [Fact]
    public async Task AnIdleEntry_IsReleased_AndTheCallerIsToldSo()
    {
        await using var pool = CreatePool();
        var agent = CreateOwnedAgent(pool, "thread-idle", Alice);
        agent.CurrentRunId = null;

        pool.TryGetHandoffState("thread-idle", out var state).Should().BeTrue();
        state.OwnerUserId.Should().Be(Alice);
        state.IsBusy.Should().BeFalse();

        var outcome = await pool.TryReleaseIdleAgentAsync("thread-idle", state);

        // The non-vacuity anchor for every refusal below: on a genuinely idle entry the release
        // still happens, so a test that saw Busy everywhere would not read as a pass.
        outcome.Should().Be(MultiTurnAgentPool.AgentReleaseOutcome.Released);
        pool.TryGetHandoffState("thread-idle", out _).Should().BeFalse();
    }

    [Fact]
    public async Task AnAcceptedButUnstartedTurn_IsNotLostToAHandoff()
    {
        // The queued-turn hole, exactly as reported. The sender already holds a 202 receipt; the
        // agent has not picked the input up yet, so it has no run id and is not running. Every
        // signal IsRunInProgress reads says "idle", and the entry was disposed with the turn on it.
        await using var pool = CreatePool();
        var agent = CreateOwnedAgent(pool, "thread-queued", Alice);
        agent.CurrentRunId = null;
        agent.IsRunning = false;

        pool.NoteInputAccepted("thread-queued");

        pool.TryGetHandoffState("thread-queued", out var state).Should().BeTrue();
        state.IsBusy.Should().BeTrue("an accepted input the agent has not started is still work in hand");

        var outcome = await pool.TryReleaseIdleAgentAsync("thread-queued", state);

        outcome.Should().Be(MultiTurnAgentPool.AgentReleaseOutcome.Busy);
        pool.TryGetHandoffState("thread-queued", out _).Should().BeTrue("the entry must survive");
    }

    [Fact]
    public async Task ATurnQueuedBehindARunningOne_SurvivesThatRunEnding()
    {
        // The sharper half of the same hole, and the one a naive "is a run in progress?" ledger
        // reopens. The input is accepted while run_1 is streaming, so in-progress covers it for now.
        // When run_1 ends, in-progress goes false while the queued turn has still not started - and
        // that is the moment a handoff arrives.
        await using var pool = CreatePool();
        var agent = CreateOwnedAgent(pool, "thread-behind", Alice);
        agent.CurrentRunId = "run_1";
        agent.IsRunning = true;

        pool.NoteInputAccepted("thread-behind");

        agent.IsRunning = false;

        pool.TryGetHandoffState("thread-behind", out var state).Should().BeTrue();
        state.IsBusy.Should().BeTrue();
        (await pool.TryReleaseIdleAgentAsync("thread-behind", state))
            .Should().Be(MultiTurnAgentPool.AgentReleaseOutcome.Busy);
    }

    [Fact]
    public async Task TheAcceptedInputMarker_ClearsOnceANewRunHasDrainedIt()
    {
        // The partner that keeps the marker from being a one-way latch. A run id the agent did not
        // have at accept time can only mean the input channel was drained, so the marker retires on
        // that evidence rather than on a timer.
        await using var pool = CreatePool();
        var agent = CreateOwnedAgent(pool, "thread-drained", Alice);
        agent.CurrentRunId = "run_1";
        agent.IsRunning = true;

        pool.NoteInputAccepted("thread-drained");

        // run_2 is the queued turn starting, then finishing.
        agent.CurrentRunId = "run_2";
        agent.IsRunning = false;

        pool.TryGetHandoffState("thread-drained", out var state).Should().BeTrue();
        state.IsBusy.Should().BeFalse();
        (await pool.TryReleaseIdleAgentAsync("thread-drained", state))
            .Should().Be(MultiTurnAgentPool.AgentReleaseOutcome.Released);
    }

    [Fact]
    public async Task AWedgedAgent_DoesNotPinTheEntryForever()
    {
        // The backstop. An agent that accepts an input and never starts it would otherwise hold the
        // marker for the process's lifetime, and every handoff for that conversation would answer
        // 409 forever - trading a lost turn for a permanently unusable thread. The grace clock runs
        // only while the entry is observed NOT in progress, so a turn queued behind a long run is
        // never timed out by it.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var pool = CreatePool(time);
        var agent = CreateOwnedAgent(pool, "thread-wedged", Alice);
        agent.CurrentRunId = null;
        agent.IsRunning = false;

        pool.NoteInputAccepted("thread-wedged");

        // First look starts the idle clock; still busy.
        pool.TryGetHandoffState("thread-wedged", out var first).Should().BeTrue();
        first.IsBusy.Should().BeTrue();

        time.Advance(MultiTurnAgentPool.AcceptedInputGrace + TimeSpan.FromSeconds(1));

        pool.TryGetHandoffState("thread-wedged", out var later).Should().BeTrue();
        later.IsBusy.Should().BeFalse();
        (await pool.TryReleaseIdleAgentAsync("thread-wedged", later))
            .Should().Be(MultiTurnAgentPool.AgentReleaseOutcome.Released);
    }

    [Fact]
    public async Task TheGraceMeasuresContinuousIdleness_NotTimeSinceTheAccept()
    {
        // The clause the grace's own remarks claim, pinned by the one sequence that can distinguish
        // it. A run that has a run id but is not running is a state this pool already names (see
        // GetRunStateInfo's IsStale), and a run that resumes under the SAME id does not retire the
        // marker - a new id is the only evidence the input channel drained. So an entry can be
        // observed idle, then observed busy again, with the accepted input still queued behind that
        // same run the whole time.
        //
        // If the clock were left running across the busy stretch, the marker would expire while the
        // agent was demonstrably working and the queued turn would be released out from under it.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var pool = CreatePool(time);
        var agent = CreateOwnedAgent(pool, "thread-resumed", Alice);
        agent.CurrentRunId = "run_1";
        agent.IsRunning = false;

        pool.NoteInputAccepted("thread-resumed");

        // Observed idle: the grace clock starts here.
        pool.TryGetHandoffState("thread-resumed", out var whileIdle).Should().BeTrue();
        whileIdle.IsBusy.Should().BeTrue();

        // run_1 resumes under its own id, and is observed doing so.
        agent.IsRunning = true;
        pool.TryGetHandoffState("thread-resumed", out var whileBusy).Should().BeTrue();
        whileBusy.IsBusy.Should().BeTrue();

        time.Advance(MultiTurnAgentPool.AcceptedInputGrace + TimeSpan.FromSeconds(1));

        // run_1 ends. The queued turn has still not started, and the grace has not been running.
        agent.IsRunning = false;
        pool.TryGetHandoffState("thread-resumed", out var afterwards).Should().BeTrue();
        afterwards.IsBusy.Should().BeTrue(
            "the entry was working for that whole stretch, so none of it counts against the grace");
        (await pool.TryReleaseIdleAgentAsync("thread-resumed", afterwards))
            .Should().Be(MultiTurnAgentPool.AgentReleaseOutcome.Busy);
    }

    [Fact]
    public async Task ARunStartingAfterTheLook_IsNotAbortedByTheRelease()
    {
        // The window. The caller read an idle entry and is about to act on it; a run starts in
        // between. Under a check-then-act removal the run is aborted anyway. The release re-reads
        // the entry under the lock, so the answer is Busy and the streaming turn survives.
        await using var pool = CreatePool();
        var agent = CreateOwnedAgent(pool, "thread-window", Alice);
        agent.CurrentRunId = null;
        agent.IsRunning = false;

        pool.TryGetHandoffState("thread-window", out var state).Should().BeTrue();
        state.IsBusy.Should().BeFalse("the caller genuinely observed an idle entry");

        // The interleave, made deterministic: everything the racing thread would have done, done
        // between the look and the release.
        agent.CurrentRunId = "run_started_late";
        agent.IsRunning = true;

        (await pool.TryReleaseIdleAgentAsync("thread-window", state))
            .Should().Be(MultiTurnAgentPool.AgentReleaseOutcome.Busy);
        pool.TryGetHandoffState("thread-window", out _).Should().BeTrue();
    }

    [Fact]
    public async Task AnEntryReplacedAfterTheLook_IsPreserved_NotDisposed()
    {
        // RemoveAgentAsync took whatever was in the dictionary at the moment it ran. If the entry
        // the caller reasoned about had since been replaced - a session refresh, a mode switch, a
        // second caller's create - the removal destroyed a live agent nobody had decided anything
        // about, along with its owner's freeze.
        await using var pool = CreatePool();
        var first = CreateOwnedAgent(pool, "thread-replaced", Alice);
        first.CurrentRunId = null;
        first.IsRunning = false;

        pool.TryGetHandoffState("thread-replaced", out var stale).Should().BeTrue();

        await pool.RemoveAgentAsync("thread-replaced");
        var second = CreateOwnedAgent(pool, "thread-replaced", Bob);
        second.CurrentRunId = null;
        second.IsRunning = false;

        var outcome = await pool.TryReleaseIdleAgentAsync("thread-replaced", stale);

        outcome.Should().Be(MultiTurnAgentPool.AgentReleaseOutcome.Replaced);
        pool.TryGetHandoffState("thread-replaced", out var current).Should().BeTrue();
        current.OwnerUserId.Should().Be(Bob, "the replacement entry and its freeze must be intact");
    }

    [Fact]
    public async Task AThreadWithNoPooledAgent_ReportsNotPooled_RatherThanSucceeding()
    {
        await using var pool = CreatePool();
        var agent = CreateOwnedAgent(pool, "thread-gone", Alice);
        agent.CurrentRunId = null;
        agent.IsRunning = false;

        pool.TryGetHandoffState("thread-gone", out var state).Should().BeTrue();
        await pool.RemoveAgentAsync("thread-gone");

        (await pool.TryReleaseIdleAgentAsync("thread-gone", state))
            .Should().Be(MultiTurnAgentPool.AgentReleaseOutcome.NotPooled);
    }

    [Fact]
    public async Task AVanishedEntry_ReportsAbsence_NotAnUnfrozenThread()
    {
        // The second instance the issue appended. The handoff used to take two independent unlocked
        // lookups - GetAgentOwnerUserId then GetAgentCallerAppId - and both answer null for a thread
        // that has no entry at all. So an entry removed between them made the app id read as null,
        // which is indistinguishable from "created by a caller with no credential". An interactive
        // caller then compared null == null and was let through a handoff that should have been
        // refused as cross-app; an S2S caller with a MATCHING app id compared null != "app-a" and was
        // refused a thread it was entitled to continue.
        //
        // One look cannot produce that view, because absence and "frozen to no app" are now different
        // answers rather than the same null: the lookup fails, and there is no state to misread.
        await using var pool = CreatePool();
        var withApp = CreateOwnedAgent(pool, "thread-frozen", Alice, new SandboxCredential("app-a", "key"));
        withApp.CurrentRunId = null;
        withApp.IsRunning = false;

        var uiAgent = CreateOwnedAgent(pool, "thread-ui", Alice);
        uiAgent.CurrentRunId = null;
        uiAgent.IsRunning = false;

        // Frozen to an app, and frozen to none, are distinguishable from each other...
        pool.TryGetHandoffState("thread-frozen", out var frozen).Should().BeTrue();
        frozen.CallerAppId.Should().Be("app-a");
        pool.TryGetHandoffState("thread-ui", out var ui).Should().BeTrue();
        ui.CallerAppId.Should().BeNull();

        // ...and BOTH are distinguishable from a thread that has no entry, which is the distinction
        // the two-accessor shape could not make.
        await pool.RemoveAgentAsync("thread-frozen");
        pool.TryGetHandoffState("thread-frozen", out _).Should().BeFalse(
            "a caller must never be handed a state that says 'frozen to no app' for a thread whose "
                + "entry is simply gone");
    }

    /// <remarks>
    /// What no test here claims: that a CONCURRENT replacement between the two old accessors is
    /// impossible. That is a property of the lock, not of an observable outcome, and a race test for
    /// it would only ever be a probabilistic one. What is pinned instead is every consequence the
    /// issue named - the mixed view is unreachable because there is one look, the stale decision is
    /// refused by <see cref="MultiTurnAgentPool.AgentReleaseOutcome.Replaced"/>, and absence no longer
    /// wears the same face as "never frozen".
    /// </remarks>
    private static FakeMultiTurnAgent CreateOwnedAgent(
        MultiTurnAgentPool pool,
        string threadId,
        string ownerUserId,
        SandboxCredential? callerCredential = null) =>
        (FakeMultiTurnAgent)pool.GetOrCreateAgent(
            threadId,
            SystemChatModes.GetById(SystemChatModes.DefaultModeId)!,
            requestedProviderId: null,
            requestResponseDumpFileName: null,
            requestedWorkspaceId: null,
            callerCredential: callerCredential,
            ownerUserId: ownerUserId);

    private static MultiTurnAgentPool CreatePool(TimeProvider? timeProvider = null) =>
        new(
            (threadId, _, _) => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(threadId)),
            NullLogger<MultiTurnAgentPool>.Instance)
        {
            TimeProvider = timeProvider ?? TimeProvider.System,
        };
}
