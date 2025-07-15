using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AchieveAi.LmDotnetTools.Misc.Storage;
using AchieveAi.LmDotnetTools.Misc.Configuration;
using AchieveAi.LmDotnetTools.Misc.Utils;

namespace AchieveAi.LmDotnetTools.Misc.Http;

/// <summary>
/// Factory for creating HttpClient instances with caching support.
/// </summary>
public static class CachingHttpClientFactory
{
    /// <summary>
    /// Creates an HttpClient with caching support using the provided cache and options.
    /// </summary>
    /// <param name="cache">The cache store to use</param>
    /// <param name="options">Cache configuration options</param>
    /// <param name="innerHandler">Optional inner handler to use (defaults to HttpClientHandler)</param>
    /// <param name="logger">Optional logger for diagnostics</param>
    /// <returns>HttpClient with caching enabled</returns>
    public static HttpClient CreateWithCache(
        IKvStore cache,
        LlmCacheOptions options,
        HttpMessageHandler? innerHandler = null,
        ILogger? logger = null)
    {
        var cachingHandler = new CachingHttpMessageHandler(
            cache,
            options,
            innerHandler ?? new HttpClientHandler(),
            logger);

        return new HttpClient(cachingHandler);
    }

    /// <summary>
    /// Creates an HttpClient with caching and API key authentication for OpenAI-compatible APIs.
    /// </summary>
    /// <param name="apiKey">The API key for authentication</param>
    /// <param name="baseUrl">The base URL for the API</param>
    /// <param name="cache">The cache store to use</param>
    /// <param name="options">Cache configuration options</param>
    /// <param name="timeout">Optional timeout (defaults to 5 minutes)</param>
    /// <param name="headers">Optional additional headers</param>
    /// <param name="logger">Optional logger for diagnostics</param>
    /// <returns>Configured HttpClient with caching and OpenAI authentication</returns>
    public static HttpClient CreateForOpenAIWithCache(
        string apiKey,
        string baseUrl,
        IKvStore cache,
        LlmCacheOptions options,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? headers = null,
        ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API key cannot be null or empty", nameof(apiKey));
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("Base URL cannot be null or empty", nameof(baseUrl));

        var cachingHandler = new CachingHttpMessageHandler(
            cache,
            options,
            new HttpClientHandler(),
            logger);

        var httpClient = new HttpClient(cachingHandler)
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/')),
            Timeout = timeout ?? TimeSpan.FromMinutes(5)
        };

        // Add API key header
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

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
    /// Creates an HttpClient with caching and API key authentication for Anthropic APIs.
    /// </summary>
    /// <param name="apiKey">The API key for authentication</param>
    /// <param name="baseUrl">The base URL for the API (defaults to Anthropic API)</param>
    /// <param name="cache">The cache store to use</param>
    /// <param name="options">Cache configuration options</param>
    /// <param name="timeout">Optional timeout (defaults to 5 minutes)</param>
    /// <param name="headers">Optional additional headers</param>
    /// <param name="logger">Optional logger for diagnostics</param>
    /// <returns>Configured HttpClient with caching and Anthropic authentication</returns>
    public static HttpClient CreateForAnthropicWithCache(
        string apiKey,
        string baseUrl,
        IKvStore cache,
        LlmCacheOptions options,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? headers = null,
        ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API key cannot be null or empty", nameof(apiKey));
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("Base URL cannot be null or empty", nameof(baseUrl));

        var cachingHandler = new CachingHttpMessageHandler(
            cache,
            options,
            new HttpClientHandler(),
            logger);

        var httpClient = new HttpClient(cachingHandler)
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/')),
            Timeout = timeout ?? TimeSpan.FromMinutes(5)
        };

        // Add API key header (Anthropic uses x-api-key)
        httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);

        // Add Anthropic version header
        httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

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
    /// Creates an HttpClient with caching from provider connection information.
    /// </summary>
    /// <param name="apiKey">The API key for authentication</param>
    /// <param name="endpointUrl">The endpoint URL</param>
    /// <param name="cache">The cache store to use</param>
    /// <param name="options">Cache configuration options</param>
    /// <param name="connectionHeaders">Optional connection headers</param>
    /// <param name="timeout">Optional timeout</param>
    /// <param name="compatibility">Provider compatibility type</param>
    /// <param name="logger">Optional logger for diagnostics</param>
    /// <returns>Configured HttpClient with caching</returns>
    public static HttpClient CreateFromConnectionInfoWithCache(
        string apiKey,
        string endpointUrl,
        IKvStore cache,
        LlmCacheOptions options,
        IReadOnlyDictionary<string, string>? connectionHeaders = null,
        TimeSpan? timeout = null,
        string compatibility = "OpenAI",
        ILogger? logger = null)
    {
        return compatibility.ToUpperInvariant() switch
        {
            "ANTHROPIC" => CreateForAnthropicWithCache(apiKey, endpointUrl, cache, options, timeout, connectionHeaders, logger),
            "OPENAI" or _ => CreateForOpenAIWithCache(apiKey, endpointUrl, cache, options, timeout, connectionHeaders, logger)
        };
    }

    /// <summary>
    /// Wraps an existing HttpClient with caching capabilities.
    /// </summary>
    /// <param name="existingClient">The existing HttpClient to wrap</param>
    /// <param name="cache">The cache store to use</param>
    /// <param name="options">Cache configuration options</param>
    /// <param name="logger">Optional logger for diagnostics</param>
    /// <returns>New HttpClient with caching that uses the same configuration as the original</returns>
    /// <remarks>
    /// This method creates a new HttpClient that mimics the configuration of the existing one
    /// but adds caching capabilities. The original client is not modified.
    /// </remarks>
    public static HttpClient WrapWithCache(
        HttpClient existingClient,
        IKvStore cache,
        LlmCacheOptions options,
        ILogger? logger = null)
    {
        var cachingHandler = new CachingHttpMessageHandler(
            cache,
            options,
            new HttpClientHandler(),
            logger);

        var cachedClient = new HttpClient(cachingHandler)
        {
            BaseAddress = existingClient.BaseAddress,
            Timeout = existingClient.Timeout
        };

        // Copy headers from existing client
        foreach (var header in existingClient.DefaultRequestHeaders)
        {
            cachedClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        }

        return cachedClient;
    }
} 