using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AchieveAi.LmDotnetTools.Misc.Storage;
using AchieveAi.LmDotnetTools.Misc.Configuration;
using AchieveAi.LmDotnetTools.Misc.Utils;
using System.Net;

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
                CacheResponseAsync(cacheKey, response);
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
    /// Wraps the HTTP response content with streaming caching capability.
    /// </summary>
    private void CacheResponseAsync(string cacheKey, HttpResponseMessage response)
    {
        try
        {
            // Wrap the original content with streaming caching capability
            if (response.Content != null)
            {
                response.Content = new CachingHttpContent(
                    response.Content,
                    cacheKey,
                    _cache,
                    _options,
                    _logger,
                    _semaphore);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error setting up streaming cache for: {CacheKey}", cacheKey);
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

/// <summary>
/// HttpContent wrapper that enables streaming with concurrent caching.
/// </summary>
public class CachingHttpContent : HttpContent
{
    private readonly HttpContent _originalContent;
    private readonly string _cacheKey;
    private readonly IKvStore _cache;
    private readonly LlmCacheOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _semaphore;

    public CachingHttpContent(
        HttpContent originalContent,
        string cacheKey,
        IKvStore cache,
        LlmCacheOptions options,
        ILogger logger,
        SemaphoreSlim semaphore)
    {
        _originalContent = originalContent ?? throw new ArgumentNullException(nameof(originalContent));
        _cacheKey = cacheKey ?? throw new ArgumentNullException(nameof(cacheKey));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _semaphore = semaphore ?? throw new ArgumentNullException(nameof(semaphore));

        // Copy headers from original content
        foreach (var header in _originalContent.Headers)
        {
            Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    protected override async Task<Stream> CreateContentReadStreamAsync()
    {
        var originalStream = await _originalContent.ReadAsStreamAsync();
        return new CachingStream(originalStream, _cacheKey, _cache, _options, _logger, _semaphore);
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        using var contentStream = await CreateContentReadStreamAsync();
        await contentStream.CopyToAsync(stream);
    }

    protected override bool TryComputeLength(out long length)
    {
        if (_originalContent.Headers.ContentLength.HasValue)
        {
            length = _originalContent.Headers.ContentLength.Value;
            return length >= 0;
        }
        
        length = 0;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _originalContent?.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Stream wrapper that captures data as it's read for caching purposes.
/// </summary>
public class CachingStream : Stream
{
    private readonly Stream _originalStream;
    private readonly string _cacheKey;
    private readonly IKvStore _cache;
    private readonly LlmCacheOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _semaphore;
    private readonly MemoryStream _buffer;
    private readonly object _lock = new();
    private bool _disposed;
    private bool _cacheAttempted;
    private Task? _cachingTask;

    public CachingStream(
        Stream originalStream,
        string cacheKey,
        IKvStore cache,
        LlmCacheOptions options,
        ILogger logger,
        SemaphoreSlim semaphore)
    {
        _originalStream = originalStream ?? throw new ArgumentNullException(nameof(originalStream));
        _cacheKey = cacheKey ?? throw new ArgumentNullException(nameof(cacheKey));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _semaphore = semaphore ?? throw new ArgumentNullException(nameof(semaphore));
        _buffer = new MemoryStream();
    }

    public override bool CanRead => _originalStream.CanRead;
    public override bool CanSeek => false; // Don't allow seeking to keep it simple
    public override bool CanWrite => false;
    public override long Length => _originalStream.Length;
    public override long Position 
    { 
        get => _originalStream.Position; 
        set => throw new NotSupportedException("Seeking is not supported"); 
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var bytesRead = await _originalStream.ReadAsync(buffer, offset, count, cancellationToken);
        
        if (bytesRead > 0)
        {
            lock (_lock)
            {
                if (!_disposed)
                {
                    // Append read data to our cache buffer
                    _buffer.Write(buffer, offset, bytesRead);
                }
            }
        }
        else if (bytesRead == 0 && !_cacheAttempted)
        {
            // End of stream reached, trigger caching
            _cachingTask = Task.Run(TryCacheDataAsync, CancellationToken.None);
        }

        return bytesRead;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var bytesRead = _originalStream.Read(buffer, offset, count);
        
        if (bytesRead > 0)
        {
            lock (_lock)
            {
                if (!_disposed)
                {
                    _buffer.Write(buffer, offset, bytesRead);
                }
            }
        }
        else if (bytesRead == 0 && !_cacheAttempted)
        {
            _cachingTask = Task.Run(TryCacheDataAsync, CancellationToken.None);
        }

        return bytesRead;
    }

    private async Task TryCacheDataAsync()
    {
        if (_cacheAttempted)
            return;

        _cacheAttempted = true;

        try
        {
            byte[] data;
            lock (_lock)
            {
                // Don't return early if disposed - we can still cache the data
                data = _buffer.ToArray();
            }

            if (data.Length == 0)
            {
                _logger.LogDebug("No data to cache for key: {CacheKey}", _cacheKey);
                return;
            }

            // Convert byte array to string for storage
            var content = Encoding.UTF8.GetString(data);
            
            var cachedItem = new CachedHttpResponse
            {
                StatusCode = 200, // We only cache successful responses
                Content = content,
                ContentType = "application/json", // Default assumption for LLM APIs
                Headers = new Dictionary<string, string[]>(),
                CachedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(_options.CacheExpiration ?? TimeSpan.FromHours(24))
            };

            await _semaphore.WaitAsync(CancellationToken.None);
            try
            {
                await _cache.SetAsync(_cacheKey, cachedItem, CancellationToken.None);
                _logger.LogDebug("Successfully cached streaming response for key: {CacheKey}, size: {Size} bytes", 
                    _cacheKey, data.Length);
            }
            finally
            {
                _semaphore.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cache streaming response: {CacheKey}", _cacheKey);
            // Re-throw in debug builds to help with testing
            #if DEBUG
            throw;
            #endif
        }
    }

    public override void Flush() => _originalStream.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _originalStream.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => 
        throw new NotSupportedException("Seeking is not supported");

    public override void SetLength(long value) => 
        throw new NotSupportedException("SetLength is not supported");

    public override void Write(byte[] buffer, int offset, int count) => 
        throw new NotSupportedException("Writing is not supported");

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            Task? taskToWait = null;
            
            lock (_lock)
            {
                _disposed = true;
                
                // Trigger caching if we haven't already
                if (!_cacheAttempted)
                {
                    _cachingTask = Task.Run(TryCacheDataAsync, CancellationToken.None);
                }
                
                taskToWait = _cachingTask;
            }
            
            // Wait for the caching task to complete (but don't block indefinitely)
            try
            {
                taskToWait?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to wait for caching task completion during dispose");
            }
            
            _buffer?.Dispose();
            _originalStream?.Dispose();
        }
        base.Dispose(disposing);
    }
} 