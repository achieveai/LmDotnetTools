using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TodoEval.Runner.Metrics;

/// <summary>
/// Serializes the extracted metrics into the sweep's two report files:
/// <c>runs.jsonl</c> (one <see cref="RunMetrics"/> per line, machine-readable, diffable across
/// baseline/after sweeps) and <c>summary.md</c> (the per-model rollup #617's before/after comparison
/// reads: validity, completion, turns, per-tool error rates, retry storms).
/// </summary>
internal static class ResultsWriter
{
    public const string RunsFileName = "runs.jsonl";
    public const string SummaryFileName = "summary.md";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Symmetric with the read side: CountMap has no public constructor, so an archived sweep's
        // tallies cannot be deserialized without it.
        Converters = { new CountMapJsonConverter() },
    };

    public static void WriteRunsJsonl(string path, IReadOnlyList<RunMetrics> runs)
    {
        var lines = runs.Select(run => JsonSerializer.Serialize(run, JsonOptions));
        File.WriteAllLines(path, lines);
    }

    /// <summary>
    /// Reads an archived sweep's rows back. The inverse of <see cref="WriteRunsJsonl"/>, and the only
    /// way a comparison can see a baseline that was extracted months ago.
    /// </summary>
    public static IReadOnlyList<RunMetrics> ReadRunsJsonl(string path) =>
        [
            .. File.ReadAllLines(path)
                .Where(static line => !string.IsNullOrWhiteSpace(line))
                .Select(line =>
                    JsonSerializer.Deserialize<RunMetrics>(line, JsonOptions)
                    ?? throw new InvalidOperationException($"A run row parsed to null in {path}: {line}")
                ),
        ];

    public static void WriteSummaryMarkdown(
        string path,
        IReadOnlyList<RunMetrics> runs,
        IReadOnlyList<UnattributedThread> unattributedThreads,
        ComparisonReport? comparison = null
    )
    {
        File.WriteAllText(
            path,
            BuildSummaryMarkdown(runs, unattributedThreads, comparison),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        );
    }

    internal static string BuildSummaryMarkdown(
        IReadOnlyList<RunMetrics> runs,
        IReadOnlyList<UnattributedThread> unattributedThreads,
        ComparisonReport? comparison = null
    )
    {
        var sb = new StringBuilder();
        sb.AppendLine("# todo-eval sweep summary");
        sb.AppendLine();
        sb.AppendLine(
            $"{runs.Count} runs ({runs.Select(r => r.Model).Distinct(StringComparer.Ordinal).Count()} models x "
                + $"{runs.Select(r => r.SeedIndex).Distinct().Count()} seeds)."
        );
        sb.AppendLine();

        // The numbers below mean nothing without the corpus and contract they were taken under, so
        // the fingerprints ride the report itself rather than living only in sweep-manifest.json.
        if (runs.FirstOrDefault(r => r.Fingerprints is not null)?.Fingerprints is { } fingerprints)
        {
            sb.AppendLine(
                $"Extracted under {fingerprints.SpecVersion}: taskCorpusHash `{Short(fingerprints.TaskCorpusHash)}`, "
                    + $"specHash `{Short(fingerprints.SpecHash)}`, evaluatorHash `{Short(fingerprints.EvaluatorHash)}`. "
                    + "Full values and the hashes the sweep RAN under are in sweep-manifest.json."
            );
            sb.AppendLine();
        }

        AppendComparison(sb, comparison);

        sb.AppendLine("## Per model");
        sb.AppendLine();
        sb.AppendLine(
            "| Model | Runs | Valid | Completed | Timed out | Avg turns | Task tool calls "
                + "| Task tool errors | Error rate | Retry storms | Unpaired |"
        );
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (
            var group in runs.GroupBy(r => r.Model, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
        )
        {
            var total = group.Count();
            var valid = group.Count(r => r.Validity.Valid);
            var completed = group.Count(r => r.Completion == true);
            var completedCell = group.Any(r => r.Completion is not null) ? $"{completed}/{total}" : "n/a";
            var timedOut = group.Count(r => r.Status == Sweep.RunOutcomes.TimedOut);
            var avgTurns = group.Average(r => (double)r.Turns);
            var taskCalls = group.Sum(r => r.TaskToolCalls);
            var taskErrors = group.Sum(r => r.TaskToolErrors);
            var storms = group.Sum(r => r.RetryStormCount);
            var unpaired = group.Sum(r => r.UnpairedToolCalls);
            sb.AppendLine(
                $"| {group.Key} | {total} | {valid}/{total} | {completedCell} | {timedOut} "
                    + $"| {avgTurns.ToString("0.0", CultureInfo.InvariantCulture)} | {taskCalls} | {taskErrors} "
                    + $"| {Rate(taskErrors, taskCalls)} | {storms} | {unpaired} |"
            );
        }

        sb.AppendLine();
        sb.AppendLine("## Error rate per tool (per model)");
        sb.AppendLine();
        sb.AppendLine("Zero-call tools are omitted here; runs.jsonl carries every tool's row.");
        sb.AppendLine();
        AppendPerToolTable(sb, runs, TaskTools.All);

        sb.AppendLine();
        sb.AppendLine("## Coordination tools");
        sb.AppendLine();
        var coordinationCalls = runs.Sum(r => r.CoordinationToolCalls);
        if (coordinationCalls == 0)
        {
            sb.AppendLine("No coordination tool was called in any run.");
        }
        else
        {
            sb.AppendLine(
                $"{coordinationCalls} call(s), {runs.Sum(r => r.CoordinationToolErrors)} refusal(s). A refusal is "
                    + "is_error plus an error_code - coordination results are NOT 'Error:'-prefixed."
            );
            sb.AppendLine();
            AppendPerToolTable(sb, runs, CoordinationTools.All);
            AppendCountTable(sb, "Error codes", "Code", CountMap.Merge(runs.Select(r => r.ErrorCodes)));
            AppendCountTable(sb, "Wait outcomes", "Outcome", CountMap.Merge(runs.Select(r => r.WaitOutcomes)));
        }

        sb.AppendLine();
        AppendUsage(sb, runs);
        AppendStartupCost(sb, runs);

        sb.AppendLine();
        sb.AppendLine("## Retry storms");
        sb.AppendLine();
        var allStorms = runs.SelectMany(r => r.RetryStorms.Select(s => (Run: r, Storm: s))).ToList();
        if (allStorms.Count == 0)
        {
            sb.AppendLine("None detected.");
        }
        else
        {
            sb.AppendLine("| Run | Thread | Tool | Consecutive failures | Args |");
            sb.AppendLine("|---|---|---|---:|---|");
            foreach (var (run, storm) in allStorms.OrderByDescending(s => s.Storm.Count))
            {
                sb.AppendLine(
                    $"| {run.RunKey} | {storm.ThreadId} | {storm.Tool} | {storm.Count} | {Truncate(storm.Args)} |"
                );
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Validity");
        sb.AppendLine();
        var invalidRuns = runs.Where(r => !r.Validity.Valid).ToList();
        if (invalidRuns.Count == 0)
        {
            sb.AppendLine("All runs valid: every sub-agent thread made at least one task-tool call.");
        }
        else
        {
            sb.AppendLine("| Run | Sub-agent threads | Without task-tool calls | Fabricated-compliance suspects |");
            sb.AppendLine("|---|---:|---|---|");
            foreach (var run in invalidRuns)
            {
                sb.AppendLine(
                    $"| {run.RunKey} | {run.Validity.SubAgentThreads} "
                        + $"| {string.Join(", ", run.Validity.SubAgentsWithoutTaskToolCalls)} "
                        + $"| {string.Join(", ", run.Validity.FabricatedComplianceSuspects)} |"
                );
            }
        }

        var aggregate = SweepAggregate.Of(runs);
        AppendResidualErrors(sb, aggregate);
        AppendContraryEvidence(sb, aggregate, comparison);

        sb.AppendLine();
        sb.AppendLine("## Unattributed threads");
        sb.AppendLine();
        if (unattributedThreads.Count == 0)
        {
            sb.AppendLine("None: every conversation thread is reachable from a run via its sample.subAgentOf chain.");
        }
        else
        {
            sb.AppendLine(
                $"WARNING: {unattributedThreads.Count} thread(s) are unreachable from every run's sample.subAgentOf "
                    + "chain (likeliest: the link was lost to a hard-timeout kill before the host's debounced "
                    + "metadata write). Their activity is counted here and EXCLUDED from the per-run rows above; "
                    + "treat any affected run's metrics as a lower bound."
            );
            sb.AppendLine();
            sb.AppendLine(
                "| Thread | Sub-agent | Tool calls | Task-tool calls | Task-tool errors "
                    + "| Fabricated-compliance suspect |"
            );
            sb.AppendLine("|---|---|---:|---:|---:|---|");
            foreach (var thread in unattributedThreads)
            {
                sb.AppendLine(
                    $"| {thread.ThreadId} | {(thread.IsSubAgentThread ? "yes" : "no")} | {thread.TotalToolCalls} "
                        + $"| {thread.TaskToolCalls} | {thread.TaskToolErrors} "
                        + $"| {(thread.FabricatedComplianceSuspect ? "yes" : "no")} |"
                );
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// The verdict, printed before the numbers it judges. A refused comparison prints its reason and
    /// NOTHING else: two sweeps that may not be compared publish no delta and no gate table.
    /// </summary>
    private static void AppendComparison(StringBuilder sb, ComparisonReport? comparison)
    {
        if (comparison is null)
        {
            return;
        }

        sb.AppendLine("## Before / after");
        sb.AppendLine();
        sb.AppendLine($"Baseline: `{comparison.BaselineDirectory}`");
        sb.AppendLine($"Candidate: `{comparison.CandidateDirectory}`");
        sb.AppendLine();

        if (!comparison.Compared)
        {
            sb.AppendLine($"**REFUSED ({comparison.Refusal})** — {comparison.Reason}");
            sb.AppendLine();
            sb.AppendLine(
                "No before/after number is published for a refused comparison: the two sweeps are not "
                    + "entitled to share a scale."
            );
            sb.AppendLine();
            return;
        }

        sb.AppendLine(comparison.Reason);
        sb.AppendLine();
        foreach (var drift in comparison.ContractDrift)
        {
            sb.AppendLine($"- {drift}");
        }

        if (comparison.ContractDrift.Count > 0)
        {
            sb.AppendLine();
        }

        sb.AppendLine("| Metric | Baseline | Candidate | Change | Better | Moved |");
        sb.AppendLine("|---|---:|---:|---:|---|---|");
        foreach (var delta in comparison.Deltas ?? [])
        {
            sb.AppendLine(
                $"| {delta.MetricId} | {Num(delta.Baseline)} | {Num(delta.Candidate)} | {Num(delta.Change)} "
                    + $"| {Better(delta.Better)} | {(delta.MovedTheWrongWay ? "the wrong way" : "ok")} |"
            );
        }

        sb.AppendLine();
        sb.AppendLine("### Gates");
        sb.AppendLine();
        sb.AppendLine(
            "A gate nothing could measure is reported as `not measurable` — the criterion it covers is "
                + "UNPROVEN, never passed."
        );
        sb.AppendLine();
        sb.AppendLine("| Gate | Outcome | Actual | Threshold | Baseline | Description |");
        sb.AppendLine("|---|---|---:|---:|---:|---|");
        foreach (var gate in comparison.Gates ?? [])
        {
            sb.AppendLine(
                $"| {gate.GateId} | {Outcome(gate)} | {Num(gate.Actual)} | {Num(gate.Threshold)} "
                    + $"| {Num(gate.Baseline)} | {gate.Description} |"
            );
        }

        sb.AppendLine();
    }

    /// <summary>
    /// Every failure the sweep still carries, listed rather than summarised: a fix that halves an
    /// error rate has still left every row printed here behind.
    /// </summary>
    private static void AppendResidualErrors(StringBuilder sb, SweepAggregate aggregate)
    {
        sb.AppendLine();
        sb.AppendLine("## Residual errors");
        sb.AppendLine();

        var failing = aggregate
            .PerTool.Where(kvp => kvp.Value.Errors > 0)
            .OrderByDescending(kvp => kvp.Value.Errors)
            .ThenBy(kvp => kvp.Key, StringComparer.Ordinal)
            .ToList();
        if (failing.Count == 0 && aggregate.ErrorCodes.Count == 0)
        {
            sb.AppendLine("None: no tool row carries a failure and no error code remains in the sweep.");
            return;
        }

        sb.AppendLine("| Tool | Family | Calls | Errors | Error rate | Codes |");
        sb.AppendLine("|---|---|---:|---:|---:|---|");
        foreach (var (tool, row) in failing)
        {
            var codes = string.Join(
                ", ",
                row.ErrorCodes.OrderBy(kvp => kvp.Key, StringComparer.Ordinal).Select(kvp => $"{kvp.Key}={kvp.Value}")
            );
            sb.AppendLine(
                $"| {tool} | {row.Family} | {row.Calls} | {row.Errors} | {Rate(row.Errors, row.Calls)} | {codes} |"
            );
        }

        AppendCountTable(sb, "Error codes still present", "Code", aggregate.ErrorCodes);
    }

    /// <summary>
    /// Everything that argues against the headline. Printed even when empty, because a section that
    /// disappears when there is nothing to say is indistinguishable from one nobody wrote.
    /// </summary>
    private static void AppendContraryEvidence(StringBuilder sb, SweepAggregate aggregate, ComparisonReport? comparison)
    {
        sb.AppendLine();
        sb.AppendLine("## Contrary evidence");
        sb.AppendLine();

        List<string> lines = [];
        foreach (var delta in (comparison?.Deltas ?? []).Where(d => d.MovedTheWrongWay))
        {
            lines.Add(
                $"`{delta.MetricId}` moved the wrong way: {Num(delta.Baseline)} -> {Num(delta.Candidate)} "
                    + $"({delta.Description})."
            );
        }

        foreach (var gate in comparison?.Gates ?? [])
        {
            if (gate.Outcome == GateOutcome.NotMeasurable)
            {
                lines.Add($"`{gate.GateId}` is **not measurable**, so the criterion is UNPROVEN: {gate.Note}");
            }
            else if (gate.WithinMargin)
            {
                lines.Add(
                    $"`{gate.GateId}` passed only within {DeterministicGates.PassMarginFraction:P0} of its "
                        + $"threshold ({Num(gate.Actual)} against {Num(gate.Threshold)}): it did not get "
                        + "measurably worse, which is not the same as an improvement."
                );
            }
        }

        foreach (var reason in aggregate.ValidityReasons)
        {
            lines.Add($"Validity: {reason}.");
        }

        if (aggregate.FabricatedComplianceSuspects.Count > 0)
        {
            lines.Add(
                "Fabricated-compliance suspects (a triage pointer into the transcript, never a verdict): "
                    + string.Join(", ", aggregate.FabricatedComplianceSuspects)
                    + "."
            );
        }

        if (lines.Count == 0)
        {
            sb.AppendLine(
                "None: no reported metric moved the wrong way, every gate was measurable and cleared its "
                    + "threshold by more than its margin, every run was valid, and no thread was flagged."
            );
            return;
        }

        foreach (var line in lines)
        {
            sb.AppendLine($"- {line}");
        }
    }

    private static string Outcome(GateResult gate) =>
        gate.Outcome switch
        {
            GateOutcome.Passed => gate.WithinMargin ? "pass (within margin)" : "pass",
            GateOutcome.Failed => "**FAIL**",
            _ => "not measurable",
        };

    private static string Better(GateDirection direction) => direction == GateDirection.AtMost ? "lower" : "higher";

    /// <summary>Integral values print as integers so a count does not read as a measurement to 4dp.</summary>
    private static string Num(double? value) =>
        value is not { } number ? "n/a"
        : Math.Abs(number % 1) < 1e-9 ? number.ToString("0", CultureInfo.InvariantCulture)
        : number.ToString("0.####", CultureInfo.InvariantCulture);

    private static void AppendPerToolTable(StringBuilder sb, IReadOnlyList<RunMetrics> runs, IEnumerable<string> tools)
    {
        sb.AppendLine("| Model | Tool | Calls | Errors | Error rate |");
        sb.AppendLine("|---|---|---:|---:|---:|");
        foreach (
            var modelGroup in runs.GroupBy(r => r.Model, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
        )
        {
            foreach (var tool in tools)
            {
                var calls = 0;
                var errors = 0;
                foreach (var run in modelGroup)
                {
                    if (run.PerTool.TryGetValue(tool, out var stats))
                    {
                        calls += stats.Calls;
                        errors += stats.Errors;
                    }
                }

                if (calls > 0)
                {
                    sb.AppendLine($"| {modelGroup.Key} | {tool} | {calls} | {errors} | {Rate(errors, calls)} |");
                }
            }
        }
    }

    private static void AppendUsage(StringBuilder sb, IReadOnlyList<RunMetrics> runs)
    {
        sb.AppendLine("## Usage");
        sb.AppendLine();
        var records = runs.Sum(r => r.Usage.Totals.Records);
        if (records == 0)
        {
            sb.AppendLine(
                "No usage records were persisted with these runs, so no tokens are attributable. This is "
                    + "ABSENT data, not zero consumption."
            );
            return;
        }

        sb.AppendLine(
            "| Model | Execution kind | Records | Input | Output | Cache read | Cache write | Reasoning | Total |"
        );
        sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (
            var modelGroup in runs.GroupBy(r => r.Model, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
        )
        {
            var byKind = new Dictionary<string, UsageTotals>(StringComparer.Ordinal);
            foreach (var (kind, totals) in modelGroup.SelectMany(r => r.Usage.ByExecutionKind))
            {
                byKind[kind] = byKind.TryGetValue(kind, out var seen) ? Combine(seen, totals) : totals;
            }

            foreach (var (kind, totals) in byKind.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
            {
                sb.AppendLine(
                    $"| {modelGroup.Key} | {kind} | {totals.Records} | {totals.InputTokens} | {totals.OutputTokens} "
                        + $"| {totals.CacheReadTokens} | {totals.CacheWriteTokens} | {totals.ReasoningTokens} "
                        + $"| {totals.TotalTokens} |"
                );
            }
        }

        sb.AppendLine();
        sb.AppendLine(
            $"Turn-attributed tokens: {runs.Sum(r => r.Usage.AttributedTurnTokens)}; unattributed: "
                + $"{runs.Sum(r => r.Usage.UnattributedTurnTokens)}. Execution kinds this build never emits: "
                + $"{string.Join(", ", UsageReport.KindsNotEmittedByThisBuild)} - listed rather than reported as "
                + "zero rows, because a zero would read as 'measured, none happened'."
        );
        sb.AppendLine();
        foreach (var note in runs.SelectMany(r => r.Usage.Notes).Distinct(StringComparer.Ordinal))
        {
            sb.AppendLine($"- {note}");
        }
    }

    private static void AppendStartupCost(StringBuilder sb, IReadOnlyList<RunMetrics> runs)
    {
        sb.AppendLine();
        sb.AppendLine("## Startup cost");
        sb.AppendLine();
        sb.AppendLine(
            "Measured, not judged: #670 records what coordination costs so #671-#676 can be read against "
                + "it. Host publish/readiness milliseconds are in sweep-manifest.json under startupWork."
        );
        sb.AppendLine();

        var spawns = runs.SelectMany(r => r.SpawnTimings).ToList();
        if (spawns.Count == 0)
        {
            sb.AppendLine("No per-spawn timings were stamped (no sub-agent spawned, or the host predates the seam).");
        }
        else
        {
            sb.AppendLine(
                "| Spawns | Rebuilt | Avg tool-registry ms | Avg context fan-out ms | Avg total ms | Avg tool-catalog bytes |"
            );
            sb.AppendLine("|---:|---:|---:|---:|---:|---:|");
            sb.AppendLine(
                $"| {spawns.Count} | {spawns.Count(t => t.Reconstructed)} | {Avg(spawns.Select(t => t.ToolRegistryMs))} "
                    + $"| {Avg(spawns.Select(t => t.ContextFanOutMs))} | {Avg(spawns.Select(t => t.TotalMs))} "
                    + $"| {Avg(spawns.Select(t => (long)t.ToolCatalogBytes))} |"
            );
        }

        sb.AppendLine();
        var works = runs.Select(r => r.StartupWork).OfType<StartupWork>().ToList();
        if (works.Count == 0)
        {
            sb.AppendLine(
                "No subagents.startupWork block was stamped, so this sweep's construction and directory work "
                    + "is UNMEASURED here - not zero. The host opts in through SubAgentOptions.Instrumentation."
            );
            return;
        }

        // Summed across runs, so this row shares a denominator with the per-spawn table above it.
        // Reporting the FIRST run's block instead would print one run's counts directly beneath a
        // sweep-wide total under a matching "Spawns" column, and a threshold read off that row would
        // be low by the number of runs - the same misreporting the per-thread roll-up already cost us,
        // one level up.
        sb.AppendLine(
            $"Summed over the {works.Count} of {runs.Count} run(s) that carried a stamp. Each run contributes "
                + "one de-duplicated observation, so these are sweep totals, not per-run figures."
        );
        sb.AppendLine();
        sb.AppendLine(
            "| Spawns | Rebuilt | Tool-registry ms | Context fan-out ms | Catalog builds | Catalog bytes | Listings | entries | bytes |"
        );
        sb.AppendLine("|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        sb.AppendLine(
            $"| {works.Sum(w => w.Spawns)} | {works.Sum(w => w.Reconstructions)} "
                + $"| {works.Sum(w => w.SpawnToolRegistryMs)} | {works.Sum(w => w.SpawnContextFanOutMs)} "
                + $"| {works.Sum(w => w.TemplateCatalogBuilds)} | {works.Sum(w => w.TemplateCatalogBytes)} "
                + $"| {works.Sum(w => w.DirectoryListings)} | {works.Sum(w => w.DirectoryListingEntries)} "
                + $"| {works.Sum(w => w.DirectoryListingBytes)} |"
        );
    }

    private static void AppendCountTable(
        StringBuilder sb,
        string title,
        string keyHeader,
        IReadOnlyDictionary<string, int> counts
    )
    {
        if (counts.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine($"### {title}");
        sb.AppendLine();
        sb.AppendLine($"| {keyHeader} | Count |");
        sb.AppendLine("|---|---:|");
        foreach (
            var (key, count) in counts
                .OrderByDescending(kvp => kvp.Value)
                .ThenBy(kvp => kvp.Key, StringComparer.Ordinal)
        )
        {
            sb.AppendLine($"| {key} | {count} |");
        }
    }

    private static UsageTotals Combine(UsageTotals a, UsageTotals b) =>
        new()
        {
            Records = a.Records + b.Records,
            InputTokens = a.InputTokens + b.InputTokens,
            OutputTokens = a.OutputTokens + b.OutputTokens,
            CacheReadTokens = a.CacheReadTokens + b.CacheReadTokens,
            CacheWriteTokens = a.CacheWriteTokens + b.CacheWriteTokens,
            ReasoningTokens = a.ReasoningTokens + b.ReasoningTokens,
            TotalTokens = a.TotalTokens + b.TotalTokens,
        };

    private static string Avg(IEnumerable<long> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? "n/a" : ((long)list.Average()).ToString(CultureInfo.InvariantCulture);
    }

    private static string Short(string hash) => hash.Length <= 12 ? hash : hash[..12];

    private static string Rate(int errors, int calls) =>
        calls == 0 ? "n/a" : ((double)errors / calls).ToString("P1", CultureInfo.InvariantCulture);

    /// <summary>Storm args can be an entire note payload; keep the table skimmable.</summary>
    private static string Truncate(string value) => value.Length <= 80 ? value : value[..77] + "...";
}
