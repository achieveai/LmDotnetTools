using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LmMultiTurn.Tests.Compaction.Corpus;

/// <summary>One run the corpus started and how it ended.</summary>
public sealed record RunOutcome(string Input, string RunId, bool IsError, string? Error, bool ExpectedError);

/// <summary>The four zero-tolerance invariants the corpus asserts in every mode (issue #686 AC 4).</summary>
public sealed record InvariantReport
{
    /// <summary>Tool calls without their result, or results without their call, in any provider request or in the persisted rows.</summary>
    public int InvalidToolPairs { get; init; }

    /// <summary>Every protected-state class (spec §2.6) a checkpoint dropped, altered or paraphrased.</summary>
    public IReadOnlyList<string> ProtectedStateLoss { get; init; } = [];

    /// <summary>Message-row reads or writes through a handle that targeted another thread (D5).</summary>
    public int CrossThreadReads { get; init; }

    /// <summary>Gaps in the persisted sequence, or rows a checkpoint covered that are no longer there.</summary>
    public IReadOnlyList<string> RawHistoryLoss { get; init; } = [];

    public bool AllZero =>
        InvalidToolPairs == 0 && ProtectedStateLoss.Count == 0 && CrossThreadReads == 0 && RawHistoryLoss.Count == 0;
}

/// <summary>What each protected-state class looked like on the checkpoints the run activated.</summary>
public sealed record ProtectedStateRetention
{
    public int CheckpointsChecked { get; init; }

    public bool CurrentInstructionVerbatim { get; init; } = true;

    public bool TasksOnBoard { get; init; } = true;

    public bool AgentsMatchRoster { get; init; } = true;

    public bool RecoveryZero { get; init; } = true;

    public bool IndexContiguous { get; init; } = true;

    public bool ArtifactsCarried { get; init; } = true;

    /// <summary>Metadata property keys (ledgers, usage, board, observations) present when the run ended.</summary>
    public IReadOnlyList<string> MetadataKeys { get; init; } = [];

    public int RunLedgerEntries { get; init; }

    public int UsageRecords { get; init; }
}

/// <summary>Everything the report says about one scenario in one mode.</summary>
public sealed record ScenarioModeResult
{
    public required string ScenarioId { get; init; }

    public required string Item { get; init; }

    public required string Mode { get; init; }

    public required string Fingerprint { get; init; }

    public bool TaskSuccess { get; init; }

    public bool ExpectedSuccess { get; init; }

    public IReadOnlyList<RunOutcome> Runs { get; init; } = [];

    public int ProviderCalls { get; init; }

    public int ChildProviderCalls { get; init; }

    public long PeakRequestTokens { get; init; }

    public double MeanRequestTokens { get; init; }

    public long TotalPromptTokens { get; init; }

    public long TotalCachedTokens { get; init; }

    public double CacheHitRatio { get; init; }

    public int CheckpointsActivated { get; init; }

    public int ChildCheckpointsActivated { get; init; }

    public int ShadowCheckpoints { get; init; }

    public IReadOnlyDictionary<string, int> Decisions { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, int> Reasons { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    public long? CostMicros { get; init; }

    public long CompactionCostMicros { get; init; }

    public required string CostCompleteness { get; init; }

    /// <summary>Wall-clock for the whole scenario; reported, never asserted (D3).</summary>
    public long LatencyMs { get; init; }

    public long SummaryLatencyMs { get; init; }

    public required InvariantReport Invariants { get; init; }

    public required ProtectedStateRetention Retention { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>The machine-readable report the committed markdown is generated from (AC 3).</summary>
public sealed record CorpusReport
{
    public required string EvaluatorVersion { get; init; }

    public required DateTimeOffset GeneratedAtUtc { get; init; }

    public required IReadOnlyDictionary<string, string> Fingerprints { get; init; }

    public required IReadOnlyList<ScenarioModeResult> Results { get; init; }

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        _ = sb.AppendLine("# Compaction corpus results");
        _ = sb.AppendLine();
        _ = sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"Evaluator `{EvaluatorVersion}`, generated {GeneratedAtUtc:yyyy-MM-dd HH:mm:ss}Z by `CompactionCorpusTests` (mock providers only; window 2400 tokens unless the scenario title says otherwise, reserve 0)."
        );
        _ = sb.AppendLine();
        _ = sb.AppendLine("Latency is wall-clock on the machine that generated the report: reported, never asserted.");
        _ = sb.AppendLine();

        foreach (
            var group in Results
                .GroupBy(r => r.ScenarioId, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
        )
        {
            var first = group.First();
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"## ({group.Key}) {first.Item}");
            _ = sb.AppendLine();
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Fingerprint `{first.Fingerprint[..16]}`.");
            _ = sb.AppendLine();
            _ = sb.AppendLine(
                "| Mode | Task | Calls (root+children) | Peak req tokens | Total prompt tokens | Cache hit | Checkpoints (root/child/shadow) | Cost µ$ (compaction) | Completeness | Latency ms (summary) | Pairs / loss / x-thread / raw |"
            );
            _ = sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var r in group.OrderBy(r => ModeOrder(r.Mode)))
            {
                var task = r.TaskSuccess ? "ok" : "FAILED";
                if (r.TaskSuccess != r.ExpectedSuccess)
                {
                    task += " (unexpected)";
                }
                else if (!r.ExpectedSuccess)
                {
                    task += " (expected)";
                }

                _ = sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"| {r.Mode} | {task} | {r.ProviderCalls}+{r.ChildProviderCalls} | {r.PeakRequestTokens} | {r.TotalPromptTokens} | {r.CacheHitRatio:P0} | {r.CheckpointsActivated}/{r.ChildCheckpointsActivated}/{r.ShadowCheckpoints} | {r.CostMicros?.ToString(CultureInfo.InvariantCulture) ?? "n/a"} ({r.CompactionCostMicros}) | {r.CostCompleteness} | {r.LatencyMs} ({r.SummaryLatencyMs}) | {r.Invariants.InvalidToolPairs} / {r.Invariants.ProtectedStateLoss.Count} / {r.Invariants.CrossThreadReads} / {r.Invariants.RawHistoryLoss.Count} |"
                );
            }

            _ = sb.AppendLine();
            foreach (var r in group.OrderBy(r => ModeOrder(r.Mode)))
            {
                var decisions = string.Join(
                    ", ",
                    r.Decisions.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => $"{d.Key}×{d.Value}")
                );
                var reasons = string.Join(
                    ", ",
                    r.Reasons.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => $"{d.Key}×{d.Value}")
                );
                _ = sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"- {r.Mode}: decisions {(decisions.Length == 0 ? "none" : decisions)}; reasons {(reasons.Length == 0 ? "none" : reasons)}; retention: instruction verbatim {r.Retention.CurrentInstructionVerbatim}, tasks on board {r.Retention.TasksOnBoard}, agents match roster {r.Retention.AgentsMatchRoster}, recovery zero {r.Retention.RecoveryZero}, index contiguous {r.Retention.IndexContiguous} ({r.Retention.CheckpointsChecked} checkpoints); metadata keys {r.Retention.MetadataKeys.Count}, run-ledger entries {r.Retention.RunLedgerEntries}, usage records {r.Retention.UsageRecords}."
                );
                foreach (var loss in r.Invariants.ProtectedStateLoss.Concat(r.Invariants.RawHistoryLoss))
                {
                    _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  - LOSS: {loss}");
                }

                foreach (var note in r.Notes)
                {
                    _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  - {note}");
                }
            }

            _ = sb.AppendLine();
        }

        return sb.ToString();
    }

    private static int ModeOrder(string mode) =>
        mode switch
        {
            "Off" => 0,
            "Shadow" => 1,
            "Compact" => 2,
            _ => 3,
        };
}

/// <summary>Where the corpus reads its manifest from and writes its reports to.</summary>
internal static class CorpusPaths
{
    public static string CorpusDirectory { get; } = Path.GetDirectoryName(ThisFile())!;

    public static string RepositoryRoot { get; } =
        Path.GetFullPath(Path.Combine(CorpusDirectory, "..", "..", "..", ".."));

    public static string FingerprintsFile => Path.Combine(CorpusDirectory, "corpus.fingerprints.json");

    /// <summary>D3: <c>.logs/compaction-corpus</c> by default; <c>LMMULTITURN_CORPUS_REPORT_DIR</c> overrides.</summary>
    public static string ReportDirectory =>
        Environment.GetEnvironmentVariable("LMMULTITURN_CORPUS_REPORT_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(RepositoryRoot, ".logs", "compaction-corpus");

    private static string ThisFile([CallerFilePath] string path = "") => path;
}
