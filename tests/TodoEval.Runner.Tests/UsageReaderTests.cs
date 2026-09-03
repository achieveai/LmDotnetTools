using TodoEval.Runner.Metrics;

namespace TodoEval.Runner.Tests;

/// <summary>
/// Unit pins for the usage block. The reader parses a bag the HOST serialized with default options
/// (PascalCase names, numeric enums) and must survive both that shape and the camelCase/string shape
/// a future serializer change would produce, because a silently unparsed bag reads as "this run cost
/// nothing".
/// </summary>
public class UsageReaderTests
{
    private static readonly Dictionary<string, IReadOnlyCollection<string>> NoTurns = new(StringComparer.Ordinal);

    [Fact]
    public void ParsesTheHostsDefaultSerializerShape_PascalCaseNamesAndNumericEnums()
    {
        var rows = UsageReader.ParseRecords(
            """
            [
              {
                "ProviderAttemptId": "thread-1:gen-a",
                "ExecutionKind": 1,
                "ParentExecutionId": "subagent-0001",
                "RootConversationId": "thread-1",
                "InputTokens": 100,
                "OutputTokens": 20,
                "TotalTokens": 120
              }
            ]
            """
        );

        var row = rows.Should().ContainSingle().Subject;
        row.ExecutionKind.Should().Be("SubAgent", "enum ordinal 1 is SubAgent");
        row.AgentId.Should().Be("subagent-0001", "a sub-agent record names the CHILD, not its parent");
        row.AttemptKey.Should().Be("gen-a");
        row.TotalTokens.Should().Be(120);
    }

    [Fact]
    public void ParsesTheCamelCaseStringEnumShapeToo()
    {
        var rows = UsageReader.ParseRecords(
            """
            [{ "providerAttemptId": "thread-1:gen-a", "executionKind": "Primary", "totalTokens": 7 }]
            """
        );

        rows.Should().ContainSingle().Which.ExecutionKind.Should().Be("Primary");
    }

    [Fact]
    public void ARowWithoutAnAttemptId_IsDropped_RatherThanCountedTwice()
    {
        // The dedupe key IS the attempt id. A row without one cannot be deduped, so admitting it
        // would let a relayed record be counted in every bag it appears in.
        UsageReader.ParseRecords("""[{ "ExecutionKind": 0, "TotalTokens": 999 }]""").Should().BeEmpty();
    }

    [Fact]
    public void FallsBackToLogicalCallId_WhenTheProviderGaveNoAttemptId()
    {
        UsageReader
            .ParseRecords("""[{ "LogicalCallId": "thread-1:derived:ab12", "ExecutionKind": 0 }]""")
            .Should()
            .ContainSingle()
            .Which.AttemptKey.Should()
            .Be("derived:ab12");
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{ "records": [] }""")]
    [InlineData("")]
    [InlineData(null)]
    public void AnUnusableBag_YieldsNoRows_WithoutSinkingTheExtraction(string? bag)
    {
        // A run still has transcripts when its usage bag is corrupt; the scorer reports zero usage
        // rather than failing the run outright.
        UsageReader.ParseRecords(bag).Should().BeEmpty();
    }

    [Fact]
    public void TheSameAttemptRelayedIntoTwoBags_IsCountedOnce()
    {
        var row = Row("thread-1:gen-a", total: 500);

        var report = UsageReader.Rollup([row, row with { }], NoTurns);

        report.DuplicateAttemptIds.Should().Be(1);
        report.Totals.Records.Should().Be(1);
        report.Totals.TotalTokens.Should().Be(500);
    }

    [Fact]
    public void TotalsSumEveryTokenClass_NotJustInputAndOutput()
    {
        var report = UsageReader.Rollup(
            [
                Row("t:1") with
                {
                    InputTokens = 1,
                    OutputTokens = 2,
                    CacheReadTokens = 4,
                    CacheWriteTokens = 8,
                    ReasoningTokens = 16,
                    TotalTokens = 31,
                },
            ],
            NoTurns
        );

        report.Totals.CacheReadTokens.Should().Be(4);
        report.Totals.CacheWriteTokens.Should().Be(8);
        report.Totals.ReasoningTokens.Should().Be(16);
        report.Totals.TotalTokens.Should().Be(31);
    }

    [Fact]
    public void TurnJoin_AttributesOnlyRecordsWhoseAttemptKeyIsARecordedGenerationId()
    {
        var turns = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
        {
            ["thread-1"] = ["gen-a"],
        };

        var report = UsageReader.Rollup(
            [
                Row("thread-1:gen-a", total: 100),
                Row("thread-1:derived:ab12", total: 40),
                Row("thread-1:gen-z", total: 7),
            ],
            turns
        );

        report.AttributedTurnTokens.Should().Be(100);
        report.UnattributedTurnTokens.Should().Be(47, "a synthetic key and an unknown generation both miss");
    }

    [Fact]
    public void TurnJoin_DoesNotMatchAcrossAgents()
    {
        // Generation ids are only unique within a thread. Joining "gen-a" from another agent's turn
        // list would attribute a sub-agent's tokens to the supervisor.
        var turns = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
        {
            ["thread-1"] = ["gen-a"],
        };

        var report = UsageReader.Rollup([Row("subagent-1:gen-a", agent: "subagent-1", total: 60)], turns);

        report.AttributedTurnTokens.Should().Be(0);
        report.UnattributedTurnTokens.Should().Be(60);
    }

    [Fact]
    public void TheReport_NamesTheKindsThisBuildCannotEmit_SoAZeroIsNotReadAsAbsence()
    {
        // Nothing in the sample runs a workflow, so those kinds are structurally zero. Saying so is
        // what stops #677 reading "0 WorkflowTask tokens" as a measured result.
        var report = UsageReader.Rollup([], NoTurns);

        report.KindsNotEmitted.Should().Contain(["WorkflowController", "WorkflowTask", "Continuation"]);
        report.Notes.Should().Contain(UsageReport.TurnJoinNote);
        report.Notes.Should().Contain(UsageReport.ToolFamilyNote);
    }

    private static UsageRecordRow Row(string attemptId, string agent = "thread-1", long total = 0) =>
        new()
        {
            ProviderAttemptId = attemptId,
            ExecutionKind = "Primary",
            RootConversationId = agent,
            TotalTokens = total,
        };
}
