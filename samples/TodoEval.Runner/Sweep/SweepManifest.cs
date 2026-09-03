using System.Text;
using System.Text.Json;
using TodoEval.Runner.Metrics;

namespace TodoEval.Runner.Sweep;

/// <summary>What the host's own startup cost the sweep, in wall-clock milliseconds.</summary>
internal sealed record HostStartupWork
{
    /// <summary>Publishing (or copying pre-published binaries into) the isolated instance directory.</summary>
    public long HostPublishMs { get; init; }

    /// <summary>Launching the process and waiting until the API answered.</summary>
    public long HostReadyMs { get; init; }
}

/// <summary>
/// <c>sweep-manifest.json</c>: the provenance a sweep's numbers are only comparable WITH. It sits
/// next to <c>runs-manifest.jsonl</c> and is what #677 reads before it agrees to compare two sweeps.
/// </summary>
/// <remarks>
/// The fingerprints appear twice on purpose. <see cref="RanUnder"/> is frozen when the sweep runs
/// and never rewritten, so it always names the corpus and contract the MODEL actually faced.
/// <see cref="ExtractedUnder"/> is recomputed on every <c>--extract-only</c>, so a later
/// re-extraction that silently reads a changed corpus or a changed measurement constant is visible
/// as a difference between the two rather than invisible in a single overwritten field.
/// </remarks>
internal sealed record SweepManifest
{
    public const string FileName = "sweep-manifest.json";
    public const string SchemaId = "todo-eval/sweep-manifest@1";

    public string Schema { get; init; } = SchemaId;
    public required string GitSha { get; init; }
    public required string RunnerVersion { get; init; }

    /// <summary>Frozen at sweep time: the corpus and contract the runs were produced under.</summary>
    public required FingerprintSet RanUnder { get; init; }

    /// <summary>Recomputed on every extraction, including <c>--extract-only</c> years later.</summary>
    public required FingerprintSet ExtractedUnder { get; init; }

    public IReadOnlyList<string> Models { get; init; } = [];
    public int Seeds { get; init; }
    public int PerRunTimeoutMinutes { get; init; }
    public HostStartupWork StartupWork { get; init; } = new();
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset FinishedUtc { get; init; }

    /// <summary>
    /// False only when the sweep was archived with <c>--archive-raw</c>. A raw archive carries model
    /// prose and must stay off-repo; the flag is recorded so a reviewer can tell which one they hold.
    /// </summary>
    public bool ConversationsRedacted { get; init; } = true;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public void Write(string sweepDir) =>
        File.WriteAllText(
            Path.Combine(sweepDir, FileName),
            JsonSerializer.Serialize(this, Options),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        );

    /// <summary>Reads the manifest of an archived sweep, or null when it predates this file.</summary>
    public static SweepManifest? Read(string sweepDir)
    {
        var path = Path.Combine(sweepDir, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SweepManifest>(File.ReadAllText(path), Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
