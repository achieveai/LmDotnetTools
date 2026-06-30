using AchieveAi.LmDotnetTools.Misc.Web;

namespace AchieveAi.LmDotnetTools.Misc.Tests.Utils;

/// <summary>
///     In-memory <see cref="IWebFetchProvider" /> test double. It records the inputs it received and
///     either returns a configured <see cref="Result" /> or throws a configured <see cref="Exception" />.
///     Carrying no reference to Jina, it proves the tools are backend-agnostic.
/// </summary>
public sealed class FakeWebFetchProvider : IWebFetchProvider
{
    /// <summary>Whether <see cref="FetchAsync" /> was invoked.</summary>
    public bool Called { get; private set; }

    /// <summary>The URL the tool passed to the provider.</summary>
    public string? ReceivedUrl { get; private set; }

    /// <summary>The options the tool passed to the provider.</summary>
    public WebFetchOptions? ReceivedOptions { get; private set; }

    /// <summary>The cancellation token the tool threaded through.</summary>
    public CancellationToken ReceivedToken { get; private set; }

    /// <summary>The result to return when no <see cref="Exception" /> is configured.</summary>
    public WebFetchResult? Result { get; set; }

    /// <summary>When set, <see cref="FetchAsync" /> throws this instead of returning.</summary>
    public Exception? Exception { get; set; }

    /// <inheritdoc />
    public Task<WebFetchResult> FetchAsync(string url, WebFetchOptions options, CancellationToken cancellationToken)
    {
        Called = true;
        ReceivedUrl = url;
        ReceivedOptions = options;
        ReceivedToken = cancellationToken;

        if (Exception is not null)
        {
            throw Exception;
        }

        return Task.FromResult(Result ?? new WebFetchResult { Content = string.Empty });
    }
}

/// <summary>
///     In-memory <see cref="IWebSearchProvider" /> test double. It records the inputs it received and
///     either returns a configured <see cref="Result" /> or throws a configured <see cref="Exception" />.
///     Carrying no reference to Jina, it proves the tools are backend-agnostic.
/// </summary>
public sealed class FakeWebSearchProvider : IWebSearchProvider
{
    /// <summary>Whether <see cref="SearchAsync" /> was invoked.</summary>
    public bool Called { get; private set; }

    /// <summary>The query the tool passed to the provider.</summary>
    public string? ReceivedQuery { get; private set; }

    /// <summary>The options the tool passed to the provider.</summary>
    public WebSearchOptions? ReceivedOptions { get; private set; }

    /// <summary>The cancellation token the tool threaded through.</summary>
    public CancellationToken ReceivedToken { get; private set; }

    /// <summary>The result to return when no <see cref="Exception" /> is configured.</summary>
    public WebSearchResult? Result { get; set; }

    /// <summary>When set, <see cref="SearchAsync" /> throws this instead of returning.</summary>
    public Exception? Exception { get; set; }

    /// <inheritdoc />
    public Task<WebSearchResult> SearchAsync(string query, WebSearchOptions options, CancellationToken cancellationToken)
    {
        Called = true;
        ReceivedQuery = query;
        ReceivedOptions = options;
        ReceivedToken = cancellationToken;

        if (Exception is not null)
        {
            throw Exception;
        }

        return Task.FromResult(Result ?? new WebSearchResult { Items = [] });
    }
}
