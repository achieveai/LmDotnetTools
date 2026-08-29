using AchieveAi.LmDotnetTools.Misc.Utils;
using LmStreaming.Sample.Services;
using Microsoft.Extensions.Time.Testing;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
///     The anti-perpetual-motion contract of #583 PR 6: assignment notices fire once per observed
///     TRANSITION (never for hydrated baseline state), stalled-agent nudges are budgeted at two per
///     idle period, and the budget resets ONLY on a real board change — a claim-refresh heartbeat
///     that fires the change hook without changing what the board says resets nothing.
/// </summary>
public class TodoNudgeServiceTests
{
    private sealed class Harness
    {
        public FakeTimeProvider Clock { get; } = new();

        public TaskManager Manager { get; }

        /// <summary>Accepted deliveries only — a refused delivery must not consume budget.</summary>
        public List<(string Target, NotifyMessage Message)> Delivered { get; } = [];

        public int DeliveryAttempts { get; private set; }

        public Func<string, TodoNudgeTargetKind> Resolver { get; set; } = _ => TodoNudgeTargetKind.SubAgent;

        public bool AcceptDeliveries { get; set; } = true;

        public TodoNudgeService Service { get; private set; } = null!;

        public Harness()
        {
            Manager = new TaskManager(Clock);
        }

        /// <summary>
        ///     Constructs the service over the manager's CURRENT board — everything seeded before this
        ///     call is baseline (the hydration case), everything after is a transition.
        /// </summary>
        public TodoNudgeService Build(TodoNudgeOptions options)
        {
            Service = new TodoNudgeService(
                options,
                Manager.GetTasks,
                name => Resolver(name),
                (name, message, _) =>
                {
                    DeliveryAttempts++;
                    if (!AcceptDeliveries)
                    {
                        return ValueTask.FromResult(false);
                    }

                    Delivered.Add((name, message));
                    return ValueTask.FromResult(true);
                },
                Clock
            );
            return Service;
        }

        /// <summary>What Program.cs's OnChanged subscription does, awaited for determinism.</summary>
        public Task SyncAsync()
        {
            return Service.HandleBoardChangedAsync();
        }
    }

    private static TodoNudgeOptions NoticeOnly => new();

    private static TodoNudgeOptions RunEndOnly => new() { AssignmentNoticeEnabled = false, RunEndNudgeEnabled = true };

    // ---------------------------------------------------------------- N1: assignment notices

    [Fact]
    public async Task AssignmentNotice_FiresOncePerAssignmentTransition()
    {
        var harness = new Harness();
        _ = harness.Build(NoticeOnly);

        _ = harness.Manager.AddTask("Wire the endpoint");
        await harness.SyncAsync();
        harness.Delivered.Should().BeEmpty("adding an unassigned task is not an assignment");

        _ = harness.Manager.AssignTask("1", "agent-a");
        await harness.SyncAsync();

        var (target, message) = harness.Delivered.Should().ContainSingle().Subject;
        target.Should().Be("agent-a");
        message.NotifyKind.Should().Be(NotifyKinds.TodoNudge);
        message.Label.Should().Be("agent-a");
        message.Detail.Should().Contain("task 1").And.Contain("Wire the endpoint").And.Contain("claim-task");
    }

    [Fact]
    public async Task AssignmentNotice_DoesNotRefire_OnAnUnrelatedBoardChange()
    {
        // Pins transition-keying: a predicate broadened to "task HAS an assignee" (instead of
        // "assignee CHANGED") re-notifies agent-a here and goes red.
        var harness = new Harness();
        _ = harness.Build(NoticeOnly);
        _ = harness.Manager.AddTask("Assigned work");
        await harness.SyncAsync();
        _ = harness.Manager.AssignTask("1", "agent-a");
        await harness.SyncAsync();
        harness.Delivered.Should().HaveCount(1);

        _ = harness.Manager.AddTask("Unrelated new work");
        await harness.SyncAsync();

        harness.Delivered.Should().HaveCount(1, "the standing assignment is state now, not a transition");
    }

    [Fact]
    public async Task AssignmentNotice_DoesNotFire_ForAClaim()
    {
        // A claim sets the assignee too — but the agent ACTED; telling it what it just did is noise.
        var harness = new Harness();
        _ = harness.Build(NoticeOnly);
        _ = harness.Manager.AddTask("Self-claimed work");
        await harness.SyncAsync();

        _ = harness.Manager.ClaimTask("1", "agent-a");
        await harness.SyncAsync();

        harness.Delivered.Should().BeEmpty();
    }

    [Fact]
    public async Task AssignmentNotice_DoesNotFire_ForANewRowBornWithAnAssignee()
    {
        // Sub-items created under an assigned task inherit the assignee — they are the assignee's
        // own breakdown, not a dispatch.
        var harness = new Harness();
        _ = harness.Build(NoticeOnly);
        _ = harness.Manager.AddTask("Parent");
        await harness.SyncAsync();
        _ = harness.Manager.AssignTask("1", "agent-a");
        await harness.SyncAsync();
        harness.Delivered.Should().HaveCount(1);

        _ = harness.Manager.AddTask("Inherited sub-item", "1");
        await harness.SyncAsync();

        harness.Delivered.Should().HaveCount(1, "a new row is not an assignment transition");
    }

    [Fact]
    public async Task AssignmentNotice_DoesNotRefire_ForAssignmentsHydratedFromASnapshot()
    {
        // The trap #590's rehydration created: FromSnapshot restores assignees, and a recreate or
        // restart must not re-notify every pre-existing assignee. Seeding BEFORE Build() is exactly
        // the hydrated-baseline shape.
        var harness = new Harness();
        _ = harness.Manager.AddTask("Pre-existing assigned work");
        _ = harness.Manager.AssignTask("1", "agent-a");
        _ = harness.Build(NoticeOnly);

        _ = harness.Manager.AddTask("First change after the recreate");
        await harness.SyncAsync();

        harness.Delivered.Should().BeEmpty("hydration is not a transition");
    }

    [Fact]
    public async Task AssignmentNotice_StillFires_ForAGenuineAssignmentAfterHydration()
    {
        // The other half of the hydration guardrail: baseline suppression must come from REMEMBERING
        // the hydrated state, not from forgetting it — a service that starts with an empty baseline
        // also passes the no-refire test above, but silently swallows this legitimate dispatch.
        var harness = new Harness();
        _ = harness.Manager.AddTask("Pre-existing assigned work");
        _ = harness.Manager.AssignTask("1", "agent-a");
        _ = harness.Manager.AddTask("Pre-existing unassigned work");
        _ = harness.Build(NoticeOnly);

        _ = harness.Manager.AssignTask("2", "agent-b");
        await harness.SyncAsync();

        var (target, message) = harness.Delivered.Should().ContainSingle().Subject;
        target.Should().Be("agent-b");
        message.Detail.Should().Contain("task 2");
    }

    [Fact]
    public async Task AssignmentNotice_ToTheRootConversation_RequiresTheOptIn()
    {
        var harness = new Harness { Resolver = _ => TodoNudgeTargetKind.RootConversation };
        _ = harness.Build(NoticeOnly);
        _ = harness.Manager.AddTask("Root-owned work");
        await harness.SyncAsync();

        _ = harness.Manager.AssignTask("1", "the-user");
        await harness.SyncAsync();

        harness.Delivered.Should().BeEmpty("NudgeRootConversation defaults to false");
    }

    [Fact]
    public async Task AssignmentNotice_IsNotCountedAgainstTheStallBudget()
    {
        var harness = new Harness();
        _ = harness.Build(new TodoNudgeOptions { RunEndNudgeEnabled = true });
        _ = harness.Manager.AddTask("Dispatched work");
        await harness.SyncAsync();
        _ = harness.Manager.AssignTask("1", "agent-a");
        await harness.SyncAsync();
        harness.Delivered.Should().HaveCount(1, "the N1 notice");

        await harness.Service.HandleRunEndedAsync("agent-a", endedWithError: false, cancelled: false);
        await harness.Service.HandleRunEndedAsync("agent-a", endedWithError: false, cancelled: false);
        await harness.Service.HandleRunEndedAsync("agent-a", endedWithError: false, cancelled: false);

        harness.Delivered.Should().HaveCount(3, "the notice plus the FULL budget of two stall nudges");
    }

    // ---------------------------------------------------------------- the stall budget

    [Fact]
    public async Task StallNudges_CapAtTwoPerIdlePeriod_ThenTheAgentIsMarkedStalled()
    {
        var harness = new Harness();
        _ = harness.Manager.AddTask("Owned work");
        _ = harness.Manager.ClaimTask("1", "agent-a");
        _ = harness.Build(RunEndOnly);

        for (var i = 0; i < 5; i++)
        {
            await harness.Service.HandleRunEndedAsync("agent-a", endedWithError: false, cancelled: false);
        }

        harness.Delivered.Should().HaveCount(TodoNudgeService.MaxStallNudgesPerIdlePeriod);
        harness.Service.StalledAgents.Should().ContainSingle().Which.Should().Be("agent-a");
    }

    [Fact]
    public async Task SecondNudge_EscalatesAndSaysItIsTheLast()
    {
        var harness = new Harness();
        _ = harness.Manager.AddTask("Owned work");
        _ = harness.Manager.ClaimTask("1", "agent-a");
        _ = harness.Build(RunEndOnly);

        await harness.Service.HandleRunEndedAsync("agent-a", endedWithError: false, cancelled: false);
        await harness.Service.HandleRunEndedAsync("agent-a", endedWithError: false, cancelled: false);

        harness.Delivered.Should().HaveCount(2);
        harness.Delivered[0].Message.Detail.Should().NotContain("second and final");
        harness.Delivered[1].Message.Detail.Should().Contain("second and final nudge");
    }

    [Fact]
    public async Task Budget_Resets_OnARealBoardChange_ButNotOnAHeartbeat()
    {
        // THE load-bearing test. Two mutations must go red against it: an unconditional reset in
        // HandleBoardChangedAsync (the heartbeat step below would re-arm the budget), and a reset
        // keyed on nudge delivery (the cap test above would never stop at two).
        var harness = new Harness();
        _ = harness.Manager.AddTask("Owned work");
        _ = harness.Manager.ClaimTask("1", "agent-a");
        _ = harness.Build(RunEndOnly);

        await harness.Service.HandleRunEndedAsync("agent-a", endedWithError: false, cancelled: false);
        await harness.Service.HandleRunEndedAsync("agent-a", endedWithError: false, cancelled: false);
        await harness.Service.HandleRunEndedAsync("agent-a", endedWithError: false, cancelled: false);
        harness.Delivered.Should().HaveCount(2, "the budget is spent");
        harness.Service.StalledAgents.Should().Contain("agent-a");

        // Heartbeat: re-claiming an in-progress task by its own holder refreshes ONLY the lease
        // timestamp. The change hook fires — but nothing the board SAYS changed.
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        _ = harness.Manager.ClaimTask("1", "agent-a");
        await harness.SyncAsync();
        await harness.Service.HandleRunEndedAsync("agent-a", endedWithError: false, cancelled: false);
        harness.Delivered.Should().HaveCount(2, "a heartbeat is time passing, and time never re-arms the budget");
        harness.Service.StalledAgents.Should().Contain("agent-a");

        // Real change: a note is board content, so the idle period ends and the budget re-arms.
        _ = harness.Manager.AddNote("1", noteText: "made real progress");
        await harness.SyncAsync();
        harness.Service.StalledAgents.Should().BeEmpty("a real change clears the stalled marker");
        await harness.Service.HandleRunEndedAsync("agent-a", endedWithError: false, cancelled: false);
        harness.Delivered.Should().HaveCount(3, "the reset re-armed the budget");
    }

    [Fact]
    public async Task AttachingAnArtifact_IsARealBoardChange_AndResetsTheBudget()
    {
        // Semantic-conflict pin against #596: artifacts became board content in PR 5, so an
        // attach-artifact is visible progress — a fingerprint that ignores the field would
        // misclassify it as a heartbeat and keep the agent marked stalled.
        var harness = new Harness();
        _ = harness.Manager.AddTask("Owned work");
        _ = harness.Manager.ClaimTask("1", "agent-a");
        _ = harness.Build(RunEndOnly);

        await harness.Service.HandleRunEndedAsync("agent-a", endedWithError: false, cancelled: false);
        await harness.Service.HandleRunEndedAsync("agent-a", endedWithError: false, cancelled: false);
        await harness.Service.HandleRunEndedAsync("agent-a", endedWithError: false, cancelled: false);
        harness.Delivered.Should().HaveCount(2, "the budget is spent");

        _ = harness.Manager.AttachArtifact("1", "docs/output.md");
        await harness.SyncAsync();

        harness.Service.StalledAgents.Should().BeEmpty("attaching a file is visible progress");
        await harness.Service.HandleRunEndedAsync("agent-a", endedWithError: false, cancelled: false);
        harness.Delivered.Should().HaveCount(3, "the artifact attach re-armed the budget");
    }

    [Fact]
    public async Task ARefusedDelivery_DoesNotConsumeBudget()
    {
        var harness = new Harness();
        _ = harness.Manager.AddTask("Owned work");
        _ = harness.Manager.ClaimTask("1", "agent-a");
        _ = harness.Build(RunEndOnly);

        harness.AcceptDeliveries = false;
        await harness.Service.HandleRunEndedAsync("agent-a", endedWithError: false, cancelled: false);
        harness.Delivered.Should().BeEmpty();

        harness.AcceptDeliveries = true;
        await harness.Service.HandleRunEndedAsync("agent-a", endedWithError: false, cancelled: false);
        await harness.Service.HandleRunEndedAsync("agent-a", endedWithError: false, cancelled: false);

        harness.DeliveryAttempts.Should().Be(3);
        harness.Delivered.Should().HaveCount(2, "only ACCEPTED deliveries count against the budget");
    }

    // ---------------------------------------------------------------- when stall nudges must never fire

    [Fact]
    public async Task NoNudge_WhenTheTierIsDisabled()
    {
        var harness = new Harness();
        _ = harness.Manager.AddTask("Owned work");
        _ = harness.Manager.ClaimTask("1", "agent-a");
        _ = harness.Build(NoticeOnly); // shipped defaults: every stall tier OFF

        await harness.Service.HandleRunEndedAsync("agent-a", endedWithError: false, cancelled: false);
        await harness.Service.HandleTurnCompletedAsync("agent-a");
        await harness.Service.EvaluateBreakdownAsync();

        harness.Delivered.Should().BeEmpty();
    }

    [Fact]
    public async Task NoNudge_ForAnErroredOrCancelledRun()
    {
        var harness = new Harness();
        _ = harness.Manager.AddTask("Owned work");
        _ = harness.Manager.ClaimTask("1", "agent-a");
        _ = harness.Build(RunEndOnly);

        await harness.Service.HandleRunEndedAsync("agent-a", endedWithError: true, cancelled: false);
        await harness.Service.HandleRunEndedAsync("agent-a", endedWithError: false, cancelled: true);

        harness.Delivered.Should().BeEmpty("failures are surfaced, not nudged");
    }

    [Fact]
    public async Task NoNudge_WhenEverythingTheAgentOwnsIsTerminal()
    {
        var harness = new Harness();
        _ = harness.Manager.AddTask("Finished work");
        _ = harness.Manager.ClaimTask("1", "agent-a");
        _ = harness.Manager.UpdateTask("1", "completed");
        _ = harness.Build(RunEndOnly);

        await harness.Service.HandleRunEndedAsync("agent-a", endedWithError: false, cancelled: false);

        harness.Delivered.Should().BeEmpty("the agent is done, not stalled");
    }

    [Fact]
    public async Task NoNudge_WhenEverythingLeftIsBlocked()
    {
        var harness = new Harness();
        _ = harness.Manager.AddTask("Blocked work");
        _ = harness.Manager.AddTask("The blocker");
        _ = harness.Manager.ClaimTask("1", "agent-a");
        _ = harness.Manager.BlockTask("1", ["2"]);
        _ = harness.Build(RunEndOnly);

        await harness.Service.HandleRunEndedAsync("agent-a", endedWithError: false, cancelled: false);

        harness.Delivered.Should().BeEmpty("waiting on a named blocker is a correct stop, not a stall");
    }

    [Fact]
    public async Task NoStallNudge_ToTheRootConversation_WithoutTheOptIn()
    {
        var harness = new Harness { Resolver = _ => TodoNudgeTargetKind.RootConversation };
        _ = harness.Manager.AddTask("Root-owned work");
        _ = harness.Manager.ClaimTask("1", "the-user");
        _ = harness.Build(RunEndOnly);

        await harness.Service.HandleRootRunEndedAsync(endedWithError: false);
        harness.Delivered.Should().BeEmpty();

        _ = harness.Build(RunEndOnly with { NudgeRootConversation = true });
        await harness.Service.HandleRootRunEndedAsync(endedWithError: false);
        harness.Delivered.Should().ContainSingle().Which.Target.Should().Be("the-user");
    }

    // ---------------------------------------------------------------- N3: idle turns

    [Fact]
    public async Task IdleTurns_NudgeAtTheThreshold_AndTheCounterReArms()
    {
        var harness = new Harness();
        _ = harness.Manager.AddTask("Owned work");
        _ = harness.Manager.ClaimTask("1", "agent-a");
        _ = harness.Build(
            new TodoNudgeOptions
            {
                AssignmentNoticeEnabled = false,
                IdleTurnsNudgeEnabled = true,
                IdleTurnThreshold = 3,
            }
        );

        await harness.Service.HandleTurnCompletedAsync("agent-a");
        await harness.Service.HandleTurnCompletedAsync("agent-a");
        harness.Delivered.Should().BeEmpty("below the threshold");

        await harness.Service.HandleTurnCompletedAsync("agent-a");
        harness.Delivered.Should().ContainSingle().Which.Message.Detail.Should().Contain("No task has moved in 3");

        // The counter re-armed; three more silent turns spend the second (and final) nudge.
        await harness.Service.HandleTurnCompletedAsync("agent-a");
        await harness.Service.HandleTurnCompletedAsync("agent-a");
        harness.Delivered.Should().HaveCount(1);
        await harness.Service.HandleTurnCompletedAsync("agent-a");
        harness.Delivered.Should().HaveCount(2);

        for (var i = 0; i < 3; i++)
        {
            await harness.Service.HandleTurnCompletedAsync("agent-a");
        }

        harness.Delivered.Should().HaveCount(2, "the budget caps N3 like every other stall tier");
        harness.Service.StalledAgents.Should().Contain("agent-a");
    }

    [Fact]
    public async Task IdleTurnCounter_ResetsOnARealBoardChange()
    {
        var harness = new Harness();
        _ = harness.Manager.AddTask("Owned work");
        _ = harness.Manager.ClaimTask("1", "agent-a");
        _ = harness.Build(
            new TodoNudgeOptions
            {
                AssignmentNoticeEnabled = false,
                IdleTurnsNudgeEnabled = true,
                IdleTurnThreshold = 2,
            }
        );

        await harness.Service.HandleTurnCompletedAsync("agent-a");
        _ = harness.Manager.AddNote("1", noteText: "progress");
        await harness.SyncAsync();
        await harness.Service.HandleTurnCompletedAsync("agent-a");

        harness.Delivered.Should().BeEmpty("the board change reset the idle-turn counter");
    }

    [Fact]
    public async Task RootTurnTicks_ReachRootAssignees_OnlyWithTheOptIn()
    {
        var harness = new Harness { Resolver = _ => TodoNudgeTargetKind.RootConversation };
        _ = harness.Manager.AddTask("Root-owned work");
        _ = harness.Manager.ClaimTask("1", "the-user");
        var optIn = new TodoNudgeOptions
        {
            AssignmentNoticeEnabled = false,
            IdleTurnsNudgeEnabled = true,
            IdleTurnThreshold = 1,
        };
        _ = harness.Build(optIn);

        await harness.Service.HandleRootTurnCompletedAsync();
        harness.Delivered.Should().BeEmpty();

        _ = harness.Build(optIn with { NudgeRootConversation = true });
        await harness.Service.HandleRootTurnCompletedAsync();
        harness.Delivered.Should().ContainSingle().Which.Target.Should().Be("the-user");
    }

    // ---------------------------------------------------------------- N4: breakdown

    [Fact]
    public async Task Breakdown_NudgesAStaleUndecomposedInProgressTask()
    {
        var harness = new Harness();
        _ = harness.Manager.AddTask("Big opaque work");
        _ = harness.Manager.ClaimTask("1", "agent-a");
        _ = harness.Build(
            new TodoNudgeOptions
            {
                AssignmentNoticeEnabled = false,
                BreakdownNudgeEnabled = true,
                BreakdownAfterMinutes = 20,
            }
        );

        await harness.Service.EvaluateBreakdownAsync();
        harness.Delivered.Should().BeEmpty("the lease is fresh");

        harness.Clock.Advance(TimeSpan.FromMinutes(21));
        await harness.Service.EvaluateBreakdownAsync();

        var message = harness.Delivered.Should().ContainSingle().Subject.Message;
        message.Detail.Should().Contain("Task 1").And.Contain("add-task");
    }

    [Fact]
    public async Task Breakdown_DoesNotNudge_ATaskThatAlreadyHasSubItems()
    {
        var harness = new Harness();
        _ = harness.Manager.AddTask("Decomposed work");
        _ = harness.Manager.ClaimTask("1", "agent-a");
        _ = harness.Manager.AddTask("Sub-item", "1");
        _ = harness.Build(
            new TodoNudgeOptions
            {
                AssignmentNoticeEnabled = false,
                BreakdownNudgeEnabled = true,
                BreakdownAfterMinutes = 20,
            }
        );

        harness.Clock.Advance(TimeSpan.FromHours(2));
        await harness.Service.EvaluateBreakdownAsync();

        harness.Delivered.Should().BeEmpty("the breakdown the nudge would ask for already exists");
    }

    // ---------------------------------------------------------------- discipline pins

    [Fact]
    public async Task AnUnmappedTargetKind_ThrowsInsteadOfSilentlyNudgingOrDropping()
    {
        // CS8524 pin: the IsTargetAllowed switch must fail loudly on an enum value nobody mapped.
        var harness = new Harness { Resolver = _ => (TodoNudgeTargetKind)999 };
        _ = harness.Manager.AddTask("Owned work");
        _ = harness.Manager.ClaimTask("1", "agent-a");
        _ = harness.Build(RunEndOnly);

        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            harness.Service.HandleRunEndedAsync("agent-a", endedWithError: false, cancelled: false)
        );
    }

    [Fact]
    public async Task ConcurrentBoardChangeHooks_CannotRewindTheBaselineToAStaleSnapshot()
    {
        // #599 review F-002. TaskManager fires OnChanged after releasing its own lock and
        // sub-agents share one manager, so two hooks race here. The board read and the fingerprint
        // must happen INSIDE _sync: read outside it, a losing thread commits a snapshot OLDER than
        // the winner's — rewinding the baseline so the winner's assignment is re-detected as a
        // transition (duplicate notice) on the next change.
        //
        // Deterministic seam, no sleep race: the LOSER's board read blocks on a gate. With the fix,
        // that read happens under the lock, so the winner cannot commit while the loser is parked —
        // the interleaving the defect needs is structurally impossible. With the read outside the
        // lock (the mutation), the winner commits the newer board first, the loser then commits the
        // stale one over it, and the final pass re-notifies agent-b: 2 notices instead of 1.
        var v0 = new List<TaskManager.TaskItem> { FakeTask("1", null), FakeTask("2", null) };
        var v1 = new List<TaskManager.TaskItem> { FakeTask("1", "agent-a"), FakeTask("2", null) };
        var v2 = new List<TaskManager.TaskItem> { FakeTask("1", "agent-a"), FakeTask("2", "agent-b") };

        var currentBoard = v0;
        var readerCalls = 0;
        using var loserIsReading = new ManualResetEventSlim(false);
        using var loserMayReturn = new ManualResetEventSlim(false);
        var deliveries = new System.Collections.Concurrent.ConcurrentQueue<string>();

        var service = new TodoNudgeService(
            new TodoNudgeOptions(),
            () =>
            {
                var snapshot = Volatile.Read(ref currentBoard);
                if (Interlocked.Increment(ref readerCalls) == 2)
                {
                    // The loser's read (call 1 is the constructor's baseline capture): park it so
                    // the winner can be raced against it deterministically.
                    loserIsReading.Set();
                    _ = loserMayReturn.Wait(TimeSpan.FromSeconds(10));
                }

                return snapshot;
            },
            _ => TodoNudgeTargetKind.SubAgent,
            (name, _, _) =>
            {
                deliveries.Enqueue(name);
                return ValueTask.FromResult(true);
            },
            new FakeTimeProvider()
        );

        Volatile.Write(ref currentBoard, v1);
        var loser = Task.Run(() => service.HandleBoardChangedAsync());
        loserIsReading.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("the loser must reach its board read");

        Volatile.Write(ref currentBoard, v2);
        var winner = Task.Run(() => service.HandleBoardChangedAsync());
        // With the read outside the lock the winner finishes here; with the fix it is lock-blocked
        // behind the parked loser, and this wait simply elapses — both outcomes are deterministic.
        _ = await Task.WhenAny(winner, Task.Delay(TimeSpan.FromSeconds(1)));

        loserMayReturn.Set();
        await loser;
        await winner;

        // The settling pass every real burst ends with: one more hook fire over the final board.
        await service.HandleBoardChangedAsync();

        deliveries
            .Count(name => name == "agent-b")
            .Should()
            .Be(1, "a stale commit rewinds the baseline and re-notifies");
        deliveries.Count(name => name == "agent-a").Should().Be(1);
    }

    private static TaskManager.TaskItem FakeTask(string id, string? assignee)
    {
        return new TaskManager.TaskItem
        {
            Id = id,
            Status = AchieveAi.LmDotnetTools.Misc.Utils.TaskManager.TaskStatus.NotStarted,
            Title = $"Task {id}",
            Notes = [],
            SubTasks = [],
            Assignee = assignee,
        };
    }

    [Fact]
    public async Task BoardChangeBookkeeping_AloneDeliversNothing()
    {
        // The service's OnChanged subscription is bookkeeping: without an assignment transition it
        // must never inject anything, no matter how much the board churns.
        var harness = new Harness();
        _ = harness.Build(NoticeOnly);

        _ = harness.Manager.AddTask("One");
        await harness.SyncAsync();
        _ = harness.Manager.AddTask("Two");
        await harness.SyncAsync();
        _ = harness.Manager.ClaimTask("1", "agent-a");
        await harness.SyncAsync();
        _ = harness.Manager.UpdateTask("1", "completed");
        await harness.SyncAsync();
        _ = harness.Manager.AddNote("2", noteText: "a note");
        await harness.SyncAsync();

        harness.Delivered.Should().BeEmpty();
        harness.DeliveryAttempts.Should().Be(0);
    }
}
