namespace AchieveAi.LmDotnetTools.Misc.Web;

/// <summary>
///     Abstraction over a backend capable of running a web search.
/// </summary>
public interface IWebSearchProvider
{
    /// <summary>
    ///     Runs a web search for <paramref name="query" />.
    /// </summary>
    /// <param name="query">The search query (already validated by the caller).</param>
    /// <param name="options">Optional search tuning parameters.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The ordered list of search results.</returns>
    Task<WebSearchResult> SearchAsync(string query, WebSearchOptions options, CancellationToken cancellationToken);
}
