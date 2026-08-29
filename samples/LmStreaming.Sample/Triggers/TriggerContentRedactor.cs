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
/// deliberately fail-closed — see <see cref="Redact(string)"/>.
/// </para>
/// </remarks>
internal static class TriggerContentRedactor
{
    private const string Placeholder = "[redacted]";

    /// <summary>What a caller gets when redaction could not be completed. Deliberately not the
    /// original content: "I do not know what is in here" must never render as "there was nothing in
    /// here".</summary>
    internal const string WithheldOnFailure = "[redaction failed; content withheld]";

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
        // PEM private key header. Only the header line: this redactor is line-oriented, and a PEM
        // body arrives as its own lines, which no pattern here matches. Redacting the header is
        // therefore a marker that key material is present, NOT containment of the key itself — a
        // deployment tailing a file that can carry PEM bodies wants FileTailContentMode.MetadataOnly.
        Pattern(@"-----BEGIN[A-Z ]*PRIVATE KEY-----"),
        // Labelled secret assignments: `password=...`, `api_key: ...`, `Authorization: Bearer x`,
        // and the quoted forms — `password: "hunter2"`, `api_key='sk_live_x'`, `{"password": "p"}`.
        // The quoted forms are why the value alternation leads with the quoted branches: a value
        // class that merely EXCLUDED the quote (`[^\s,;"']+`) did not leave the quotes behind, it
        // failed to match the assignment at all and forwarded the secret verbatim — and JSON-shaped
        // logs (this repo's own Serilog output) are the common case, not the exotic one. An
        // unterminated quote still redacts, bounded to the line so a stray quote cannot swallow the
        // rest of a multi-line payload. The whole key/value pair is replaced, deliberately: see the
        // ordering note above on why leaving a bare label behind is worse than removing it.
        Pattern(
            @"[""']?\b(?:authorization|password|passwd|pwd|secret|api[_-]?key|access[_-]?token|refresh[_-]?token|client[_-]?secret|token)\b[""']?\s*[:=]\s*(?:bearer\s+|basic\s+)?(?:""[^""\r\n]*""?|'[^'\r\n]*'?|[^\s,;""']+)"
        ),
        // Connection-string fields, which are `;`-delimited rather than whitespace-delimited.
        Pattern(@"\b(?:password|pwd|user\s?id|uid|account\s?key|shared\s?access\s?key)\s*=\s*[^;]+"),
        // Vendor token shapes, each self-identifying by prefix.
        Pattern(@"\bgh[pousr]_[A-Za-z0-9]{16,}\b"), // GitHub PAT / OAuth / refresh
        Pattern(@"\bxox[baprs]-[A-Za-z0-9-]{10,}\b"), // Slack
        Pattern(@"\bAKIA[0-9A-Z]{16}\b"), // AWS access key id
        Pattern(@"\bsk-[A-Za-z0-9_-]{16,}\b"), // OpenAI-style secret key
        Pattern(@"\bey[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b"), // JWT
        // Email addresses (PII).
        Pattern(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}"),
    ];

    /// <summary>
    /// Replaces every recognized credential or PII shape in <paramref name="content"/> with
    /// <c>[redacted]</c>, leaving the surrounding text intact.
    /// </summary>
    /// <remarks>
    /// Fail-closed on ANY redaction failure — a match timeout on pathological input, or anything
    /// else — the whole line is withheld rather than forwarded unredacted. Passing content through
    /// because the redactor could not finish is the one outcome this must never produce: a failure
    /// means "I do not know what is in here", which is not "there was nothing in here". Narrowing
    /// this to the timeout alone had a second cost beyond the leak it did not cause: any other
    /// exception escaped into the caller's poll loop, faulting a task nobody observes, which is the
    /// silently-inert watcher this whole surface exists to eliminate.
    /// </remarks>
    internal static string Redact(string content) => Redact(content, ApplyPatterns);

    /// <summary>
    /// The fail-closed wrapper, with the pattern sweep as a parameter so the failure arm is
    /// reachable from a test. Production callers use <see cref="Redact(string)"/>.
    /// </summary>
    /// <remarks>
    /// The arm exists to be exercised: a fail-closed claim that no test drives is indistinguishable
    /// from a fail-open one, and inverting this <c>catch</c> to <c>return content</c> is a silent
    /// change from "withhold what I could not inspect" to "forward it unredacted".
    /// </remarks>
    internal static string Redact(string content, Func<string, string> applyPatterns)
    {
        ArgumentNullException.ThrowIfNull(applyPatterns);

        if (string.IsNullOrEmpty(content))
        {
            return content;
        }

        try
        {
            return applyPatterns(content);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the caller shutting down, not a redaction verdict. Withholding would
            // be harmless, but reporting a fabricated "content withheld" line for a wait that is
            // being torn down would put a message in front of the model about an event nobody is
            // waiting for. Let it propagate to the cancellation-aware caller.
            throw;
        }
        catch (Exception)
        {
            return WithheldOnFailure;
        }
    }

    private static string ApplyPatterns(string content)
    {
        var result = content;
        foreach (var pattern in Patterns)
        {
            result = pattern.Replace(result, Placeholder);
        }

        return result;
    }
}
