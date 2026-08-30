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
        sb.AppendLine("| Model | Tool | Calls | Errors | Error rate |");
        sb.AppendLine("|---|---|---:|---:|---:|");
        foreach (
            var modelGroup in runs.GroupBy(r => r.Model, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
        )
        {
            foreach (var tool in TaskTools.All)
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

    private static string Rate(int errors, int calls) =>
        calls == 0 ? "n/a" : ((double)errors / calls).ToString("P1", CultureInfo.InvariantCulture);

    /// <summary>Storm args can be an entire note payload; keep the table skimmable.</summary>
    private static string Truncate(string value) => value.Length <= 80 ? value : value[..77] + "...";
}
