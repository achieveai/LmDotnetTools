using TodoEval.Runner.Metrics;
using TodoEval.Runner.Sweep;

namespace TodoEval.Runner.Tests;

public class ResultsWriterTests
{
    private static RunMetrics Run(
        string model,
        int seed,
        bool? completion,
        int calls,
        int errors,
        int storms,
        bool valid = true
    ) =>
        new()
        {
            RunKey = $"{model}/seed{seed}",
            Model = model,
            SeedIndex = seed,
            Topic = "t",
            Status = RunOutcomes.Completed,
            ThreadId = $"thread-{model}-{seed}",
            Turns = 4,
            PrimaryTurns = 3,
            TotalToolCalls = calls,
            TaskToolCalls = calls,
            TaskToolErrors = errors,
            PerTool = new Dictionary<string, PerToolScore>(StringComparer.Ordinal)
            {
                ["add-note"] = new()
                {
                    Calls = calls,
                    Errors = errors,
                    ErrorRate = calls > 0 ? Math.Round((double)errors / calls, 4) : 0,
                    Family = "task",
                },
            },
            RetryStormCount = storms,
            RetryStorms =
            [
                .. Enumerable
                    .Range(0, storms)
                    .Select(i => new RetryStorm
                    {
                        ThreadId = $"thread-{model}-{seed}",
                        Tool = "add-note",
                        Count = 3 + i,
                        Args = """{"taskId":"2.1"}""",
                    }),
            ],
            Completion = completion,
            Validity = RunValidity.From(2, 1, valid ? [] : ["subagent-x"], []),
        };

    [Fact]
    public void Summary_RollsUpPerModel()
    {
        var summary = ResultsWriter.BuildSummaryMarkdown(
            [
                Run("model-a", 0, true, 10, 2, 1),
                Run("model-a", 1, false, 8, 4, 0, valid: false),
                Run("model-b", 0, true, 6, 0, 0),
            ],
            []
        );

        summary.Should().Contain("| model-a | 2 | 1/2 | 1/2 |");
        summary.Should().Contain("| model-b | 1 | 1/1 | 1/1 |");
        summary.Should().Contain("| model-a | add-note | 18 | 6 |");
        summary.Should().Contain("| model-a/seed0 | thread-model-a-0 | add-note | 3 |");
        summary.Should().Contain("| model-a/seed1 | 1 | subagent-x |", "invalid runs are listed");
    }

    [Fact]
    public void Summary_ReportsCompletionAsNotApplicable_WhenNoExpectedBoardExisted()
    {
        var summary = ResultsWriter.BuildSummaryMarkdown([Run("model-a", 0, completion: null, 5, 0, 0)], []);

        summary.Should().Contain("| model-a | 1 | 1/1 | n/a |");
    }

    [Fact]
    public void Summary_OmitsZeroCallToolsFromThePerToolTable()
    {
        var summary = ResultsWriter.BuildSummaryMarkdown([Run("model-a", 0, true, 5, 1, 0)], []);

        summary.Should().Contain("| model-a | add-note | 5 | 1 |");
        summary.Should().NotContain("| model-a | delete-task |");
    }

    [Fact]
    public void Summary_ListsUnattributedThreads_WithTheirActivity()
    {
        var summary = ResultsWriter.BuildSummaryMarkdown(
            [Run("model-a", 0, true, 5, 1, 0)],
            [
                new UnattributedThread
                {
                    ThreadId = "subagent-lost",
                    IsSubAgentThread = true,
                    TotalToolCalls = 3,
                    TaskToolCalls = 2,
                    TaskToolErrors = 1,
                    FabricatedComplianceSuspect = false,
                },
            ]
        );

        summary.Should().Contain("## Unattributed threads");
        summary.Should().Contain("WARNING: 1 thread(s) are unreachable");
        summary.Should().Contain("| subagent-lost | yes | 3 | 2 | 1 | no |");
    }

    [Fact]
    public void Summary_ReportsNoUnattributedThreads_WhenEveryThreadIsClaimed()
    {
        var summary = ResultsWriter.BuildSummaryMarkdown([Run("model-a", 0, true, 5, 1, 0)], []);

        summary.Should().Contain("## Unattributed threads");
        summary.Should().Contain("None: every conversation thread is reachable");
    }

    [Fact]
    public void RunsJsonl_WritesOneScoreShapedLinePerRun()
    {
        var path = Path.Combine(Path.GetTempPath(), $"runs-{Guid.NewGuid():N}.jsonl");
        try
        {
            ResultsWriter.WriteRunsJsonl(path, [Run("model-a", 0, true, 1, 0, 0), Run("model-b", 1, false, 2, 1, 0)]);

            var lines = File.ReadAllLines(path);
            lines.Should().HaveCount(2);
            lines[0]
                .Should()
                .Contain("\"schema\":\"todo-eval/score@2\"")
                .And.Contain("\"runKey\":\"model-a/seed0\"")
                .And.Contain("\"completion\":true")
                .And.Contain("\"validity\":{\"valid\":true");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
