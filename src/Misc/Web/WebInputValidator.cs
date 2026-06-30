using System.Net;
using System.Net.Sockets;

namespace AchieveAi.LmDotnetTools.Misc.Web;

/// <summary>
///     Outcome of validating a web tool input (URL or query).
/// </summary>
/// <param name="IsValid">Whether the input passed validation.</param>
/// <param name="Value">
///     The normalized value when valid (URL with the fragment stripped, or trimmed query); otherwise <c>null</c>.
/// </param>
/// <param name="Error">A human-readable error when invalid; otherwise <c>null</c>.</param>
public sealed record WebInputValidation(bool IsValid, string? Value, string? Error)
{
    /// <summary>
    ///     Creates a successful validation result carrying the normalized <paramref name="value" />.
    /// </summary>
    public static WebInputValidation Ok(string value) => new(true, value, null);

    /// <summary>
    ///     Creates a failed validation result carrying the <paramref name="error" /> message.
    /// </summary>
    public static WebInputValidation Fail(string error) => new(false, null, error);
}

/// <summary>
///     Validates URLs and search queries for the web tools without performing any network I/O.
///     URL validation blocks SSRF-prone targets (localhost, loopback, private, link-local, multicast,
///     and internal host suffixes) and strips the fragment from the returned value.
/// </summary>
public static class WebInputValidator
{
    private const int MaxUrlLength = 2048;

    // Internal/loopback suffixes plus the RFC 6761 reserved TLDs (.test/.example/.invalid), which must
    // never resolve to a real, fetchable host.
    private static readonly string[] BlockedHostSuffixes =
    [
        ".local",
        ".internal",
        ".localhost",
        ".test",
        ".example",
        ".invalid",
    ];

    /// <summary>
    ///     Validates an absolute http/https <paramref name="url" />.
    /// </summary>
    /// <param name="url">The URL to validate.</param>
    /// <returns>A <see cref="WebInputValidation" /> whose value (when valid) has the fragment removed.</returns>
    public static WebInputValidation ValidateUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return WebInputValidation.Fail("URL cannot be empty.");
        }

        var trimmed = url.Trim();

        if (trimmed.Length > MaxUrlLength)
        {
            return WebInputValidation.Fail($"URL exceeds the maximum allowed length of {MaxUrlLength} characters.");
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return WebInputValidation.Fail("URL is not a valid absolute URI.");
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return WebInputValidation.Fail("URL scheme must be http or https.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return WebInputValidation.Fail("URL must not contain user information.");
        }

        if (IsBlockedHost(uri))
        {
            return WebInputValidation.Fail("URL host is not allowed (private, loopback, or internal address).");
        }

        // Strip the fragment: GetLeftPart(Query) yields scheme + authority + path + query, excluding "#...".
        return WebInputValidation.Ok(uri.GetLeftPart(UriPartial.Query));
    }

    /// <summary>
    ///     Validates a search <paramref name="query" />.
    /// </summary>
    /// <param name="query">The query to validate.</param>
    /// <param name="maxLength">The maximum allowed length of the trimmed query.</param>
    /// <returns>A <see cref="WebInputValidation" /> whose value (when valid) is the trimmed query.</returns>
    public static WebInputValidation ValidateQuery(string? query, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return WebInputValidation.Fail("Query cannot be empty.");
        }

        var trimmed = query.Trim();

        if (trimmed.Length > maxLength)
        {
            return WebInputValidation.Fail($"Query exceeds the maximum allowed length of {maxLength} characters.");
        }

        if (trimmed.Any(char.IsControl))
        {
            return WebInputValidation.Fail("Query must not contain control characters.");
        }

        return WebInputValidation.Ok(trimmed);
    }

    /// <summary>
    ///     Determines whether the host of <paramref name="uri" /> is a blocked target.
    /// </summary>
    private static bool IsBlockedHost(Uri uri)
    {
        var host = uri.Host;

        if (string.IsNullOrEmpty(host))
        {
            return true;
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var suffix in BlockedHostSuffixes)
        {
            if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // IPv6 literals arrive bracketed via Uri.Host (e.g. "[::1]"); strip the brackets before parsing.
        var candidate = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;

        return IPAddress.TryParse(candidate, out var ip) && IsBlockedIp(ip);
    }

    /// <summary>
    ///     Determines whether <paramref name="ip" /> is loopback, private, link-local, or multicast.
    /// </summary>
    private static bool IsBlockedIp(IPAddress ip)
    {
        // Unspecified / "any" address (0.0.0.0 or [::]): routes to every local interface, so it is an
        // SSRF vector equivalent to loopback and must be rejected.
        if (IPAddress.Any.Equals(ip) || IPAddress.IPv6Any.Equals(ip))
        {
            return true;
        }

        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6UniqueLocal;
        }

        if (ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = ip.GetAddressBytes();

        // 10.0.0.0/8 (private)
        if (bytes[0] == 10)
        {
            return true;
        }

        // 172.16.0.0/12 (private)
        if (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
        {
            return true;
        }

        // 192.168.0.0/16 (private)
        if (bytes[0] == 192 && bytes[1] == 168)
        {
            return true;
        }

        // 169.254.0.0/16 (link-local)
        if (bytes[0] == 169 && bytes[1] == 254)
        {
            return true;
        }

        // 224.0.0.0/4 (multicast)
        return bytes[0] is >= 224 and <= 239;
    }
}
