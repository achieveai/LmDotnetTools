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
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void WriteRunsJsonl(string path, IReadOnlyList<RunMetrics> runs)
    {
        var lines = runs.Select(run => JsonSerializer.Serialize(run, JsonOptions));
        File.WriteAllLines(path, lines);
    }

    public static void WriteSummaryMarkdown(
        string path,
        IReadOnlyList<RunMetrics> runs,
        IReadOnlyList<UnattributedThread> unattributedThreads
    )
    {
        File.WriteAllText(
            path,
            BuildSummaryMarkdown(runs, unattributedThreads),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        );
    }

    internal static string BuildSummaryMarkdown(
        IReadOnlyList<RunMetrics> runs,
        IReadOnlyList<UnattributedThread> unattributedThreads
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
                "| Spawns | Avg queued ms | Avg tool-registry ms | Avg context fan-out ms | Avg total ms | Avg tool-catalog bytes |"
            );
            sb.AppendLine("|---:|---:|---:|---:|---:|---:|");
            sb.AppendLine(
                $"| {spawns.Count} | {Avg(spawns.Select(t => t.QueuedMs))} | {Avg(spawns.Select(t => t.ToolRegistryMs))} "
                    + $"| {Avg(spawns.Select(t => t.ContextFanOutMs))} | {Avg(spawns.Select(t => t.TotalMs))} "
                    + $"| {Avg(spawns.Select(t => (long)t.ToolCatalogBytes))} |"
            );
        }

        sb.AppendLine();
        var work = runs.Select(r => r.StartupWork).FirstOrDefault(w => w is not null);
        if (work is null)
        {
            sb.AppendLine(
                "The host stamped no sample.startupWork block, so its registry and directory work is unmeasured here."
            );
            return;
        }

        sb.AppendLine(
            "| Registry builds | Descriptor cache hits | misses | Restart rebuilds | GetAgents calls | entries | bytes |"
        );
        sb.AppendLine("|---:|---:|---:|---:|---:|---:|---:|");
        sb.AppendLine(
            $"| {work.FunctionRegistryBuilds} | {work.DescriptorCacheHits} | {work.DescriptorCacheMisses} "
                + $"| {work.RestartRebuilds} | {work.GetAgentsCalls} | {work.GetAgentsEntries} | {work.GetAgentsBytes} |"
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
