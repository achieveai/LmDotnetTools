using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AchieveAi.LmDotnetTools.Misc.Storage;
using AchieveAi.LmDotnetTools.Misc.Configuration;
using AchieveAi.LmDotnetTools.Misc.Utils;

namespace AchieveAi.LmDotnetTools.Misc.Http;

/// <summary>
/// HttpMessageHandler that provides caching for HTTP requests and responses.
/// Caches based on URL + POST body content using SHA256 hashing.
/// </summary>
public class CachingHttpMessageHandler : DelegatingHandler
{
    private readonly IKvStore _cache;
    private readonly LlmCacheOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _semaphore;

    /// <summary>
    /// Initializes a new instance of the CachingHttpMessageHandler.
    /// </summary>
    /// <param name="cache">The cache store to use</param>
    /// <param name="options">Cache configuration options</param>
    /// <param name="innerHandler">The inner HTTP handler to delegate to when cache misses occur</param>
    /// <param name="logger">Optional logger for diagnostics</param>
    public CachingHttpMessageHandler(
        IKvStore cache,
        LlmCacheOptions options,
        HttpMessageHandler? innerHandler = null,
        ILogger? logger = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger.Instance;
        _semaphore = new SemaphoreSlim(1, 1);
        
        if (innerHandler != null)
        {
            InnerHandler = innerHandler;
        }
    }

    /// <summary>
    /// Processes HTTP requests with caching logic.
    /// </summary>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!_options.EnableCaching)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        // Only cache POST requests (which are typically LLM API calls)
        if (request.Method != HttpMethod.Post)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        try
        {
            // Generate cache key from URL and POST body
            var cacheKey = await GenerateCacheKeyAsync(request, cancellationToken);
            
            // Try to get from cache first
            var cachedResponse = await GetFromCacheAsync(cacheKey, cancellationToken);
            if (cachedResponse != null)
            {
                _logger.LogDebug("Cache hit for key: {CacheKey}", cacheKey);
                return cachedResponse;
            }

            _logger.LogDebug("Cache miss for key: {CacheKey}", cacheKey);

            // Not in cache, make the actual request
            var response = await base.SendAsync(request, cancellationToken);

            // Cache the response if it's successful
            if (response.IsSuccessStatusCode)
            {
                await CacheResponseAsync(cacheKey, response, cancellationToken);
            }

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in caching logic, falling back to direct request");
            return await base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// Generates a cache key from the HTTP request URL and POST body.
    /// </summary>
    private async Task<string> GenerateCacheKeyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var keyBuilder = new StringBuilder();
        
        // Add URL
        keyBuilder.Append(request.RequestUri?.ToString() ?? "");
        
        // Add POST body if present
        if (request.Content != null)
        {
            var content = await request.Content.ReadAsStringAsync(cancellationToken);
            keyBuilder.Append(content);
        }

        // Add relevant headers that might affect the response
        if (request.Headers.Authorization != null)
        {
            keyBuilder.Append($"auth:{request.Headers.Authorization.Scheme}");
            // Don't include the actual token for security
        }

        var keyString = keyBuilder.ToString();
        
        // Generate SHA256 hash
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(keyString));
        return Convert.ToHexString(hashBytes);
    }

    /// <summary>
    /// Retrieves a cached response if it exists and is not expired.
    /// </summary>
    private async Task<HttpResponseMessage?> GetFromCacheAsync(string cacheKey, CancellationToken cancellationToken)
    {
        try
        {
            var cachedItem = await _cache.GetAsync<CachedHttpResponse>(cacheKey, cancellationToken);
            if (cachedItem == null)
            {
                return null;
            }

            // Check if expired
            if (DateTime.UtcNow > cachedItem.ExpiresAt)
            {
                // Item is expired, don't return it
                // Note: We don't delete expired items from cache here to keep IKvStore interface simple
                // Expired items will be cleaned up by cache maintenance routines
                return null;
            }

            // Reconstruct HttpResponseMessage
            var response = new HttpResponseMessage((System.Net.HttpStatusCode)cachedItem.StatusCode)
            {
                Content = new StringContent(cachedItem.Content, Encoding.UTF8, cachedItem.ContentType),
                ReasonPhrase = cachedItem.ReasonPhrase
            };

            // Add headers
            foreach (var header in cachedItem.Headers)
            {
                response.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error retrieving from cache: {CacheKey}", cacheKey);
            return null;
        }
    }

    /// <summary>
    /// Caches an HTTP response.
    /// </summary>
    private async Task CacheResponseAsync(string cacheKey, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            // Read response content
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            
            // Create cached item
            var cachedItem = new CachedHttpResponse
            {
                StatusCode = (int)response.StatusCode,
                ReasonPhrase = response.ReasonPhrase,
                Content = content,
                ContentType = response.Content.Headers.ContentType?.MediaType ?? "application/json",
                Headers = response.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray()),
                CachedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(_options.CacheExpiration ?? TimeSpan.FromHours(24))
            };

            // Store in cache (fire and forget to not slow down the response)
            _ = Task.Run(async () =>
            {
                try
                {
                    await _semaphore.WaitAsync(CancellationToken.None);
                    try
                    {
                        await _cache.SetAsync(cacheKey, cachedItem, CancellationToken.None);
                        _logger.LogDebug("Cached response for key: {CacheKey}", cacheKey);
                    }
                    finally
                    {
                        _semaphore.Release();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to cache response: {CacheKey}", cacheKey);
                }
            }, CancellationToken.None);

            // Reset content stream position so it can be read again
            if (response.Content is StringContent)
            {
                response.Content = new StringContent(content, Encoding.UTF8, 
                    response.Content.Headers.ContentType?.MediaType ?? "application/json");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error caching response: {CacheKey}", cacheKey);
        }
    }

    /// <summary>
    /// Disposes the handler and its resources.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _semaphore?.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Represents a cached HTTP response.
/// </summary>
public class CachedHttpResponse
{
    /// <summary>
    /// HTTP status code of the response.
    /// </summary>
    public int StatusCode { get; set; }

    /// <summary>
    /// HTTP reason phrase of the response.
    /// </summary>
    public string? ReasonPhrase { get; set; }

    /// <summary>
    /// Content of the response as a string.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Content type of the response.
    /// </summary>
    public string ContentType { get; set; } = "application/json";

    /// <summary>
    /// HTTP headers of the response.
    /// </summary>
    public Dictionary<string, string[]> Headers { get; set; } = new();

    /// <summary>
    /// When the response was cached.
    /// </summary>
    public DateTime CachedAt { get; set; }

    /// <summary>
    /// When the cached response expires.
    /// </summary>
    public DateTime ExpiresAt { get; set; }
} 