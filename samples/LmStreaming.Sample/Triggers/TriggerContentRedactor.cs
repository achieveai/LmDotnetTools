using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.LmAgentInfra.Security;

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
    private static readonly TimeSpan MatchTimeout = CredentialPatterns.MatchTimeout;

    private static Regex Pattern(string pattern) =>
        new(pattern, RegexOptions.NonBacktracking | RegexOptions.IgnoreCase, MatchTimeout);

    /// <summary>
    /// The PII shapes, which are this redactor's own policy. The credential shapes live in
    /// <see cref="CredentialPatterns"/> and are shared with the review-artifact export, because a shape
    /// added for one of them is a shape the other must not silently lack. The split is by policy, not by
    /// convenience: personal data has a different false-positive cost and is not something a git-branch
    /// export has any business refusing on.
    /// </summary>
    private static readonly Regex[] PersonalDataPatterns =
    [
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
        // Credentials first (shared catalogue, most-specific-first within itself), then this
        // redactor's own PII policy.
        var result = CredentialPatterns.Redact(content, Placeholder);
        foreach (var pattern in PersonalDataPatterns)
        {
            result = pattern.Replace(result, Placeholder);
        }

        return result;
    }
}
