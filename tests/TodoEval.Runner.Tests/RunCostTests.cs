using System.Text.Json;
using TodoEval.Runner.Metrics;
using TodoEval.Runner.Sweep;

namespace TodoEval.Runner.Tests;

/// <summary>
/// The cache-aware cost block. Compaction rewrites the cached prefix and bills every summary, so a
/// strategy comparison stands or falls on the cache split and the money — not the token total.
/// </summary>
public class RunCostTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyCollection<string>> NoTurns = new Dictionary<
        string,
        IReadOnlyCollection<string>
    >(StringComparer.Ordinal);

    private static RunCost CostOf(params UsageRecordRow[] rows) => UsageReader.Rollup(rows, NoTurns).Cost;

    private static UsageRecordRow Row(
        string attemptId = "root:g1",
        long input = 0,
        long output = 0,
        long cacheRead = 0,
        long cacheWrite = 0,
        long total = 0,
        long? cost = null,
        string kind = "Primary",
        string? checkpointId = null
    ) =>
        new()
        {
            ProviderAttemptId = attemptId,
            ExecutionKind = kind,
            RootConversationId = "root",
            InputTokens = input,
            OutputTokens = output,
            CacheReadTokens = cacheRead,
            CacheWriteTokens = cacheWrite,
            TotalTokens = total,
            CostMicros = cost,
            CompactionCheckpointId = checkpointId,
        };

    // ── tokens and the cache split ───────────────────────────────────────────────────────────────

    [Fact]
    public void UncachedInput_IsInputMinusCacheRead()
    {
        // UsageRecord declares cacheRead ⊆ input as normative and carries no accounting-mode field,
        // so the subtraction IS the definition rather than a guess about one provider.
        var cost = CostOf(Row(input: 1000, cacheRead: 750, output: 200, cacheWrite: 50));

        cost.InputTokens.Should().Be(1000);
        cost.InputTokensUncached.Should().Be(250);
        cost.CacheReadTokens.Should().Be(750);
        cost.CacheWriteTokens.Should().Be(50);
        cost.OutputTokens.Should().Be(200);
        cost.CacheHitRatio.Should().Be(0.75);
    }

    [Fact]
    public void CacheHitRatio_IsZeroWhenNoInputTokenWasBilled()
    {
        CostOf(Row(output: 10)).CacheHitRatio.Should().Be(0);
    }

    [Fact]
    public void UncachedInput_IsClampedWhenAProviderViolatesTheSubsetRule()
    {
        // A negative token count would propagate into every rollup above it; the clamp keeps a
        // malformed row from turning into a number that looks like a saving.
        CostOf(Row(input: 100, cacheRead: 400)).InputTokensUncached.Should().Be(0);
    }

    [Fact]
    public void Records_AreDedupedByAttemptIdBeforeAnyCostIsCounted()
    {
        // The same attempt is relayed into more than one metadata bag by design. Counting it twice
        // would double both the tokens and the money.
        var cost = CostOf(Row(input: 100, cost: 500), Row(input: 100, cost: 500));

        cost.Records.Should().Be(1);
        cost.InputTokens.Should().Be(100);
        cost.CostMicros.Should().Be(500);
    }

    // ── money ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CostMicros_SumsOnlyThePricedRecords_AndCountsThem()
    {
        var cost = CostOf(Row("a:1", cost: 1200), Row("b:2", cost: 800), Row("c:3"));

        cost.CostMicros.Should().Be(2000);
        cost.RecordsWithCost.Should().Be(2);
        cost.Records.Should().Be(3, "the two counts together say the figure is a lower bound");
    }

    [Fact]
    public void CostMicros_IsNullWhenNoRecordCarriedOne()
    {
        // Null means UNPRICED. A zero here would read as a free run.
        CostOf(Row("a:1", input: 100)).CostMicros.Should().BeNull();
    }

    [Fact]
    public void CostMicros_IsZeroWhenARecordReallyCostNothing()
    {
        var cost = CostOf(Row("a:1", cost: 0));

        cost.CostMicros.Should().Be(0, "a free model is a measurement, not an absence");
        cost.RecordsWithCost.Should().Be(1);
    }

    [Fact]
    public void NoRecordsAtAll_YieldsTheAbsentBlock()
    {
        var cost = UsageReader.Rollup([], NoTurns).Cost;

        cost.Should().Be(RunCost.Absent);
        cost.Records.Should().Be(0);
        cost.CostMicros.Should().BeNull();
        cost.CompactionSummaryTokens.Should().BeNull();
    }

    // ── compaction attribution ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Compaction", "root:g1", null)]
    [InlineData("Primary", "root:g1", "ckpt-1")]
    [InlineData("Primary", "root:compaction-abc123", null)]
    public void ASummaryPass_IsRecognisedByAnyOneOfItsThreeMarks(string kind, string attemptId, string? checkpointId)
    {
        // A host that stamps only one of the three still attributes correctly, which is what lets the
        // eval read archives written across the rollout.
        var cost = CostOf(Row(attemptId, total: 4000, kind: kind, checkpointId: checkpointId));

        cost.CompactionSummaryRecords.Should().Be(1);
        cost.CompactionSummaryTokens.Should().Be(4000);
    }

    [Fact]
    public void SummaryTokens_CountOnlyTheSummaryPasses()
    {
        var cost = CostOf(Row("a:1", total: 9000), Row("b:2", total: 4000, kind: "Compaction"));

        cost.CompactionSummaryRecords.Should().Be(1);
        cost.CompactionSummaryTokens.Should().Be(4000);
    }

    [Fact]
    public void SummaryTokens_AreNullWhenNothingIsMarked()
    {
        // Not zero: a zero would read as "measured, the summaries were free".
        CostOf(Row("a:1", total: 9000)).CompactionSummaryTokens.Should().BeNull();
    }

    // ── the JSON shape the host actually persists ────────────────────────────────────────────────

    [Fact]
    public void ParseRecords_ReadsTheCostAndCheckpointFields()
    {
        // PascalCase with numeric enums: usage.records is written with the DEFAULT serializer, unlike
        // the camelCase metadata.json envelope around it.
        var row = UsageReader
            .ParseRecords(
                """
                [{
                  "ProviderAttemptId": "root:compaction-1",
                  "ExecutionKind": 5,
                  "RootConversationId": "root",
                  "InputTokens": 900, "CacheReadTokens": 300, "OutputTokens": 80,
                  "TotalTokens": 980,
                  "EstimatedPublicCostMicros": 1500,
                  "ProviderReportedCostMicros": 1234,
                  "PreferredCostMicros": 1234,
                  "CompactionCheckpointId": "ckpt-7"
                }]
                """
            )
            .Should()
            .ContainSingle()
            .Subject;

        row.ExecutionKind.Should().Be("Compaction", "ordinal 5 is the compaction kind, not an unknown one");
        row.CostMicros.Should().Be(1234, "the provider's own figure is what was actually billed");
        row.CompactionCheckpointId.Should().Be("ckpt-7");
        row.IsCompactionSummary.Should().BeTrue();
        row.InputTokensUncached.Should().Be(600);
    }

    [Fact]
    public void ParseRecords_FallsBackToThePublicEstimateWhenTheProviderReportedNone()
    {
        var row = UsageReader
            .ParseRecords("""[{"ProviderAttemptId":"a:1","EstimatedPublicCostMicros":777}]""")
            .Should()
            .ContainSingle()
            .Subject;

        row.CostMicros.Should().Be(777);
    }

    [Fact]
    public void ParseRecords_AnArchiveWithNoCostFields_ReadsAsUnpriced()
    {
        // The reader is a deliberately partial mirror: an archive written before costs were persisted
        // must keep parsing, and its silence must not become a zero.
        var row = UsageReader
            .ParseRecords("""[{"ProviderAttemptId":"a:1","InputTokens":10}]""")
            .Should()
            .ContainSingle()
            .Subject;

        row.CostMicros.Should().BeNull();
    }

    [Fact]
    public void Rollup_GivesTheCompactionKindItsOwnByKindRow()
    {
        var report = UsageReader.Rollup([Row("a:1", total: 100), Row("b:2", total: 40, kind: "Compaction")], NoTurns);

        report.ByExecutionKind.Should().ContainKey("Compaction");
        report.ByExecutionKind["Compaction"].TotalTokens.Should().Be(40);
        UsageReport.KindsNotEmittedByThisBuild.Should().NotContain("Compaction", "this build does emit one");
    }
}

/// <summary>
/// Compaction counting through the real store path: checkpoints are envelopes in <c>messages.json</c>
/// and their cost is in the usage bag, and the extractor has to reconcile the two.
/// </summary>
public class CompactionMetricsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"todo-eval-compaction-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Writes a one-thread store with <paramref name="checkpoints"/> checkpoint envelopes.</summary>
    private string WriteStore(int checkpoints, string usageRecordsJson)
    {
        var conversations = Path.Combine(_dir, "conversations");
        var thread = Path.Combine(conversations, "thread-1");
        Directory.CreateDirectory(thread);

        var envelopes = Enumerable
            .Range(0, checkpoints)
            .Select(i => new
            {
                threadId = "thread-1",
                generationId = $"compaction-{i}",
                messageType = ConversationStoreReader.CompactionCheckpointType,
                role = "Assistant",
                messageJson = """{"$type":"compaction_checkpoint","text":"summary prose"}""",
            });
        File.WriteAllText(Path.Combine(thread, "messages.json"), JsonSerializer.Serialize(envelopes));
        File.WriteAllText(
            Path.Combine(thread, "metadata.json"),
            JsonSerializer.Serialize(
                new
                {
                    threadId = "thread-1",
                    properties = new Dictionary<string, string> { ["usage.records"] = usageRecordsJson },
                }
            )
        );
        return conversations;
    }

    private static RunMetrics Extract(string conversationsDir) =>
        MetricsExtractor
            .Extract(
                conversationsDir,
                [
                    new RunManifestEntry
                    {
                        RunKey = "compact-v0/c1/m1/seed0",
                        Model = "m1",
                        SeedIndex = 0,
                        Topic = "t",
                        Variant = "compact-v0",
                        Task = "c1",
                        Status = RunOutcomes.Completed,
                        ThreadId = "thread-1",
                    },
                ],
                expectedBoard: null
            )
            .Runs.Single();

    [Fact]
    public void Compactions_CountTheCheckpointEnvelopesOnTheRootThread()
    {
        var run = Extract(
            WriteStore(
                checkpoints: 2,
                """[{"ProviderAttemptId":"root:compaction-0","TotalTokens":4000,"ExecutionKind":5}]"""
            )
        );

        run.Compactions.Should().Be(2);
        run.Cost.CompactionSummaryTokens.Should().Be(4000);
        run.Variant.Should().Be("compact-v0", "the axes travel from the manifest onto the score row");
        run.Task.Should().Be("c1");
    }

    [Fact]
    public void NoCheckpoints_IsAMeasuredZero()
    {
        var run = Extract(WriteStore(checkpoints: 0, """[{"ProviderAttemptId":"root:g1","TotalTokens":900}]"""));

        run.Compactions.Should().Be(0);
        run.Cost.CompactionSummaryTokens.Should().BeNull("nothing was attributed, so nothing is claimed");
        run.Cost.Records.Should().Be(1);
    }

    [Fact]
    public void CheckpointsWithNoAttributableUsage_ReportSummaryTokensAsUnknown()
    {
        // The store says compaction happened and the usage bag names no summary pass. Attribution
        // FAILED; a 0 beside a checkpoint count of 2 would read as "compaction summarised for free".
        var run = Extract(WriteStore(checkpoints: 2, """[{"ProviderAttemptId":"root:g1","TotalTokens":900}]"""));

        run.Compactions.Should().Be(2);
        run.Cost.CompactionSummaryTokens.Should().BeNull();
    }

    [Fact]
    public void CheckpointEnvelopes_SurviveRedaction()
    {
        // The committed archive is redacted, so the count has to come off a field redaction keeps.
        var redacted = TranscriptRedactor.RedactMessages(
            File.ReadAllText(Path.Combine(WriteStore(checkpoints: 3, "[]"), "thread-1", "messages.json"))
        );

        redacted
            .Should()
            .Contain(ConversationStoreReader.CompactionCheckpointType)
            .And.NotContain("summary prose", "a checkpoint's narrative is model prose and must not be committed");
    }
}
