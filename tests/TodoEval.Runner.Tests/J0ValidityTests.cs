using System.Text.Json;
using TodoEval.Runner.Metrics;
using TodoEval.Runner.Sweep;

namespace TodoEval.Runner.Tests;

/// <summary>
/// The J0 layer: which runs measured what they set out to measure. A run that the harness ended, or
/// that a compacting variant produced without ever compacting, is not a cheap run — it is a run of
/// something else, and the aggregates must not average it in.
/// </summary>
public class J0ValidityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"todo-eval-j0-{Guid.NewGuid():N}");

    public J0ValidityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>A one-thread store whose root thread carries <paramref name="checkpoints"/> compactions.</summary>
    private string WriteStore(int checkpoints)
    {
        var conversations = Path.Combine(_dir, $"conversations-{Guid.NewGuid():N}");
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
            JsonSerializer.Serialize(new { threadId = "thread-1", properties = new Dictionary<string, string>() })
        );
        return conversations;
    }

    private RunMetrics Extract(RunManifestEntry entry, int checkpoints) =>
        MetricsExtractor.Extract(WriteStore(checkpoints), [entry], expectedBoard: null).Runs.Single();

    private static RunManifestEntry Entry(
        string status,
        bool variantCompacts = false,
        int? minCompactions = null,
        string? threadId = "thread-1"
    ) =>
        new()
        {
            RunKey = "compact-v0/c1/m1/seed0",
            Model = "m1",
            SeedIndex = 0,
            Topic = "t",
            Variant = "compact-v0",
            Task = "c1",
            Status = status,
            ThreadId = threadId,
            VariantCompacts = variantCompacts,
            MinCompactions = minCompactions,
        };

    [Fact]
    public void ACompletedRunThatMetItsFloor_IsValid()
    {
        var run = Extract(Entry(RunOutcomes.Completed, variantCompacts: true, minCompactions: 1), checkpoints: 2);

        run.Valid.Should().BeTrue();
        run.Compactions.Should().Be(2);
    }

    [Theory]
    [InlineData(RunOutcomes.TimedOut)]
    [InlineData(RunOutcomes.HarnessError)]
    [InlineData(RunOutcomes.Errored)]
    [InlineData(RunOutcomes.Interrupted)]
    public void ARunThatDidNotCompleteOnItsOwnTerms_IsInvalid(string status)
    {
        Extract(Entry(status), checkpoints: 0).Valid.Should().BeFalse();
    }

    [Fact]
    public void ACompactingVariantBelowTheFloor_IsInvalid()
    {
        // It finished, and it may even have scored well — but it never exercised compaction, so its
        // cost is not the cost of compacting and must not be averaged as though it were.
        var run = Extract(Entry(RunOutcomes.Completed, variantCompacts: true, minCompactions: 1), checkpoints: 0);

        run.Valid.Should().BeFalse();
        run.Status.Should().Be(RunOutcomes.Completed, "the row still records what actually happened");
    }

    [Fact]
    public void ANonCompactingVariantIgnoresTheFloor()
    {
        // The control arm is SUPPOSED to take zero checkpoints; invalidating it would delete the
        // baseline every other arm is measured against.
        var run = Extract(Entry(RunOutcomes.Completed, variantCompacts: false, minCompactions: 1), checkpoints: 0);

        run.Valid.Should().BeTrue();
    }

    [Fact]
    public void ATaskWithNoFloorIsNeverInvalidatedForCompacting()
    {
        Extract(Entry(RunOutcomes.Completed, variantCompacts: true, minCompactions: null), checkpoints: 0)
            .Valid.Should()
            .BeTrue();
    }

    [Fact]
    public void ARunWithNoConversationInTheStore_IsInvalid()
    {
        // Nothing to read means nothing was measured, whatever the status claims.
        var run = Extract(
            Entry(RunOutcomes.Completed, variantCompacts: true, minCompactions: 1, threadId: "missing"),
            checkpoints: 1
        );

        run.Valid.Should().BeFalse();
    }

    [Fact]
    public void TheJ1VerdictAndSteerTimeTravelFromTheManifestOntoTheRow()
    {
        var sentAt = DateTimeOffset.Parse("2026-09-16T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var entry = Entry(RunOutcomes.Completed) with
        {
            SteerSentAt = sentAt,
            J1 = new J1Result
            {
                Outcome = "partial",
                Score = 0.5,
                FailedChecks = ["test_two"],
            },
        };

        var run = Extract(entry, checkpoints: 0);

        run.J1!.Outcome.Should().Be("partial");
        run.J1.FailedChecks.Should().Equal("test_two");
        run.SteerSentAt.Should().Be(sentAt);
    }
}
