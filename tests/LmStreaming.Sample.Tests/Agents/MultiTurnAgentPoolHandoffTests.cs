using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmTestUtils;
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

        ReportAccept(pool, "thread-queued", "input-1", agent);

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
        agent.StartRun("run_1", "input-0");

        ReportAccept(pool, "thread-behind", "input-1", agent);

        agent.CompleteRun();
        agent.IsRunning = false;

        pool.TryGetHandoffState("thread-behind", out var state).Should().BeTrue();
        state.IsBusy.Should().BeTrue();
        (await pool.TryReleaseIdleAgentAsync("thread-behind", state))
            .Should().Be(MultiTurnAgentPool.AgentReleaseOutcome.Busy);
    }

    [Fact]
    public async Task TheAcceptedInputMarker_ClearsWhenTheAgentReportsARunTookThatInput()
    {
        // The partner that keeps the ledger from being a one-way latch, run through the sequence a
        // real agent actually produces: accepted while idle, picked up by a run that names the id it
        // took, then completed - and completing a run puts CurrentRunId back to null, exactly as
        // MultiTurnAgentBase does. The id retires on that echo, not on a timer and not on an
        // inference from whichever run id happens to be current at the moment somebody looks.
        // A frozen clock, so the grace can never fire during the wait below. Without it the wait's
        // own budget races the 30s backstop and a pass could mean the backstop cleared the ledger -
        // the very mechanism this test exists to distinguish the evidence path from.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var pool = CreatePool(time);
        var agent = CreateOwnedAgent(pool, "thread-drained", Alice);
        agent.CurrentRunId = null;
        agent.IsRunning = false;

        ReportAccept(pool, "thread-drained", "input-1", agent);
        pool.TryGetHandoffState("thread-drained", out var queued).Should().BeTrue();
        queued.IsBusy.Should().BeTrue("nothing has picked the input up yet");

        agent.StartRun("run_1", "input-1");
        agent.CompleteRun();
        agent.IsRunning = false;

        // The BUDGET is the timeout argument, not a cancellation token: Wait.UntilAsync defaults to
        // 10s and a token only bounds the wait from outside, so passing a token alone leaves the real
        // deadline at the default. 30s is safe here only because the clock is frozen - the grace can
        // never fire during the wait, so a pass cannot mean the backstop cleared the ledger.
        await Wait.UntilAsync(
            () => pool.TryGetHandoffState("thread-drained", out var s) && !s.IsBusy,
            "the run assignment naming input-1 retires it from the ledger",
            timeout: TimeSpan.FromSeconds(30));

        // The clock never moved, so what cleared the ledger is the evidence and not the backstop.
        pool.TryGetHandoffState("thread-drained", out var state).Should().BeTrue();
        (await pool.TryReleaseIdleAgentAsync("thread-drained", state))
            .Should().Be(MultiTurnAgentPool.AgentReleaseOutcome.Released);
    }

    [Fact]
    public async Task ASecondAcceptedTurn_SurvivesTheFirstOnesRunEnding()
    {
        // Two accepts before anything starts, which a single accepted-input FLAG cannot represent:
        // the flag says "an input is outstanding", so the first run to pick one up clears it and the
        // second turn - still queued, still owed an answer - is left unprotected. The entry then
        // reads idle in the gap between run_1 ending and run_2 starting, and a handoff arriving in
        // that gap disposes the agent with the second turn on it.
        //
        // Recording the IDS makes the two distinguishable: run_1's assignment names input-1, so
        // input-2 is untouched by it.
        await using var pool = CreatePool();
        var agent = CreateOwnedAgent(pool, "thread-two", Alice);
        agent.CurrentRunId = null;
        agent.IsRunning = false;

        ReportAccept(pool, "thread-two", "input-1", agent);
        ReportAccept(pool, "thread-two", "input-2", agent);

        agent.StartRun("run_1", "input-1");
        agent.CompleteRun();
        agent.IsRunning = false;

        // What this test does NOT claim: that the watcher has already retired input-1 by now. It
        // cannot - the ledger is not observable from outside, and both "input-1 and input-2 still
        // outstanding" and "only input-2 outstanding" read as Busy. Retirement is pinned by
        // TheAcceptedInputMarker_ClearsWhenTheAgentReportsARunTookThatInput; what is pinned HERE is
        // that run_1 ending does not take the second turn with it, which a flag-shaped ledger fails
        // at whatever the timing.
        pool.TryGetHandoffState("thread-two", out var state).Should().BeTrue();
        state.IsBusy.Should().BeTrue("input-2 was never named by any run assignment");
        (await pool.TryReleaseIdleAgentAsync("thread-two", state))
            .Should().Be(MultiTurnAgentPool.AgentReleaseOutcome.Busy);
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

        ReportAccept(pool, "thread-wedged", "input-1", agent);

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
        // GetRunStateInfo's IsStale), and no run assignment ever names the accepted id here, so
        // nothing retires it. So an entry can be observed idle, then observed busy again, with the
        // accepted input still queued behind that same run the whole time.
        //
        // If the clock were left running across the busy stretch, the marker would expire while the
        // agent was demonstrably working and the queued turn would be released out from under it.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var pool = CreatePool(time);
        var agent = CreateOwnedAgent(pool, "thread-resumed", Alice);
        agent.CurrentRunId = "run_1";
        agent.IsRunning = false;

        ReportAccept(pool, "thread-resumed", "input-1", agent);

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
    public async Task AnAcceptOnAReplacedAgent_DoesNotMarkTheReplacement()
    {
        // The accept and the ledger write are two steps, and an entry can be replaced between them -
        // a sandbox session refresh, a mode switch, a second caller's create. Marking whatever is
        // pooled NOW gets it wrong twice over: the replacement is held busy for a turn it never
        // received, and the agent that actually holds that turn is not held at all. So the write
        // names the agent that accepted, and a mismatch is a no-op.
        await using var pool = CreatePool();
        var first = CreateOwnedAgent(pool, "thread-swapped", Alice);
        first.CurrentRunId = null;
        first.IsRunning = false;

        await pool.RemoveAgentAsync("thread-swapped");
        var second = CreateOwnedAgent(pool, "thread-swapped", Alice);
        second.CurrentRunId = null;
        second.IsRunning = false;
        second.Should().NotBeSameAs(first);

        // The late write, carrying the agent that took the input.
        ReportAccept(pool, "thread-swapped", "input-1", first);

        pool.TryGetHandoffState("thread-swapped", out var state).Should().BeTrue();
        state.IsBusy.Should().BeFalse("the pooled agent never accepted that input");

        // Non-vacuity: the same call against the agent that IS pooled does mark it, so what the
        // assertion above caught is the reference check and not a ledger that stopped working.
        ReportAccept(pool, "thread-swapped", "input-2", second);
        pool.TryGetHandoffState("thread-swapped", out var marked).Should().BeTrue();
        marked.IsBusy.Should().BeTrue();
    }

    [Fact]
    public async Task SwitchingMode_DiscardsAQueuedTurn_AndDoesNotCarryItToTheReplacement()
    {
        // The THIRD place "does this entry have work in hand?" could be asked. The grantee handoff
        // and the sandbox session refresh both ask it and both refuse; a mode switch does not ask at
        // all - it builds the replacement, swaps it in and disposes the old entry, taking that
        // agent's input channel with it.
        //
        // That is the decided behaviour, not an oversight, and this test is what makes it a decision:
        // a switch is the conversation's OWN explicit request and already discards a streaming run
        // without asking, so refusing only for a turn that has not started yet would make the pool
        // stricter about queued work than about work actively producing tokens.
        await using var pool = CreatePool();
        var original = CreateOwnedAgent(pool, "thread-switch", Alice);
        original.CurrentRunId = null;
        original.IsRunning = false;

        ReportAccept(pool, "thread-switch", "input-1", original);
        pool.TryGetHandoffState("thread-switch", out var queued).Should().BeTrue();
        queued.IsBusy.Should().BeTrue("the precondition: there really is a turn in hand to lose");

        var replacement = await pool.RecreateAgentWithModeAsync(
            "thread-switch",
            SystemChatModes.GetById(SystemChatModes.DefaultModeId)!,
            ownerUserId: Alice);

        replacement.Should().NotBeSameAs(original, "the switch replaced the agent");

        // The ledger does NOT travel. Carrying the id would be a lie: the replacement's input channel
        // never received that input, so no run of its could ever name it, and the entry would read
        // busy for the whole grace and then clear - with the turn just as lost and thirty seconds of
        // refused handoffs added on top.
        pool.TryGetHandoffState("thread-switch", out var after).Should().BeTrue();
        after.IsBusy.Should().BeFalse(
            "the replacement holds no work, and pretending otherwise would not bring the turn back");
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

    [Fact]
    public async Task AnAgentThatReportsItsOwnAccept_HoldsTheEntry_WithNoExplicitLedgerCall()
    {
        // Issue #434, the whole of it. Three accept paths live in LmMultiTurn - a sub-agent relaying
        // a descendant's question to its parent, a sub-agent completion notification, and a peer's
        // collaboration message - and none of them can call AddOutstandingInput, because the pool is
        // in an assembly that depends on theirs. A handoff landing between such an accept and the run
        // that would start it read the entry as idle and disposed the agent with the turn on it.
        //
        // Nothing below calls AddOutstandingInput. The agent reports its OWN accept from the place
        // the receipt id is minted, which is what makes the ledger complete rather than merely
        // well-maintained: the send here goes through exactly the method those three sites call.
        await using var pool = CreatePool(agentFactory: threadId => new PooledReportingAgent(threadId));
        var agent = (PooledReportingAgent)pool.GetOrCreateAgent(
            "thread-reported",
            SystemChatModes.GetById(SystemChatModes.DefaultModeId)!,
            requestedProviderId: null,
            requestResponseDumpFileName: null,
            requestedWorkspaceId: null,
            callerCredential: null,
            ownerUserId: Alice);

        // Non-vacuity anchor: before any accept the entry really is releasable, so the Busy below
        // cannot be an entry that was never idle to begin with. The loop parks without draining, so
        // CurrentRunId stays null and every signal IsEntryInProgress reads says "idle".
        pool.TryGetHandoffState("thread-reported", out var beforeAccept).Should().BeTrue();
        beforeAccept.IsBusy.Should().BeFalse("nothing has been accepted yet");

        var receipt = await agent.SendAsync([new TextMessage { Text = "relayed", Role = Role.User }]);

        pool.TryGetHandoffState("thread-reported", out var state).Should().BeTrue();
        state.IsBusy.Should().BeTrue(
            "the agent reported the accept itself, so the pool knows a turn is in hand");

        (await pool.TryReleaseIdleAgentAsync("thread-reported", state))
            .Should().Be(MultiTurnAgentPool.AgentReleaseOutcome.Busy);
        pool.TryGetHandoffState("thread-reported", out _).Should().BeTrue("the entry must survive");

        receipt.ReceiptId.Should().NotBeNullOrEmpty(
            "the id the pool holds is the one the sender was given");
    }

    [Fact]
    public async Task AReportedAccept_RetiresOnTheRunAssignmentThatNamesIt()
    {
        // The ordering guarantee, end to end and through the product's own code: an id retires only
        // once a run has actually TAKEN it. The report happens before the enqueue and the assignment
        // after the dequeue, so the two can never be observed out of order - which matters, because
        // an assignment observed FIRST would retire nothing and leave the id stranded until the
        // grace expired.
        //
        // The clock is frozen, so a pass cannot mean the 30s backstop cleared the ledger instead.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var pool = CreatePool(
            time,
            agentFactory: threadId => new PooledReportingAgent(threadId) { DrainInputs = true });
        var agent = (PooledReportingAgent)pool.GetOrCreateAgent(
            "thread-reported-drain",
            SystemChatModes.GetById(SystemChatModes.DefaultModeId)!,
            requestedProviderId: null,
            requestResponseDumpFileName: null,
            requestedWorkspaceId: null,
            callerCredential: null,
            ownerUserId: Alice);

        _ = await agent.SendAsync([new TextMessage { Text = "relayed", Role = Role.User }]);

        // Non-vacuity, and the reason the drain is gated: an EMPTY ledger is also "not busy", so a
        // wait for !IsBusy would be satisfied just as well by an agent that reported nothing. The id
        // has to be observably held first for its disappearance to mean retirement.
        pool.TryGetHandoffState("thread-reported-drain", out var held).Should().BeTrue();
        held.IsBusy.Should().BeTrue("the reported id is in the ledger before any run takes it");

        agent.OpenDrainGate();

        // The BUDGET is the timeout argument, not a token: Wait.UntilAsync defaults to 10s and a
        // token only bounds the wait from outside. Safe at 30s only because the clock is frozen.
        await Wait.UntilAsync(
            () => pool.TryGetHandoffState("thread-reported-drain", out var s) && !s.IsBusy,
            "the run assignment naming the reported id retires it from the ledger",
            timeout: TimeSpan.FromSeconds(30));

        pool.TryGetHandoffState("thread-reported-drain", out var state).Should().BeTrue();
        (await pool.TryReleaseIdleAgentAsync("thread-reported-drain", state))
            .Should().Be(MultiTurnAgentPool.AgentReleaseOutcome.Released);
    }

    [Fact]
    public async Task APooledAgentThatCannotReportItsOwnAccepts_IsRefusedByThePool()
    {
        // The decision #442 required before the four synchronous host AddOutstandingInput calls could
        // go, and the flipped form of the test that used to prove those calls were load-bearing.
        //
        // Reporting is declared as a CAPABILITY (IAcceptanceReportingAgent), not an obligation of
        // IMultiTurnAgent, so a pooled agent that does not declare it announces nothing. While the
        // host sites existed, such an agent was covered on the four transport paths and uncovered on
        // the three that live in LmMultiTurn - a ledger with a hole in it exactly the size of a
        // sub-agent relay. Removing the host sites would have made it uncovered everywhere.
        //
        // So the pool refuses to pool it at all. This is the ONLY moment the pool can detect the
        // condition: nothing calls the pool at accept time any more, so an unreported accept is
        // invisible by construction and the first symptom is a disposed agent with a turn on it. A
        // refusal here is a deterministic failure in whatever wires the factory, at the first
        // conversation, with the offending type named - and the alternative (keep the host calls as a
        // fallback) was never a fallback at all: it covers four of the seven accept paths.
        await using var pool = CreatePool(agentFactory: threadId => new SilentPooledAgent(threadId));

        var act = () => pool.GetOrCreateAgent(
            "thread-silent",
            SystemChatModes.GetById(SystemChatModes.DefaultModeId)!,
            requestedProviderId: null,
            requestResponseDumpFileName: null,
            requestedWorkspaceId: null,
            callerCredential: null,
            ownerUserId: Alice);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{nameof(IAcceptanceReportingAgent)}*")
            .WithMessage($"*{nameof(SilentPooledAgent)}*",
                "the refusal has to name the type that has to change, or it is a puzzle rather than "
                    + "a diagnosis");

        pool.TryGetHandoffState("thread-silent", out _).Should().BeFalse(
            "a refused agent must leave no entry behind - a half-registered thread would be worse "
                + "than the hole this closes");
    }

    /// <summary>
    /// An <see cref="IMultiTurnAgent"/> that is deliberately NOT
    /// <see cref="IAcceptanceReportingAgent"/> — the premise of the refusal above, and the reason it
    /// cannot derive from <see cref="FakeMultiTurnAgent"/>, which declares the capability so the pool
    /// will accept it.
    /// </summary>
    private sealed class SilentPooledAgent(string threadId) : IMultiTurnAgent
    {
        public string? CurrentRunId => null;

        public string ThreadId { get; } = threadId;

        public bool IsRunning => false;

        public ValueTask<SendReceipt> SendAsync(
            List<IMessage> messages,
            string? inputId = null,
            string? parentRunId = null,
            CancellationToken ct = default) =>
            ValueTask.FromResult(
                new SendReceipt(inputId ?? Guid.NewGuid().ToString("N"), inputId, DateTimeOffset.UtcNow));

        public async ValueTask<SendReceipt?> TrySendAsync(
            List<IMessage> messages,
            string? inputId = null,
            string? parentRunId = null,
            CancellationToken ct = default) =>
            await SendAsync(messages, inputId, parentRunId, ct);

#pragma warning disable CS1998, IDE0391 // Async iterator without await — an intentionally empty stub.
        public async IAsyncEnumerable<IMessage> ExecuteRunAsync(
            UserInput userInput,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield break;
        }

        public async IAsyncEnumerable<IMessage> SubscribeAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield break;
        }
#pragma warning restore CS1998, IDE0391

        public Task RunAsync(CancellationToken ct = default) => Task.Delay(Timeout.InfiniteTimeSpan, ct);

        public Task StopAsync(TimeSpan? timeout = null) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task AReportFromAnAgentTheThreadNoLongerHolds_DoesNotMarkThePooledOne()
    {
        // The reference check, reached through the reporting path rather than a direct ledger call:
        // a report must name the entry's OWN agent, or it would hold a replacement busy for a turn it
        // never received while the agent that really holds the turn is not held at all.
        //
        // The stray is constructed and attached here rather than evicted from the pool, because an
        // evicted agent cannot reach this state by any later call: RemoveAgentAsync disposes it and
        // SendAsync refuses outright on a disposed agent (ObjectDisposedException before the report).
        // The reachable shape is therefore a report already IN FLIGHT when the swap lands - a race
        // whose window cannot be pinned deterministically. What is pinned is the state that race
        // produces, wired exactly as CreateAgentEntry wires it.
        await using var pool = CreatePool(agentFactory: threadId => new PooledReportingAgent(threadId));
        var pooled = (PooledReportingAgent)pool.GetOrCreateAgent(
            "thread-reported-swap",
            SystemChatModes.GetById(SystemChatModes.DefaultModeId)!,
            requestedProviderId: null,
            requestResponseDumpFileName: null,
            requestedWorkspaceId: null,
            callerCredential: null,
            ownerUserId: Alice);

        await using var stray = new PooledReportingAgent("thread-reported-swap")
        {
            InputAcceptanceObserver = pool,
        };
        stray.Should().NotBeSameAs(pooled);

        // The stray accepts, and reports - for a thread whose entry is a different agent.
        _ = await stray.SendAsync([new TextMessage { Text = "late", Role = Role.User }]);

        pool.TryGetHandoffState("thread-reported-swap", out var state).Should().BeTrue();
        state.IsBusy.Should().BeFalse("the pooled agent never accepted that input");

        // Non-vacuity: the agent that IS pooled reports into the same ledger and does mark it, so
        // what the assertion above caught is the reference check, not reporting that stopped working.
        _ = await pooled.SendAsync([new TextMessage { Text = "current", Role = Role.User }]);
        pool.TryGetHandoffState("thread-reported-swap", out var marked).Should().BeTrue();
        marked.IsBusy.Should().BeTrue();
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

    /// <summary>
    /// Puts an accepted id into the pool's ledger the only way anything can since #442: as the
    /// accepting agent's own report. <c>AddOutstandingInput</c> is private now, because with no host
    /// caller left it would only be a way to hold an entry busy for an accept no agent made.
    /// </summary>
    /// <remarks>
    /// The stand-in agents here are not <c>MultiTurnAgentBase</c>-derived, so their send stubs do not
    /// run the product's mint sites; this reports on their behalf, with exactly the arguments
    /// <c>SendAsync</c> would have passed. Where the REPORTING itself is what is under test, the tests
    /// use <see cref="PooledReportingAgent"/> and a real send instead.
    /// </remarks>
    private static void ReportAccept(
        MultiTurnAgentPool pool,
        string threadId,
        string inputId,
        IMultiTurnAgent acceptedBy) =>
        ((IInputAcceptanceObserver)pool).OnInputAccepted(threadId, inputId, acceptedBy);

    /// <param name="timeProvider">Drives the accepted-input grace; the system clock when omitted.</param>
    /// <param name="agentFactory">
    /// What the pool builds for a thread. The default stand-in reports no acceptances of its own,
    /// which is the right double for the ledger's host-driven half; pass
    /// <see cref="PooledReportingAgent"/> to exercise the half an agent reports for itself.
    /// </param>
    private static MultiTurnAgentPool CreatePool(
        TimeProvider? timeProvider = null,
        Func<string, IMultiTurnAgent>? agentFactory = null) =>
        new(
            // KeepSubscriptionOpen: the pool subscribes to every agent it creates and retires accepted
            // inputs on the run assignment that names them, so the stand-in has to keep that stream up.
            (threadId, _, _) => new MultiTurnAgentPool.AgentCreationResult(
                agentFactory?.Invoke(threadId)
                    ?? new FakeMultiTurnAgent(threadId) { KeepSubscriptionOpen = true }),
            NullLogger<MultiTurnAgentPool>.Instance)
        {
            TimeProvider = timeProvider ?? TimeProvider.System,
        };
}
