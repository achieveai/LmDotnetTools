using TodoEval.Runner.Metrics;
using TodoEval.Runner.Sweep;

namespace TodoEval.Runner.Tests;

/// <summary>
/// End-to-end extractor pins against the committed sweep1 fixture store. Every expected number
/// below was cross-validated against the reference oracle (evals/todo-eval/score.ps1 from the
/// #618 asset set) run over the same fixture threads — the two implementations must agree.
/// </summary>
public class MetricsExtractorTests
{
    private static string FixtureRoot => Path.Combine(AppContext.BaseDirectory, "Fixtures", "sweep1");
    private static string ConversationsDir => Path.Combine(FixtureRoot, "conversations");

    private static RunManifestEntry Entry(string runKey, string model, int seed, string? threadId) =>
        new()
        {
            RunKey = runKey,
            Model = model,
            SeedIndex = seed,
            Topic = "topic",
            Status = threadId is null ? RunOutcomes.HarnessError : RunOutcomes.Completed,
            ThreadId = threadId,
            DurationMs = 1234,
        };

    private static BoardShapeExpectation ExpectedBoard() =>
        BoardShapeExpectation.Load(Path.Combine(FixtureRoot, "expected-board.json"));

    [Fact]
    public void Extract_StormRun_MatchesOracleScore()
    {
        var metrics = MetricsExtractor.Extract(
            ConversationsDir,
            [Entry("m1/seed0", "m1", 0, "thread-storm")],
            ExpectedBoard()
        );

        var run = metrics.Runs.Should().ContainSingle().Subject;

        // Conversation totals: thread-storm plus its subagent-child1 descendant.
        run.Threads.Should().Be(2);
        run.SubAgentCount.Should().Be(1);
        run.TotalToolCalls.Should().Be(14);
        run.TaskToolCalls.Should().Be(14);
        run.TaskToolErrors.Should().Be(6);
        run.UnpairedToolCalls.Should().Be(1);

        // Exactly ONE storm of exactly 3: the success closed the first run at 3, and the
        // trailing errors + unpaired call stay below threshold.
        run.RetryStormCount.Should().Be(1);
        var storm = run.RetryStorms.Should().ContainSingle().Subject;
        storm.ThreadId.Should().Be("thread-storm");
        storm.Tool.Should().Be("add-note");
        storm.Count.Should().Be(3);
        storm.Args.Should().Be("""{"noteText":"x","subtaskId":0,"taskId":"2.1"}""");

        // Per-tool rows exist for all 15 tools, zero-call rows included.
        run.PerTool.Should().HaveCount(15);
        run.PerTool["add-note"]
            .Should()
            .Be(
                new PerToolScore
                {
                    Calls = 8,
                    Errors = 5,
                    ErrorRate = 0.625,
                }
            );
        run.PerTool["block-task"]
            .Should()
            .Be(
                new PerToolScore
                {
                    Calls = 2,
                    Errors = 0,
                    ErrorRate = 0,
                }
            );
        run.PerTool["update-task"]
            .Should()
            .Be(
                new PerToolScore
                {
                    Calls = 1,
                    Errors = 1,
                    ErrorRate = 1,
                }
            );
        run.PerTool["search-tasks"]
            .Should()
            .Be(
                new PerToolScore
                {
                    Calls = 0,
                    Errors = 0,
                    ErrorRate = 0,
                }
            );

        run.BlockRecorded.Should().BeTrue();
        run.BlockExplicitlyCleared.Should().BeTrue();
        run.BlockCleared.Should().BeTrue();

        run.Completion.Should().BeTrue();
        run.CompletionFailures.Should().BeEmpty();

        run.Turns.Should().Be(5);
        run.PrimaryTurns.Should().Be(4);

        run.Validity.Valid.Should().BeTrue();
        run.Validity.Reasons.Should().BeEmpty();
        run.Validity.SubAgentThreads.Should().Be(1);
    }

    [Fact]
    public void Extract_ErrorRun_MatchesOracleScore_AndIsInvalid()
    {
        var metrics = MetricsExtractor.Extract(
            ConversationsDir,
            [Entry("m1/seed1", "m1", 1, "thread-errors")],
            ExpectedBoard()
        );

        var run = metrics.Runs.Should().ContainSingle().Subject;

        run.Threads.Should().Be(2);
        run.TotalToolCalls.Should().Be(8, "web-search is counted in the total");
        run.TaskToolCalls.Should().Be(7, "web-search is not a task tool");
        run.TaskToolErrors.Should().Be(2, "the JSON-quoted 'Error:' string is NOT an error");
        run.UnpairedToolCalls.Should().Be(0);
        run.PerTool["get-task"]
            .Should()
            .Be(
                new PerToolScore
                {
                    Calls = 4,
                    Errors = 2,
                    ErrorRate = 0.5,
                }
            );
        run.PerTool["add-task"]
            .Should()
            .Be(
                new PerToolScore
                {
                    Calls = 2,
                    Errors = 0,
                    ErrorRate = 0,
                }
            );
        run.PerTool["list-notes"]
            .Should()
            .Be(
                new PerToolScore
                {
                    Calls = 1,
                    Errors = 0,
                    ErrorRate = 0,
                }
            );
        run.RetryStormCount.Should().Be(0, "two consecutive failures are below the storm threshold");

        run.BlockRecorded.Should().BeFalse();
        run.BlockCleared.Should().BeFalse();

        // No todo.board was persisted for this thread: completion must fail, not throw.
        run.Completion.Should().BeFalse();
        run.CompletionFailures.Should().ContainSingle().Which.Should().Contain("no todo board snapshot");

        run.Turns.Should().Be(3);
        run.PrimaryTurns.Should().Be(2);

        // Validity: subagent-child2 made zero task-tool calls AND claims board work in text.
        run.Validity.Valid.Should().BeFalse();
        run.Validity.Reasons.Should()
            .ContainSingle()
            .Which.Should()
            .Be("sub-agent thread(s) with zero task-tool calls: subagent-child2");
        run.Validity.SubAgentThreads.Should().Be(1);
        run.SubAgentCount.Should().Be(1);
        run.Validity.SubAgentsWithoutTaskToolCalls.Should().Equal("subagent-child2");
        run.Validity.FabricatedComplianceSuspects.Should().Equal("subagent-child2");
    }

    [Fact]
    public void Extract_MissingThread_KeepsRunInDenominatorAsIncomplete()
    {
        var metrics = MetricsExtractor.Extract(
            ConversationsDir,
            [Entry("m1/seed2", "m1", 2, "thread-vanished")],
            ExpectedBoard()
        );

        var run = metrics.Runs.Should().ContainSingle().Subject;
        run.Completion.Should().BeFalse();
        run.CompletionFailures.Should().ContainSingle().Which.Should().Contain("thread-vanished");
        run.Threads.Should().Be(0);
        run.PerTool.Should().HaveCount(15, "the zero row set is emitted even without a conversation");

        // Spec: zero threads is harness misconfiguration, never a valid failed run.
        run.Validity.Valid.Should().BeFalse();
        run.Validity.Reasons.Should().ContainSingle().Which.Should().Be(RunValidity.NoThreadsReason);
    }

    [Fact]
    public void Extract_NoExpectedBoard_LeavesCompletionNull()
    {
        var metrics = MetricsExtractor.Extract(
            ConversationsDir,
            [Entry("m1/seed0", "m1", 0, "thread-storm")],
            expectedBoard: null
        );

        metrics.Runs.Should().ContainSingle().Which.Completion.Should().BeNull();
    }

    [Fact]
    public void Extract_OrphanThread_IsSurfacedAsUnattributed_NotDropped()
    {
        // subagent-orphan has NO metadata.json (the debounced write died with a hard-timeout kill),
        // so no sample.subAgentOf chain reaches it from either run. F-003: it must surface with its
        // task-tool activity, never silently vanish from the extraction.
        var metrics = MetricsExtractor.Extract(
            ConversationsDir,
            [Entry("m1/seed0", "m1", 0, "thread-storm"), Entry("m1/seed1", "m1", 1, "thread-errors")],
            ExpectedBoard()
        );

        var orphan = metrics.UnattributedThreads.Should().ContainSingle().Subject;
        orphan.ThreadId.Should().Be("subagent-orphan");
        orphan.IsSubAgentThread.Should().BeTrue();
        orphan.TotalToolCalls.Should().Be(1);
        orphan.TaskToolCalls.Should().Be(1, "an orphan WITH task-tool calls must count, not drop");
        orphan.TaskToolErrors.Should().Be(1);
        orphan.FabricatedComplianceSuspect.Should().BeFalse();

        // The orphan is surfaced separately, never misattributed into a run's rows.
        metrics.Runs.Should().HaveCount(2);
        metrics.Runs.Single(r => r.RunKey == "m1/seed0").Threads.Should().Be(2);
        metrics.Runs.Single(r => r.RunKey == "m1/seed1").Threads.Should().Be(2);
    }

    [Fact]
    public void Extract_ClaimedRootsAndTheirLinkedDescendants_AreNeverUnattributed()
    {
        var metrics = MetricsExtractor.Extract(
            ConversationsDir,
            [Entry("m1/seed0", "m1", 0, "thread-storm"), Entry("m1/seed1", "m1", 1, "thread-errors")],
            ExpectedBoard()
        );

        metrics
            .UnattributedThreads.Select(t => t.ThreadId)
            .Should()
            .OnlyContain(
                id => id == "subagent-orphan",
                "roots named by the manifest and their sample.subAgentOf descendants are all claimed"
            );
    }
}
