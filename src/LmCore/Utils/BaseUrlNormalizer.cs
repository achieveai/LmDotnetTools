namespace AchieveAi.LmDotnetTools.LmCore.Utils;

/// <summary>
///     Normalizes provider <c>BaseUrl</c> values to the convention each consumer expects.
/// </summary>
/// <remarks>
///     Two conventions exist in this codebase and they are easy to confuse:
///     <list type="bullet">
///         <item>
///             <description>
///                 The in-house <c>AnthropicClient</c> appends <c>/messages</c> to <c>BaseUrl</c>
///                 and therefore expects the configured URL to <em>include</em> a trailing
///                 <c>/v1</c> segment (e.g. <c>https://api.anthropic.com/v1</c>).
///             </description>
///         </item>
///         <item>
///             <description>
///                 The official <c>@anthropic-ai/claude-agent-sdk</c> CLI (and the underlying
///                 Anthropic SDK) appends <c>/v1/messages</c> to whatever <c>ANTHROPIC_BASE_URL</c>
///                 it reads, so that env var must <em>exclude</em> the <c>/v1</c> suffix
///                 (e.g. <c>https://api.anthropic.com</c>). Passing a URL ending in <c>/v1</c>
///                 produces requests against <c>/v1/v1/messages</c>, which 404 silently and surface
///                 as "the agent completes with no assistant content rendered" (issue #29).
///             </description>
///         </item>
///     </list>
///     This helper exposes both transformations so callers can pick the convention they need
///     instead of open-coding string surgery at every wiring site.
/// </remarks>
public static class BaseUrlNormalizer
{
    private const string V1Segment = "/v1";

    /// <summary>
    ///     Returns <paramref name="baseUrl"/> with a single trailing <c>/v1</c> segment, suitable
    ///     for the in-house <c>AnthropicClient</c> which appends <c>/messages</c> directly.
    /// </summary>
    /// <param name="baseUrl">The configured base URL. May or may not already end in <c>/v1</c>.</param>
    /// <returns>The URL guaranteed to end in <c>/v1</c> (no trailing slash). Returns <c>null</c>
    /// when <paramref name="baseUrl"/> is null or whitespace.</returns>
    public static string? EnsureV1Suffix(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return baseUrl;
        }

        var trimmed = baseUrl.TrimEnd('/');
        return trimmed.EndsWith(V1Segment, StringComparison.Ordinal)
            ? trimmed
            : trimmed + V1Segment;
    }

    /// <summary>
    ///     Returns <paramref name="baseUrl"/> with any trailing <c>/v1</c> segment removed,
    ///     suitable for <c>ANTHROPIC_BASE_URL</c> as consumed by the Anthropic SDK / Claude
    ///     Agent SDK CLI which append <c>/v1/messages</c> themselves.
    /// </summary>
    /// <param name="baseUrl">The configured base URL. May or may not already end in <c>/v1</c>.</param>
    /// <returns>The URL guaranteed to <em>not</em> end in <c>/v1</c> (no trailing slash). Returns
    /// <c>null</c> when <paramref name="baseUrl"/> is null or whitespace.</returns>
    public static string? StripV1Suffix(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return baseUrl;
        }

        var trimmed = baseUrl.TrimEnd('/');
        return trimmed.EndsWith(V1Segment, StringComparison.Ordinal)
            ? trimmed[..^V1Segment.Length]
            : trimmed;
    }
}
