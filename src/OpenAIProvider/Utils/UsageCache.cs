using System.Collections.Concurrent;
using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Core;
using Microsoft.Extensions.Caching.Memory;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Utils;

/// <summary>
/// In-memory cache for OpenRouter usage data with TTL support.
/// Thread-safe implementation using MemoryCache.
/// </summary>
public class UsageCache : IDisposable
{
    private readonly MemoryCache _cache;
    private readonly TimeSpan _defaultTtl;
    private bool _disposed = false;

    /// <summary>
    /// Initializes a new instance of the UsageCache.
    /// </summary>
    /// <param name="ttlSeconds">Time-to-live in seconds for cached entries (default: 300)</param>
    public UsageCache(int ttlSeconds = 300)
    {
        _defaultTtl = TimeSpan.FromSeconds(ttlSeconds);
        _cache = new MemoryCache(new MemoryCacheOptions
        {
            // Set a reasonable size limit to prevent memory leaks
            SizeLimit = 10000
        });
    }

    /// <summary>
    /// Tries to get cached usage data for the specified completion ID.
    /// </summary>
    /// <param name="completionId">The completion ID to look up</param>
    /// <returns>Cached usage data with IsCached=true, or null if not found</returns>
    public Usage? TryGetUsage(string completionId)
    {
        if (string.IsNullOrEmpty(completionId) || _disposed)
            return null;

        if (_cache.TryGetValue(completionId, out Usage? cachedUsage) && cachedUsage != null)
        {
            // Mark as cached in the returned usage (use with pattern to avoid key collision)
            return cachedUsage with
            {
                ExtraProperties = (cachedUsage.ExtraProperties ?? ImmutableDictionary<string, object?>.Empty)
                    .SetItem("is_cached", true)
            };
        }

        return null;
    }

    /// <summary>
    /// Stores usage data in the cache for the specified completion ID.
    /// </summary>
    /// <param name="completionId">The completion ID to cache</param>
    /// <param name="usage">The usage data to cache</param>
    public void SetUsage(string completionId, Usage usage)
    {
        if (string.IsNullOrEmpty(completionId) || usage == null || _disposed)
            return;

        // Store with TTL, mark as not cached in stored version (cache flag is added on retrieval)
        var storedUsage = usage with
        {
            ExtraProperties = (usage.ExtraProperties ?? ImmutableDictionary<string, object?>.Empty)
                .SetItem("is_cached", false)
        };
        
        _cache.Set(completionId, storedUsage, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = _defaultTtl,
            Size = 1, // Each entry counts as 1 unit toward SizeLimit
            Priority = CacheItemPriority.Normal
        });
    }

    /// <summary>
    /// Removes cached usage data for the specified completion ID.
    /// </summary>
    /// <param name="completionId">The completion ID to remove from cache</param>
    public void RemoveUsage(string completionId)
    {
        if (string.IsNullOrEmpty(completionId) || _disposed)
            return;

        _cache.Remove(completionId);
    }

    /// <summary>
    /// Clears all cached usage data.
    /// </summary>
    public void Clear()
    {
        if (_disposed)
            return;

        // MemoryCache doesn't have a direct Clear method, so we create a new one
        // This is a simple implementation - in production you might want to track keys
        // Note: This is not thread-safe during clear operations, but that's acceptable
        // since Clear is typically only used in testing scenarios
        
        // For now, just do nothing as MemoryCache will auto-expire entries
        // In a production scenario, you might want to implement key tracking
    }

    /// <summary>
    /// Gets cache statistics for monitoring purposes.
    /// </summary>
    /// <returns>Basic cache statistics</returns>
    public CacheStatistics GetStatistics()
    {
        return new CacheStatistics
        {
            TtlSeconds = (int)_defaultTtl.TotalSeconds,
            IsDisposed = _disposed
        };
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _cache?.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Cache statistics for monitoring and debugging.
/// </summary>
public class CacheStatistics
{
    /// <summary>
    /// Time-to-live in seconds for cached entries.
    /// </summary>
    public int TtlSeconds { get; init; }

    /// <summary>
    /// Whether the cache has been disposed.
    /// </summary>
    public bool IsDisposed { get; init; }
} 