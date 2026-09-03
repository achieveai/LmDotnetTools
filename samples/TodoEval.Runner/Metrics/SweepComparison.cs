using System.Text;
using System.Text.Json;
using TodoEval.Runner.Sweep;

namespace TodoEval.Runner.Metrics;

/// <summary>
/// Why two sweeps may not be compared. Evaluated in <see cref="SweepComparison.RefusalOrder"/>;
/// the first match wins so the report names the most specific cause rather than a downstream one.
/// </summary>
internal enum ComparisonRefusal
{
    /// <summary>The two sweeps are comparable.</summary>
    None = 0,

    /// <summary>A side is not an archived sweep: no <c>sweep-manifest.json</c>, or no <c>runs.jsonl</c>.</summary>
    ManifestMissing = 1,

    /// <summary>The models were asked to do different things — different task.md / mode.json / expected-board.json.</summary>
    CorpusHashDiffers = 2,

    /// <summary>The two sets of numbers were extracted under different metrics-spec revisions.</summary>
    SpecVersionDiffers = 3,

    /// <summary>Same spec revision, but a measurement-defining constant differs between the extractions.</summary>
    EvaluatorHashDiffers = 4,

    /// <summary>Too few runs completed for the sweep to say anything about the ones that did.</summary>
    CoverageBelowMinimum = 5,

    /// <summary>So many runs died in the harness that the sweep measured its own plumbing.</summary>
    FaultRateAboveMaximum = 6,
}

/// <summary>One sweep as a comparison sees it: its provenance manifest and its extracted rows.</summary>
internal sealed record SweepSnapshot
{
    public required string Directory { get; init; }
    public required SweepManifest? Manifest { get; init; }
    public required IReadOnlyList<RunMetrics> Runs { get; init; }

    /// <summary>The artifact that is missing, or null when the directory is a complete archived sweep.</summary>
    public required string? MissingArtifact { get; init; }

    private SweepAggregate? _aggregate;

    /// <summary>Rolled up once and reused: the delta table and the gates must read the same numbers.</summary>
    public SweepAggregate Aggregate => _aggregate ??= SweepAggregate.Of(Runs);

    /// <summary>Reads both artifacts of an archived sweep off disk.</summary>
    public static SweepSnapshot Load(string directory)
    {
        var runsPath = Path.Combine(directory, ResultsWriter.RunsFileName);
        return File.Exists(runsPath)
            ? WithRuns(directory, ResultsWriter.ReadRunsJsonl(runsPath))
            : new SweepSnapshot
            {
                Directory = directory,
                Manifest = null,
                Runs = [],
                MissingArtifact = ResultsWriter.RunsFileName,
            };
    }

    /// <summary>
    /// The just-extracted sweep: its rows are already in memory, so they are used verbatim rather
    /// than round-tripped through the file that was written from them.
    /// </summary>
    public static SweepSnapshot WithRuns(string directory, IReadOnlyList<RunMetrics> runs)
    {
        var manifest = SweepManifest.Read(directory);
        return new SweepSnapshot
        {
            Directory = directory,
            Manifest = manifest,
            Runs = runs,
            MissingArtifact = manifest is null ? SweepManifest.FileName : null,
        };
    }
}

/// <summary>
/// The published verdict: whether the two sweeps could be compared, what drifted between them, and
/// — only when they could — the before/after rows and the gate table.
/// </summary>
internal sealed record ComparisonReport
{
    public const string FileName = "comparison.json";
    public const string SchemaId = "todo-eval/comparison@1";

    public string Schema { get; init; } = SchemaId;
    public required ComparisonRefusal Refusal { get; init; }
    public required string Reason { get; init; }
    public required string BaselineDirectory { get; init; }
    public required string CandidateDirectory { get; init; }

    /// <summary>
    /// Differences between what the two sweeps RAN under. Reported, never a refusal (shared-decisions
    /// §13): both archives are re-scored by today's identical evaluator, so their numbers stay
    /// commensurable even when the contract moved between the runs. It is still recorded, because a
    /// reader comparing a sweep run under an older tool contract deserves to know that.
    /// </summary>
    public IReadOnlyList<string> ContractDrift { get; init; } = [];

    /// <summary>Null on refusal: a refused comparison publishes no numbers at all.</summary>
    public IReadOnlyList<MetricDelta>? Deltas { get; init; }

    /// <summary>Null on refusal.</summary>
    public IReadOnlyList<GateResult>? Gates { get; init; }

    public bool Compared => Refusal == ComparisonRefusal.None;

    /// <summary>Any gate that actually failed — what exit code 4 is computed from.</summary>
    public bool HasGateFailure => Gates?.Any(g => g.Outcome == GateOutcome.Failed) == true;

    /// <summary>
    /// True only when every gate was measurable AND passed. A gate that could not be measured makes
    /// this false without making <see cref="HasGateFailure"/> true, because "unproven" is neither a
    /// pass nor a failure.
    /// </summary>
    public bool AllGatesPassed => Gates?.All(g => g.Outcome == GateOutcome.Passed) == true;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public void Write(string sweepDir) =>
        File.WriteAllText(
            Path.Combine(sweepDir, FileName),
            JsonSerializer.Serialize(this, JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        );
}

/// <summary>One before/after row. Reported for gated and ungated metrics alike.</summary>
internal sealed record MetricDelta
{
    public required string MetricId { get; init; }
    public required string Description { get; init; }
    public required double Baseline { get; init; }
    public required double Candidate { get; init; }

    /// <summary>Which direction counts as an improvement for this metric.</summary>
    public required GateDirection Better { get; init; }

    public double Change => Candidate - Baseline;

    /// <summary>
    /// True when the metric moved against <see cref="Better"/>. Every such row is republished under
    /// "Contrary evidence", so a summary cannot report only the metrics that improved.
    /// </summary>
    public bool MovedTheWrongWay => Better == GateDirection.AtMost ? Candidate > Baseline : Candidate < Baseline;
}

/// <summary>
/// Decides whether a post-fix sweep may be compared against an archived baseline at all, and — only
/// if it may — produces the before/after rows and runs <see cref="DeterministicGates"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which fingerprint each refusal reads (shared-decisions §13).</b> The corpus refusal reads the
/// FROZEN <c>ranUnder.taskCorpusHash</c>, because that is the only recording of what the model was
/// actually asked. <c>extractedUnder</c> is recomputed from the corpus on disk NOW, so two sweeps
/// re-extracted on one checkout always share it — a corpus refusal reading it could never fire.
/// The spec and evaluator refusals read <c>extractedUnder</c>, because that is what says whether the
/// two sets of NUMBERS were produced by the same evaluator. A <c>ranUnder</c> spec or evaluator
/// difference is reported as contract drift and never refuses: both archives are re-scored by
/// today's evaluator, which is exactly what lets a spec revision land without forcing a baseline
/// re-run.
/// </para>
/// <para>
/// <b>Task type</b> (the issue's wording) needs no refusal of its own: <c>mode.json</c> is one of the
/// three corpus files, so a different task type is already a different <c>taskCorpusHash</c>.
/// </para>
/// </remarks>
internal static class SweepComparison
{
    /// <summary>
    /// A sweep in which fewer than half the runs completed cannot characterise the ones that did.
    /// This is a COMPARABILITY floor, not a quality target — "did completion improve" is a gate.
    /// </summary>
    public const double MinimumCoverage = 0.5;

    /// <summary>Above this share of harness faults the sweep measured its own plumbing, not the model.</summary>
    public const double MaximumFaultRate = 0.25;

    /// <summary>
    /// Evaluation order. Most specific first: <c>specVersion</c> feeds <c>specHash</c> feeds
    /// <c>evaluatorHash</c>, so ordering the evaluator check first would mask every spec bump behind
    /// it and leave <see cref="ComparisonRefusal.SpecVersionDiffers"/> unreachable.
    /// </summary>
    public static readonly IReadOnlyList<ComparisonRefusal> RefusalOrder =
    [
        ComparisonRefusal.ManifestMissing,
        ComparisonRefusal.CorpusHashDiffers,
        ComparisonRefusal.SpecVersionDiffers,
        ComparisonRefusal.EvaluatorHashDiffers,
        ComparisonRefusal.CoverageBelowMinimum,
        ComparisonRefusal.FaultRateAboveMaximum,
    ];

    public static ComparisonReport Compare(SweepSnapshot baseline, SweepSnapshot candidate)
    {
        var (refusal, reason) = Refuse(baseline, candidate);
        if (refusal != ComparisonRefusal.None)
        {
            // Deltas, gates and even the contract-drift notes stay off a refused report: publishing
            // any number from two sweeps that may not be compared is the failure mode this exists to
            // prevent.
            return new ComparisonReport
            {
                Refusal = refusal,
                Reason = reason,
                BaselineDirectory = baseline.Directory,
                CandidateDirectory = candidate.Directory,
            };
        }

        return new ComparisonReport
        {
            Refusal = ComparisonRefusal.None,
            Reason = "The two sweeps share a corpus and were extracted under one measurement contract.",
            BaselineDirectory = baseline.Directory,
            CandidateDirectory = candidate.Directory,
            ContractDrift = DriftBetweenRunContracts(baseline.Manifest!.RanUnder, candidate.Manifest!.RanUnder),
            Deltas = Deltas(baseline.Aggregate, candidate.Aggregate),
            Gates = DeterministicGates.Evaluate(baseline.Aggregate, candidate.Aggregate),
        };
    }

    /// <summary>
    /// The ordered refusal checks. Evaluated in <see cref="RefusalOrder"/> so the reported cause is
    /// the most specific one, not whichever downstream hash it also happened to move.
    /// </summary>
    private static (ComparisonRefusal Refusal, string Reason) Refuse(SweepSnapshot baseline, SweepSnapshot candidate)
    {
        foreach (var check in RefusalOrder)
        {
            if (Evaluate(check, baseline, candidate) is { } reason)
            {
                return (check, reason);
            }
        }

        return (ComparisonRefusal.None, "");
    }

    private static string? Evaluate(ComparisonRefusal check, SweepSnapshot baseline, SweepSnapshot candidate) =>
        check switch
        {
            ComparisonRefusal.ManifestMissing => MissingArtifact(baseline, candidate),
            ComparisonRefusal.CorpusHashDiffers => Differs(
                "taskCorpusHash",
                "ranUnder",
                baseline.Manifest!.RanUnder.TaskCorpusHash,
                candidate.Manifest!.RanUnder.TaskCorpusHash,
                "the two sweeps were asked to do different things (task.md, mode.json or expected-board.json moved)"
            ),
            ComparisonRefusal.SpecVersionDiffers => Differs(
                "specVersion",
                "extractedUnder",
                baseline.Manifest!.ExtractedUnder.SpecVersion,
                candidate.Manifest!.ExtractedUnder.SpecVersion,
                "the two sets of numbers were extracted under different metrics-spec revisions; "
                    + "re-extract the older archive with --extract-only"
            ),
            ComparisonRefusal.EvaluatorHashDiffers => Differs(
                "evaluatorHash",
                "extractedUnder",
                baseline.Manifest!.ExtractedUnder.EvaluatorHash,
                candidate.Manifest!.ExtractedUnder.EvaluatorHash,
                "a measurement-defining constant differs between the two extractions; "
                    + "re-extract the older archive with --extract-only"
            ),
            ComparisonRefusal.CoverageBelowMinimum => Below(
                "coverage",
                MinimumCoverage,
                baseline.Aggregate.Coverage,
                candidate.Aggregate.Coverage
            ),
            ComparisonRefusal.FaultRateAboveMaximum => Above(
                "fault rate",
                MaximumFaultRate,
                baseline.Aggregate.FaultRate,
                candidate.Aggregate.FaultRate
            ),
            _ => null,
        };

    private static string? MissingArtifact(SweepSnapshot baseline, SweepSnapshot candidate) =>
        baseline.MissingArtifact is { } missingBaseline
            ? $"'{baseline.Directory}' is not a comparable sweep: {missingBaseline} is missing. "
                + "An archive from before the fingerprint manifest cannot say what corpus or contract "
                + "produced its numbers, and the comparison refuses rather than guessing."
        : candidate.MissingArtifact is { } missingCandidate
            ? $"'{candidate.Directory}' is not a comparable sweep: {missingCandidate} is missing."
        : null;

    private static string? Differs(string field, string recording, string baseline, string candidate, string why) =>
        string.Equals(baseline, candidate, StringComparison.Ordinal)
            ? null
            : $"{recording}.{field} differs (baseline '{baseline}', candidate '{candidate}'): {why}.";

    private static string? Below(string what, double minimum, double baseline, double candidate) =>
        baseline < minimum || candidate < minimum
            ? $"{what} is below the {minimum:P0} comparability floor "
                + $"(baseline {baseline:P1}, candidate {candidate:P1}): too few runs completed for either "
                + "sweep to characterise the ones that did. This is a comparability bound, not a quality target."
            : null;

    private static string? Above(string what, double maximum, double baseline, double candidate) =>
        baseline > maximum || candidate > maximum
            ? $"{what} is above the {maximum:P0} comparability ceiling "
                + $"(baseline {baseline:P1}, candidate {candidate:P1}): the sweep mostly measured its own "
                + "plumbing rather than the model."
            : null;

    /// <summary>
    /// What the two sweeps RAN under, where it differs. Never a refusal: both archives are re-scored
    /// by today's identical evaluator, so the numbers stay commensurable. The corpus is absent here
    /// on purpose — a corpus difference has already refused above.
    /// </summary>
    private static IReadOnlyList<string> DriftBetweenRunContracts(FingerprintSet baseline, FingerprintSet candidate)
    {
        var drift = new List<string>();
        Note("specVersion", baseline.SpecVersion, candidate.SpecVersion);
        Note("specHash", baseline.SpecHash, candidate.SpecHash);
        Note("evaluatorHash", baseline.EvaluatorHash, candidate.EvaluatorHash);
        return drift;

        void Note(string field, string before, string after)
        {
            if (!string.Equals(before, after, StringComparison.Ordinal))
            {
                drift.Add(
                    $"ranUnder.{field} differs (baseline '{before}', candidate '{after}'): the measurement "
                        + "contract moved between the two runs. Reported, not refused — both archives were "
                        + "re-extracted under one evaluator, so the numbers below are still commensurable."
                );
            }
        }
    }

    /// <summary>
    /// Every reported before/after row, gated or not. The issue asks for validity, completion, turns,
    /// calls, error rates, storms, obligations, wait outcomes, startup work and usage — so the table
    /// carries all of them and the gate table judges the subset that has a threshold.
    /// </summary>
    private static IReadOnlyList<MetricDelta> Deltas(SweepAggregate baseline, SweepAggregate candidate)
    {
        List<MetricDelta> deltas = [];
        Add(
            "completion-rate",
            "Runs whose board matched the expectation",
            GateDirection.AtLeast,
            a => a.CompletionRate
        );
        Add("validity-rate", "Runs that passed the validity preconditions", GateDirection.AtLeast, a => a.ValidityRate);
        Add("average-turns", "Model calls per run", GateDirection.AtMost, a => a.AverageTurns);
        Add("task-tool-calls", "Board tool calls", GateDirection.AtMost, a => a.TaskToolCalls);
        Add(
            "task-tool-error-rate",
            "Share of board tool calls that failed",
            GateDirection.AtMost,
            a => a.TaskErrorRate
        );
        Add(
            "coordination-tool-calls",
            "Sub-agent coordination calls",
            GateDirection.AtMost,
            a => a.CoordinationToolCalls
        );
        Add(
            "coordination-tool-error-rate",
            "Share of coordination calls refused",
            GateDirection.AtMost,
            a => a.CoordinationErrorRate
        );
        Add("retry-storms", "Runs of 3+ identical failing calls", GateDirection.AtMost, a => a.RetryStorms);
        Add(
            "board-id-vanished",
            "Board rows minted, never deleted, later not found",
            GateDirection.AtMost,
            a => a.BoardIdVanished
        );
        Add(
            "open-obligations",
            "Obligations still open when the runs ended",
            GateDirection.AtMost,
            a => a.OpenObligations
        );
        Add(
            "unknown-agent-waits",
            "Waits naming an agent the directory does not know",
            GateDirection.AtMost,
            a => a.UnknownAgentWaits
        );
        Add("spawns", "Sub-agent constructions the host timed", GateDirection.AtMost, a => a.SpawnTimings);
        Add(
            "tool-catalog-bytes-per-spawn",
            "Tool contract carried into each spawn",
            GateDirection.AtMost,
            a => a.ToolCatalogBytesPerSpawn
        );
        Add(
            "tool-calls-per-successful-run",
            "Tool calls per valid completed run (redundancy proxy)",
            GateDirection.AtMost,
            a => a.ToolCallsPerComparableRun
        );
        Add(
            "input-tokens-per-successful-run",
            "Input tokens per valid completed run",
            GateDirection.AtMost,
            a => a.InputTokensPerComparableRun
        );
        return deltas;

        void Add(string id, string description, GateDirection better, Func<SweepAggregate, double> read) =>
            deltas.Add(
                new MetricDelta
                {
                    MetricId = id,
                    Description = description,
                    Baseline = read(baseline),
                    Candidate = read(candidate),
                    Better = better,
                }
            );
    }
}
