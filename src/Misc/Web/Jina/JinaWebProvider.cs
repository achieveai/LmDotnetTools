using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Http;
using AchieveAi.LmDotnetTools.Misc.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.Misc.Web.Jina;

/// <summary>
///     Jina-backed implementation of both web tool provider abstractions. Calls the Jina Reader
///     (<c>r.jina.ai</c>) for <see cref="FetchAsync" /> and Jina Search (<c>s.jina.ai</c>) for
///     <see cref="SearchAsync" /> using POST + JSON bodies, and returns Markdown content.
///
///     Ownership: <see cref="BaseHttpService" /> does NOT dispose the injected
///     <see cref="HttpClient" />. The default constructor creates a plain <see cref="HttpClient" />
///     that is intended to be owned for the process lifetime (the provider is a process singleton).
///
///     Log hygiene: the base service's HTTP-retry logging is deliberately suppressed by passing
///     <see cref="NullLogger.Instance" /> to <see cref="BaseHttpService" />, because the inherited
///     retry helper can log <c>HttpRequestException.Message</c> — which may embed the raw upstream
///     response body (and therefore secrets) — at Warning. The caller-supplied logger is retained and
///     used only for the provider's own minimal, sanitized diagnostics: a single host-only debug line.
///     URLs, query strings, request/response bodies, and the Authorization header are never logged.
/// </summary>
public sealed class JinaWebProvider : BaseHttpService, IWebFetchProvider, IWebSearchProvider
{
    /// <summary>The Jina Reader endpoint used by <see cref="FetchAsync" />.</summary>
    public const string ReaderUrl = "https://r.jina.ai/";

    /// <summary>The Jina Search endpoint used by <see cref="SearchAsync" />.</summary>
    public const string SearchUrl = "https://s.jina.ai/";

    private readonly WebToolsOptions _options;
    private readonly RetryOptions _retryOptions;

    /// <summary>
    ///     The provider's own logger, used only for minimal, sanitized host-only diagnostics. The base
    ///     service is intentionally wired to <see cref="NullLogger.Instance" /> (see class remarks) so
    ///     the inherited retry logging never emits raw upstream bodies or secrets.
    /// </summary>
    private readonly ILogger _logger;

    /// <summary>
    ///     Initializes the provider with a caller-supplied <see cref="HttpClient" /> (used by tests).
    /// </summary>
    /// <param name="httpClient">The HTTP client used to issue requests.</param>
    /// <param name="options">Web tools configuration (API key, per-call timeout).</param>
    /// <param name="logger">Optional logger for host-only diagnostics; defaults to <see cref="NullLogger.Instance" />.</param>
    /// <param name="retryOptions">Optional retry configuration; defaults to <see cref="RetryOptions.Default" />.</param>
    public JinaWebProvider(
        HttpClient httpClient,
        WebToolsOptions options,
        ILogger? logger = null,
        RetryOptions? retryOptions = null
    )
        : base(NullLogger.Instance, httpClient)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _retryOptions = retryOptions ?? RetryOptions.Default;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    ///     Initializes the provider with a default <see cref="HttpClient" /> (production use).
    ///     A plain client is used (not <c>HttpClientFactory.CreateWithApiKey</c>) because WebFetch
    ///     must work with no API key, and authorization is applied per-request when a key is present.
    /// </summary>
    /// <param name="options">Web tools configuration (API key, per-call timeout).</param>
    /// <param name="logger">Optional logger for host-only diagnostics; defaults to <see cref="NullLogger.Instance" />.</param>
    /// <param name="retryOptions">Optional retry configuration; defaults to <see cref="RetryOptions.Default" />.</param>
    public JinaWebProvider(WebToolsOptions options, ILogger? logger = null, RetryOptions? retryOptions = null)
        : base(NullLogger.Instance, new HttpClient())
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _retryOptions = retryOptions ?? RetryOptions.Default;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public async Task<WebFetchResult> FetchAsync(
        string url,
        WebFetchOptions options,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(options);

        LogTargetHost("WebFetch", url);

        // Bound the whole call (including retries) by the configured per-call timeout.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.TimeoutMs);

        // The request is built inside the operation lambda so each retry attempt gets a fresh
        // HttpRequestMessage (a sent request cannot be reused).
        return await ExecuteHttpWithRetryAsync(
            () => HttpClient.SendAsync(BuildFetchRequest(url, options), cts.Token),
            async response =>
            {
                var body = await response.Content.ReadAsStringAsync(cts.Token);
                return ParseFetchBody(body);
            },
            _retryOptions,
            cts.Token
        );
    }

    /// <inheritdoc />
    public async Task<WebSearchResult> SearchAsync(
        string query,
        WebSearchOptions options,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(options);

        // Log only the search endpoint host; the query itself is never logged.
        LogTargetHost("WebSearch", SearchUrl);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.TimeoutMs);

        return await ExecuteHttpWithRetryAsync(
            () => HttpClient.SendAsync(BuildSearchRequest(query, options), cts.Token),
            async response =>
            {
                var body = await response.Content.ReadAsStringAsync(cts.Token);
                return ParseSearchBody(body);
            },
            _retryOptions,
            cts.Token
        );
    }

    /// <summary>
    ///     Builds the Jina Reader POST request, applying the Markdown headers, optional tuning
    ///     headers, and bearer authorization when an API key is configured.
    /// </summary>
    private HttpRequestMessage BuildFetchRequest(string url, WebFetchOptions options)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, string> { ["url"] = url });
        var request = new HttpRequestMessage(HttpMethod.Post, ReaderUrl)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        request.Headers.Accept.ParseAdd("application/json");
        _ = request.Headers.TryAddWithoutValidation("X-Return-Format", "markdown");

        if (!string.IsNullOrEmpty(options.TargetSelector))
        {
            _ = request.Headers.TryAddWithoutValidation("X-Target-Selector", options.TargetSelector);
        }

        if (options.NoCache == true)
        {
            _ = request.Headers.TryAddWithoutValidation("X-No-Cache", "true");
        }

        ApplyAuthorization(request);
        return request;
    }

    /// <summary>
    ///     Builds the Jina Search POST request, including the optional <c>num</c>/<c>gl</c>/<c>hl</c>
    ///     parameters and bearer authorization when an API key is configured.
    /// </summary>
    private HttpRequestMessage BuildSearchRequest(string query, WebSearchOptions options)
    {
        var payload = new Dictionary<string, object> { ["q"] = query };
        if (options.Count is int count)
        {
            payload["num"] = count;
        }

        if (!string.IsNullOrEmpty(options.Country))
        {
            payload["gl"] = options.Country;
        }

        if (!string.IsNullOrEmpty(options.Language))
        {
            payload["hl"] = options.Language;
        }

        var request = new HttpRequestMessage(HttpMethod.Post, SearchUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

        request.Headers.Accept.ParseAdd("application/json");
        ApplyAuthorization(request);
        return request;
    }

    /// <summary>
    ///     Emits a single host-only debug line for diagnostics. Deliberately logs the destination host
    ///     and nothing else: the full URL, query string, request/response bodies, and Authorization
    ///     header are never logged (see the class remarks on log hygiene).
    /// </summary>
    private void LogTargetHost(string operation, string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            _logger.LogDebug("{Operation} requesting host {Host}", operation, uri.Host);
        }
    }

    /// <summary>
    ///     Adds an <c>Authorization: Bearer</c> header when an API key is configured. The key is
    ///     never logged.
    /// </summary>
    private void ApplyAuthorization(HttpRequestMessage request)
    {
        var key = _options.JinaApiKey;
        if (!string.IsNullOrEmpty(key))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
    }

    /// <summary>
    ///     Tolerantly parses a Reader response: a JSON envelope with a <c>data</c> object maps its
    ///     fields; any other body (for example plain Markdown) is used verbatim as the content.
    /// </summary>
    private static WebFetchResult ParseFetchBody(string body)
    {
        if (
            TryParseJsonObject(body, out var root)
            && root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
        )
        {
            return new WebFetchResult
            {
                Content = GetString(data, "content") ?? body,
                Title = GetString(data, "title"),
                Url = GetString(data, "url"),
                UsageTokens = GetUsageTokens(data),
                Warning = GetString(data, "warning"),
            };
        }

        return new WebFetchResult { Content = body };
    }

    /// <summary>
    ///     Tolerantly parses a Search response: maps each well-formed item in the <c>data</c> array,
    ///     skipping items missing a title or url. Missing/non-JSON bodies yield an empty result.
    /// </summary>
    private static WebSearchResult ParseSearchBody(string body)
    {
        var items = new List<WebSearchItem>();

        if (!TryParseJsonObject(body, out var root))
        {
            return new WebSearchResult { Items = items };
        }

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in data.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var title = GetString(element, "title");
                var url = GetString(element, "url");
                if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(url))
                {
                    continue;
                }

                items.Add(
                    new WebSearchItem
                    {
                        Title = title,
                        Url = url,
                        Snippet = FirstNonEmpty(element, "description", "snippet", "content"),
                    }
                );
            }
        }

        return new WebSearchResult { Items = items, UsageTokens = GetUsageTokens(root) };
    }

    /// <summary>
    ///     Attempts to parse <paramref name="body" /> as a JSON object, returning a detached clone
    ///     of the root element that remains valid after the backing document is disposed.
    /// </summary>
    private static bool TryParseJsonObject(string body, out JsonElement root)
    {
        root = default;
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            root = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Returns the string value of <paramref name="propertyName" /> when present and a JSON
    ///     string; otherwise <c>null</c>.
    /// </summary>
    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    /// <summary>
    ///     Returns the first non-empty string value among <paramref name="propertyNames" />.
    /// </summary>
    private static string? FirstNonEmpty(JsonElement element, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            var value = GetString(element, name);
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    ///     Reads <c>usage.tokens</c> from <paramref name="element" /> when present as an integer.
    /// </summary>
    private static int? GetUsageTokens(JsonElement element)
    {
        return element.TryGetProperty("usage", out var usage)
            && usage.ValueKind == JsonValueKind.Object
            && usage.TryGetProperty("tokens", out var tokens)
            && tokens.ValueKind == JsonValueKind.Number
            && tokens.TryGetInt32(out var value)
            ? value
            : null;
    }
}
