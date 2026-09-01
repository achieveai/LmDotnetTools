using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.Misc.Utils;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AchieveAi.LmDotnetTools.Misc.Tests.Utils;

/// <summary>
///     Rehydration from a persisted board (#583 PR 2, review F-002): a <see cref="TaskManager" />
///     recreated via <see cref="TaskManager.FromSnapshot(TodoBoardSnapshot)" /> starts from the durable
///     board — same ids, statuses, titles, notes, and nesting — instead of empty, where its first
///     mutation would persist a one-row board over the real one (the writer's empty-board guard and the
///     projection's monotonic guard both pass that write, because the fresh capture is non-empty and
///     genuinely newer).
/// </summary>
public class TaskManagerFromSnapshotTests
{
    /// <summary>Builds a manager with nesting, notes, and every persistable status but Removed.</summary>
    private static TaskManager BuildPopulatedManager()
    {
        var manager = new TaskManager();
        _ = manager.AddTask("Wire the SSE endpoint"); // 1
        _ = manager.AddTask("Add the map", "1"); // 1.1
        _ = manager.AddTask("Pin the schema", "1.1"); // 1.1.1
        _ = manager.AddTask("Vitest coverage"); // 2
        _ = manager.AddTask("Ship it"); // 3
        _ = manager.AddNote("1", noteText: "waiting on schema");
        _ = manager.AddNote("1", noteText: "unblocked by 2");
        _ = manager.ClaimTask("1", "agent-a");
        _ = manager.ClaimTask("2", "agent-b");
        _ = manager.UpdateTask("2", "completed", "agent-b");
        _ = manager.BlockTask("3", ["1"]);
        _ = manager.AttachArtifact("2", "out/report.md");
        return manager;
    }

    [Fact]
    public void FromSnapshot_RestoresIdsStatusesTitlesNotesAndNesting_Exactly()
    {
        // Mutation that must go red: hydrating through BulkInitialize (renumbers from 1, flattens
        // nesting, resets every status), or casting statuses instead of mapping member-by-member.
        var original = BuildPopulatedManager().GetTodoBoardSnapshot("conv-1");

        var rehydrated = TaskManager.FromSnapshot(original).GetTodoBoardSnapshot("conv-1");

        rehydrated.Tasks.Should().HaveCount(original.Tasks.Count);
        rehydrated
            .Tasks.Select(t => (t.Id, t.Status, t.Title))
            .Should()
            .Equal(original.Tasks.Select(t => (t.Id, t.Status, t.Title)));

        var task1 = rehydrated.Tasks.Single(t => t.Id == "1");
        task1.Status.Should().Be(TodoTaskStatus.InProgress);
        task1.Notes.Should().Equal("waiting on schema", "unblocked by 2");
        task1.SubTasks.Should().ContainSingle().Which.Id.Should().Be("1.1");
        task1.SubTasks[0].SubTasks.Should().ContainSingle().Which.Id.Should().Be("1.1.1");

        rehydrated.Tasks.Single(t => t.Id == "2").Status.Should().Be(TodoTaskStatus.Completed);
        rehydrated.Tasks.Single(t => t.Id == "3").Status.Should().Be(TodoTaskStatus.Blocked);

        // Artifacts are part of the persisted board (PR 5) — unlike leases, they must survive the
        // restart hydration exists for, or every chip vanishes on the first post-restart mutation.
        // Mutation that must go red: FromBoardNode dropping node.Artifacts.
        rehydrated
            .Tasks.Single(t => t.Id == "2")
            .Artifacts.Should()
            .ContainSingle()
            .Which.Should()
            .Be("out/report.md");
    }

    [Fact]
    public void FromSnapshot_AdvancesIdCounters_SoNewRowsNeverCollideWithHydratedOnes()
    {
        // Mutation that must go red: resetting NextId/NextSubTaskId to 1 during hydration — the next
        // add-task would then mint "1" again and two rows would share an id.
        var snapshot = BuildPopulatedManager().GetTodoBoardSnapshot("conv-1");

        var rehydrated = TaskManager.FromSnapshot(snapshot);
        var newRoot = rehydrated.AddTask("Fresh root row");
        var newNested = rehydrated.AddTask("Fresh nested row", "1");

        newRoot.ErrorCode.Should().BeNull();
        newNested.ErrorCode.Should().BeNull();

        var board = rehydrated.GetTodoBoardSnapshot("conv-1");
        board.Tasks.Select(t => t.Id).Should().OnlyHaveUniqueItems();
        board.Tasks.Single(t => t.Title == "Fresh root row").Id.Should().Be("4");
        board.Tasks.Single(t => t.Id == "1").SubTasks.Single(t => t.Title == "Fresh nested row").Id.Should().Be("1.2");
    }

    [Fact]
    public void FromSnapshot_ThenOneMutation_YieldsTheFullBoardPlusTheNewRow_NotATruncatedOne()
    {
        // The F-002 defect in miniature: recreate, mutate once, and the capture the writer would
        // persist must still carry every pre-existing row. Mutation that must go red: hydrating with
        // `new TaskManager()` instead of FromSnapshot.
        var persisted = BuildPopulatedManager().GetTodoBoardSnapshot("conv-1");

        var recreated = TaskManager.FromSnapshot(persisted);
        _ = recreated.AddTask("First task after the recreate");
        var captured = recreated.GetTodoBoardSnapshot("conv-1");

        captured.Tasks.Should().HaveCount(persisted.Tasks.Count + 1);
        captured
            .Tasks.Select(t => t.Id)
            .Should()
            .Contain(persisted.Tasks.Select(t => t.Id), "no persisted row may be truncated by the recreate");
        captured.Tasks.Should().ContainSingle(t => t.Title == "First task after the recreate");
    }

    [Fact]
    public void FromSnapshot_RejectsNull()
    {
        var act = () => TaskManager.FromSnapshot(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void FromSnapshot_RoundTripsBlockedBy_SoTheBlockStillRefusesClaimsAfterARestart()
    {
        // #595 (review 590/D-1): a row persisted Blocked used to rehydrate with an empty BlockedBy,
        // so RefuseIfBlocked passed and the block had no force while the panel still showed Blocked.
        // The full restart path is exercised — snapshot serialized to JSON and read back, exactly
        // what the projection persists — so the wire must actually carry the field. Mutations that
        // must go red: dropping BlockedBy from ToBoardNode, dropping the AddRange in FromBoardNode
        // (the legacy normalization then downgrades the row and the claim sails through), or
        // gutting RefuseIfBlocked.
        var manager = BuildPopulatedManager(); // task 3 is Blocked by task 1 (still InProgress)
        var json = JsonSerializer.Serialize(manager.GetTodoBoardSnapshot("conv-1"));
        var persisted = JsonSerializer.Deserialize<TodoBoardSnapshot>(json)!;

        var rehydrated = TaskManager.FromSnapshot(persisted);

        var blockedRow = rehydrated.GetTasks().Single(t => t.Id == "3");
        blockedRow.Status.Should().Be(TaskManager.TaskStatus.Blocked);
        blockedRow.BlockedBy.Should().ContainSingle().Which.Should().Be("1");

        var claim = rehydrated.ClaimTask("3", "agent-c");
        claim.IsError.Should().BeTrue();
        claim.ErrorCode.Should().Be("task_blocked");

        var agentlessStart = rehydrated.UpdateTask("3", "in progress");
        agentlessStart.IsError.Should().BeTrue();
        agentlessStart.ErrorCode.Should().Be("task_blocked");

        // And the recorded blocker still lifts the block: completing task 1 auto-unblocks task 3,
        // which only works because the blocker id itself survived the restart. Task 1 carries its
        // own subtree (1.1, 1.1.1 from BuildPopulatedManager) — task 22 refuses to complete a
        // parent over an open descendant, so those must be finished first, under a different
        // assignee so claiming them does not release task 1's own live lease.
        _ = rehydrated.ClaimTask("1.1.1", "agent-sub");
        _ = rehydrated.UpdateTask("1.1.1", "completed");
        _ = rehydrated.ClaimTask("1.1", "agent-sub");
        _ = rehydrated.UpdateTask("1.1", "completed");

        _ = rehydrated.ClaimTask("1", "agent-a");
        var complete = rehydrated.UpdateTask("1", "completed");
        complete.IsError.Should().BeFalse();
        rehydrated.GetTasks().Single(t => t.Id == "3").Status.Should().Be(TaskManager.TaskStatus.NotStarted);
    }

    [Fact]
    public void FromSnapshot_LegacyBlockedRowWithoutBlockedBy_LoadsAndHydratesAsNotStarted()
    {
        // Literal-payload compat (#595): the exact camelCase shape pre-#595 builds persisted carries
        // no `blockedBy` key at all. Such a board must still load, and its Blocked row — whose
        // blockers are unrecoverable — must come back NotStarted rather than rendering a block
        // nothing enforces. Mutation that must go red: removing the Blocked-with-empty-BlockedBy
        // normalization in FromBoardNode.
        const string legacyJson = """
            {"ThreadId":"conv-1","SchemaVersion":1,"CapturedAtUtc":"2026-08-29T12:00:00+00:00","Tasks":[{"id":"1","status":"InProgress","title":"The blocker","notes":[],"subTasks":[]},{"id":"2","status":"Blocked","title":"The dependent","notes":["waiting on 1"],"subTasks":[]}]}
            """;
        var persisted = JsonSerializer.Deserialize<TodoBoardSnapshot>(legacyJson)!;

        var rehydrated = TaskManager.FromSnapshot(persisted);

        var tasks = rehydrated.GetTasks();
        tasks.Should().HaveCount(2);
        var formerlyBlocked = tasks.Single(t => t.Id == "2");
        formerlyBlocked.Status.Should().Be(TaskManager.TaskStatus.NotStarted);
        formerlyBlocked.BlockedBy.Should().BeEmpty();
        formerlyBlocked.Notes.Should().ContainSingle().Which.Should().Be("waiting on 1");

        // The normalized row is honest: it is claimable, matching what the guards would enforce.
        var claim = rehydrated.ClaimTask("2", "agent-c");
        claim.IsError.Should().BeFalse();
    }

    [Fact]
    public void FromSnapshot_RoundTripsTheClaim_SoTheSameAgentCanCompleteAfterARecreate()
    {
        // #595 D2, the exact reproduction: claim in turn 1, reconnect recreates the agent via
        // FromSnapshot, and the turn-2 completion used to fail task_not_claimed because the
        // snapshot carried no assignee. Serialized JSON in the loop so the wire must actually
        // carry the field. Mutation that must go red: dropping Assignee from ToBoardNode or
        // FromBoardNode.
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-29T12:00:00Z"));
        var manager = new TaskManager(clock);
        _ = manager.AddTask("The claimed row");
        _ = manager.ClaimTask("1", "agent-a");

        var json = JsonSerializer.Serialize(manager.GetTodoBoardSnapshot("conv-1"));
        var persisted = JsonSerializer.Deserialize<TodoBoardSnapshot>(json)!;
        var rehydrated = TaskManager.FromSnapshot(persisted, clock);

        var row = rehydrated.GetTasks().Single();
        row.Status.Should().Be(TaskManager.TaskStatus.InProgress);
        row.Assignee.Should().Be("agent-a");

        clock.Advance(TimeSpan.FromMinutes(2));
        var complete = rehydrated.UpdateTask("1", "completed");
        complete.IsError.Should().BeFalse("the agent that claimed the row must still hold it after the recreate");
        rehydrated.GetTasks().Single().Status.Should().Be(TaskManager.TaskStatus.Completed);
    }

    [Fact]
    public void FromSnapshot_RoundTripsClaimedAt_SoTheLeaseAgesFromTheTrueClaim_NotTheCapture()
    {
        // The lease timestamp must round-trip as DATA, not be refit at hydration: a foreign claim
        // is refused while the true lease is live, and succeeds the moment the TRUE claim age
        // crosses the staleness threshold — two minutes after a capture whose instant, if it were
        // used as the lease age, would keep refusing for another thirteen. Mutation that must go
        // red: dropping ClaimedAt from the round-trip (the capture-instant fallback then keeps the
        // takeover refused).
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-29T12:00:00Z"));
        var manager = new TaskManager(clock);
        _ = manager.AddTask("The claimed row");
        _ = manager.ClaimTask("1", "agent-a"); // claimedAt = 12:00

        clock.Advance(TimeSpan.FromMinutes(14)); // captured at 12:14
        var json = JsonSerializer.Serialize(manager.GetTodoBoardSnapshot("conv-1"));
        var rehydrated = TaskManager.FromSnapshot(JsonSerializer.Deserialize<TodoBoardSnapshot>(json)!, clock);

        // 12:14 — the lease is 14 minutes old by the round-tripped claimedAt: still live.
        var earlyForeignClaim = rehydrated.ClaimTask("1", "agent-b");
        earlyForeignClaim.IsError.Should().BeTrue();
        earlyForeignClaim.ErrorCode.Should().Be("task_already_claimed");

        // 12:16 — 16 minutes after the TRUE claim (stale), 2 minutes after the capture (not).
        clock.Advance(TimeSpan.FromMinutes(2));
        var takeover = rehydrated.ClaimTask("1", "agent-b");
        takeover.IsError.Should().BeFalse("the lease went stale by the true claim age, which must have survived");
        takeover.Text.Should().Contain("stale lease from agent-a");
    }

    [Fact]
    public void FromSnapshot_LegacyClaimedRowWithoutClaimedAt_AgesItsLeaseFromTheCaptureInstant()
    {
        // Literal-payload compat (#595 D2): a snapshot persisted before the lease fields
        // round-tripped can carry an InProgress row with an assignee added by hand-off but no
        // claimedAt. Without the capture-instant fallback the lease would read as freshly claimed
        // on every staleness check and never go stale — a wedged row if its agent is gone.
        // Mutation that must go red: removing the ClaimedAt-from-CapturedAtUtc normalization.
        const string legacyJson = """
            {"ThreadId":"conv-1","SchemaVersion":1,"CapturedAtUtc":"2026-08-29T12:00:00+00:00","Tasks":[{"id":"1","status":"InProgress","title":"Claimed before the restart","notes":[],"assignee":"agent-a","subTasks":[]}]}
            """;
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-29T12:10:00Z"));
        var rehydrated = TaskManager.FromSnapshot(JsonSerializer.Deserialize<TodoBoardSnapshot>(legacyJson)!, clock);

        // 12:10 — ten minutes since the capture the lease was aged from: still live.
        var earlyForeignClaim = rehydrated.ClaimTask("1", "agent-b");
        earlyForeignClaim.IsError.Should().BeTrue();
        earlyForeignClaim.ErrorCode.Should().Be("task_already_claimed");

        // 12:16 — past the staleness threshold measured from the capture: the lease can be taken.
        clock.Advance(TimeSpan.FromMinutes(6));
        var takeover = rehydrated.ClaimTask("1", "agent-b");
        takeover.IsError.Should().BeFalse("a legacy lease must be able to go stale rather than wedging the row");
    }

    [Fact]
    public void FromSnapshot_TwentyLevelBoard_RoundTripsThroughJsonAndHydrationIntact()
    {
        // #608: the client's board panel guards its recursion at 16 levels; this pins that the cap
        // is CLIENT-ONLY. A 20-level chain survives snapshot -> JSON (the projection's own
        // serialize/deserialize shape, System.Text.Json default MaxDepth 64) -> hydration, with the
        // deepest row still addressable by its 20-segment dotted id.
        const int depth = 20;
        var manager = new TaskManager();
        _ = manager.AddTask("Level 1");
        var parentId = "1";
        for (var level = 2; level <= depth; level++)
        {
            _ = manager.AddTask($"Level {level}", parentId);
            parentId = $"{parentId}.1";
        }

        var deepestId = parentId; // "1.1.1..." — 20 segments
        deepestId.Split('.').Should().HaveCount(depth);

        // The same JSON hop ConversationTodoProjection performs: default write options, then a
        // case-insensitive read (its ReadOptions).
        var json = JsonSerializer.Serialize(manager.GetTodoBoardSnapshot("conv-1"));
        var persisted = JsonSerializer.Deserialize<TodoBoardSnapshot>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        );

        var rehydrated = TaskManager.FromSnapshot(persisted!);

        var deepest = rehydrated.GetTask(deepestId);
        deepest.IsError.Should().BeFalse("the 20-level row must still be addressable after the round trip");
        deepest.Text.Should().Contain($"Level {depth}");

        // And the re-captured snapshot still carries the full chain, node for node.
        var recaptured = rehydrated.GetTodoBoardSnapshot("conv-1");
        var node = recaptured.Tasks.Single();
        for (var level = 2; level <= depth; level++)
        {
            node = node.SubTasks.Single();
        }

        node.Id.Should().Be(deepestId);
        node.Title.Should().Be($"Level {depth}");
        node.SubTasks.Should().BeEmpty();
    }

    /// <summary>Builds a single-chain board nested exactly <paramref name="depth" /> task levels.</summary>
    private static TaskManager BuildChainOfDepth(int depth)
    {
        var manager = new TaskManager();
        _ = manager.AddTask("Level 1");
        var parentId = "1";
        for (var level = 2; level <= depth; level++)
        {
            _ = manager.AddTask($"Level {level}", parentId);
            parentId = $"{parentId}.1";
        }
        return manager;
    }

    /// <summary>
    ///     #608 (611/F-002): the practical persistence bound is 31 task levels, NOT the "~60" one
    ///     might derive from System.Text.Json's default MaxDepth of 64. The snapshot alternates
    ///     task object and SubTasks array, so task level N sits at JSON depth 1 + 2N — two depth
    ///     units per level — and 1 + 2N &lt;= 64 gives N = 31. This is the same default-options
    ///     Serialize call ConversationTodoProjection.SaveAsync performs, so the number the comment
    ///     there and requirements.md Req 1.3 state cannot silently drift from measured behaviour.
    /// </summary>
    [Fact]
    public void Serialize_ThirtyOneLevelBoard_RoundTripsAtTheDocumentedBound()
    {
        const int depth = 31;
        var json = JsonSerializer.Serialize(BuildChainOfDepth(depth).GetTodoBoardSnapshot("conv-1"));

        var persisted = JsonSerializer.Deserialize<TodoBoardSnapshot>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        );

        var node = persisted!.Tasks.Single();
        for (var level = 2; level <= depth; level++)
        {
            node = node.SubTasks.Single();
        }
        node.Title.Should().Be($"Level {depth}");
    }

    /// <summary>
    ///     The other side of the 611/F-002 pin: one level past the bound throws. The exception's
    ///     message reads "A possible object cycle was detected" — a DEPTH symptom, not an actual
    ///     cycle, so a log reader must not chase a cycle bug — and at the persistence writer it
    ///     surfaces as a permanently retrying background write, not a hard failure.
    /// </summary>
    [Fact]
    public void Serialize_ThirtyTwoLevelBoard_ThrowsJsonException_OneLevelPastTheBound()
    {
        var snapshot = BuildChainOfDepth(32).GetTodoBoardSnapshot("conv-1");

        var act = () => JsonSerializer.Serialize(snapshot);

        act.Should().Throw<JsonException>().WithMessage("*possible object cycle*");
    }
}
