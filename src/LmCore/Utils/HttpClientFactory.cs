using AchieveAi.LmDotnetTools.LmCore.Http;
using AchieveAi.LmDotnetTools.LmCore.Validation;

namespace AchieveAi.LmDotnetTools.LmCore.Utils;

/// <summary>
/// Shared utility for creating HTTP clients with consistent configuration across providers.
/// </summary>
public static class HttpClientFactory
{
    /// <summary>
    /// Default timeout for HTTP clients (5 minutes, matching existing provider behavior).
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Creates an HTTP client with API key authentication and consistent configuration.
    /// </summary>
    /// <param name="apiKey">The API key for authentication</param>
    /// <param name="baseUrl">The base URL for the API</param>
    /// <param name="timeout">Optional timeout (defaults to 5 minutes)</param>
    /// <param name="headers">Optional additional headers</param>
    /// <param name="apiKeyHeaderName">The header name for the API key (defaults to "Authorization")</param>
    /// <param name="apiKeyPrefix">The prefix for the API key value (defaults to "Bearer ")</param>
    /// <returns>Configured HTTP client</returns>
    public static HttpClient CreateWithApiKey(
        string apiKey,
        string baseUrl,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? headers = null,
        string apiKeyHeaderName = "Authorization",
        string apiKeyPrefix = "Bearer "
    )
    {
        ArgumentNullException.ThrowIfNull(baseUrl);

        ValidationHelper.ValidateApiKey(apiKey, nameof(apiKey));
        ValidationHelper.ValidateBaseUrl(baseUrl, nameof(baseUrl));

        var httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/')),
            Timeout = timeout ?? DefaultTimeout,
        };

        // Add API key header
        httpClient.DefaultRequestHeaders.Add(apiKeyHeaderName, $"{apiKeyPrefix}{apiKey}");

        // Add additional headers if provided
        if (headers != null)
        {
            foreach (var header in headers)
            {
                httpClient.DefaultRequestHeaders.Add(header.Key, header.Value);
            }
        }

        return httpClient;
    }

    /// <summary>
    /// Creates an HTTP client for OpenAI-compatible APIs.
    /// </summary>
    /// <param name="apiKey">The API key for authentication</param>
    /// <param name="baseUrl">The base URL for the API</param>
    /// <param name="timeout">Optional timeout (defaults to 5 minutes)</param>
    /// <param name="headers">Optional additional headers</param>
    /// <returns>Configured HTTP client for OpenAI-compatible APIs</returns>
    public static HttpClient CreateForOpenAI(
        string apiKey,
        string baseUrl,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? headers = null
    )
    {
        return CreateWithApiKey(apiKey, baseUrl, timeout, headers, "Authorization", "Bearer ");
    }

    /// <summary>
    /// Creates an HTTP client for Anthropic APIs.
    /// </summary>
    /// <param name="apiKey">The API key for authentication</param>
    /// <param name="baseUrl">The base URL for the API (defaults to Anthropic API)</param>
    /// <param name="timeout">Optional timeout (defaults to 5 minutes)</param>
    /// <param name="headers">Optional additional headers</param>
    /// <returns>Configured HTTP client for Anthropic APIs</returns>
    public static HttpClient CreateForAnthropic(
        string apiKey,
        string baseUrl = "https://api.anthropic.com",
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? headers = null
    )
    {
        // Anthropic uses x-api-key header
        return CreateWithApiKey(apiKey, baseUrl, timeout, headers, "x-api-key", "");
    }

    /// <summary>
    /// Creates an HTTP client using HttpConfiguration settings.
    /// </summary>
    /// <param name="apiKey">The API key for authentication</param>
    /// <param name="baseUrl">The base URL for the API</param>
    /// <param name="configuration">HTTP configuration settings</param>
    /// <param name="headers">Optional additional headers</param>
    /// <param name="apiKeyHeaderName">The header name for the API key (defaults to "Authorization")</param>
    /// <param name="apiKeyPrefix">The prefix for the API key value (defaults to "Bearer ")</param>
    /// <returns>Configured HTTP client</returns>
    public static HttpClient CreateWithConfiguration(
        string apiKey,
        string baseUrl,
        HttpConfiguration configuration,
        IReadOnlyDictionary<string, string>? headers = null,
        string apiKeyHeaderName = "Authorization",
        string apiKeyPrefix = "Bearer "
    )
    {
        ArgumentNullException.ThrowIfNull(configuration);

        configuration.Validate();

        return CreateWithApiKey(apiKey, baseUrl, configuration.Timeout, headers, apiKeyHeaderName, apiKeyPrefix);
    }

    /// <summary>
    /// Creates an HTTP client from provider connection information.
    /// </summary>
    /// <param name="apiKey">The API key for authentication</param>
    /// <param name="endpointUrl">The endpoint URL</param>
    /// <param name="connectionHeaders">Optional connection headers</param>
    /// <param name="timeout">Optional timeout</param>
    /// <param name="compatibility">Provider compatibility type</param>
    /// <returns>Configured HTTP client</returns>
    public static HttpClient CreateFromConnectionInfo(
        string apiKey,
        string endpointUrl,
        IReadOnlyDictionary<string, string>? connectionHeaders = null,
        TimeSpan? timeout = null,
        string compatibility = "OpenAI"
    )
    {
        ArgumentNullException.ThrowIfNull(compatibility);

        return compatibility.ToUpperInvariant() switch
        {
            "ANTHROPIC" => CreateForAnthropic(apiKey, endpointUrl, timeout, connectionHeaders),
            "OPENAI" or _ => CreateForOpenAI(apiKey, endpointUrl, timeout, connectionHeaders),
        };
    }
}
