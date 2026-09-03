using TodoEval.Runner.Sweep;

namespace TodoEval.Runner.Metrics;

/// <summary>
/// One sweep's numbers rolled up to the sweep level, computed ONCE per side of a comparison.
/// </summary>
/// <remarks>
/// The before/after table and the gate table read the same instance on purpose: two roll-ups of the
/// same runs computed by two pieces of code is exactly how a delta and the gate that judges it end
/// up disagreeing in a published report.
/// <para>
/// Every field is a COUNT or an average of counts. Nothing here decides whether a number is good;
/// <see cref="DeterministicGates"/> does that, and it needs the denominators to tell "zero measured"
/// apart from "nothing was measurable".
/// </para>
/// </remarks>
internal sealed record SweepAggregate
{
    /// <summary>The board tool whose failure rate #621 set an absolute target for.</summary>
    public const string AddNote = "add-note";

    /// <summary>The wait outcome that says the supervisor waited on an agent that does not exist.</summary>
    public const string UnknownAgentOutcome = "unknown_agent";

    public required int Runs { get; init; }
    public required int CompletedRuns { get; init; }

    /// <summary>Runs that passed the spec's validity preconditions — a sweep must discard the rest.</summary>
    public required int ValidRuns { get; init; }

    /// <summary>Runs whose terminal status is a harness fault (harness error, timeout, interruption).</summary>
    public required int FaultedRuns { get; init; }

    /// <summary>Runs for which completion could be judged at all (null when no expected board existed).</summary>
    public required int CompletionMeasuredRuns { get; init; }

    /// <summary>Runs whose board matched the expectation, among <see cref="CompletionMeasuredRuns"/>.</summary>
    public required int CompletedBoardRuns { get; init; }

    public required int TaskToolCalls { get; init; }
    public required int TaskToolErrors { get; init; }
    public required int CoordinationToolCalls { get; init; }
    public required int CoordinationToolErrors { get; init; }
    public required int AddNoteCalls { get; init; }
    public required int AddNoteErrors { get; init; }
    public required int RetryStorms { get; init; }
    public required int BoardIdVanished { get; init; }

    /// <summary>
    /// Obligations still open when the runs ended, summed over the sweep. Meaningless on its own:
    /// read it with <see cref="ObligationReportingResults"/>, which is zero while no build emits the
    /// field, and a zero count then means NOT REPORTED rather than "none were open" (#673).
    /// </summary>
    public required int OpenObligations { get; init; }

    public required int ObligationReportingResults { get; init; }

    /// <summary>Every wait call's outcome, whatever it was — the denominator for the wait gate.</summary>
    public required int WaitOutcomes { get; init; }

    /// <summary>Waits that named an agent the directory does not know (shared-decisions §4's storm signal).</summary>
    public required int UnknownAgentWaits { get; init; }

    public required double AverageTurns { get; init; }

    /// <summary>
    /// Runs that are valid AND completed the board — the only runs criterion 3 may compare, because
    /// a run that did less work by failing earlier is not an improvement in redundancy or tokens.
    /// </summary>
    public required int ComparableRuns { get; init; }

    public required double ToolCallsPerComparableRun { get; init; }
    public required double InputTokensPerComparableRun { get; init; }

    /// <summary>Usage records behind <see cref="InputTokensPerComparableRun"/>; zero means ABSENT, not free.</summary>
    public required int ComparableUsageRecords { get; init; }

    public required int SpawnTimings { get; init; }

    /// <summary>Average per-spawn tool-catalog size: the per-turn tool-contract growth §15 bounds.</summary>
    public required double ToolCatalogBytesPerSpawn { get; init; }

    /// <summary>Every failure in the sweep rolled up by error code, across both families.</summary>
    public required CountMap ErrorCodes { get; init; }

    /// <summary>The 22 per-tool rows summed over the sweep, so residual errors can be listed per tool.</summary>
    public required IReadOnlyDictionary<string, PerToolScore> PerTool { get; init; }

    /// <summary>Distinct validity reasons the sweep recorded, in first-seen order.</summary>
    public required IReadOnlyList<string> ValidityReasons { get; init; }

    /// <summary>Threads the fabricated-compliance heuristic flagged — a triage pointer, never a verdict.</summary>
    public required IReadOnlyList<string> FabricatedComplianceSuspects { get; init; }

    public double ValidityRate => Ratio(ValidRuns, Runs);
    public double Coverage => Ratio(CompletedRuns, Runs);
    public double FaultRate => Ratio(FaultedRuns, Runs);
    public double CompletionRate => Ratio(CompletedBoardRuns, CompletionMeasuredRuns);
    public double TaskErrorRate => Ratio(TaskToolErrors, TaskToolCalls);
    public double CoordinationErrorRate => Ratio(CoordinationToolErrors, CoordinationToolCalls);
    public double AddNoteErrorRate => Ratio(AddNoteErrors, AddNoteCalls);

    public static SweepAggregate Of(IReadOnlyList<RunMetrics> runs)
    {
        var comparable = runs.Where(r => r.Validity.Valid && r.Completion == true).ToList();
        var spawns = runs.SelectMany(r => r.SpawnTimings).ToList();
        var perTool = MergePerTool(runs);
        var addNote = perTool.TryGetValue(AddNote, out var note) ? note : null;

        return new SweepAggregate
        {
            Runs = runs.Count,
            CompletedRuns = runs.Count(r => r.Status == RunOutcomes.Completed),
            ValidRuns = runs.Count(r => r.Validity.Valid),
            FaultedRuns = runs.Count(IsFault),
            CompletionMeasuredRuns = runs.Count(r => r.Completion is not null),
            CompletedBoardRuns = runs.Count(r => r.Completion == true),
            TaskToolCalls = runs.Sum(r => r.TaskToolCalls),
            TaskToolErrors = runs.Sum(r => r.TaskToolErrors),
            CoordinationToolCalls = runs.Sum(r => r.CoordinationToolCalls),
            CoordinationToolErrors = runs.Sum(r => r.CoordinationToolErrors),
            AddNoteCalls = addNote?.Calls ?? 0,
            AddNoteErrors = addNote?.Errors ?? 0,
            RetryStorms = runs.Sum(r => r.RetryStormCount),
            BoardIdVanished = runs.Sum(r => r.BoardIdVanished.Count),
            OpenObligations = runs.Sum(r => r.OpenObligations.LastObserved),
            ObligationReportingResults = runs.Sum(r => r.OpenObligations.ResultsCarryingField),
            WaitOutcomes = runs.Sum(r => r.WaitOutcomes.Values.Sum()),
            UnknownAgentWaits = runs.Sum(r =>
                r.WaitOutcomes.TryGetValue(UnknownAgentOutcome, out var count) ? count : 0
            ),
            AverageTurns = Mean(runs.Select(r => (double)r.Turns)),
            ComparableRuns = comparable.Count,
            ToolCallsPerComparableRun = Mean(comparable.Select(r => (double)r.TotalToolCalls)),
            InputTokensPerComparableRun = Mean(comparable.Select(r => (double)r.Usage.Totals.InputTokens)),
            ComparableUsageRecords = comparable.Sum(r => r.Usage.Totals.Records),
            SpawnTimings = spawns.Count,
            ToolCatalogBytesPerSpawn = Mean(spawns.Select(t => (double)t.ToolCatalogBytes)),
            ErrorCodes = CountMap.Merge(runs.Select(r => r.ErrorCodes)),
            PerTool = perTool,
            ValidityReasons = [.. runs.SelectMany(r => r.Validity.Reasons).Distinct(StringComparer.Ordinal)],
            FabricatedComplianceSuspects =
            [
                .. runs.SelectMany(r => r.Validity.FabricatedComplianceSuspects).Distinct(StringComparer.Ordinal),
            ],
        };
    }

    /// <summary>
    /// A run the harness rather than the model ended: the fault-rate bound refuses a comparison
    /// against a sweep that mostly measured its own plumbing (labels in <see cref="RunOutcomes"/>).
    /// </summary>
    private static bool IsFault(RunMetrics run) =>
        run.Status is RunOutcomes.HarnessError or RunOutcomes.TimedOut or RunOutcomes.Interrupted;

    private static IReadOnlyDictionary<string, PerToolScore> MergePerTool(IReadOnlyList<RunMetrics> runs)
    {
        var merged = new Dictionary<string, PerToolScore>(StringComparer.Ordinal);
        foreach (var (tool, row) in runs.SelectMany(r => r.PerTool))
        {
            var calls = row.Calls + (merged.TryGetValue(tool, out var seen) ? seen.Calls : 0);
            var errors = row.Errors + (seen?.Errors ?? 0);
            merged[tool] = new PerToolScore
            {
                Calls = calls,
                Errors = errors,
                ErrorRate = calls > 0 ? Math.Round((double)errors / calls, 4) : 0,
                Family = row.Family,
                ErrorCodes = CountMap.Merge([seen?.ErrorCodes ?? CountMap.Empty, row.ErrorCodes]),
            };
        }

        return merged;
    }

    /// <summary>Zero denominator yields 0, and every caller that cares checks the denominator itself.</summary>
    private static double Ratio(int numerator, int denominator) =>
        denominator == 0 ? 0 : (double)numerator / denominator;

    private static double Mean(IEnumerable<double> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? 0 : list.Average();
    }
}
