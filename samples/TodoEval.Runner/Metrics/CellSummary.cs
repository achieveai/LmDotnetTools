using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TodoEval.Runner.Metrics;

/// <summary>
/// One (variant, task) cell of the sweep: how many of its runs measured anything, what the task
/// checker made of them, and what they cost.
/// </summary>
/// <remarks>
/// Every average is taken over the VALID runs only (<see cref="RunMetrics.Valid"/>). A run that timed
/// out, errored, or never reached its compaction floor did a different, usually cheaper, amount of
/// work; averaging it in would make a variant look cheap precisely when it is failing. The
/// denominators travel with the averages so a reader can tell a measured zero from an empty cell.
/// </remarks>
internal sealed record CellSummary
{
    public required string Variant { get; init; }

    /// <summary>The task id, or null in the single-task layout.</summary>
    public required string? Task { get; init; }

    /// <summary>Every run of this cell, valid or not.</summary>
    public required int Runs { get; init; }

    /// <summary>Runs that passed J0 — the denominator of every average below.</summary>
    public required int ValidRuns { get; init; }

    /// <summary>Valid runs a checker actually judged; <see cref="MeanScore"/> and <see cref="PassRate"/> are over these.</summary>
    public required int JudgedRuns { get; init; }

    /// <summary>Mean J1 score over the judged runs, or null when none was judged.</summary>
    public required double? MeanScore { get; init; }

    /// <summary>Judged runs whose outcome was <c>pass</c>, over the judged runs. Null when none was judged.</summary>
    public required double? PassRate { get; init; }

    public required double? MeanInputTokensUncached { get; init; }
    public required double? MeanCacheReadTokens { get; init; }
    public required double? MeanOutputTokens { get; init; }

    /// <summary>Mean cost over the valid runs that carried one. Null when NONE did: unpriced, not free.</summary>
    /// <remarks>
    /// <para>
    /// The mean over <see cref="RunsWithCost"/> runs, which is NOT the mean over the cell when those
    /// are fewer than <see cref="ValidRuns"/>: the unpriced runs are unmeasured, not zero, so nothing
    /// here bounds them in either direction. Read it with its denominator or not at all.
    /// </para>
    /// <para>
    /// A LOWER BOUND of even that subset's mean whenever <see cref="MeanCostIsLowerBound"/> is set. A
    /// run's own <see cref="RunCost.CostMicros"/> covers only its <see cref="RunCost.RecordsWithCost"/>,
    /// so a run the resolver priced in part is under the truth before this mean is taken.
    /// </para>
    /// </remarks>
    public required double? MeanCostMicros { get; init; }

    /// <summary>Valid runs whose usage carried a cost — the denominator of <see cref="MeanCostMicros"/>.</summary>
    public required int RunsWithCost { get; init; }

    /// <summary>
    /// Priced runs whose price covered only some of their usage records. Each such run's cost is a
    /// lower bound, so this is the count that decides whether the cell's mean is a measurement.
    /// </summary>
    public required int PartlyPricedRuns { get; init; }

    /// <summary>
    /// True when <see cref="MeanCostMicros"/> understates the mean of the runs it covers: at least one
    /// of them was priced in part, so its own figure is already under the truth. False on a null mean,
    /// which is not a bound on anything — it is the absence of a measurement, and says so as <c>n/a</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT set by <see cref="RunsWithCost"/> being under <see cref="ValidRuns"/>. Excluded
    /// runs make the figure cover less of the cell; they do not make it a bound on the cell. A mean of
    /// 100 over 100 priced runs, beside 100 unpriced runs that in truth cost nothing, would be printed
    /// as <c>&gt;=100</c> while the cell's true mean is 50 — a bound pointing the wrong way. That
    /// incompleteness is reported as coverage (<c>n priced</c>), which claims nothing about the
    /// unmeasured runs, and is the one honest thing to say about them.
    /// </remarks>
    public bool MeanCostIsLowerBound => MeanCostMicros is not null && PartlyPricedRuns > 0;

    public required double? MeanCompactions { get; init; }

    /// <summary>The checker outcome tasks/README.md reserves for a run that passed every check.</summary>
    private const string PassOutcome = "pass";

    /// <summary>
    /// Rolls the sweep up into one row per (variant, task), ordered by variant then task so two sweeps'
    /// tables line up.
    /// </summary>
    public static IReadOnlyList<CellSummary> Of(IReadOnlyList<RunMetrics> runs) =>
        [
            .. runs.GroupBy(r => (Variant: r.Variant ?? VariantConfig.DefaultName, r.Task))
                .OrderBy(g => g.Key.Variant, StringComparer.Ordinal)
                .ThenBy(g => g.Key.Task, StringComparer.Ordinal)
                .Select(g => For(g.Key.Variant, g.Key.Task, [.. g])),
        ];

    private static CellSummary For(string variant, string? task, IReadOnlyList<RunMetrics> cell)
    {
        var valid = cell.Where(r => r.Valid).ToList();
        var judged = valid.Where(r => r.J1 is { Judged: true }).ToList();
        var priced = valid.Where(r => r.Cost.CostMicros is not null).ToList();

        return new CellSummary
        {
            Variant = variant,
            Task = task,
            Runs = cell.Count,
            ValidRuns = valid.Count,
            JudgedRuns = judged.Count,
            MeanScore = Mean(judged.Select(r => r.J1!.Score ?? 0)),
            PassRate = Mean(
                judged.Select(r => string.Equals(r.J1!.Outcome, PassOutcome, StringComparison.Ordinal) ? 1d : 0d)
            ),
            MeanInputTokensUncached = Mean(valid.Select(r => (double)r.Cost.InputTokensUncached)),
            MeanCacheReadTokens = Mean(valid.Select(r => (double)r.Cost.CacheReadTokens)),
            MeanOutputTokens = Mean(valid.Select(r => (double)r.Cost.OutputTokens)),
            MeanCostMicros = Mean(priced.Select(r => (double)r.Cost.CostMicros!.Value)),
            RunsWithCost = priced.Count,
            PartlyPricedRuns = priced.Count(r => r.Cost.RecordsWithCost < r.Cost.Records),
            MeanCompactions = Mean(valid.Select(r => (double)r.Compactions)),
        };
    }

    /// <summary>Null for an empty sequence: no runs is not an average of zero.</summary>
    private static double? Mean(IEnumerable<double> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? null : list.Average();
    }
}

/// <summary>Writes the per-cell roll-up as <c>summary.json</c> and as a table for the console.</summary>
internal static class CellSummaryWriter
{
    public const string FileName = "summary.json";

    /// <summary>Identifies the shape for anything reading an archived sweep's roll-up.</summary>
    public const string Schema = "compaction-eval/summary@1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static void Write(string sweepDir, IReadOnlyList<CellSummary> cells)
    {
        File.WriteAllText(
            Path.Combine(sweepDir, FileName),
            JsonSerializer.Serialize(
                new
                {
                    schema = Schema,
                    generatedUtc = DateTimeOffset.UtcNow,
                    note = "Every average is over the J0-valid runs only; the denominators are in the row. "
                        + "meanCostMicros covers runsWithCost runs, not the cell: where that is under validRuns the "
                        + "rest are unmeasured, not zero. meanCostIsLowerBound means something further -- a run in "
                        + "that subset was itself priced only in part (partlyPricedRuns).",
                    cells,
                },
                JsonOptions
            ),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        );
    }

    /// <summary>
    /// The console table. Empty cells print <c>n/a</c> rather than 0, because the one thing this table
    /// must never do is let "nothing was measured" read as "measured, and it was zero".
    /// </summary>
    public static string BuildTable(IReadOnlyList<CellSummary> cells)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "| variant | task | valid/runs | judged | mean score | pass rate | in (uncached) | cache read | out | cost (micros) | compactions |"
        );
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var cell in cells)
        {
            sb.AppendLine(
                $"| {cell.Variant} | {cell.Task ?? "(single)"} | {cell.ValidRuns}/{cell.Runs} | {cell.JudgedRuns} "
                    + $"| {Num(cell.MeanScore, "0.###")} | {Num(cell.PassRate, "0.##")} "
                    + $"| {Num(cell.MeanInputTokensUncached, "0")} | {Num(cell.MeanCacheReadTokens, "0")} "
                    + $"| {Num(cell.MeanOutputTokens, "0")} "
                    + $"| {Cost(cell)} "
                    + $"| {Num(cell.MeanCompactions, "0.##")} |"
            );
        }

        return sb.ToString();
    }

    private static string Num(double? value, string format) =>
        value is { } number ? number.ToString(format, CultureInfo.InvariantCulture) : "n/a";

    /// <summary>
    /// The cost cell with its coverage attached: <c>&gt;=</c> only when the figure is genuinely a lower
    /// bound of what it covers, and the counts that say how much of the cell it covers. ASCII on
    /// purpose — this table is printed to a console whose code page is not guaranteed, and a mangled
    /// inequality is worse than none.
    /// </summary>
    private static string Cost(CellSummary cell)
    {
        if (cell.MeanCostMicros is null)
        {
            return "n/a";
        }

        var coverage = new List<string>(2);
        if (cell.RunsWithCost < cell.ValidRuns)
        {
            coverage.Add($"{cell.RunsWithCost} priced");
        }

        if (cell.PartlyPricedRuns > 0)
        {
            coverage.Add($"{cell.PartlyPricedRuns} partly priced");
        }

        var prefix = cell.MeanCostIsLowerBound ? ">=" : "";
        var suffix = coverage.Count == 0 ? "" : $" ({string.Join(", ", coverage)})";
        return prefix + Num(cell.MeanCostMicros, "0") + suffix;
    }
}
