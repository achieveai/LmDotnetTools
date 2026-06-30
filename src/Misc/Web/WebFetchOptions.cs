namespace AchieveAi.LmDotnetTools.Misc.Web;

/// <summary>
///     Optional parameters that tune a single <see cref="IWebFetchProvider.FetchAsync" /> call.
/// </summary>
public sealed record WebFetchOptions
{
    /// <summary>
    ///     Optional CSS selector identifying the region of the page to extract.
    ///     When <c>null</c>, the whole page is returned.
    /// </summary>
    public string? TargetSelector { get; init; }

    /// <summary>
    ///     When <c>true</c>, instructs the backend to bypass any cached copy and fetch fresh content.
    /// </summary>
    public bool? NoCache { get; init; }
}
