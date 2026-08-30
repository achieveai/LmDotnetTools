using TodoEval.Runner.Metrics;

namespace TodoEval.Runner.Tests;

/// <summary>
/// Store-parsing pins against the committed sweep1 fixtures, through the real JSON path
/// (envelope camelCase, messageJson string of snake_case inner JSON).
/// </summary>
public class ConversationStoreReaderTests
{
    private static string ConversationsDir =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "sweep1", "conversations");

    private static ConversationStoreReader.ThreadData Load(string threadId) =>
        ConversationStoreReader.LoadThread(Path.Combine(ConversationsDir, threadId));

    [Fact]
    public void StormThread_PairsCallsToResults_AndTalliesPerTool()
    {
        var thread = Load("thread-storm");

        thread.ThreadId.Should().Be("thread-storm");
        thread.TotalToolCalls.Should().Be(12);
        thread.TaskToolCallCount.Should().Be(12);
        thread.UnpairedToolCalls.Should().Be(1, "call c09 has no result envelope");
        thread.PerTaskTool["add-note"].Should().Be(new ToolStats { Calls = 7, Errors = 5 });
        thread.PerTaskTool["bulk-initialize"].Should().Be(new ToolStats { Calls = 1, Errors = 0 });
        thread.PerTaskTool["block-task"].Should().Be(new ToolStats { Calls = 2, Errors = 0 });
    }

    [Fact]
    public void ErrorDetection_IsTheDefensiveUnion_OfIsErrorFlagAndTextPrefix()
    {
        var thread = Load("thread-storm");

        // Every fixture error row records is_error:false (production reality the spec pins) —
        // the "Error:" text prefix alone must flag them. The update-task row records
        // is_error:true on a success text — the flag alone must flag it too (defensive union).
        thread.PerTaskTool["add-note"].Errors.Should().Be(5, "is_error:false must not hide 'Error:' texts");
        thread.PerTaskTool["update-task"].Errors.Should().Be(1, "is_error:true alone makes an error");
    }

    [Fact]
    public void QuotedErrorString_IsNotAnError_AndFirstResultWins()
    {
        var thread = Load("thread-errors");

        // Call e04's result is a JSON-quoted string: its text starts with '"', not "Error:".
        // Call e01 has TWO results (error first, success second): the FIRST wins, so it stays
        // an error — 2 errors total, not 1 (last-wins) and not 3 (quoted string as error).
        thread.PerTaskTool["get-task"].Should().Be(new ToolStats { Calls = 4, Errors = 2 });
    }

    [Fact]
    public void NonStringResult_IsSerializedBeforeThePrefixCheck()
    {
        var thread = Load("thread-errors");

        thread.PerTaskTool["list-notes"].Should().Be(new ToolStats { Calls = 1, Errors = 0 });
    }

    [Fact]
    public void NonTaskTools_CountInTotalsOnly()
    {
        var thread = Load("thread-errors");

        thread.TotalToolCalls.Should().Be(8);
        thread.TaskToolCallCount.Should().Be(7, "web-search is not a task tool");
        thread.PerTaskTool.Should().NotContainKey("web-search");
    }

    [Fact]
    public void StormFixture_YieldsExactlyOneStormOfThree_WithCanonicalArgs()
    {
        // The acceptance pin: a committed fixture conversation with a known 3x retry storm counts
        // as exactly ONE storm — the closing success used shuffled JSON key order, so this also
        // pins arg canonicalization end to end.
        var thread = Load("thread-storm");

        var storm = thread.RetryStorms.Should().ContainSingle().Subject;
        storm.Count.Should().Be(3);
        storm.Tool.Should().Be("add-note");
        storm.Args.Should().Be("""{"noteText":"x","subtaskId":0,"taskId":"2.1"}""");
    }

    [Fact]
    public void Turns_AreDistinctGenerationIds_WithInnerFallback()
    {
        // thread-storm spans r1..r4; the final TextMessage envelope has generationId null and
        // only the inner generation_id "r4" — the fallback must not mint a 5th turn.
        Load("thread-storm").TurnCount.Should().Be(4);
        Load("thread-errors").TurnCount.Should().Be(2);
    }

    [Fact]
    public void BlockTask_SuccessWithNonEmptyBlockedBy_RecordsAndThenClears()
    {
        var thread = Load("thread-storm");

        thread.BlockRecorded.Should().BeTrue();
        thread.BlockExplicitlyCleared.Should().BeTrue("the second block-task success sent an empty blockedBy");
    }

    [Fact]
    public void Metadata_YieldsParentLinkAndBoardSnapshot()
    {
        var child = Load("subagent-child1");
        child.ParentThreadId.Should().Be("thread-storm");
        child.IsSubAgentThread.Should().BeTrue();

        var root = Load("thread-storm");
        root.ParentThreadId.Should().BeNull();
        root.TodoBoardJson.Should().NotBeNullOrEmpty();
        root.IsSubAgentThread.Should().BeFalse();
    }

    [Fact]
    public void ToollessSubAgent_WithComplianceClaimText_IsFlagged()
    {
        var orphan = Load("subagent-orphan");

        orphan.TaskToolCallCount.Should().Be(0);
        orphan.FabricatedComplianceSuspect.Should().BeTrue();
    }

    [Fact]
    public void SubAgentWithTaskToolCalls_IsNeverASuspect()
    {
        Load("subagent-child1").FabricatedComplianceSuspect.Should().BeFalse();
    }

    [Fact]
    public void GroupByRootThread_FollowsParentLinks()
    {
        var threads = ConversationStoreReader.LoadAllThreads(ConversationsDir);
        var groups = ConversationStoreReader.GroupByRootThread(threads);

        groups.Should().HaveCount(2);
        groups["thread-storm"].Select(t => t.ThreadId).Should().BeEquivalentTo("thread-storm", "subagent-child1");
        groups["thread-errors"].Select(t => t.ThreadId).Should().BeEquivalentTo("thread-errors", "subagent-orphan");
    }

    [Fact]
    public void IsErrorText_RequiresTheOrdinalCaseSensitivePrefix()
    {
        ConversationStoreReader.IsErrorText("Error: nope").Should().BeTrue();
        ConversationStoreReader.IsErrorText("   Error: leading whitespace ok").Should().BeTrue();
        ConversationStoreReader.IsErrorText("error: lowercase is not an error").Should().BeFalse();
        ConversationStoreReader.IsErrorText("ERROR: uppercase is not an error").Should().BeFalse();
        ConversationStoreReader.IsErrorText("The tool returned Error: mid-text").Should().BeFalse();
        ConversationStoreReader.IsErrorText("\"Error: quoted\"").Should().BeFalse();
    }
}
