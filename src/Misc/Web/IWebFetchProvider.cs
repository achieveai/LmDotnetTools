namespace AchieveAi.LmDotnetTools.Misc.Web;

/// <summary>
///     Abstraction over a backend capable of fetching a single web page and returning it as Markdown.
/// </summary>
public interface IWebFetchProvider
{
    /// <summary>
    ///     Fetches <paramref name="url" /> and returns its content as Markdown.
    /// </summary>
    /// <param name="url">The absolute http/https URL to fetch (already validated by the caller).</param>
    /// <param name="options">Optional fetch tuning parameters.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The fetched content and associated metadata.</returns>
    Task<WebFetchResult> FetchAsync(string url, WebFetchOptions options, CancellationToken cancellationToken);
}
