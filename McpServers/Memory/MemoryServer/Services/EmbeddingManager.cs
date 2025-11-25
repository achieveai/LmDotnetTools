using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmEmbeddings.Interfaces;
using AchieveAi.LmDotnetTools.LmEmbeddings.Models;
using MemoryServer.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using EmbeddingOptions = MemoryServer.Models.EmbeddingOptions;

namespace MemoryServer.Services;

/// <summary>
///     Implementation of embedding manager with caching and batch processing capabilities.
///     Integrates with LmConfigService for embedding model selection and provides vector search functionality.
/// </summary>
public class EmbeddingManager : IEmbeddingManager
{
    private readonly IMemoryCache _cache;
    private readonly ILmConfigService _lmConfigService;
    private readonly ILogger<EmbeddingManager> _logger;
    private readonly IMemoryRepository _memoryRepository;
    private readonly EmbeddingOptions _options;
    private long _cacheHits;
    private long _cacheMisses;
    private int _cachedEmbeddingDimension;

    // Cached embedding service to avoid recreating
    private IEmbeddingService? _cachedEmbeddingService;
    private string? _cachedModelName;

    // Cache statistics
    private long _totalRequests;

    public EmbeddingManager(
        ILmConfigService lmConfigService,
        IMemoryRepository memoryRepository,
        IMemoryCache cache,
        ILogger<EmbeddingManager> logger,
        IOptions<MemoryServerOptions> options
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        _lmConfigService = lmConfigService;
        _memoryRepository = memoryRepository;
        _cache = cache;
        _logger = logger;
        _options = options.Value.Embedding;
    }

    /// <summary>
    ///     Gets the embedding dimension for the current model.
    /// </summary>
    public int EmbeddingDimension
    {
        get
        {
            EnsureEmbeddingServiceInitialized();
            return _cachedEmbeddingDimension;
        }
    }

    /// <summary>
    ///     Gets the current embedding model name.
    /// </summary>
    public string ModelName
    {
        get
        {
            EnsureEmbeddingServiceInitialized();
            return _cachedModelName ?? "unknown";
        }
    }

    /// <summary>
    ///     Generates an embedding for the given content with caching.
    /// </summary>
    public async Task<float[]> GenerateEmbeddingAsync(string content, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("Content cannot be empty", nameof(content));
        }

        _ = Interlocked.Increment(ref _totalRequests);

        // Check cache first
        var cacheKey = GenerateCacheKey(content);
        if (_cache.TryGetValue(cacheKey, out float[]? cachedEmbedding))
        {
            _ = Interlocked.Increment(ref _cacheHits);
            _logger.LogDebug("Cache hit for embedding generation");
            return cachedEmbedding!;
        }

        _ = Interlocked.Increment(ref _cacheMisses);

        // Generate new embedding
        try
        {
            var embeddingService = await GetEmbeddingServiceAsync(cancellationToken);
            var response = await embeddingService.GenerateEmbeddingAsync(content, ModelName, cancellationToken);

            if (response.Embeddings == null || response.Embeddings.Count == 0)
            {
                throw new InvalidOperationException("Embedding service returned no embeddings");
            }

            var embedding = response.Embeddings[0].Vector;

            // Cache the result
            var cacheOptions = new MemoryCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromHours(_options.CacheExpirationHours),
                Size = embedding.Length * sizeof(float),
            };
            _ = _cache.Set(cacheKey, embedding, cacheOptions);

            _logger.LogDebug("Generated and cached embedding for content (length: {Length})", content.Length);
            return embedding;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate embedding for content (length: {Length})", content.Length);
            throw;
        }
    }

    /// <summary>
    ///     Generates embeddings for multiple contents in a batch.
    /// </summary>
    public async Task<List<float[]>> GenerateBatchEmbeddingsAsync(
        List<string> contents,
        CancellationToken cancellationToken = default
    )
    {
        if (contents == null || contents.Count == 0)
        {
            return [];
        }

        var results = new List<float[]>();
        var uncachedContents = new List<(int index, string content)>();

        // Check cache for existing embeddings
        for (var i = 0; i < contents.Count; i++)
        {
            var content = contents[i];
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new ArgumentException($"Content at index {i} cannot be empty", nameof(contents));
            }

            _ = Interlocked.Increment(ref _totalRequests);

            var cacheKey = GenerateCacheKey(content);
            if (_cache.TryGetValue(cacheKey, out float[]? cachedEmbedding))
            {
                _ = Interlocked.Increment(ref _cacheHits);
                results.Add(cachedEmbedding!);
            }
            else
            {
                _ = Interlocked.Increment(ref _cacheMisses);
                uncachedContents.Add((i, content));
                results.Add(null!); // Placeholder
            }
        }

        // Generate embeddings for uncached content
        if (uncachedContents.Count != 0)
        {
            try
            {
                var embeddingService = await GetEmbeddingServiceAsync(cancellationToken);
                var uncachedTexts = uncachedContents.Select(x => x.content).ToList();

                var request = new EmbeddingRequest { Inputs = uncachedTexts, Model = ModelName };

                var response = await embeddingService.GenerateEmbeddingsAsync(request, cancellationToken);

                if (response.Embeddings == null || response.Embeddings.Count != uncachedTexts.Count)
                {
                    throw new InvalidOperationException("Embedding service returned unexpected number of embeddings");
                }

                // Update results and cache
                for (var i = 0; i < uncachedContents.Count; i++)
                {
                    var (index, content) = uncachedContents[i];
                    var embedding = response.Embeddings[i].Vector;

                    results[index] = embedding;

                    // Cache the result
                    var cacheKey = GenerateCacheKey(content);
                    var cacheOptions = new MemoryCacheEntryOptions
                    {
                        SlidingExpiration = TimeSpan.FromHours(_options.CacheExpirationHours),
                        Size = embedding.Length * sizeof(float),
                    };
                    _ = _cache.Set(cacheKey, embedding, cacheOptions);
                }

                _logger.LogDebug("Generated and cached {Count} embeddings in batch", uncachedContents.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to generate batch embeddings for {Count} contents",
                    uncachedContents.Count
                );
                throw;
            }
        }

        return results;
    }

    /// <summary>
    ///     Searches for similar content using vector similarity search.
    /// </summary>
    public async Task<List<MemorySearchResult>> SearchSimilarAsync(
        float[] queryEmbedding,
        SessionContext sessionContext,
        int limit = 10,
        float threshold = 0.7f,
        CancellationToken cancellationToken = default
    )
    {
        if (queryEmbedding == null || queryEmbedding.Length == 0)
        {
            throw new ArgumentException("Query embedding cannot be empty", nameof(queryEmbedding));
        }

        if (queryEmbedding.Length != EmbeddingDimension)
        {
            throw new ArgumentException(
                $"Query embedding dimension ({queryEmbedding.Length}) does not match expected dimension ({EmbeddingDimension})",
                nameof(queryEmbedding)
            );
        }

        try
        {
            _logger.LogDebug(
                "Performing vector similarity search with threshold {Threshold} and limit {Limit}",
                threshold,
                limit
            );

            // Use the repository's vector search functionality
            var searchResults = await _memoryRepository.SearchVectorAsync(
                queryEmbedding,
                sessionContext,
                limit,
                threshold,
                cancellationToken
            );

            var results = searchResults
                .Select(result => new MemorySearchResult
                {
                    Memory = result.Memory,
                    SimilarityScore = result.Score,
                    DistanceMetric = "cosine",
                })
                .ToList();

            _logger.LogInformation(
                "Vector similarity search returned {Count} results for session {SessionContext}",
                results.Count,
                sessionContext
            );
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to perform vector similarity search for session {SessionContext}",
                sessionContext
            );
            throw;
        }
    }

    /// <summary>
    ///     Clears the embedding cache.
    /// </summary>
    public void ClearCache()
    {
        if (_cache is MemoryCache memoryCache)
        {
            memoryCache.Clear();
            _logger.LogInformation("Embedding cache cleared");
        }
    }

    /// <summary>
    ///     Gets cache statistics.
    /// </summary>
    public EmbeddingCacheStats GetCacheStats()
    {
        var entryCount = 0;
        var estimatedMemoryUsage = 0L;

        // Try to get cache entry count (this is implementation-dependent)
        if (_cache is MemoryCache memoryCache)
        {
            var field = typeof(MemoryCache).GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field?.GetValue(memoryCache) is IDictionary entries)
            {
                entryCount = entries.Count;
                estimatedMemoryUsage = entryCount * EmbeddingDimension * sizeof(float); // Rough estimate
            }
        }

        return new EmbeddingCacheStats
        {
            TotalRequests = _totalRequests,
            CacheHits = _cacheHits,
            CacheMisses = _cacheMisses,
            EntryCount = entryCount,
            EstimatedMemoryUsage = estimatedMemoryUsage,
        };
    }

    /// <summary>
    ///     Ensures the embedding service is initialized and cached.
    /// </summary>
    private void EnsureEmbeddingServiceInitialized()
    {
        if (_cachedEmbeddingService == null)
        {
            // Initialize synchronously for property access
            var task = Task.Run(async () => await GetEmbeddingServiceAsync(CancellationToken.None));
            task.Wait();
        }
    }

    /// <summary>
    ///     Gets or creates the embedding service instance.
    /// </summary>
    private async Task<IEmbeddingService> GetEmbeddingServiceAsync(CancellationToken cancellationToken)
    {
        if (_cachedEmbeddingService != null)
        {
            return _cachedEmbeddingService;
        }

        _logger.LogDebug("Creating embedding service via LmConfigService");

        _cachedEmbeddingService = await _lmConfigService.CreateEmbeddingServiceAsync(cancellationToken);

        // Get model information from environment variables
        _cachedModelName = EnvironmentVariableHelper.GetEnvironmentVariableWithFallback(
            "EMBEDDING_MODEL",
            null,
            "text-embedding-3-small"
        );

        // Set embedding dimension from environment variable or based on model
        var embeddingSizeEnv = EnvironmentVariableHelper.GetEnvironmentVariableWithFallback("EMBEDDING_SIZE");
        if (
            !string.IsNullOrEmpty(embeddingSizeEnv)
            && int.TryParse(embeddingSizeEnv, out var customDimension)
            && customDimension > 0
        )
        {
            _cachedEmbeddingDimension = customDimension;
            _logger.LogInformation(
                "Using custom embedding dimension from EMBEDDING_SIZE: {Dimension}",
                customDimension
            );
        }
        else
        {
            // Fallback to model-based dimension
            _cachedEmbeddingDimension = _cachedModelName switch
            {
                "text-embedding-3-small" => 1536,
                "text-embedding-3-large" => 3072,
                "text-embedding-ada-002" => 1536,
                _ => 1536, // Default to OpenAI small dimension
            };
            _logger.LogDebug("Using model-based embedding dimension: {Dimension}", _cachedEmbeddingDimension);
        }

        _logger.LogInformation(
            "Embedding service initialized with model {ModelName} (dimension: {Dimension})",
            _cachedModelName,
            _cachedEmbeddingDimension
        );

        return _cachedEmbeddingService;
    }

    /// <summary>
    ///     Generates a cache key for the given content.
    /// </summary>
    private string GenerateCacheKey(string content)
    {
        // Use SHA256 hash of content + model name for cache key
        var input = $"{_cachedModelName ?? "default"}:{content}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return $"embedding:{Convert.ToHexString(hash)[..16]}"; // Use first 16 chars of hex
    }
}
