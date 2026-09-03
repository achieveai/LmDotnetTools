using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TodoEval.Runner.Metrics;

/// <summary>
/// The three fingerprints that make a sweep's numbers comparable to another sweep's
/// (metrics-spec.md, "Fingerprints"):
/// <list type="bullet">
/// <item><c>taskCorpusHash</c> — WHAT the model was asked to do (task.md + mode.json +
/// expected-board.json). Frozen at run time; a later comparison that finds a different corpus hash
/// is comparing two different tasks.</item>
/// <item><c>specHash</c> — the measurement contract's identity (spec version + score schema).</item>
/// <item><c>evaluatorHash</c> — every constant that can change a measured NUMBER: the spec hash,
/// both tool vocabularies, the storm threshold, and the redaction marker the reader keys on.</item>
/// </list>
/// </summary>
/// <remarks>
/// The evaluator hash deliberately does NOT include the Runner's assembly version. Rebuilding the
/// harness must not invalidate an archived baseline; only a change to a measurement-defining
/// constant may. That is also what lets <c>score.ps1</c> compute the identical values — the recipe
/// is a handful of constants and three files, nothing about the binary.
/// </remarks>
internal static class Fingerprints
{
    /// <summary>The score object's schema string, emitted by both implementations.</summary>
    public const string Schema = "todo-eval/score@2";

    /// <summary>The metrics-spec revision the schema belongs to.</summary>
    public const string SpecVersion = "todo-eval/metrics-spec@3";

    /// <summary>The key a redacted <c>function_args</c> carries in place of the arguments.</summary>
    public const string RedactedArgsKey = "__argsSha256";

    /// <summary>The corpus files, in the fixed order they are hashed in.</summary>
    public static readonly IReadOnlyList<string> CorpusFileNames = ["task.md", "mode.json", "expected-board.json"];

    /// <summary>
    /// SHA-256 over the eval corpus. Each file contributes
    /// <c>&lt;name&gt;\n&lt;byteCount&gt;\n&lt;bytes&gt;\n</c> — the name and the length are what stop
    /// two files' contents from sliding into each other and producing the same digest. CR bytes are
    /// stripped first so a CRLF checkout of these text files hashes the same as an LF one.
    /// A missing file contributes a byte count of <c>-1</c> and no bytes, which is distinguishable
    /// from a genuinely empty file.
    /// </summary>
    public static string CorpusHash(string evalDir)
    {
        var buffer = new MemoryStream();
        foreach (var name in CorpusFileNames)
        {
            var path = Path.Combine(evalDir, name);
            var bytes = File.Exists(path) ? StripCarriageReturns(File.ReadAllBytes(path)) : null;
            AppendLine(buffer, name);
            AppendLine(buffer, (bytes?.Length ?? -1).ToString(CultureInfo.InvariantCulture));
            if (bytes is not null)
            {
                buffer.Write(bytes);
            }

            buffer.WriteByte((byte)'\n');
        }

        return Hex(SHA256.HashData(buffer.ToArray()));
    }

    /// <summary>SHA-256 over the measurement contract's identity: spec version then schema.</summary>
    public static string SpecHash() => HashLines([SpecVersion, Schema]);

    /// <summary>
    /// SHA-256 over every constant that can move a measured number: the spec hash, both tool
    /// vocabularies (ordinal-sorted so declaration order is irrelevant), the storm threshold, and
    /// the redaction marker key.
    /// </summary>
    public static string EvaluatorHash() =>
        HashLines([
            SpecHash(),
            string.Join(",", TaskTools.All.Order(StringComparer.Ordinal)),
            string.Join(",", CoordinationTools.All.Order(StringComparer.Ordinal)),
            RetryStormDetector.StormThreshold.ToString(CultureInfo.InvariantCulture),
            RedactedArgsKey,
        ]);

    /// <summary>The repo commit the sweep ran at, or <c>"unknown"</c> when git cannot answer.</summary>
    public static string GitSha(string repoRoot)
    {
        try
        {
            using var git = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "rev-parse HEAD",
                    WorkingDirectory = repoRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                }
            );
            if (git is null)
            {
                return "unknown";
            }

            var sha = git.StandardOutput.ReadToEnd().Trim();
            git.WaitForExit(milliseconds: 10_000);
            return git.ExitCode == 0 && sha.Length > 0 ? sha : "unknown";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No git on PATH, or not a repository: the sweep is still measurable, it just cannot
            // name its own commit. Never fail an archive over provenance metadata.
            return "unknown";
        }
    }

    private static string HashLines(IReadOnlyList<string> lines) =>
        Hex(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))));

    private static void AppendLine(Stream buffer, string value)
    {
        buffer.Write(Encoding.UTF8.GetBytes(value));
        buffer.WriteByte((byte)'\n');
    }

    private static byte[] StripCarriageReturns(byte[] bytes) => [.. bytes.Where(b => b != (byte)'\r')];

    private static string Hex(byte[] hash) => Convert.ToHexStringLower(hash);
}

/// <summary>The fingerprint triple as it appears in the score object and the sweep manifest.</summary>
internal sealed record FingerprintSet
{
    public required string TaskCorpusHash { get; init; }
    public required string SpecHash { get; init; }
    public required string EvaluatorHash { get; init; }
    public string SpecVersion { get; init; } = Fingerprints.SpecVersion;

    public static FingerprintSet Compute(string evalDir) =>
        new()
        {
            TaskCorpusHash = Fingerprints.CorpusHash(evalDir),
            SpecHash = Fingerprints.SpecHash(),
            EvaluatorHash = Fingerprints.EvaluatorHash(),
        };
}
