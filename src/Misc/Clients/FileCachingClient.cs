using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.Misc.Configuration;
using AchieveAi.LmDotnetTools.Misc.Storage;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;

namespace AchieveAi.LmDotnetTools.Misc.Clients;

/// <summary>
/// A caching wrapper for IOpenClient that stores responses in files using SHA256-based filenames.
/// Provides transparent caching of LLM requests and responses with configurable cache management.
/// </summary>
public class FileCachingClient : IOpenClient, IDisposable
{
    private readonly IOpenClient _innerClient;
    private readonly FileKvStore _cache;
    private readonly LlmCacheOptions _options;
    private readonly JsonSerializerOptions _jsonOptions;
    private bool _disposed = false;

    /// <summary>
    /// Creates a new FileCachingClient that wraps the specified client with file-based caching.
    /// </summary>
    /// <param name="innerClient">The client to wrap with caching</param>
    /// <param name="cache">The file-based key-value store for caching</param>
    /// <param name="options">Cache configuration options</param>
    /// <param name="jsonOptions">JSON serialization options (optional)</param>
    public FileCachingClient(
        IOpenClient innerClient, 
        FileKvStore cache, 
        LlmCacheOptions options,
        JsonSerializerOptions? jsonOptions = null)
    {
        _innerClient = innerClient ?? throw new ArgumentNullException(nameof(innerClient));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _jsonOptions = jsonOptions ?? new JsonSerializerOptions 
        { 
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase 
        };

        // Validate options
        var validationErrors = _options.Validate();
        if (validationErrors.Any())
        {
            throw new ArgumentException($"Invalid cache options: {string.Join(", ", validationErrors)}");
        }

        // Cleanup expired cache if enabled
        if (_options.CleanupOnStartup)
        {
            _ = Task.Run(CleanupExpiredCacheAsync);
        }
    }

    /// <summary>
    /// Gets the cache directory being used by this client.
    /// </summary>
    public string CacheDirectory => _cache.CacheDirectory;

    /// <summary>
    /// Gets the cache options being used by this client.
    /// </summary>
    public LlmCacheOptions Options => _options;

    /// <inheritdoc/>
    public async Task<ChatCompletionResponse> CreateChatCompletionsAsync(
        ChatCompletionRequest chatCompletionRequest, 
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        if (!_options.EnableCaching)
        {
            return await _innerClient.CreateChatCompletionsAsync(chatCompletionRequest, cancellationToken);
        }

        var cacheKey = GenerateCacheKey(chatCompletionRequest);

        // Try to get from cache first
        var cachedEntry = await _cache.GetAsync<CachedResponse>(cacheKey, cancellationToken);
        if (cachedEntry != null && !IsExpired(cachedEntry))
        {
            return cachedEntry.Response;
        }

        // Cache miss - call the inner client
        var response = await _innerClient.CreateChatCompletionsAsync(chatCompletionRequest, cancellationToken);

        // Cache the response (fire and forget)
        _ = Task.Run(async () =>
        {
            try
            {
                var cacheEntry = new CachedResponse
                {
                    Response = response,
                    CachedAt = DateTime.UtcNow
                };
                await _cache.SetAsync(cacheKey, cacheEntry, CancellationToken.None);
            }
            catch
            {
                // Ignore caching errors - don't fail the actual request
            }
        }, CancellationToken.None);

        return response;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatCompletionResponse> StreamingChatCompletionsAsync(
        ChatCompletionRequest chatCompletionRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        if (!_options.EnableCaching)
        {
            await foreach (var response in _innerClient.StreamingChatCompletionsAsync(chatCompletionRequest, cancellationToken))
            {
                yield return response;
            }
            yield break;
        }

        var cacheKey = GenerateCacheKey(chatCompletionRequest);

        // Try to get from cache first
        var cachedEntry = await _cache.GetAsync<CachedStreamingResponse>(cacheKey, cancellationToken);
        if (cachedEntry != null && !IsExpired(cachedEntry))
        {
            foreach (var response in cachedEntry.Responses)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                // Add a small delay to simulate streaming behavior
                await Task.Delay(10, cancellationToken);
                yield return response;
            }
            yield break;
        }

        // Cache miss - call the inner client and collect responses
        var responses = new List<ChatCompletionResponse>();
        await foreach (var response in _innerClient.StreamingChatCompletionsAsync(chatCompletionRequest, cancellationToken))
        {
            responses.Add(response);
            yield return response;
        }

        // Cache the collected responses (fire and forget)
        _ = Task.Run(async () =>
        {
            try
            {
                var cacheEntry = new CachedStreamingResponse
                {
                    Responses = responses,
                    CachedAt = DateTime.UtcNow
                };
                await _cache.SetAsync(cacheKey, cacheEntry, CancellationToken.None);
            }
            catch
            {
                // Ignore caching errors - don't fail the actual request
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Clears all cached responses.
    /// </summary>
    public async Task ClearCacheAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _cache.ClearAsync(cancellationToken);
    }

    /// <summary>
    /// Gets the current number of cached items.
    /// </summary>
    public async Task<int> GetCacheCountAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await _cache.GetCountAsync(cancellationToken);
    }

    /// <summary>
    /// Generates a cache key for the given request using SHA256 hash.
    /// </summary>
    private string GenerateCacheKey(ChatCompletionRequest request)
    {
        var requestJson = JsonSerializer.Serialize(request, _jsonOptions);
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(requestJson));
        return Convert.ToBase64String(hashBytes);
    }

    /// <summary>
    /// Checks if a cached entry has expired based on the cache options.
    /// </summary>
    private bool IsExpired(CachedResponseBase entry)
    {
        if (!_options.CacheExpiration.HasValue)
        {
            return false; // Never expires
        }

        var expirationTime = entry.CachedAt.Add(_options.CacheExpiration.Value);
        return DateTime.UtcNow > expirationTime;
    }

    /// <summary>
    /// Cleans up expired cache entries in the background.
    /// </summary>
    private async Task CleanupExpiredCacheAsync()
    {
        try
        {
            if (!_options.CacheExpiration.HasValue)
            {
                return; // No expiration, nothing to clean up
            }

            var keys = new List<string>();
            await foreach (var key in await _cache.EnumerateKeysAsync())
            {
                keys.Add(key);
            }

            foreach (var key in keys)
            {
                try
                {
                    // Try both cached response types
                    var cachedResponse = await _cache.GetAsync<CachedResponse>(key);
                    if (cachedResponse != null && IsExpired(cachedResponse))
                    {
                        // Expired entry found, but we can't delete individual keys with current IKvStore interface
                        // This would require extending the interface or using direct file operations
                        continue;
                    }

                    var cachedStreamingResponse = await _cache.GetAsync<CachedStreamingResponse>(key);
                    if (cachedStreamingResponse != null && IsExpired(cachedStreamingResponse))
                    {
                        // Expired entry found
                        continue;
                    }
                }
                catch
                {
                    // Continue with other keys if one fails
                }
            }
        }
        catch
        {
            // Ignore cleanup errors - it's a background operation
        }
    }

    /// <summary>
    /// Throws ObjectDisposedException if the client has been disposed.
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(FileCachingClient));
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _innerClient?.Dispose();
            _cache?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}

/// <summary>
/// Base class for cached response entries.
/// </summary>
public abstract class CachedResponseBase
{
    /// <summary>
    /// Gets or sets the timestamp when this entry was cached.
    /// </summary>
    public DateTime CachedAt { get; set; }
}

/// <summary>
/// Represents a cached non-streaming response.
/// </summary>
public class CachedResponse : CachedResponseBase
{
    /// <summary>
    /// Gets or sets the cached response.
    /// </summary>
    public ChatCompletionResponse Response { get; set; } = null!;
}

/// <summary>
/// Represents a cached streaming response.
/// </summary>
public class CachedStreamingResponse : CachedResponseBase
{
    /// <summary>
    /// Gets or sets the list of cached streaming responses.
    /// </summary>
    public List<ChatCompletionResponse> Responses { get; set; } = new();
} 