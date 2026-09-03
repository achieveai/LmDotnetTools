using TodoEval.Runner.Metrics;
using TodoEval.Runner.Sweep;

namespace TodoEval.Runner.Tests;

/// <summary>
/// The refusal contract (shared-decisions §13). Every test flips exactly ONE input away from a
/// clean self-comparison, so a refusal that fires for a second reason fails here rather than
/// hiding behind the one under test.
/// </summary>
public class SweepComparisonTests : IDisposable
{
    private readonly SweepFixture _fixture = new();

    public void Dispose()
    {
        _fixture.Dispose();
        GC.SuppressFinalize(this);
    }

    private static IReadOnlyList<RunMetrics> CleanRuns(int count = 4) =>
        [.. Enumerable.Range(0, count).Select(i => TestRuns.Run(seed: i))];

    private ComparisonReport CompareWith(
        FingerprintSet? candidateRanUnder = null,
        FingerprintSet? candidateExtractedUnder = null,
        IReadOnlyList<RunMetrics>? candidateRuns = null,
        bool candidateManifest = true,
        bool candidateRunsFile = true
    )
    {
        var baseline = _fixture.Write("baseline", CleanRuns());
        var candidate = _fixture.Write(
            "candidate",
            candidateRuns ?? CleanRuns(),
            candidateRanUnder,
            candidateExtractedUnder,
            candidateManifest,
            candidateRunsFile
        );
        return SweepComparison.Compare(SweepFixture.Load(baseline), SweepFixture.Load(candidate));
    }

    [Fact]
    public void IdenticalSweeps_CompareCleanly()
    {
        var report = CompareWith();

        report.Refusal.Should().Be(ComparisonRefusal.None);
        report.Compared.Should().BeTrue();
        report.Deltas.Should().NotBeNull().And.NotBeEmpty();
        report.Gates.Should().NotBeNull().And.NotBeEmpty();
        report.ContractDrift.Should().BeEmpty();
    }

    [Fact]
    public void CorpusHashDiffers_RefusesOnTheFrozenRanUnderHash()
    {
        // The corpus a model FACED is recorded only in ranUnder: extractedUnder is recomputed from
        // the working tree, so it is identical for any two sweeps extracted on one checkout and a
        // refusal reading it there could never fire.
        var report = CompareWith(candidateRanUnder: SweepFixture.Prints(corpus: "ffff"));

        report.Refusal.Should().Be(ComparisonRefusal.CorpusHashDiffers);
        report.Reason.Should().Contain("taskCorpusHash");
    }

    [Fact]
    public void SpecVersionDiffers_RefusesOnTheExtractedUnderVersion()
    {
        var report = CompareWith(candidateExtractedUnder: SweepFixture.Prints(version: "todo-eval/metrics-spec@99"));

        report.Refusal.Should().Be(ComparisonRefusal.SpecVersionDiffers);
    }

    [Fact]
    public void EvaluatorHashDiffers_RefusesWhenAMeasurementConstantMovedWithoutASpecBump()
    {
        // Reachable on its own: the tool vocabularies and the storm threshold feed the evaluator
        // hash without touching the spec version.
        var report = CompareWith(candidateExtractedUnder: SweepFixture.Prints(evaluator: "ffff"));

        report.Refusal.Should().Be(ComparisonRefusal.EvaluatorHashDiffers);
    }

    [Fact]
    public void ManifestMissing_RefusesRatherThanGuessing_ForAPreFingerprintArchive()
    {
        var report = CompareWith(candidateManifest: false);

        report.Refusal.Should().Be(ComparisonRefusal.ManifestMissing);
        report.Reason.Should().Contain(SweepManifest.FileName);
    }

    [Fact]
    public void ManifestMissing_AlsoCoversASweepDirectoryWithNoRunsFile()
    {
        var report = CompareWith(candidateRunsFile: false);

        report.Refusal.Should().Be(ComparisonRefusal.ManifestMissing);
        report.Reason.Should().Contain(ResultsWriter.RunsFileName);
    }

    [Fact]
    public void CoverageBelowMinimum_RefusesASweepTooThinToCharacteriseItself()
    {
        // 1 of 4 completed = 0.25, under the 0.5 comparability floor. The three others errored, so
        // the fault rate stays 0 and cannot be the reason.
        var report = CompareWith(
            candidateRuns:
            [
                TestRuns.Run(seed: 0),
                TestRuns.Run(seed: 1, status: RunOutcomes.Errored, completion: false),
                TestRuns.Run(seed: 2, status: RunOutcomes.Errored, completion: false),
                TestRuns.Run(seed: 3, status: RunOutcomes.Errored, completion: false),
            ]
        );

        report.Refusal.Should().Be(ComparisonRefusal.CoverageBelowMinimum);
    }

    [Fact]
    public void FaultRateAboveMaximum_RefusesASweepThatMostlyMeasuredItsOwnPlumbing()
    {
        // 2 of 4 timed out: coverage is 0.5 and so passes its floor, leaving the fault rate as the
        // only reason this can refuse.
        var report = CompareWith(
            candidateRuns:
            [
                TestRuns.Run(seed: 0),
                TestRuns.Run(seed: 1),
                TestRuns.Run(seed: 2, status: RunOutcomes.TimedOut, completion: false),
                TestRuns.Run(seed: 3, status: RunOutcomes.TimedOut, completion: false),
            ]
        );

        report.Refusal.Should().Be(ComparisonRefusal.FaultRateAboveMaximum);
    }

    [Fact]
    public void Refusal_LeavesEveryDeltaAndGateNull()
    {
        var report = CompareWith(candidateRanUnder: SweepFixture.Prints(corpus: "ffff"));

        report.Deltas.Should().BeNull();
        report.Gates.Should().BeNull();
        report.Compared.Should().BeFalse();
        report.AllGatesPassed.Should().BeFalse("a refusal is never a pass");
        report.HasGateFailure.Should().BeFalse("a refused comparison ran no gate to fail");
    }

    [Fact]
    public void TwoSimultaneousMismatches_ReportTheEarlierRefusal()
    {
        var report = CompareWith(
            candidateRanUnder: SweepFixture.Prints(corpus: "ffff"),
            candidateExtractedUnder: SweepFixture.Prints(version: "todo-eval/metrics-spec@99")
        );

        report.Refusal.Should().Be(ComparisonRefusal.CorpusHashDiffers);
    }

    /// <summary>
    /// A spec bump moves specVersion, specHash AND evaluatorHash together, so ordering the evaluator
    /// check first would make <c>SpecVersionDiffers</c> unreachable for the rest of time.
    /// </summary>
    [Fact]
    public void ASpecBump_ReportsSpecVersionDiffers_NotTheEvaluatorHashItAlsoMoved()
    {
        var report = CompareWith(
            candidateExtractedUnder: SweepFixture.Prints(
                spec: "ffff",
                evaluator: "eeee",
                version: "todo-eval/metrics-spec@99"
            )
        );

        report.Refusal.Should().Be(ComparisonRefusal.SpecVersionDiffers);
        SweepComparison
            .RefusalOrder.Should()
            .ContainInOrder(ComparisonRefusal.SpecVersionDiffers, ComparisonRefusal.EvaluatorHashDiffers);
    }

    /// <summary>
    /// The §13 asymmetry: what the sweeps RAN under may differ in spec and evaluator without refusing,
    /// because both archives are re-scored by today's identical evaluator. It is still published.
    /// </summary>
    [Fact]
    public void RanUnderContractDrift_IsReportedAndNeverRefuses()
    {
        var report = CompareWith(
            candidateRanUnder: SweepFixture.Prints(spec: "ffff", evaluator: "eeee", version: "todo-eval/metrics-spec@1")
        );

        report.Refusal.Should().Be(ComparisonRefusal.None);
        report.Compared.Should().BeTrue();
        report.ContractDrift.Should().HaveCount(3);
        report.ContractDrift.Should().Contain(d => d.Contains("specVersion", StringComparison.Ordinal));
        report.ContractDrift.Should().Contain(d => d.Contains("evaluatorHash", StringComparison.Ordinal));
    }

    [Fact]
    public void RanUnderCorpusDrift_StillRefuses_BecauseTheTwoSweepsWereAskedDifferentThings()
    {
        var report = CompareWith(candidateRanUnder: SweepFixture.Prints(corpus: "ffff"));

        report.Refusal.Should().Be(ComparisonRefusal.CorpusHashDiffers);
        report.ContractDrift.Should().BeEmpty("a refused comparison publishes nothing but its reason");
    }

    [Fact]
    public void Deltas_CarryEveryReportedMetricAndNameTheOnesThatMovedTheWrongWay()
    {
        var baseline = _fixture.Write("baseline", CleanRuns());
        var candidate = _fixture.Write(
            "candidate",
            [.. Enumerable.Range(0, 4).Select(i => TestRuns.Run(seed: i, turns: 20))]
        );

        var report = SweepComparison.Compare(SweepFixture.Load(baseline), SweepFixture.Load(candidate));

        var turns = report.Deltas!.Single(d => d.MetricId == "average-turns");
        turns.Baseline.Should().Be(10);
        turns.Candidate.Should().Be(20);
        turns.Change.Should().Be(10);
        turns.MovedTheWrongWay.Should().BeTrue();
    }

    /// <summary>
    /// The baseline is read back off disk months after it was written, so the round-trip through
    /// <c>runs.jsonl</c> has to survive every tally the gates read.
    /// </summary>
    [Fact]
    public void RunsJsonl_RoundTripsTheTalliesTheGatesRead()
    {
        var dir = _fixture.Write(
            "archive",
            [TestRuns.Run(addNoteCalls: 20, addNoteErrors: 5, waitUnknownAgent: 2, errorCodes: ("depth_limit", 5))]
        );

        var reloaded = SweepFixture.Load(dir);

        reloaded.Runs.Should().HaveCount(1);
        reloaded.Aggregate.AddNoteErrors.Should().Be(5);
        reloaded.Aggregate.ErrorCodes.Should().Contain(new KeyValuePair<string, int>("depth_limit", 5));
        reloaded.Aggregate.UnknownAgentWaits.Should().Be(2);
    }
}
