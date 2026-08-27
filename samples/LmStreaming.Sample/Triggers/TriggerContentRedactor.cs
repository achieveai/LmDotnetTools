using System.Text.RegularExpressions;

namespace AchieveAi.LmDotnetTools.LmStreaming.Sample.Triggers;

/// <summary>
/// Removes credentials and personal data from external content before a trigger forwards it into
/// the model's context and the conversation's persisted history.
/// </summary>
/// <remarks>
/// <para>
/// This is privacy redaction, and it is a DIFFERENT job from envelope sanitization. Escaping
/// <c>&lt;</c>/<c>&gt;</c> and capping length defend the <c>&lt;trigger&gt;</c> envelope boundary
/// against content that wants to be read as markup; neither removes a bearer token. A matched log
/// line is forwarded verbatim to the model and written to history, so a secret in it leaves the
/// host — the two concerns are applied in sequence, never conflated.
/// </para>
/// <para>
/// Pattern-based redaction is a mitigation, not a guarantee: it removes the shapes listed below and
/// nothing else, and a secret in a shape not listed here still gets through. A deployment that
/// cannot accept that residual risk should forward no content at all — see
/// <see cref="FileTailContentMode.MetadataOnly"/>, which is the only setting that makes the
/// question moot.
/// </para>
/// <para>
/// Every pattern is <see cref="RegexOptions.NonBacktracking"/> with an explicit match timeout: this
/// runs over attacker-influenced input (anyone who can write to a watched log chooses these bytes),
/// so linear-time matching is a correctness requirement, not a nicety. Redaction failure is
/// deliberately fail-closed — see <see cref="Redact"/>.
/// </para>
/// </remarks>
internal static class TriggerContentRedactor
{
    private const string Placeholder = "[redacted]";

    // Same rationale as FileTailTriggerSource.MatchTimeout: NonBacktracking already guarantees
    // linear time, so this is an independent backstop rather than the primary defense.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private static Regex Pattern(string pattern) =>
        new(pattern, RegexOptions.NonBacktracking | RegexOptions.IgnoreCase, MatchTimeout);

    // Ordered most-specific first: a labelled `Authorization: Bearer <tok>` is redacted as a whole
    // key/value pair before the generic token shapes get a chance to redact only the value and leave
    // the label looking like it still carries one.
    private static readonly Regex[] Patterns =
    [
        // PEM private key header — the body that follows is caught by the long-base64-run rule.
        Pattern(@"-----BEGIN[A-Z ]*PRIVATE KEY-----"),

        // Labelled secret assignments: `password=...`, `api_key: ...`, `Authorization: Bearer x`.
        // The value runs to end-of-token (or end-of-line for quoted/spaced values).
        Pattern(@"\b(?:authorization|password|passwd|pwd|secret|api[_-]?key|access[_-]?token|refresh[_-]?token|client[_-]?secret|token)\b\s*[:=]\s*(?:bearer\s+|basic\s+)?[^\s,;""']+"),

        // Connection-string fields, which are `;`-delimited rather than whitespace-delimited.
        Pattern(@"\b(?:password|pwd|user\s?id|uid|account\s?key|shared\s?access\s?key)\s*=\s*[^;]+"),

        // Vendor token shapes, each self-identifying by prefix.
        Pattern(@"\bgh[pousr]_[A-Za-z0-9]{16,}\b"),           // GitHub PAT / OAuth / refresh
        Pattern(@"\bxox[baprs]-[A-Za-z0-9-]{10,}\b"),         // Slack
        Pattern(@"\bAKIA[0-9A-Z]{16}\b"),                     // AWS access key id
        Pattern(@"\bsk-[A-Za-z0-9_-]{16,}\b"),                // OpenAI-style secret key
        Pattern(@"\bey[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b"), // JWT

        // Email addresses (PII).
        Pattern(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}"),
    ];

    /// <summary>
    /// Replaces every recognized credential or PII shape in <paramref name="content"/> with
    /// <c>[redacted]</c>, leaving the surrounding text intact.
    /// </summary>
    /// <remarks>
    /// Fail-closed: if a pattern times out on pathological input, the whole line is withheld rather
    /// than forwarded unredacted. Passing content through because the redactor could not finish is
    /// the one outcome this must never produce — a timeout means "I do not know what is in here".
    /// </remarks>
    internal static string Redact(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content;
        }

        try
        {
            var result = content;
            foreach (var pattern in Patterns)
            {
                result = pattern.Replace(result, Placeholder);
            }

            return result;
        }
        catch (RegexMatchTimeoutException)
        {
            return "[redaction timed out; content withheld]";
        }
    }
}
