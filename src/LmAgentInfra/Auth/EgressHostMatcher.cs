using System.Text.RegularExpressions;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Auth;

/// <summary>
/// Host-pattern matching + header-name/value validation shared by every egress-auth caller: the
/// OAuth provider host allowlist (<see cref="OAuthProviderHosts"/> delegates its match here), the
/// predefined-key sandbox network rules (<c>SandboxSessionRegistry.BuildAuthProviders</c>), the
/// predefined-key webhook host gate (<c>AuthWebhookController</c>), and the predefined-key CRUD
/// validation (<c>EgressKeysController</c>). One algorithm for all of them so the rule that opens a
/// host and the webhook check that injects into it can never drift.
/// </summary>
/// <remarks>
/// A user-entered predefined-key host widens a default-deny egress boundary, so
/// <see cref="ValidateHostPattern"/> is a genuine SSRF trust-boundary check: it rejects bare
/// wildcards, schemes/ports/paths, loopback, link-local/metadata addresses, and malformed hosts.
/// </remarks>
internal static partial class EgressHostMatcher
{
    /// <summary>
    /// Hop-by-hop / framing headers a predefined key must never set: overriding them can break or
    /// hijack the outbound request. <c>Cookie</c> and <c>Authorization</c> are deliberately allowed.
    /// </summary>
    private static readonly HashSet<string> ForbiddenHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host",
        "Content-Length",
        "Connection",
        "Transfer-Encoding",
        "Content-Type",
    };

    /// <summary>Literal hosts that must never be openable from the sandbox (loopback + cloud metadata).</summary>
    private static readonly HashSet<string> BlockedLiteralHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost",
        "0.0.0.0",
        "::1",
        "[::1]",
        "169.254.169.254", // cloud instance-metadata endpoint
        "metadata.google.internal",
    };

    /// <summary>
    /// True when <paramref name="destinationHost"/> matches one of the <paramref name="hosts"/>
    /// patterns — exact (case-insensitive) or a <c>*.</c> wildcard suffix. Fails closed: a null/empty
    /// destination or host list yields <c>false</c>.
    /// </summary>
    public static bool IsAllowed(IReadOnlyList<string>? hosts, string? destinationHost)
    {
        if (string.IsNullOrWhiteSpace(destinationHost) || hosts is null)
        {
            return false;
        }

        foreach (var host in hosts)
        {
            if (Matches(host, destinationHost))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Exact or <c>*.suffix</c> match of a single pattern against a destination host.</summary>
    private static bool Matches(string pattern, string destinationHost) =>
        pattern.StartsWith("*.", StringComparison.Ordinal)
            ? destinationHost.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)
            : string.Equals(destinationHost, pattern, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Validates a user-entered host pattern (exact host or <c>*.suffix</c>). Returns <c>null</c> when
    /// valid, otherwise a UI-safe error message. Rejects bare <c>*</c>, empty/whitespace, an embedded
    /// scheme/port/path, loopback (<c>localhost</c>/<c>127.x</c>/<c>[::1]</c>), link-local/metadata
    /// (<c>169.254.169.254</c>), <c>0.0.0.0</c>, a trailing dot, a wildcard covering a bare TLD
    /// (<c>*.com</c>), and anything that is not a syntactically valid DNS host.
    /// </summary>
    public static string? ValidateHostPattern(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return "Host is required.";
        }

        if (pattern != pattern.Trim())
        {
            return "Host must not contain leading or trailing whitespace.";
        }

        if (pattern.Contains("://", StringComparison.Ordinal) || pattern.Contains('/', StringComparison.Ordinal))
        {
            return "Host must be a bare hostname — no scheme or path.";
        }

        if (pattern.Contains(':', StringComparison.Ordinal))
        {
            return "Host must not include a port (rules are HTTPS/443 only).";
        }

        if (pattern == "*")
        {
            return "A bare '*' is not allowed — scope the host, e.g. api.example.com or *.example.com.";
        }

        if (pattern.EndsWith('.'))
        {
            return "Host must not end with a dot.";
        }

        // The concrete host to reason about (strip a single leading "*." wildcard).
        var isWildcard = pattern.StartsWith("*.", StringComparison.Ordinal);
        var bareHost = isWildcard ? pattern[2..] : pattern;

        if (bareHost.Length == 0 || bareHost.Contains('*', StringComparison.Ordinal))
        {
            return "Wildcards are only allowed as a single leading '*.' label.";
        }

        if (isWildcard && !bareHost.Contains('.', StringComparison.Ordinal))
        {
            return "A wildcard must cover at least two labels, e.g. *.example.com (not *.com).";
        }

        if (BlockedLiteralHosts.Contains(bareHost) || IsLoopbackOrLinkLocalIp(bareHost))
        {
            return "Loopback, link-local, and metadata hosts are not allowed.";
        }

        if (!HostLabelsRegex().IsMatch(bareHost))
        {
            return "Host is not a valid hostname.";
        }

        return null;
    }

    /// <summary>
    /// Validates a header name: RFC 7230 token charset and not a hop-by-hop/framing header. Returns
    /// <c>null</c> when valid, otherwise a UI-safe error message.
    /// </summary>
    public static string? ValidateHeaderName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "Header name is required.";
        }

        if (!HeaderNameRegex().IsMatch(name))
        {
            return "Header name contains invalid characters (RFC 7230 token expected).";
        }

        if (ForbiddenHeaderNames.Contains(name))
        {
            return $"Header '{name}' cannot be set (hop-by-hop / framing header).";
        }

        return null;
    }

    /// <summary>
    /// Validates a header value: non-empty and free of CR/LF and other control characters (prevents
    /// header injection). Returns <c>null</c> when valid, otherwise a UI-safe error message.
    /// </summary>
    public static string? ValidateHeaderValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "Header value is required.";
        }

        foreach (var ch in value)
        {
            if (ch is '\r' or '\n' || char.IsControl(ch))
            {
                return "Header value must not contain control characters or line breaks.";
            }
        }

        return null;
    }

    /// <summary>Rejects IPv4 loopback (<c>127.0.0.0/8</c>) and link-local (<c>169.254.0.0/16</c>) literals.</summary>
    private static bool IsLoopbackOrLinkLocalIp(string host) =>
        host.StartsWith("127.", StringComparison.Ordinal) || host.StartsWith("169.254.", StringComparison.Ordinal);

    // Syntactically valid DNS host: dot-separated labels of [A-Za-z0-9-], each 1-63 chars, not
    // starting/ending with a hyphen. Anchored; case-insensitive.
    [GeneratedRegex(
        @"^(?=.{1,253}$)([A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex HostLabelsRegex();

    // RFC 7230 token: one or more tchar. tchar = "!#$%&'*+-.^_`|~" / DIGIT / ALPHA.
    [GeneratedRegex(@"^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderNameRegex();
}
