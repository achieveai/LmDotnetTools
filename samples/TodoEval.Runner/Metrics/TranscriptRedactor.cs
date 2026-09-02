using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TodoEval.Runner.Metrics;

/// <summary>
/// Rewrites an archived conversation store into the form that gets COMMITTED: every free-text field
/// the model or the operator authored is replaced by the measurements taken from it, and nothing
/// else changes.
/// </summary>
/// <remarks>
/// <para>
/// The design constraint is metric preservation, not minimal disclosure. A redaction that dropped
/// <c>function_args</c> outright would erase call identity and with it every retry storm — the
/// single most important number in the archive — so arguments become the SHA-256 of their
/// canonical bytes instead: two calls that were identical still hash identically, and two that
/// differed still differ, while the argument text itself is gone. The same reasoning keeps tool
/// RESULT text verbatim: it is deterministic server output from a fixed task corpus, not user
/// content, and the board-id-vanished ledger is built entirely out of it.
/// </para>
/// <para>
/// What is actually redacted is what a model wrote in prose: assistant and reasoning text, and the
/// operator-authored sub-agent task/name in <c>metadata.json</c>. Assistant text is replaced by the
/// three facts the spec computes from it — its length and the two fabricated-compliance regex
/// verdicts — so that check still runs against a redacted archive.
/// </para>
/// </remarks>
internal static class TranscriptRedactor
{
    /// <summary>Inner-message string fields that hold model prose.</summary>
    private static readonly string[] ProseFields = ["text", "reasoning", "thinking"];

    /// <summary>Operator- or model-authored metadata properties.</summary>
    private static readonly string[] ProseProperties = ["sample.subAgentTask", "sample.subAgentName"];

    // The spec's fabricated-compliance heuristic, mirrored so the redacted form can carry its verdict.
    private static readonly Regex ClaimVerb = new("(?i)(claim|complet|marked)", RegexOptions.CultureInvariant);
    private static readonly Regex ClaimNoun = new("(?i)(task|todo|board)", RegexOptions.CultureInvariant);

    /// <summary>
    /// Copies <paramref name="sourceDir"/> to <paramref name="destinationDir"/>, redacting every
    /// <c>messages.json</c> and <c>metadata.json</c> on the way. Any other file is copied verbatim.
    /// </summary>
    public static void CopyRedacted(string sourceDir, string destinationDir)
    {
        if (!Directory.Exists(sourceDir))
        {
            return;
        }

        Directory.CreateDirectory(destinationDir);
        foreach (var directory in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destinationDir, Path.GetRelativePath(sourceDir, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destinationDir, Path.GetRelativePath(sourceDir, file));
            var name = Path.GetFileName(file);
            var redacted = name switch
            {
                "messages.json" => TryRedact(file, RedactMessages),
                "metadata.json" => TryRedact(file, RedactMetadata),
                _ => null,
            };

            if (redacted is null)
            {
                File.Copy(file, target, overwrite: true);
            }
            else
            {
                File.WriteAllText(target, redacted, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
    }

    /// <summary>Redacts one <c>messages.json</c> document. Public for the round-trip tests.</summary>
    public static string RedactMessages(string json)
    {
        var envelopes = JsonNode.Parse(json)?.AsArray();
        if (envelopes is null)
        {
            return json;
        }

        foreach (var envelope in envelopes.OfType<JsonObject>())
        {
            if (envelope["messageJson"]?.GetValue<string>() is not { } inner)
            {
                continue;
            }

            var messageType = envelope["messageType"]?.GetValue<string>();
            if (JsonNode.Parse(inner) is not JsonObject message)
            {
                continue;
            }

            // A tool RESULT is deterministic server output over a fixed corpus: it stays verbatim,
            // because the board-id-vanished ledger is derived from nothing else.
            if (messageType == "ToolCallResultMessage")
            {
                continue;
            }

            if (messageType == "ToolCallMessage")
            {
                RedactArgs(message);
            }
            else
            {
                RedactProse(message);
            }

            envelope["messageJson"] = message.ToJsonString();
        }

        return envelopes.ToJsonString(WriteOptions);
    }

    /// <summary>Redacts one <c>metadata.json</c> document. Public for the round-trip tests.</summary>
    public static string RedactMetadata(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject root || root["properties"] is not JsonObject properties)
        {
            return json;
        }

        foreach (var key in ProseProperties)
        {
            // Same guard and same OBJECT form as RedactProse, and for two reasons beyond symmetry:
            // metrics-spec.md promises one form for both, and `GetValue<string>()` THROWS once the value
            // is already the signals object - so redacting an archive twice used to be a crash rather
            // than a no-op. Matching on JsonValue makes the second pass skip what the first replaced.
            if (properties[key] is JsonValue value && value.TryGetValue<string>(out var prose))
            {
                properties[key] = ClaimSignals(prose);
            }
        }

        return root.ToJsonString(WriteOptions);
    }

    /// <summary>
    /// The SHA-256 an archived call carries in place of its arguments: taken over the CANONICAL
    /// bytes, so two calls the spec considers identical produce the same digest and a retry storm
    /// survives redaction intact.
    /// </summary>
    public static string ArgsHash(string rawArgs) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonCanonicalizer.CanonicalizeArgs(rawArgs))));

    /// <summary>
    /// The digest OBJECT an archived call carries in place of its arguments. Idempotent: arguments
    /// that are already a digest come back unchanged, which is what lets a redacted archive and the
    /// raw store it was taken from score identically.
    /// </summary>
    public static string ArgsDigest(string args) =>
        args.Contains(Fingerprints.RedactedArgsKey, StringComparison.Ordinal)
            ? args
            : new JsonObject { [Fingerprints.RedactedArgsKey] = ArgsHash(args) }.ToJsonString();

    private static void RedactArgs(JsonObject message)
    {
        if (!message.ContainsKey("function_args"))
        {
            return;
        }

        message["function_args"] = ArgsDigest(message["function_args"]?.GetValue<string>() ?? "");
    }

    private static void RedactProse(JsonObject message)
    {
        foreach (var field in ProseFields)
        {
            if (message[field] is JsonValue value && value.TryGetValue<string>(out var prose))
            {
                message[field] = ClaimSignals(prose);
            }
        }
    }

    /// <summary>
    /// The three facts metrics-spec.md computes from a prose field. Emitted in place of the prose so
    /// the fabricated-compliance check still has its inputs after redaction.
    /// </summary>
    private static JsonObject ClaimSignals(string prose) =>
        new()
        {
            ["length"] = prose.Length,
            ["claimVerbMatch"] = ClaimVerb.IsMatch(prose),
            ["claimNounMatch"] = ClaimNoun.IsMatch(prose),
        };

    private static string? TryRedact(string path, Func<string, string> redact)
    {
        try
        {
            return redact(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // A file this redactor cannot parse is a file it cannot prove is safe to publish, so it
            // must NOT fall through to the verbatim copy below. Emitting an empty document loses the
            // thread from the archive, which is visible; copying unreviewed prose is not.
            return path.EndsWith("messages.json", StringComparison.Ordinal) ? "[]" : "{}";
        }
    }

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
}
