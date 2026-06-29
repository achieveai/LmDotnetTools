namespace AchieveAi.LmDotnetTools.Misc.Web;

/// <summary>
///     A single search hit returned by <see cref="IWebSearchProvider.SearchAsync" />.
/// </summary>
public sealed record WebSearchItem
{
    /// <summary>
    ///     The result title.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    ///     The result URL.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    ///     An optional snippet/description for the result.
    /// </summary>
    public string? Snippet { get; init; }
}

/// <summary>
///     Result of an <see cref="IWebSearchProvider.SearchAsync" /> call.
/// </summary>
public sealed record WebSearchResult
{
    /// <summary>
    ///     The ordered list of search hits (may be empty).
    /// </summary>
    public required IReadOnlyList<WebSearchItem> Items { get; init; }

    /// <summary>
    ///     Token usage reported by the backend for this call, when available.
    /// </summary>
    public int? UsageTokens { get; init; }
}
