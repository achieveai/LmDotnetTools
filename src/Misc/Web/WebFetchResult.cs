namespace AchieveAi.LmDotnetTools.Misc.Web;

/// <summary>
///     Result of an <see cref="IWebFetchProvider.FetchAsync" /> call.
/// </summary>
public sealed record WebFetchResult
{
    /// <summary>
    ///     The fetched page content rendered as Markdown.
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    ///     The page title, when the backend reports one.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    ///     The canonical/final URL the content was fetched from, when reported.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    ///     Token usage reported by the backend for this call, when available.
    /// </summary>
    public int? UsageTokens { get; init; }

    /// <summary>
    ///     An optional non-fatal warning (for example, partial content) surfaced by the backend.
    /// </summary>
    public string? Warning { get; init; }
}
