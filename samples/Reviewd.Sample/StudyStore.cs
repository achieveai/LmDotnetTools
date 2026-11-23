using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Reviewd.Sample;

/// <summary>
///     The reviewer-evolution study store: <c>.experimentation/reviewer-evolution/</c> in a review-store clone, in the
///     format of the <c>experimentation:research</c> skill's <c>research-store.mjs</c> (<c>study.json</c>, one
///     <c>records/&lt;id&gt;.json</c> per record, <c>.write-lock</c> while one writer adds). It is the only state between
///     rounds, so an arm is built or reviewed only under an experiment record it holds, and <c>compare</c> files its
///     result there as an evidence record that is never overwritten.
/// </summary>
internal static partial class StudyStore
{
    internal const string RelativeRoot = ".experimentation/reviewer-evolution";

    /// <summary>research-store.mjs's record id: a lowercase kebab identifier of at most 100 characters.</summary>
    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex RecordIdPattern();

    internal static bool IsRecordId(string id) => id.Length <= 100 && RecordIdPattern().IsMatch(id);

    internal static string Root(string store) => Path.Combine(store, ".experimentation", "reviewer-evolution");

    /// <summary>
    ///     Refuses <paramref name="experiment" /> unless the study in <paramref name="store" /> holds it as an
    ///     experiment record that no later record supersedes.
    /// </summary>
    internal static void RequireExperiment(string store, string experiment)
    {
        var root = Root(store);
        if (!File.Exists(Path.Combine(root, "study.json")))
        {
            throw new ArgumentException(
                $"--store {store} holds no study at {RelativeRoot}/ (no study.json). Create it with research-store.mjs "
                    + "init, then add the experiment record."
            );
        }
        if (!IsRecordId(experiment))
        {
            throw new ArgumentException(
                $"--experiment {experiment} is not a study record id (lowercase letters, digits and single dashes)."
            );
        }
        var records = Records(root);
        if (!records.TryGetValue(experiment, out var record))
        {
            throw new ArgumentException(
                $"The study at {Path.Combine(store, RelativeRoot)} has no record {experiment}. Add the experiment record "
                    + "(research-store.mjs add) before building or reviewing an arm for it."
            );
        }
        if (record["kind"]?.GetValue<string>() != "experiment")
        {
            throw new ArgumentException($"Study record {experiment} is a {record["kind"]} record, not an experiment.");
        }
        if (records.Values.FirstOrDefault(r => r["supersedes"]?.GetValue<string>() == experiment) is { } newer)
        {
            throw new ArgumentException(
                $"Study record {experiment} was superseded by {newer["id"]}; use the current experiment record."
            );
        }
    }

    /// <summary>
    ///     Files <paramref name="artifact" /> under <c>artifacts/</c> and an evidence record that cites its path and
    ///     SHA-256, linked <c>derived-from</c> the experiment. Both are named by that hash, so the same result filed
    ///     twice is one record ("unchanged"), and a new result is a new record beside the old one, never over it.
    /// </summary>
    internal static (string Outcome, string RecordId, string ArtifactPath) AddEvidence(
        string store,
        string experiment,
        string title,
        string artifact,
        Func<string, string, JsonObject> data
    )
    {
        RequireExperiment(store, experiment);
        var root = Root(store);
        var bytes = Encoding.UTF8.GetBytes(artifact);
        var sha = VariantManifest.Sha256Hex(bytes);
        var id = $"{experiment[..Math.Min(experiment.Length, 70)].TrimEnd('-')}-compare-{sha[..16]}";
        var relativeArtifact = $"artifacts/{id}.json";
        using var _ = Lock(root);
        var recordPath = Path.Combine(root, "records", id + ".json");
        if (File.Exists(recordPath))
        {
            return ("unchanged", id, relativeArtifact);
        }
        Directory.CreateDirectory(Path.Combine(root, "artifacts"));
        var artifactPath = Path.Combine(root, "artifacts", id + ".json");
        if (!File.Exists(artifactPath))
        {
            WriteNew(artifactPath, bytes);
        }
        else if (VariantManifest.Sha256Hex(File.ReadAllBytes(artifactPath)) != sha)
        {
            throw new InvalidOperationException($"{artifactPath} exists with other content; nothing was filed.");
        }
        var record = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["id"] = id,
            ["kind"] = "evidence",
            ["title"] = title,
            ["summary"] = $"Paired compare of experiment {experiment}; results in {relativeArtifact} (sha256 {sha}).",
            ["createdAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["links"] = new JsonArray(new JsonObject { ["rel"] = "derived-from", ["target"] = experiment }),
            ["data"] = data(relativeArtifact, sha),
        };
        WriteNew(recordPath, Encoding.UTF8.GetBytes(record.ToJsonString(Indented) + "\n"));
        return ("created", id, relativeArtifact);
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static Dictionary<string, JsonObject> Records(string root)
    {
        var directory = Path.Combine(root, "records");
        var records = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (!Directory.Exists(directory))
        {
            return records;
        }
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            if (
                JsonNode.Parse(File.ReadAllText(file)) is JsonObject record
                && record["id"]?.GetValue<string>() is { } id
            )
            {
                records[id] = record;
            }
        }
        return records;
    }

    // research-store.mjs's writer lock: one writer adds at a time, and a lock left behind is the operator's to clear.
    private static FileStream Lock(string root)
    {
        var path = Path.Combine(root, ".write-lock");
        try
        {
            var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.DeleteOnClose
            );
            stream.Write(Encoding.UTF8.GetBytes($$"""{"pid":{{Environment.ProcessId}},"writer":"reviewd compare"}"""));
            stream.Flush();
            return stream;
        }
        catch (IOException) when (File.Exists(path))
        {
            throw new InvalidOperationException(
                $"The study is locked ({path}). Check whether its writer is still running; remove an abandoned lock only "
                    + "after confirming it is inactive."
            );
        }
    }

    // Written whole to a temporary name, then moved into a name that must not exist yet.
    private static void WriteNew(string path, byte[] bytes)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
