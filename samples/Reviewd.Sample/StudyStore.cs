using System.Globalization;
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
    [GeneratedRegex(@"\A[a-z0-9]+(?:-[a-z0-9]+)*\z")]
    private static partial Regex RecordIdPattern();

    internal static bool IsRecordId(string id) => id.Length <= 100 && RecordIdPattern().IsMatch(id);

    internal static string Root(string store) => Path.Combine(store, ".experimentation", "reviewer-evolution");

    /// <summary>The indented JSON the study's records and compare's result are written in.</summary>
    internal static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    ///     Whether <paramref name="path" /> is the study's own folder or anywhere under it, as written or once every
    ///     link along either path is followed (a directory symlink or junction to <c>records/</c> lands there too).
    /// </summary>
    internal static bool IsInside(string store, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool Under(string root, string full) =>
            full.Equals(root, comparison) || full.StartsWith(root + Path.DirectorySeparatorChar, comparison);
        return Under(Normal(Root(store)), Normal(path)) || Under(Resolve(Root(store)), Resolve(path));
    }

    private static string Normal(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    // The path with each existing link along it replaced by its final target, so a check sees where a write lands.
    // One hop at a time, and each target is resolved the same way, so a target that itself passes through a link
    // (out -> bridge/records, bridge -> the study) lands where the write would on every platform. (Windows' final-target
    // lookup already follows the whole path; Unix's follows only the last name.)
    private static string Resolve(string path, int depth = 0)
    {
        if (depth > 32)
        {
            throw new IOException($"{path} passes through more than 32 links.");
        }
        var full = Normal(path);
        var current = Path.GetPathRoot(full)!;
        foreach (
            var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
        )
        {
            current = Path.Combine(current, part);
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (entry.LinkTarget is not null && entry.ResolveLinkTarget(returnFinalTarget: false) is { } target)
            {
                current = Resolve(target.FullName, depth + 1);
            }
        }
        return current;
    }

    /// <summary>
    ///     Writes <paramref name="path" /> whole under a temporary name, then moves it over whatever stands there. A
    ///     move replaces the name itself, so a file there that is a link (or a hard link) to a study record is
    ///     replaced, never written through.
    /// </summary>
    internal static void Replace(string path, string text)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, text);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

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
    ///     twice is one record ("unchanged", once the record and its artifact are checked to be that result's), and a
    ///     new result is a new record beside the old one, never over it. <paramref name="whileLocked" /> runs under the
    ///     same study lock, after the filing, so two writers never interleave their outputs.
    /// </summary>
    internal static (string Outcome, string RecordId, string ArtifactPath) AddEvidence(
        string store,
        string experiment,
        string title,
        string artifact,
        Func<string, string, JsonObject> data,
        Action whileLocked
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
        var artifactPath = Path.Combine(root, "artifacts", id + ".json");
        if (File.Exists(recordPath))
        {
            RequireFiled(recordPath, artifactPath, experiment, relativeArtifact, sha);
            whileLocked();
            return ("unchanged", id, relativeArtifact);
        }
        Directory.CreateDirectory(Path.Combine(root, "artifacts"));
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
            ["createdAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            ["links"] = new JsonArray(new JsonObject { ["rel"] = "derived-from", ["target"] = experiment }),
            ["data"] = data(relativeArtifact, sha),
        };
        WriteNew(recordPath, Encoding.UTF8.GetBytes(record.ToJsonString(Indented) + "\n"));
        whileLocked();
        return ("created", id, relativeArtifact);
    }

    // The record already at this result's name must be its evidence record, and the artifact it cites must still hold
    // the result, before the result counts as filed.
    private static void RequireFiled(
        string recordPath,
        string artifactPath,
        string experiment,
        string relativeArtifact,
        string sha
    )
    {
        var record = Parse(recordPath);
        var cites =
            record["kind"]?.GetValue<string>() == "evidence"
            && record["links"] is JsonArray links
            && links.Any(link =>
                link?["rel"]?.GetValue<string>() == "derived-from" && link["target"]?.GetValue<string>() == experiment
            )
            && record["summary"]?.GetValue<string>() is { } summary
            && summary.Contains(relativeArtifact, StringComparison.Ordinal)
            && summary.Contains(sha, StringComparison.Ordinal);
        if (!cites)
        {
            throw new InvalidOperationException(
                $"{recordPath} exists but is not this result's evidence record (kind evidence, derived-from "
                    + $"{experiment}, citing {relativeArtifact} sha256 {sha}); nothing was filed."
            );
        }
        if (!File.Exists(artifactPath) || VariantManifest.Sha256Hex(File.ReadAllBytes(artifactPath)) != sha)
        {
            throw new InvalidOperationException(
                $"{recordPath} cites {relativeArtifact} sha256 {sha}, but that artifact is "
                    + $"{(File.Exists(artifactPath) ? "changed" : "missing")}; nothing was filed."
            );
        }
    }

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
            var record = Parse(file);
            if (record["id"]?.GetValue<string>() is { } id)
            {
                records[id] = record;
            }
        }
        return records;
    }

    // A record file that does not parse stops every gate, so the error names it.
    private static JsonObject Parse(string file)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(file)) as JsonObject
                ?? throw new InvalidOperationException($"Study record file {file} is not a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"Study record file {file} is not valid JSON: {exception.Message}",
                exception
            );
        }
    }

    // research-store.mjs's writer lock: one writer adds at a time, and a lock left behind is the operator's to clear.
    private static FileStream Lock(string root)
    {
        var path = Path.Combine(root, ".write-lock");
        FileStream stream;
        try
        {
            stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.DeleteOnClose
            );
        }
        catch (IOException) when (File.Exists(path))
        {
            throw new InvalidOperationException(
                $"The study is locked ({path}). Check whether its writer is still running; remove an abandoned lock only "
                    + "after confirming it is inactive."
            );
        }
        try
        {
            stream.Write(Encoding.UTF8.GetBytes($$"""{"pid":{{Environment.ProcessId}},"writer":"reviewd compare"}"""));
            stream.Flush();
            return stream;
        }
        catch
        {
            // A lock whose metadata could not be written is released at once (DeleteOnClose), not left to block the
            // next writer.
            stream.Dispose();
            throw;
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
