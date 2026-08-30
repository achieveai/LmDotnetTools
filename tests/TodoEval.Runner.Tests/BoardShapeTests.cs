using System.Text.Json;
using TodoEval.Runner.Metrics;

namespace TodoEval.Runner.Tests;

/// <summary>
/// Completion checks per metrics-spec.md over the committed expected-board@1 fixture shape:
/// counts, depths, statuses and note minimums — never free text.
/// </summary>
public class BoardShapeTests
{
    private static string FixtureRoot => Path.Combine(AppContext.BaseDirectory, "Fixtures", "sweep1");

    private static BoardShapeExpectation Fixture() =>
        BoardShapeExpectation.Load(Path.Combine(FixtureRoot, "expected-board.json"));

    private static string StormBoardJson()
    {
        var metadataPath = Path.Combine(FixtureRoot, "conversations", "thread-storm", "metadata.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(metadataPath));
        return doc.RootElement.GetProperty("properties").GetProperty("todo.board").GetString()!;
    }

    [Fact]
    public void Fixture_ParsesTheCommittedSchema()
    {
        var fixture = Fixture();

        fixture.Schema.Should().Be("todo-eval/expected-board@1");
        fixture.Board!.TopLevelTaskCount.Should().Be(3);
        fixture.Board.SubtaskCountsSorted.Should().Equal(3, 3, 4);
        fixture.Board.Level3!.MinParents.Should().Be(1);
        fixture.Board.Level3.MinChildrenPerParent.Should().Be(2);
        fixture.Board.AllTasksCompleted.Should().BeTrue();
        fixture.Board.MinNotesPerSubtask.Should().Be(1);
        fixture.Board.MaxBlockedTasks.Should().Be(0);
        fixture.Conversation!.RequireBlockRecorded.Should().BeTrue();
        fixture.Conversation.RequireBlockCleared.Should().BeTrue();
    }

    [Fact]
    public void CompletingBoard_PassesEveryCheck()
    {
        var flat = BoardSnapshot.Parse(StormBoardJson()).Flatten();

        Fixture().Evaluate(flat, blockRecorded: true, blockCleared: true).Should().BeEmpty();
    }

    [Fact]
    public void StatusIsCompared_CaseInsensitively()
    {
        // The fixture board deliberately carries one lowercase "completed" row (task 3.2); the
        // previous test passing already proves it. This pins the inverse: flipping ONE status to
        // a non-completed value must fail allTasksCompleted.
        var mutated = StormBoardJson().Replace("\"status\":\"completed\"", "\"status\":\"InProgress\"");
        var flat = BoardSnapshot.Parse(mutated).Flatten();

        var failures = Fixture().Evaluate(flat, blockRecorded: true, blockCleared: true);
        failures.Should().ContainSingle().Which.Should().StartWith("allTasksCompleted: 1 task(s) not Completed");
    }

    [Fact]
    public void TopLevelTaskCount_IsExact()
    {
        var flat = new[] { Top(3), Top(3) };

        var failures = Fixture().Evaluate(flat, blockRecorded: true, blockCleared: true);
        failures.Should().Contain(f => f.StartsWith("topLevelTaskCount: expected 3, found 2"));
    }

    [Fact]
    public void SubtaskCounts_CompareSorted_NotPositionally()
    {
        // Actual counts arrive in board order [4,3,3]; the fixture says [3,3,4]. Sorted-equal.
        var flat = BoardSnapshot.Parse(StormBoardJson()).Flatten();
        flat.Where(t => t.Depth == 1).Select(t => t.ChildCount).Should().Equal(4, 3, 3);

        Fixture()
            .Evaluate(flat, blockRecorded: true, blockCleared: true)
            .Should()
            .NotContain(f => f.StartsWith("subtaskCountsSorted"));
    }

    [Fact]
    public void WrongSubtaskCounts_Fail()
    {
        var flat = new[] { Top(3), Top(3), Top(3) };

        var failures = Fixture().Evaluate(flat, blockRecorded: true, blockCleared: true);
        failures.Should().Contain(f => f.StartsWith("subtaskCountsSorted: expected [3,3,4], found [3,3,3]"));
    }

    [Fact]
    public void Level3_RequiresADepth2ParentWithEnoughChildren()
    {
        // Depth-2 rows exist but none has >= 2 children.
        var flat = new List<FlatTask> { Top(3), Top(3), Top(4) };
        flat.AddRange(Enumerable.Range(0, 10).Select(_ => new FlatTask(2, "Completed", 1, 0)));

        var failures = Fixture().Evaluate(flat, blockRecorded: true, blockCleared: true);
        failures.Should().Contain(f => f.StartsWith("level3: expected >= 1 depth-2 task(s) with >= 2 children"));
    }

    [Fact]
    public void MinNotesPerSubtask_AppliesToDepth2AndBelow_NotTopLevel()
    {
        // Top-level rows have zero notes in the completing fixture board and still pass; a
        // depth-2 row without notes must fail.
        var mutated = StormBoardJson().Replace("\"notes\":[\"Dry-run OK.\"]", "\"notes\":[]");
        var flat = BoardSnapshot.Parse(mutated).Flatten();

        var failures = Fixture().Evaluate(flat, blockRecorded: true, blockCleared: true);
        failures.Should().ContainSingle().Which.Should().StartWith("minNotesPerSubtask: 1 subtask(s)");
    }

    [Fact]
    public void BlockedTask_FailsMaxBlockedTasks()
    {
        var mutated = StormBoardJson().Replace("\"status\":\"completed\"", "\"status\":\"Blocked\"");
        var flat = BoardSnapshot.Parse(mutated).Flatten();

        BoardShapeExpectation.CountBlocked(flat).Should().Be(1);
        var failures = Fixture().Evaluate(flat, blockRecorded: true, blockCleared: true);
        failures.Should().Contain(f => f.StartsWith("maxBlockedTasks: expected <= 0, found 1"));
    }

    [Fact]
    public void BlockFlags_AreRequiredByTheConversationSection()
    {
        var flat = BoardSnapshot.Parse(StormBoardJson()).Flatten();

        var failures = Fixture().Evaluate(flat, blockRecorded: false, blockCleared: false);
        failures.Should().HaveCount(2);
        failures.Should().Contain(f => f.StartsWith("requireBlockRecorded"));
        failures.Should().Contain(f => f.StartsWith("requireBlockCleared"));
    }

    [Fact]
    public void OmittedChecks_AreNotEvaluated()
    {
        var empty = new BoardShapeExpectation();

        empty
            .Evaluate([new FlatTask(1, "InProgress", 0, 0)], blockRecorded: false, blockCleared: false)
            .Should()
            .BeEmpty();
    }

    private static FlatTask Top(int children) => new(1, "Completed", 0, children);
}
