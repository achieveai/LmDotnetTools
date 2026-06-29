namespace AchieveAi.LmDotnetTools.Misc.Web;

/// <summary>
///     Optional parameters that tune a single <see cref="IWebSearchProvider.SearchAsync" /> call.
/// </summary>
public sealed record WebSearchOptions
{
    /// <summary>
    ///     Maximum number of results to request. When <c>null</c>, the backend default applies.
    /// </summary>
    public int? Count { get; init; }

    /// <summary>
    ///     Optional ISO country code used to localize results (e.g. <c>"US"</c>).
    /// </summary>
    public string? Country { get; init; }

    /// <summary>
    ///     Optional language code used to localize results (e.g. <c>"en"</c>).
    /// </summary>
    public string? Language { get; init; }
}
