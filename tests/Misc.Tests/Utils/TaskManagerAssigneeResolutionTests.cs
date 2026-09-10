using AchieveAi.LmDotnetTools.Misc.Utils;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Misc.Tests.Utils;

/// <summary>
///     #672: an assignee name may not silently decide ownership. Every path that writes
///     <c>Assignee</c> — <c>add-task</c>, <c>claim-task</c>, <c>assign-task</c>, and <c>update-task</c>
///     moving a row to in-progress on an agent's behalf — asks the host's
///     <see cref="TaskManager.AssigneeResolver" /> first, refuses a name that matches more than one
///     agent or no agent, and stores the resolved canonical identity so the ordinal comparisons that
///     decide ownership compare one stable string. With no resolver wired the board behaves exactly as
///     it did before.
/// </summary>
/// <remarks>
///     <c>add-task</c> is listed above because it did NOT resolve until #agent-naming: it wrote the
///     caller's text verbatim while every other path wrote the resolved identity, so a task assigned by
///     name could never be claimed by the agent it was assigned to. This file's own class doc used to
///     enumerate the other three and omit it, which is how the hole stayed invisible.
/// </remarks>
public class TaskManagerAssigneeResolutionTests
{
    private const string TaskTitle = "Wire the SSE endpoint";

    private static TaskManager BoardWithOneTask(out string taskId)
    {
        var board = new TaskManager();
        _ = board.AddTask(TaskTitle);
        taskId = "1";
        return board;
    }

    private static TaskManager.AssigneeResolution Ambiguous(params string[] candidates) =>
        new(null, null, TaskManager.AssigneeLiveness.Unknown, candidates);

    private static TaskManager.AssigneeResolution Unknown() => new(null, null, TaskManager.AssigneeLiveness.Unknown);

    private static TaskManager.AssigneeResolution Live(string agentId) =>
        new(agentId, agentId, TaskManager.AssigneeLiveness.Live);

    /// <summary>A live agent whose human name differs from the identity ownership is keyed on.</summary>
    private static TaskManager.AssigneeResolution LiveNamed(string agentId, string displayName) =>
        new(agentId, agentId, TaskManager.AssigneeLiveness.Live, Candidates: null, DisplayName: displayName);

    /// <summary>An unknown name, with the names the host could have meant riding alongside.</summary>
    private static TaskManager.AssigneeResolution UnknownAmong(params string[] knownNames) =>
        new(
            null,
            null,
            TaskManager.AssigneeLiveness.Unknown,
            Candidates: null,
            DisplayName: null,
            KnownNames: knownNames
        );

    [Fact]
    public void AssignTask_Receipt_SpeaksTheResolvedName_NotTheIdentity()
    {
        // The board keys on agent-3 and says reviewer. The receipt is the sentence the assigning model
        // reads back, so an ordinal here teaches it to address the agent by a number it was never told.
        var board = BoardWithOneTask(out var taskId);
        board.AssigneeResolver = _ => LiveNamed("agent-3", "reviewer");

        var result = board.AssignTask(taskId, "reviewer");

        result.IsError.Should().BeFalse();
        result.Text.Should().Contain("Assigned task 1 to reviewer.").And.NotContain("agent-3");
        board.GetTasks().Single().Assignee.Should().Be("agent-3");
    }

    [Fact]
    public void AssignTask_Receipt_WithNoDisplayName_StillSpeaksTheStoredAssignee()
    {
        var board = BoardWithOneTask(out var taskId);
        board.AssigneeResolver = _ => Live("agent-3");

        board.AssignTask(taskId, "agent-3").Text.Should().Contain("Assigned task 1 to agent-3.");
    }

    [Fact]
    public void AlreadyClaimed_Refusals_SpeakTheHoldersName()
    {
        // Both lease refusals — claim-task's and assign-task's — name the holder so the refused caller
        // can go talk to them. "wait for agent-3" is an address nothing else in the conversation uses.
        var board = BoardWithOneTask(out var taskId);
        board.AssigneeResolver = name =>
            name == "reviewer" ? LiveNamed("agent-3", "reviewer") : LiveNamed("agent-4", "author");
        board.ClaimTask(taskId, "reviewer").IsError.Should().BeFalse();

        var claim = board.ClaimTask(taskId, "author");
        var assign = board.AssignTask(taskId, "author");

        claim.ErrorCode.Should().Be("task_already_claimed");
        claim.Text.Should().Contain("claimed by reviewer").And.NotContain("agent-3");
        assign.ErrorCode.Should().Be("task_already_claimed");
        assign.Text.Should().Contain("claimed by reviewer").And.Contain("wait for reviewer").And.NotContain("agent-3");
    }

    [Fact]
    public void UnknownAssignee_Refusal_NamesTheAgentsTheHostKnows()
    {
        // The refusal is the one place a model that guessed a name learns the real ones. Listing them
        // here saves the roster call the old sentence sent it on — and the old sentence sent it after
        // an *id*, which is the wrong thing to come back with.
        var board = BoardWithOneTask(out var taskId);
        board.AssigneeResolver = _ => UnknownAmong("MainAgent", "reviewer", "reviewer-2");

        var result = board.AssignTask(taskId, "lead");

        result.ErrorCode.Should().Be("assignee_unknown");
        result.Text.Should().Contain("'lead' does not name an agent in this conversation");
        result.Text.Should().Contain("Agents in this conversation: MainAgent, reviewer, reviewer-2.");
        result.Text.Should().NotContain("numbered").And.NotContain("Agent ids");
    }

    [Fact]
    public void UnknownAssignee_Refusal_WithNoNamesToOffer_PointsAtTheRoster()
    {
        // A host that resolves without a directory has no names to offer; the fallback still sends the
        // caller after a name, not an id.
        var board = BoardWithOneTask(out var taskId);
        board.AssigneeResolver = _ => Unknown();

        var result = board.AssignTask(taskId, "lead");

        result.ErrorCode.Should().Be("assignee_unknown");
        result.Text.Should().Contain("does not name an agent in this conversation");
        result.Text.Should().Contain("agent listing").And.NotContain("Agent ids");
    }

    [Fact]
    public void AddTask_WithAnUnknownAssignee_IsRefused()
    {
        // Mutation that must go red: removing the ResolveAssignee call from AddTaskCore.
        var board = new TaskManager { AssigneeResolver = _ => Unknown() };

        var result = board.AddTask(TaskTitle, parentId: null, assignee: "reviewer");

        result.IsError.Should().BeTrue();
        result.ErrorCode.Should().Be("assignee_unknown");
        board.GetTasks().Should().BeEmpty();
    }

    [Fact]
    public void AddTask_ThenClaimByTheSameName_LeavesTheRowOwnedByTheResolvedIdentity()
    {
        // The D1 regression, end to end. The assertion that discriminates is the one taken BEFORE the
        // claim: claiming a not-started row overwrites its assignee outright, so "add by name then
        // claim by name succeeds" is true even with the create path writing raw text. What was broken
        // is what the row is OWNED BY between the two calls — every other reader of the board (the
        // one-in-progress-per-assignee sweep, assign-task's lease comparison, an agent looking for
        // its own work under the ordinal GetAgents handed it) compares the resolved identity.
        var board = new TaskManager { AssigneeResolver = _ => Live("agent-3") };
        _ = board.AddTask(TaskTitle, parentId: null, assignee: "reviewer");

        board.GetTasks().Single().Assignee.Should().Be("agent-3");

        var claim = board.ClaimTask("1", "reviewer");

        claim.IsError.Should().BeFalse();
        var task = board.GetTasks().Single();
        task.Assignee.Should().Be("agent-3");
        task.Status.Should().Be(TaskManager.TaskStatus.InProgress);
    }

    [Fact]
    public void AddTask_WithNoResolverWired_KeepsTheRawTextExactlyAsBefore()
    {
        var board = new TaskManager();

        _ = board.AddTask(TaskTitle, parentId: null, assignee: "reviewer");

        board.GetTasks().Single().Assignee.Should().Be("reviewer");
    }

    [Fact]
    public void AddTask_SubItemInheritsTheCanonicalAssignee_NotTheTypedName()
    {
        // Inheritance is the mechanism behind "lead assigns, assignee breaks it down", so the value
        // it copies down has to be the same identity ownership compares — otherwise every sub-item
        // reintroduces the hole its parent just closed.
        var board = new TaskManager { AssigneeResolver = _ => Live("agent-3") };
        _ = board.AddTask(TaskTitle, parentId: null, assignee: "reviewer");

        _ = board.AddTask("Break it down", parentId: "1");

        board.GetTasks().Single().SubTasks.Single().Assignee.Should().Be("agent-3");
    }

    [Fact]
    public void ListTasks_ShowsTheDisplayNameWhileOwnershipComparesTheId()
    {
        // The board speaks names and keys on ids. Both halves are asserted together on purpose: a
        // renderer that showed the name by storing it as the assignee would satisfy the first half
        // while putting free text back in the ownership key.
        var board = new TaskManager { AssigneeResolver = _ => LiveNamed("agent-3", "reviewer") };
        _ = board.AddTask(TaskTitle, parentId: null, assignee: "reviewer");

        board.ListTasks().Text.Should().Contain("reviewer").And.NotContain("agent-3");
        board.GetTasks().Single().Assignee.Should().Be("agent-3");
    }

    [Fact]
    public void ListTasks_WithNoDisplayName_StillShowsTheStoredAssignee()
    {
        // The fallback, pinned: a resolver that reports no display name — and the no-resolver board —
        // must render exactly what they rendered before this member existed.
        var board = new TaskManager { AssigneeResolver = _ => Live("agent-3") };
        _ = board.AddTask(TaskTitle, parentId: null, assignee: "reviewer");

        board.ListTasks().Text.Should().Contain("agent-3");
    }

    [Fact]
    public void ClaimTask_StoresTheDisplayNameBesideTheIdentity_NotInsteadOfIt()
    {
        var board = new TaskManager { AssigneeResolver = _ => LiveNamed("agent-3", "reviewer") };
        _ = board.AddTask(TaskTitle);

        _ = board.ClaimTask("1", "agent-3");

        board.GetTasks().Single().Assignee.Should().Be("agent-3");
        board.ListTasks().Text.Should().Contain("reviewer");
    }

    [Fact]
    public void ADisplayNameSurvivesTheSnapshotRoundTrip()
    {
        // It has to persist beside the assignee for the same reason the assignee itself does (#595):
        // a rehydrated board that lost it would silently start rendering ordinals again after a
        // restart, which reads as the feature having been reverted.
        var board = new TaskManager { AssigneeResolver = _ => LiveNamed("agent-3", "reviewer") };
        _ = board.AddTask(TaskTitle, parentId: null, assignee: "reviewer");

        var rehydrated = TaskManager.FromSnapshot(board.GetTodoBoardSnapshot("thread-1"));

        rehydrated.GetTasks().Single().Assignee.Should().Be("agent-3");
        rehydrated.ListTasks().Text.Should().Contain("reviewer").And.NotContain("agent-3");
    }

    [Fact]
    public void ALegacyRowHoldingTheRawName_IsStillOwnedByTheAgentItNames_AndConvergesOnFirstTouch()
    {
        // A board snapshotted before add-task resolved its assignee carries "reviewer" where every newer
        // row carries "agent-3". Comparing those strings made the same agent a stranger to its own row
        // after a restart: its refresh was refused as already claimed, and a second claim left it with
        // two active tasks, one under each spelling.
        // Mutation that must go red: IsHeldBy returning the plain ordinal comparison.
        var legacy = new TaskManager();
        _ = legacy.AddTask(TaskTitle, parentId: null, assignee: "reviewer");
        _ = legacy.AddTask("Second unit of work");
        _ = legacy.ClaimTask("1", "reviewer");
        var snapshot = legacy.GetTodoBoardSnapshot("thread-1");
        snapshot.Tasks.Should().Contain(t => t.Assignee == "reviewer", "the fixture is the pre-resolution shape");

        var board = TaskManager.FromSnapshot(snapshot);
        board.AssigneeResolver = _ => LiveNamed("agent-3", "reviewer");

        var refresh = board.ClaimTask("1", "reviewer");

        refresh.IsError.Should().BeFalse(refresh.Text);
        refresh
            .Text.Should()
            .Contain("claim refreshed", "the agent is recognised as the holder, not refused as a stranger");
        var converged = board.GetTasks().Single(t => t.Id == "1");
        converged.Assignee.Should().Be("agent-3", "the row is rewritten in the canonical shape");
        board.ListTasks().Text.Should().Contain("reviewer");

        // The one-active-task rule sees through the legacy spelling too: claiming another task releases
        // the first instead of leaving the agent holding both.
        var second = board.ClaimTask("2", "reviewer");

        second.IsError.Should().BeFalse(second.Text);
        second.Text.Should().Contain("Released task 1");
        board.GetTasks().Single(t => t.Id == "1").Status.Should().Be(TaskManager.TaskStatus.NotStarted);
        board.GetTasks().Single(t => t.Id == "2").Assignee.Should().Be("agent-3");
    }

    [Fact]
    public void ALegacyRowWhoseNameIsNowAmbiguous_IsNotHandedToEitherClaimant()
    {
        // Convergence is only safe when the legacy name resolves to exactly one live agent. Two agents
        // called reviewer means the row's owner is unknowable, and guessing would give one of them a task
        // it never claimed. The fixture's resolver picks agent-3 AND reports the contest, so the only
        // thing standing between the caller and the row is the candidate check itself — the board's
        // convention everywhere else is that more than one candidate means ambiguous, whatever else the
        // resolver filled in.
        var legacy = new TaskManager();
        _ = legacy.AddTask(TaskTitle, parentId: null, assignee: "reviewer");
        _ = legacy.ClaimTask("1", "reviewer");
        var board = TaskManager.FromSnapshot(legacy.GetTodoBoardSnapshot("thread-1"));
        board.AssigneeResolver = name =>
            name == "reviewer"
                ? new TaskManager.AssigneeResolution(
                    "agent-3",
                    "agent-3",
                    TaskManager.AssigneeLiveness.Live,
                    Candidates: ["agent-3", "agent-4"],
                    DisplayName: "reviewer"
                )
                : LiveNamed("agent-3", "reviewer");

        var claim = board.ClaimTask("1", "agent-3");

        claim.IsError.Should().BeTrue("a live lease held under an unresolvable name is not the caller's to refresh");
        board.GetTasks().Single().Assignee.Should().Be("reviewer", "nothing was rewritten on a guess");
    }

    [Fact]
    public void ClaimTask_RefreshingAnExistingClaimByName_ResolvesBeforeComparing()
    {
        // The refresh branch compared task.Assignee — the RESOLVED identity — against the caller's raw
        // trimmed text, so an agent heartbeating its own claim by name never matched its own lease and
        // fell through to the take-a-claim path instead. The result text is what discriminates: both
        // paths leave the row claimed by the same agent, and only the refresh branch says so.
        var board = new TaskManager { AssigneeResolver = _ => Live("agent-3") };
        _ = board.AddTask(TaskTitle);
        _ = board.ClaimTask("1", "reviewer");

        var refresh = board.ClaimTask("1", "reviewer");

        refresh.IsError.Should().BeFalse();
        refresh.Text.Should().Contain("claim refreshed");
        board.GetTasks().Single().Assignee.Should().Be("agent-3");
    }

    [Fact]
    public void RefreshingAClaimOnARowPersistedBeforeDisplayNamesExisted_FillsTheNameIn()
    {
        // A row hydrated from a snapshot written before assigneeDisplayName existed carries the
        // identity and no name, and nothing re-runs the resolver on hydration. The holder's next
        // heartbeat is what fills it in — without that, every row claimed before this change would
        // keep rendering its ordinal for the rest of its life and read as the feature not shipping.
        var board = new TaskManager { AssigneeResolver = _ => LiveNamed("agent-3", "reviewer") };
        _ = board.AddTask(TaskTitle);
        _ = board.ClaimTask("1", "reviewer");

        var snapshot = board.GetTodoBoardSnapshot("thread-1");
        var withoutTheName = snapshot with { Tasks = [snapshot.Tasks[0] with { AssigneeDisplayName = null }] };
        var rehydrated = TaskManager.FromSnapshot(withoutTheName);
        rehydrated.AssigneeResolver = _ => LiveNamed("agent-3", "reviewer");

        // The pre-state, so this cannot pass by the name having survived hydration after all.
        rehydrated.ListTasks().Text.Should().Contain("agent-3");

        var refresh = rehydrated.ClaimTask("1", "reviewer");

        refresh.IsError.Should().BeFalse();
        refresh.Text.Should().Contain("claim refreshed");
        rehydrated.ListTasks().Text.Should().Contain("reviewer").And.NotContain("agent-3");
        rehydrated.GetTasks().Single().Assignee.Should().Be("agent-3");
    }

    [Fact]
    public void ClaimTask_WithAmbiguousAgent_IsRefusedAndLeavesTheRowUntouched()
    {
        // Mutation that must go red: dropping the ambiguity arm from ResolveAssignee.
        var board = BoardWithOneTask(out var taskId);
        board.AssigneeResolver = _ => Ambiguous("agent-1", "agent-4");

        var result = board.ClaimTask(taskId, "reviewer");

        result.IsError.Should().BeTrue();
        result.ErrorCode.Should().Be("assignee_ambiguous");
        result.Text.Should().Contain("agent-1").And.Contain("agent-4");

        var task = board.GetTasks().Single();
        task.Assignee.Should().BeNull();
        task.Status.Should().Be(TaskManager.TaskStatus.NotStarted);
    }

    [Fact]
    public void AssignTask_WithAmbiguousAgent_IsRefused()
    {
        var board = BoardWithOneTask(out var taskId);
        board.AssigneeResolver = _ => Ambiguous("agent-1", "agent-4");

        var result = board.AssignTask(taskId, "reviewer");

        result.IsError.Should().BeTrue();
        result.ErrorCode.Should().Be("assignee_ambiguous");
        board.GetTasks().Single().Assignee.Should().BeNull();
    }

    [Fact]
    public void UpdateTask_ToInProgress_WithAmbiguousAgent_IsRefused()
    {
        var board = BoardWithOneTask(out var taskId);
        board.AssigneeResolver = _ => Ambiguous("agent-1", "agent-4");

        var result = board.UpdateTask(taskId, "in progress", "reviewer");

        result.IsError.Should().BeTrue();
        result.ErrorCode.Should().Be("assignee_ambiguous");
        board.GetTasks().Single().Status.Should().Be(TaskManager.TaskStatus.NotStarted);
    }

    [Fact]
    public void ClaimTask_WithUnknownAgent_IsRefused()
    {
        // Mutation that must go red: dropping the Unknown arm from ResolveAssignee.
        var board = BoardWithOneTask(out var taskId);
        board.AssigneeResolver = _ => Unknown();

        var result = board.ClaimTask(taskId, "ghost");

        result.IsError.Should().BeTrue();
        result.ErrorCode.Should().Be("assignee_unknown");
        board.GetTasks().Single().Assignee.Should().BeNull();
    }

    [Fact]
    public void AssignTask_WithUnknownAgent_IsRefused()
    {
        var board = BoardWithOneTask(out var taskId);
        board.AssigneeResolver = _ => Unknown();

        var result = board.AssignTask(taskId, "ghost");

        result.IsError.Should().BeTrue();
        result.ErrorCode.Should().Be("assignee_unknown");
        board.GetTasks().Single().Assignee.Should().BeNull();
    }

    [Fact]
    public void ClaimTask_StoresTheResolvedCanonicalIdentity_NotTheTypedName()
    {
        // Mutation that must go red: writing the caller's text instead of CanonicalName.
        var board = BoardWithOneTask(out var taskId);
        board.AssigneeResolver = _ => Live("agent-3");

        var result = board.ClaimTask(taskId, "Reviewer Bot");

        result.IsError.Should().BeFalse();
        board.GetTasks().Single().Assignee.Should().Be("agent-3");
    }

    [Fact]
    public void AssignTask_StoresTheResolvedCanonicalIdentity_NotTheTypedName()
    {
        var board = BoardWithOneTask(out var taskId);
        board.AssigneeResolver = _ => Live("agent-3");

        var result = board.AssignTask(taskId, "Reviewer Bot");

        result.IsError.Should().BeFalse();
        board.GetTasks().Single().Assignee.Should().Be("agent-3");
    }

    [Fact]
    public void ASingleCandidateIsNotAmbiguous()
    {
        // Pins the qualifier, not just the clause: a resolver that reports the one agent it matched
        // has decided ownership, and refusing that would make Candidates unusable as an audit trail.
        var board = BoardWithOneTask(out var taskId);
        board.AssigneeResolver = _ => new TaskManager.AssigneeResolution(
            "agent-3",
            "agent-3",
            TaskManager.AssigneeLiveness.Live,
            ["agent-3"]
        );

        var result = board.ClaimTask(taskId, "alpha");

        result.IsError.Should().BeFalse();
        board.GetTasks().Single().Assignee.Should().Be("agent-3");
    }

    [Fact]
    public void UnreachableAgent_StillOwnsTheClaim()
    {
        // Reachability is not authority: an agent that stopped responding keeps its lease so the
        // stale-lease path stays the single way a claim changes hands.
        var board = BoardWithOneTask(out var taskId);
        board.AssigneeResolver = _ => new TaskManager.AssigneeResolution(
            "agent-3",
            "agent-3",
            TaskManager.AssigneeLiveness.Unreachable
        );

        var result = board.ClaimTask(taskId, "Reviewer Bot");

        result.IsError.Should().BeFalse();
        board.GetTasks().Single().Assignee.Should().Be("agent-3");
    }

    [Fact]
    public void UnreachableWithNoResolvedIdentity_IsRefused()
    {
        // #676 made a restarted-away agent resolvable as not-live, and a tombstone carries no directory
        // entry — so a resolver can now legitimately report "not reachable" while being unable to name
        // the agent. Recording that would put the caller's raw text back in the ownership key, which is
        // the exact free-text ownership this whole feature removes.
        var board = BoardWithOneTask(out var taskId);
        board.AssigneeResolver = _ => new TaskManager.AssigneeResolution(
            null,
            null,
            TaskManager.AssigneeLiveness.Unreachable
        );

        var result = board.ClaimTask(taskId, "ghost");

        result.IsError.Should().BeTrue();
        result.ErrorCode.Should().Be("assignee_unknown");
        board.GetTasks().Single().Assignee.Should().BeNull();
    }

    [Fact]
    public void WithNoResolver_TheRawNameStillDecidesOwnership()
    {
        // The board must keep working with no collaboration layer wired at all.
        var board = BoardWithOneTask(out var taskId);

        var result = board.ClaimTask(taskId, "reviewer");

        result.IsError.Should().BeFalse();
        board.GetTasks().Single().Assignee.Should().Be("reviewer");
    }

    [Fact]
    public void TheResolverIsAskedOnceWithTheTrimmedName()
    {
        var board = BoardWithOneTask(out var taskId);
        var asked = new List<string>();
        board.AssigneeResolver = name =>
        {
            asked.Add(name);
            return Live("agent-3");
        };

        _ = board.ClaimTask(taskId, "  Reviewer Bot  ");

        asked.Should().Equal("Reviewer Bot");
    }
}
