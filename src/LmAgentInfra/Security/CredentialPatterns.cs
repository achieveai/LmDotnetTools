using System.Text.RegularExpressions;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Security;

/// <summary>One named credential shape, and the linear-time matcher that recognizes it.</summary>
/// <param name="Name">
/// A stable, non-revealing label for the shape — safe to put in an exception message, a log line or a
/// refusal reason. It never contains any part of what matched.
/// </param>
/// <param name="Matcher">The compiled matcher. Always <see cref="RegexOptions.NonBacktracking"/>.</param>
public sealed record CredentialPattern(string Name, Regex Matcher);

/// <summary>
/// The shared catalogue of credential shapes, and the two things anyone ever wants to do with it:
/// <see cref="Redact"/> (replace what matched, keep the text) and <see cref="FindFirstName"/> (refuse the
/// text entirely). Both consumers run over content an attacker or a language model chose, so every pattern
/// is <see cref="RegexOptions.NonBacktracking"/> with an explicit timeout — linear-time matching is a
/// correctness requirement here, not a nicety.
/// <para>
/// It lives in this assembly because two otherwise-unrelated callers need the SAME list, and a second copy
/// of a list like this does not stay a copy: the trigger redactor sanitizes tailed log lines before they
/// reach a model's context, and the review-artifact export refuses to publish a file to a git branch. A
/// shape added for one is a shape the other silently lacks.
/// </para>
/// <para>
/// Credential shapes ONLY. Personal data (email addresses and the like) is a different policy with a
/// different false-positive cost, and it stays with the caller that wants it.
/// </para>
/// <para>
/// Pattern matching is a mitigation, not a guarantee. It recognizes the shapes listed below and nothing
/// else; a secret in an unlisted shape passes. What it does buy is that the listed shapes — which are the
/// ones that actually leak — cannot pass silently.
/// </para>
/// </summary>
public static class CredentialPatterns
{
    /// <summary>
    /// An independent backstop rather than the primary defense: <see cref="RegexOptions.NonBacktracking"/>
    /// already guarantees linear time.
    /// </summary>
    public static TimeSpan MatchTimeout { get; } = TimeSpan.FromMilliseconds(100);

    private static Regex Pattern(string pattern) =>
        new(pattern, RegexOptions.NonBacktracking | RegexOptions.IgnoreCase, MatchTimeout);

    /// <summary>
    /// Ordered most-specific first, so a labelled <c>Authorization: Bearer &lt;tok&gt;</c> is handled as a
    /// whole key/value pair before a generic token shape gets a chance to take only the value and leave the
    /// label looking like it still carries one.
    /// </summary>
    public static IReadOnlyList<CredentialPattern> All { get; } =
    [
        // PEM private key header. The header line only: a PEM body arrives as its own lines, which no
        // pattern here matches. For a redactor this marks that key material is present rather than
        // containing it; for a refusal it is decisive, which is the stricter and the right reading.
        new("pem-private-key", Pattern(@"-----BEGIN[A-Z ]*PRIVATE KEY-----")),
        // Labelled secret assignments: `password=...`, `api_key: ...`, `Authorization: Bearer x`, and the
        // quoted forms — `password: "hunter2"`, `api_key='sk_live_x'`, `{"password": "p"}`. The value
        // alternation leads with the quoted branches deliberately: a value class that merely EXCLUDED the
        // quote did not leave the quotes behind, it failed to match the assignment at all and passed the
        // secret through — and JSON-shaped content is the common case, not the exotic one. An unterminated
        // quote still matches, bounded to the line so a stray quote cannot swallow a multi-line payload.
        new(
            "labelled-secret-assignment",
            Pattern(
                @"[""']?\b(?:authorization|password|passwd|pwd|secret|api[_-]?key|access[_-]?token|refresh[_-]?token|client[_-]?secret|token)\b[""']?\s*[:=]\s*(?:bearer\s+|basic\s+)?(?:""[^""\r\n]*""?|'[^'\r\n]*'?|[^\s,;""']+)"
            )
        ),
        // Connection-string fields, which are `;`-delimited rather than whitespace-delimited.
        new(
            "connection-string-field",
            Pattern(@"\b(?:password|pwd|user\s?id|uid|account\s?key|shared\s?access\s?key)\s*=\s*[^;]+")
        ),
        // Userinfo in a URI — `https://user:token@host/…`, which is how a git remote carries a credential
        // (`https://x-access-token:ghs_…@github.com/…`) and how a connection URL carries a password. The
        // token inside may be in no recognizable vendor shape at all, so this catches it by POSITION.
        new("uri-userinfo", Pattern(@"\b[a-z][a-z0-9+.\-]*://[^\s/:@]+:[^\s/@]+@")),
        // Vendor shapes, each self-identifying by prefix.
        new("github-token", Pattern(@"\bgh[pousr]_[A-Za-z0-9]{16,}\b")),
        new("github-fine-grained-pat", Pattern(@"\bgithub_pat_[A-Za-z0-9_]{20,}\b")),
        new("slack-token", Pattern(@"\bxox[baprs]-[A-Za-z0-9-]{10,}\b")),
        // Every AWS unique-id prefix, not only AKIA: a temporary ASIA key is just as live.
        new("aws-access-key-id", Pattern(@"\b(?:AKIA|ASIA|ABIA|ACCA|AGPA|AIDA|AIPA|ANPA|ANVA|AROA)[0-9A-Z]{16}\b")),
        new("openai-secret-key", Pattern(@"\bsk-[A-Za-z0-9_-]{16,}\b")),
        new("stripe-key", Pattern(@"\b[sr]k_(?:live|test)_[A-Za-z0-9]{16,}\b")),
        new("google-api-key", Pattern(@"\bAIza[0-9A-Za-z_\-]{35}\b")),
        new("npm-token", Pattern(@"\bnpm_[A-Za-z0-9]{36}\b")),
        new("jwt", Pattern(@"\bey[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b")),
    ];

    /// <summary>
    /// The NAME of the first shape found in <paramref name="content"/>, or <c>null</c> when none matched.
    /// <para>
    /// It returns the name and never the match, so a caller can say precisely what it refused and why
    /// without copying the secret into the very message, log or exception that made it refuse — which is
    /// how a detector leaks the thing it detected.
    /// </para>
    /// </summary>
    /// <exception cref="RegexMatchTimeoutException">
    /// Deliberately propagated rather than swallowed. "I could not finish inspecting this" is not "this is
    /// clean", and only the caller knows which fail-closed answer its own contract requires.
    /// </exception>
    public static string? FindFirstName(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return null;
        }
        foreach (var pattern in All)
        {
            if (pattern.Matcher.IsMatch(content))
            {
                return pattern.Name;
            }
        }
        return null;
    }

    /// <summary>
    /// Replaces every recognized credential shape with <paramref name="placeholder"/>, leaving the
    /// surrounding text intact. The caller owns the fail-closed behaviour on a timeout.
    /// </summary>
    /// <param name="content">The text to redact.</param>
    /// <param name="placeholder">What each match is replaced with.</param>
    public static string Redact(string content, string placeholder)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content;
        }
        var result = content;
        foreach (var pattern in All)
        {
            result = pattern.Matcher.Replace(result, _ => placeholder);
        }
        return result;
    }

    /// <summary>The placeholder a shape named <paramref name="shapeName"/> is replaced with.</summary>
    public static string PlaceholderFor(string shapeName) => $"[REDACTED:{shapeName}]";

    /// <summary>
    /// Replaces every recognized credential shape with a placeholder that NAMES the shape —
    /// <c>[REDACTED:jwt]</c>, <c>[REDACTED:uri-userinfo]</c> — so a reader can see what was removed and
    /// where, without any part of the value surviving.
    /// <para>
    /// Deterministic: the replacement for a given shape is a constant, so the same input always produces
    /// the same output. It is produced through a <see cref="MatchEvaluator"/> rather than a replacement
    /// STRING on purpose — a replacement string is a template in which <c>$1</c>, <c>$&amp;</c> and friends
    /// expand to the captured text, so a placeholder that ever grew such a token would quietly re-emit the
    /// secret it was added to remove. An evaluator that ignores its <see cref="Match"/> cannot.
    /// </para>
    /// </summary>
    /// <param name="content">The text to redact.</param>
    /// <exception cref="RegexMatchTimeoutException">
    /// Propagated, not swallowed: partially-redacted text is indistinguishable from clean text, and only
    /// the caller knows what its own contract requires.
    /// </exception>
    public static string RedactNamed(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content;
        }
        var result = content;
        foreach (var pattern in All)
        {
            var placeholder = PlaceholderFor(pattern.Name);
            result = pattern.Matcher.Replace(result, _ => placeholder);
        }
        return result;
    }
}
