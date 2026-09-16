using System.Text.Json;

namespace TodoEval.Runner.Sweep;

/// <summary>Terminal outcome labels a sweep run can carry (the host's terminal statuses plus the harness's own).</summary>
internal static class RunOutcomes
{
    public const string Completed = "Completed";
    public const string Errored = "Errored";
    public const string Interrupted = "Interrupted";
    public const string TimedOut = "TimedOut";
    public const string HarnessError = "HarnessError";
}

/// <summary>
/// One sweep run as the RUNNER saw it — model, seed, thread id, terminal status, timing. Appended to
/// <c>runs-manifest.jsonl</c> as each run finishes (so a crashed sweep still leaves a usable partial
/// record) and joined with the conversation store by the extractor. The manifest is the only bridge
/// between "what was asked" and "what the store contains", so it must survive independently of both.
/// </summary>
internal sealed record RunManifestEntry
{
    public required string RunKey { get; init; }
    public required string Model { get; init; }
    public required int SeedIndex { get; init; }
    public required string Topic { get; init; }
    public required string Status { get; init; }

    /// <summary>
    /// The host option-set this run was produced under. Absent on an archive written before the
    /// variant axis existed, which is read as the <c>default</c> variant.
    /// </summary>
    public string? Variant { get; init; }

    /// <summary>The task id, or null in the single-task layout.</summary>
    public string? Task { get; init; }
    public string? ThreadId { get; init; }
    public string? InputId { get; init; }
    public string? RunId { get; init; }
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset EndedUtc { get; init; }
    public long DurationMs { get; init; }
    public string? Error { get; init; }

    /// <summary>
    /// The run's stable identity across the whole sweep. A segment appears only when it distinguishes
    /// something: the default variant and the unnamed task contribute nothing, so a single-variant
    /// single-task sweep keeps the original <c>{model}/seed{n}</c> key and stays diffable against
    /// every archived baseline. Variant names are unique by validation and the task layout is
    /// all-or-nothing, so the key is unique in every combination.
    /// </summary>
    public static string MakeRunKey(VariantConfig variant, string? taskId, string model, int seedIndex)
    {
        var prefix = new List<string>(2);
        if (!string.Equals(variant.Name, VariantConfig.DefaultName, StringComparison.Ordinal))
        {
            prefix.Add(variant.Name);
        }

        if (taskId is not null)
        {
            prefix.Add(taskId);
        }

        return string.Join("/", prefix.Append($"{model}/seed{seedIndex}"));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string ToJsonLine() => JsonSerializer.Serialize(this, JsonOptions);

    public static IReadOnlyList<RunManifestEntry> ReadJsonl(string path) =>
        [
            .. File.ReadAllLines(path)
                .Where(static line => !string.IsNullOrWhiteSpace(line))
                .Select(line =>
                    JsonSerializer.Deserialize<RunManifestEntry>(line, JsonOptions)
                    ?? throw new InvalidOperationException($"Manifest line parsed to null in {path}: {line}")
                ),
        ];
}
