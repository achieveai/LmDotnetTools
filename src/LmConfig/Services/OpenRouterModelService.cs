using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmConfig.Capabilities;
using AchieveAi.LmDotnetTools.LmConfig.Models;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmConfig.Services;

/// <summary>
/// Service for discovering and caching OpenRouter model configurations.
/// Implements cache-first logic with background refresh capabilities.
/// </summary>
public class OpenRouterModelService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenRouterModelService> _logger;
    private readonly string _cacheFilePath;
    private readonly SemaphoreSlim _cacheSemaphore = new(1, 1);
    private readonly SemaphoreSlim _backgroundRefreshSemaphore = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions;

    private const string OpenRouterModelsUrl = "https://openrouter.ai/api/frontend/models";
    private const string OpenRouterStatsUrlTemplate =
        "https://openrouter.ai/api/frontend/stats/endpoint?permaslug={0}&variant=standard";
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromHours(24);

    // Retry and timeout constants
    private const int MaxRetries = 3;
    private const int MaxModelDetailRetries = 2;
    private const int HttpTimeoutSeconds = 30;
    private const int BackgroundRefreshTimeoutMinutes = 5;
    private const int CacheLoadTimeoutMs = 100;
    private const int CacheSaveTimeoutMs = 500;
    private const int ConcurrentRequestLimit = 5;
    private const int FileBufferSize = 65536;

    public OpenRouterModelService(HttpClient httpClient, ILogger<OpenRouterModelService> logger)
        : this(httpClient, logger, null) { }

    public OpenRouterModelService(
        HttpClient httpClient,
        ILogger<OpenRouterModelService> logger,
        string? cacheFilePath
    )
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Set up cache file path
        if (!string.IsNullOrEmpty(cacheFilePath))
        {
            var cacheDir = Path.GetDirectoryName(cacheFilePath);
            if (!string.IsNullOrEmpty(cacheDir))
            {
                Directory.CreateDirectory(cacheDir);
            }
            _cacheFilePath = cacheFilePath;
        }
        else
        {
            // Default to temp directory
            var tempDir = Path.Combine(Path.GetTempPath(), "LmDotnetTools");
            Directory.CreateDirectory(tempDir);
            _cacheFilePath = Path.Combine(tempDir, "openrouter-cache.json");
        }

        // Configure JSON serialization options for optimal performance
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false, // Disable indentation for smaller file size and faster serialization
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, // Use snake_case for consistency with API
            NumberHandling = JsonNumberHandling.AllowReadingFromString, // Handle number parsing robustly
            AllowTrailingCommas = true, // Be more forgiving when parsing
            ReadCommentHandling = JsonCommentHandling.Skip, // Skip comments if present
            UnknownTypeHandling = JsonUnknownTypeHandling.JsonNode, // Handle unknown types gracefully
            PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate, // Optimize object creation
        };

        // Configure HTTP client timeout
        _httpClient.Timeout = TimeSpan.FromSeconds(HttpTimeoutSeconds);
    }

    #region Helper Methods

    /// <summary>
    /// Safely extracts a string value from a JsonNode.
    /// </summary>
    private static string? GetStringValue(JsonNode? node, string propertyName)
    {
        return node?[propertyName]?.GetValue<string>();
    }

    /// <summary>
    /// Safely extracts an integer value from a JsonNode.
    /// </summary>
    private static int GetIntValue(JsonNode? node, string propertyName, int defaultValue = 0)
    {
        return node?[propertyName]?.GetValue<int>() ?? defaultValue;
    }

    /// <summary>
    /// Safely extracts a boolean value from a JsonNode.
    /// </summary>
    private static bool GetBoolValue(JsonNode? node, string propertyName, bool defaultValue = false)
    {
        return node?[propertyName]?.GetValue<bool>() ?? defaultValue;
    }

    /// <summary>
    /// Safely extracts a long value from a JsonNode.
    /// </summary>
    private static long? GetLongValue(JsonNode? node, string propertyName)
    {
        return node?[propertyName]?.GetValue<long>();
    }

    /// <summary>
    /// Safely extracts a string array from a JsonNode.
    /// </summary>
    private static string[] GetStringArray(JsonNode? node, string propertyName)
    {
        return node?[propertyName]?.AsArray()
                ?.Select(x => x?.GetValue<string>())
                .Where(x => !string.IsNullOrEmpty(x))
                .Cast<string>()
                .ToArray() ?? [];
    }

    /// <summary>
    /// Safely parses an ISO 8601 datetime string.
    /// </summary>
    private static DateTime? TryParseDateTime(string? dateTimeString)
    {
        if (string.IsNullOrWhiteSpace(dateTimeString))
        {
            return null;
        }

        return DateTime.TryParse(dateTimeString, null, DateTimeStyles.RoundtripKind, out var dateTime)
            ? dateTime.ToUniversalTime()
            : DateTimeOffset.TryParse(dateTimeString, out var dateTimeOffset) ? dateTimeOffset.UtcDateTime : null;
    }

    /// <summary>
    /// Generic method to execute operations with retry logic and exponential backoff.
    /// </summary>
    private async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> operation,
        int maxRetries,
        TimeSpan baseDelay,
        string operationName,
        CancellationToken cancellationToken
    )
    {
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                _logger.LogDebug(
                    "{OperationName} (attempt {Attempt}/{MaxRetries})",
                    operationName,
                    attempt + 1,
                    maxRetries + 1
                );
                return await operation();
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                _logger.LogWarning(
                    ex,
                    "Timeout in {OperationName} (attempt {Attempt}/{MaxRetries})",
                    operationName,
                    attempt + 1,
                    maxRetries + 1
                );
                if (attempt == maxRetries)
                {
                    throw new HttpRequestException(
                        $"Request timed out after multiple attempts",
                        ex
                    );
                }

                await DelayWithExponentialBackoff(attempt, baseDelay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("{OperationName} was cancelled", operationName);
                throw;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(
                    ex,
                    "HTTP error in {OperationName} (attempt {Attempt}/{MaxRetries})",
                    operationName,
                    attempt + 1,
                    maxRetries + 1
                );
                if (attempt == maxRetries)
                {
                    throw;
                }

                await DelayWithExponentialBackoff(attempt, baseDelay, cancellationToken);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Invalid JSON in {OperationName} (attempt {Attempt}/{MaxRetries})",
                    operationName,
                    attempt + 1,
                    maxRetries + 1
                );
                if (attempt == maxRetries)
                {
                    throw new InvalidOperationException($"Invalid JSON in {operationName}", ex);
                }

                await DelayWithExponentialBackoff(attempt, baseDelay, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Invalid response in {OperationName} (attempt {Attempt}/{MaxRetries})",
                    operationName,
                    attempt + 1,
                    maxRetries + 1
                );
                if (attempt == maxRetries)
                {
                    throw;
                }

                await DelayWithExponentialBackoff(attempt, baseDelay, cancellationToken);
            }
        }
        throw new InvalidOperationException(
            $"Failed to execute {operationName} after all retry attempts"
        );
    }

    /// <summary>
    /// Generic method to fetch and validate JSON data from a URL.
    /// </summary>
    private async Task<JsonNode> FetchJsonAsync(
        string url,
        Func<JsonNode?, bool> validator,
        string operationName,
        CancellationToken cancellationToken
    )
    {
        var response = await _httpClient.GetAsync(url, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "{OperationName} API returned {StatusCode}: {ReasonPhrase}",
                operationName,
                response.StatusCode,
                response.ReasonPhrase
            );
            throw new HttpRequestException(
                $"{operationName} API returned {response.StatusCode}: {response.ReasonPhrase}"
            );
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(content))
        {
            _logger.LogWarning("{OperationName} API returned empty response", operationName);
            throw new InvalidOperationException($"{operationName} API returned empty response");
        }

        var jsonData = JsonNode.Parse(content);

        if (!validator(jsonData))
        {
            _logger.LogWarning("{OperationName} response has invalid structure", operationName);
            throw new InvalidOperationException($"{operationName} response has invalid structure");
        }

        return jsonData!;
    }

    /// <summary>
    /// Generic validation method for JSON responses with data arrays.
    /// </summary>
    private bool ValidateJsonResponse(
        JsonNode? jsonData,
        Func<JsonNode, bool> itemValidator,
        string responseType
    )
    {
        try
        {
            if (jsonData == null)
            {
                return false;
            }

            var dataArray = jsonData["data"]?.AsArray();
            if (dataArray == null)
            {
                return false;
            }

            // Check if we have at least one valid item
            foreach (var item in dataArray)
            {
                if (item != null && itemValidator(item))
                {
                    return true; // Found at least one valid item
                }
            }

            return false; // No valid items found
        }
        catch (Exception ex)
        {
            _logger.LogTrace(
                ex,
                "Error validating {ResponseType} response structure",
                responseType
            );
            return false;
        }
    }

    /// <summary>
    /// Implements exponential backoff delay for retry logic.
    /// </summary>
    private async Task DelayWithExponentialBackoff(
        int attempt,
        TimeSpan baseDelay,
        CancellationToken cancellationToken
    )
    {
        var delay = TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * Math.Pow(2, attempt));
        var jitter = TimeSpan.FromMilliseconds(
            Random.Shared.Next(0, (int)(delay.TotalMilliseconds * 0.1))
        );
        var totalDelay = delay + jitter;

        _logger.LogTrace("Waiting {DelayMs}ms before retry", totalDelay.TotalMilliseconds);
        await Task.Delay(totalDelay, cancellationToken);
    }

    #endregion

    /// <summary>
    /// Gets model configurations with cache-first logic.
    /// Returns cached data immediately if valid, then refreshes cache in background.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of model configurations</returns>
    public async Task<IReadOnlyList<ModelConfig>> GetModelConfigsAsync(
        CancellationToken cancellationToken = default
    )
    {
        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogDebug("Getting OpenRouter model configurations");

        // Try to load from cache first
        var cache = await LoadCacheAsync(cancellationToken);

        if (cache != null && cache.IsValid)
        {
            _logger.LogDebug(
                "Cache is valid, returning cached data and starting background refresh"
            );

            // Requirement 1.3: Start background refresh when returning cached configurations
            _ = StartBackgroundRefreshAsync();

            // Return cached data immediately
            var result = await ConvertToModelConfigsAsync(cache, cancellationToken);
            totalStopwatch.Stop();

            _logger.LogDebug(
                "Returned {Count} cached model configurations in {ElapsedMs}ms",
                result.Count,
                totalStopwatch.ElapsedMilliseconds
            );

            return result;
        }

        if (cache != null && !cache.IsValid)
        {
            _logger.LogDebug("Cache is stale, fetching fresh data");
            // Note: For stale cache, we fetch fresh data synchronously rather than using background refresh
            // since the cache is already stale and we need fresh data immediately
        }
        else
        {
            _logger.LogDebug("Cache is missing, fetching fresh data");
        }

        // Cache is invalid or missing, fetch fresh data
        try
        {
            var freshCache = await FetchAndCacheDataAsync(cancellationToken);
            var result = await ConvertToModelConfigsAsync(freshCache, cancellationToken);
            totalStopwatch.Stop();

            _logger.LogDebug(
                "Returned {Count} fresh model configurations in {ElapsedMs}ms",
                result.Count,
                totalStopwatch.ElapsedMilliseconds
            );

            return result;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "Timeout fetching fresh OpenRouter data");
            return await HandleNetworkFailure(cache, "Request timeout");
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Fresh data fetch was cancelled");
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Network error fetching fresh OpenRouter data");
            return await HandleNetworkFailure(cache, "Network error");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Invalid response from OpenRouter API");
            return await HandleNetworkFailure(cache, "Invalid API response");
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Invalid JSON response from OpenRouter API");
            return await HandleNetworkFailure(cache, "Invalid JSON response");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching fresh OpenRouter data");
            return await HandleNetworkFailure(cache, "Unexpected error");
        }
    }

    /// <summary>
    /// Starts a background cache refresh operation that doesn't block foreground requests.
    /// Implements requirements 1.3, 2.2, 2.3, and 6.2.
    /// </summary>
    /// <returns>A task that represents the background refresh operation</returns>
    private Task StartBackgroundRefreshAsync()
    {
        return Task.Run(
            async () =>
            {
                // Requirement 6.2: Ensure background operations don't block foreground requests
                // Use a non-blocking semaphore check to prevent multiple concurrent background refreshes
                if (!await _backgroundRefreshSemaphore.WaitAsync(0, CancellationToken.None))
                {
                    _logger.LogDebug("Background cache refresh already in progress, skipping");
                    return;
                }

                try
                {
                    _logger.LogDebug("Starting background cache refresh");

                    // Use a separate cancellation token for background operations
                    // This ensures background refresh can complete even if the original request is cancelled
                    using var backgroundCts = new CancellationTokenSource(
                        TimeSpan.FromMinutes(BackgroundRefreshTimeoutMinutes)
                    );

                    await RefreshCacheAsync(backgroundCts.Token);

                    _logger.LogDebug("Background cache refresh completed successfully");
                }
                catch (OperationCanceledException ex)
                    when (ex.CancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("Background cache refresh was cancelled due to timeout");
                }
                catch (Exception ex)
                {
                    // Requirement 2.3: Continue using existing cache and log errors on refresh failure
                    _logger.LogWarning(
                        ex,
                        "Background cache refresh failed, continuing with existing cache"
                    );
                }
                finally
                {
                    _backgroundRefreshSemaphore.Release();
                }
            },
            CancellationToken.None
        ); // Use CancellationToken.None to ensure background task isn't cancelled by request cancellation
    }

    /// <summary>
    /// Manually refreshes the cache.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task RefreshCacheAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Manually refreshing OpenRouter cache");

        try
        {
            await FetchAndCacheDataAsync(cancellationToken);
            _logger.LogDebug("Cache refresh completed successfully");
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Cache refresh was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh OpenRouter cache");
            throw;
        }
    }

    /// <summary>
    /// Loads cache from disk if it exists with comprehensive integrity validation.
    /// </summary>
    private async Task<OpenRouterCache?> LoadCacheAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_cacheFilePath))
        {
            _logger.LogDebug("Cache file does not exist: {CacheFilePath}", _cacheFilePath);
            return null;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            await _cacheSemaphore.WaitAsync(cancellationToken);
            try
            {
                // Read file with efficient buffering for performance
                using var fileStream = new FileStream(
                    _cacheFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: FileBufferSize
                );

                // Deserialize directly from stream for better performance
                var cache = await JsonSerializer.DeserializeAsync<OpenRouterCache>(
                    fileStream,
                    _jsonOptions,
                    cancellationToken
                );

                stopwatch.Stop();

                if (cache == null)
                {
                    _logger.LogWarning(
                        "Cache file contains null data, deleting: {CacheFilePath}",
                        _cacheFilePath
                    );
                    await DeleteCorruptedCacheFileAsync();
                    return null;
                }

                // Comprehensive cache integrity validation
                if (!ValidateCacheIntegrity(cache))
                {
                    _logger.LogWarning(
                        "Cache integrity validation failed, deleting corrupted cache: {CacheFilePath}",
                        _cacheFilePath
                    );
                    await DeleteCorruptedCacheFileAsync();
                    return null;
                }

                _logger.LogDebug(
                    "Loaded cache from {CacheFilePath} in {ElapsedMs}ms, cached at {CachedAt}, valid: {IsValid}",
                    _cacheFilePath,
                    stopwatch.ElapsedMilliseconds,
                    cache.CachedAt,
                    cache.IsValid
                );

                // Ensure load time is under performance requirement
                if (stopwatch.ElapsedMilliseconds > CacheLoadTimeoutMs)
                {
                    _logger.LogWarning(
                        "Cache load took {ElapsedMs}ms, exceeding {TimeoutMs}ms performance requirement",
                        stopwatch.ElapsedMilliseconds,
                        CacheLoadTimeoutMs
                    );
                }

                return cache;
            }
            finally
            {
                _cacheSemaphore.Release();
            }
        }
        catch (JsonException ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(
                ex,
                "JSON deserialization failed for cache file {CacheFilePath}, deleting corrupted file",
                _cacheFilePath
            );
            await DeleteCorruptedCacheFileAsync();
            return null;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(
                ex,
                "Failed to load cache from {CacheFilePath}, deleting corrupted file",
                _cacheFilePath
            );
            await DeleteCorruptedCacheFileAsync();
            return null;
        }
    }

    /// <summary>
    /// Validates cache integrity to ensure data consistency and completeness.
    /// </summary>
    private bool ValidateCacheIntegrity(OpenRouterCache cache)
    {
        try
        {
            // Validate basic structure
            if (cache.CachedAt == default)
            {
                _logger.LogTrace("Cache integrity check failed: Invalid CachedAt timestamp");
                return false;
            }

            // Validate CachedAt is not in the future (allowing for small clock skew)
            if (cache.CachedAt > DateTime.UtcNow.AddMinutes(5))
            {
                _logger.LogTrace("Cache integrity check failed: CachedAt is in the future");
                return false;
            }

            // Validate models data structure
            if (cache.ModelsData == null)
            {
                _logger.LogTrace("Cache integrity check failed: ModelsData is null");
                return false;
            }

            var modelsArray = cache.ModelsData["data"]?.AsArray();
            if (modelsArray == null || modelsArray.Count == 0)
            {
                _logger.LogTrace("Cache integrity check failed: No models data found");
                return false;
            }

            // Validate at least some models have required fields
            int validModels = 0;
            foreach (var modelNode in modelsArray)
            {
                if (modelNode == null)
                {
                    continue;
                }

                var slug = GetStringValue(modelNode, "slug");
                var name = GetStringValue(modelNode, "name");

                if (!string.IsNullOrEmpty(slug) && !string.IsNullOrEmpty(name))
                {
                    validModels++;
                }
            }

            if (validModels == 0)
            {
                _logger.LogTrace("Cache integrity check failed: No valid models found");
                return false;
            }

            // Validate model details dictionary
            if (cache.ModelDetails == null)
            {
                _logger.LogTrace("Cache integrity check failed: ModelDetails is null");
                return false;
            }

            // Validate model details structure for a sample of entries
            int checkedDetails = 0;
            foreach (var kvp in cache.ModelDetails.Take(5)) // Check first 5 entries for performance
            {
                if (string.IsNullOrEmpty(kvp.Key) || kvp.Value == null)
                {
                    _logger.LogTrace(
                        "Cache integrity check failed: Invalid model details entry for key {Key}",
                        kvp.Key
                    );
                    return false;
                }

                var dataArray = kvp.Value["data"]?.AsArray();
                if (dataArray == null)
                {
                    _logger.LogTrace(
                        "Cache integrity check failed: Invalid model details structure for {ModelSlug}",
                        kvp.Key
                    );
                    return false;
                }

                checkedDetails++;
            }

            _logger.LogTrace(
                "Cache integrity validation passed: {ValidModels} valid models, {CheckedDetails} details entries validated",
                validModels,
                checkedDetails
            );

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Cache integrity validation failed with exception");
            return false;
        }
    }

    /// <summary>
    /// Safely deletes a corrupted cache file.
    /// </summary>
    private async Task DeleteCorruptedCacheFileAsync()
    {
        try
        {
            if (File.Exists(_cacheFilePath))
            {
                File.Delete(_cacheFilePath);
                _logger.LogDebug("Deleted corrupted cache file: {CacheFilePath}", _cacheFilePath);
            }
        }
        catch (Exception deleteEx)
        {
            _logger.LogWarning(
                deleteEx,
                "Failed to delete corrupted cache file: {CacheFilePath}",
                _cacheFilePath
            );
        }

        await Task.CompletedTask; // Make method async for consistency
    }

    /// <summary>
    /// Fetches fresh data from OpenRouter API and caches it.
    /// </summary>
    private async Task<OpenRouterCache> FetchAndCacheDataAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Fetching fresh data from OpenRouter API");

        // Fetch models list with retry logic
        var modelsData = await FetchModelsListAsync(cancellationToken);

        // Fetch model details for each model
        var modelDetails = await FetchModelDetailsAsync(modelsData, cancellationToken);

        var cache = new OpenRouterCache
        {
            CachedAt = DateTime.UtcNow,
            ModelsData = modelsData,
            ModelDetails = modelDetails,
        };

        // Save to cache atomically
        await SaveCacheAsync(cache, cancellationToken);

        _logger.LogDebug("Successfully fetched and cached OpenRouter data");
        return cache;
    }

    /// <summary>
    /// Fetches the models list from OpenRouter API with retry logic and validation.
    /// </summary>
    private async Task<JsonNode> FetchModelsListAsync(CancellationToken cancellationToken)
    {
        return await ExecuteWithRetryAsync(
            async () =>
                await FetchJsonAsync(
                    OpenRouterModelsUrl,
                    ValidateModelsResponse,
                    "OpenRouter models",
                    cancellationToken
                ),
            MaxRetries,
            TimeSpan.FromSeconds(1),
            "Fetching models list",
            cancellationToken
        );
    }

    /// <summary>
    /// Fetches detailed model information for each model.
    /// </summary>
    private async Task<Dictionary<string, JsonNode>> FetchModelDetailsAsync(
        JsonNode modelsData,
        CancellationToken cancellationToken
    )
    {
        var modelDetails = new Dictionary<string, JsonNode>();
        var modelsArray = modelsData?["data"]?.AsArray();

        if (modelsArray == null)
        {
            _logger.LogWarning("No models data found to fetch details for");
            return modelDetails;
        }

        var semaphore = new SemaphoreSlim(ConcurrentRequestLimit, ConcurrentRequestLimit); // Limit concurrent requests
        var tasks = new List<Task>();

        foreach (var modelNode in modelsArray)
        {
            if (modelNode == null)
            {
                continue;
            }

            var permaslug = GetStringValue(modelNode, "permaslug");
            if (string.IsNullOrEmpty(permaslug))
            {
                // Fallback to slug if permaslug is not available
                var slug = GetStringValue(modelNode, "slug");
                if (string.IsNullOrEmpty(slug))
                {
                    continue;
                }

                permaslug = slug;
            }

            tasks.Add(
                FetchSingleModelDetailsAsync(permaslug, modelDetails, semaphore, cancellationToken)
            );
        }

        await Task.WhenAll(tasks);

        _logger.LogDebug("Fetched details for {Count} models", modelDetails.Count);
        return modelDetails;
    }

    /// <summary>
    /// Fetches details for a single model with retry logic.
    /// </summary>
    private async Task FetchSingleModelDetailsAsync(
        string modelPermaslug,
        Dictionary<string, JsonNode> modelDetails,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken
    )
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            var url = string.Format(OpenRouterStatsUrlTemplate, modelPermaslug);

            try
            {
                var detailsData = await ExecuteWithRetryAsync(
                    async () =>
                        await FetchJsonAsync(
                            url,
                            ValidateModelDetailsResponse,
                            $"Model details for {modelPermaslug}",
                            cancellationToken
                        ),
                    MaxModelDetailRetries,
                    TimeSpan.FromMilliseconds(500),
                    $"Fetching details for model {modelPermaslug}",
                    cancellationToken
                );

                lock (modelDetails)
                {
                    modelDetails[modelPermaslug] = detailsData;
                }

                _logger.LogTrace(
                    "Successfully fetched details for model {ModelPermaslug}",
                    modelPermaslug
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to fetch details for model {ModelPermaslug} after all attempts",
                    modelPermaslug
                );
                // Skip this model, don't fail the entire operation
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Validates the structure of the models list response.
    /// </summary>
    private bool ValidateModelsResponse(JsonNode? modelsData)
    {
        return ValidateJsonResponse(
            modelsData,
            item => GetStringValue(item, "slug") != null && GetStringValue(item, "name") != null,
            "models"
        );
    }

    /// <summary>
    /// Validates the structure of a model details response.
    /// </summary>
    private bool ValidateModelDetailsResponse(JsonNode? detailsData)
    {
        return ValidateJsonResponse(
            detailsData,
            item =>
                GetStringValue(item, "id") != null && GetStringValue(item, "provider_name") != null,
            "model details"
        );
    }

    /// <summary>
    /// Handles network failures by falling back to cache or returning empty list.
    /// </summary>
    private async Task<IReadOnlyList<ModelConfig>> HandleNetworkFailure(
        OpenRouterCache? cache,
        string errorType
    )
    {
        // If we have stale cache, use it as fallback
        if (cache != null)
        {
            _logger.LogWarning(
                "{ErrorType}: Using stale cache as fallback (cached at {CachedAt})",
                errorType,
                cache.CachedAt
            );
            return await ConvertToModelConfigsAsync(cache, CancellationToken.None);
        }

        // No cache available, return empty list
        _logger.LogWarning(
            "{ErrorType}: No cache available, returning empty model list",
            errorType
        );
        return [];
    }

    /// <summary>
    /// Saves cache to disk atomically with integrity validation and performance optimization.
    /// </summary>
    private async Task SaveCacheAsync(OpenRouterCache cache, CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await _cacheSemaphore.WaitAsync(cancellationToken);
        try
        {
            // Validate cache before saving
            if (!ValidateCacheIntegrity(cache))
            {
                throw new InvalidOperationException("Cannot save cache with invalid integrity");
            }

            var tempFile = _cacheFilePath + ".tmp";
            var backupFile = _cacheFilePath + ".backup";

            try
            {
                // Create backup of existing cache if it exists
                if (File.Exists(_cacheFilePath))
                {
                    File.Copy(_cacheFilePath, backupFile, overwrite: true);
                    _logger.LogTrace("Created backup of existing cache file");
                }

                // Write to temporary file with optimized streaming for performance
                using (
                    var fileStream = new FileStream(
                        tempFile,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: FileBufferSize
                    )
                )
                {
                    await JsonSerializer.SerializeAsync(
                        fileStream,
                        cache,
                        _jsonOptions,
                        cancellationToken
                    );
                    await fileStream.FlushAsync(cancellationToken);
                }

                // Verify the written file can be read back correctly
                if (!await VerifyWrittenCacheFile(tempFile, cancellationToken))
                {
                    throw new InvalidOperationException(
                        "Cache file verification failed after write"
                    );
                }

                // Atomic move to final location
                File.Move(tempFile, _cacheFilePath, overwrite: true);

                // Clean up backup file on successful write
                if (File.Exists(backupFile))
                {
                    File.Delete(backupFile);
                }

                stopwatch.Stop();
                _logger.LogDebug(
                    "Cache saved atomically to {CacheFilePath} in {ElapsedMs}ms",
                    _cacheFilePath,
                    stopwatch.ElapsedMilliseconds
                );

                // Log performance warning if save takes too long
                if (stopwatch.ElapsedMilliseconds > CacheSaveTimeoutMs)
                {
                    _logger.LogWarning(
                        "Cache save took {ElapsedMs}ms, consider optimizing cache size",
                        stopwatch.ElapsedMilliseconds
                    );
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "Failed to save cache atomically");

                // Clean up temporary file
                try
                {
                    if (File.Exists(tempFile))
                    {
                        File.Delete(tempFile);
                    }
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(
                        cleanupEx,
                        "Failed to clean up temporary cache file: {TempFile}",
                        tempFile
                    );
                }

                // Restore from backup if available
                try
                {
                    if (File.Exists(backupFile))
                    {
                        File.Move(backupFile, _cacheFilePath, overwrite: true);
                        _logger.LogDebug("Restored cache from backup after save failure");
                    }
                }
                catch (Exception restoreEx)
                {
                    _logger.LogWarning(restoreEx, "Failed to restore cache from backup");
                }

                throw;
            }
        }
        finally
        {
            _cacheSemaphore.Release();
        }
    }

    /// <summary>
    /// Verifies that a written cache file can be read back correctly.
    /// </summary>
    private async Task<bool> VerifyWrittenCacheFile(
        string filePath,
        CancellationToken cancellationToken
    )
    {
        try
        {
            using var fileStream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read
            );
            var verifyCache = await JsonSerializer.DeserializeAsync<OpenRouterCache>(
                fileStream,
                _jsonOptions,
                cancellationToken
            );

            if (verifyCache == null)
            {
                _logger.LogTrace("Cache file verification failed: Deserialized to null");
                return false;
            }

            if (!ValidateCacheIntegrity(verifyCache))
            {
                _logger.LogTrace("Cache file verification failed: Integrity validation failed");
                return false;
            }

            _logger.LogTrace("Cache file verification passed");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Cache file verification failed with exception");
            return false;
        }
    }

    /// <summary>
    /// Converts OpenRouter cache data to ModelConfig objects with comprehensive mapping.
    /// Maps OpenRouter model data to ModelConfig objects with all required fields,
    /// creates ProviderConfig entries for each model variant with appropriate priorities,
    /// extracts unique provider information including metadata and capabilities,
    /// and allows selective filtering of providers during configuration creation.
    /// </summary>
    private async Task<IReadOnlyList<ModelConfig>> ConvertToModelConfigsAsync(
        OpenRouterCache cache,
        CancellationToken cancellationToken
    )
    {
        await Task.CompletedTask; // Placeholder for async operations in future tasks

        var modelConfigs = new List<ModelConfig>();

        try
        {
            // Parse the models data
            var modelsArray = cache.ModelsData?["data"]?.AsArray();
            if (modelsArray == null)
            {
                _logger.LogWarning("No models data found in cache");
                return modelConfigs;
            }

            // Group models by slug to handle multiple endpoints per model
            var modelGroups = new Dictionary<string, List<JsonNode>>();

            foreach (var modelNode in modelsArray)
            {
                if (modelNode == null)
                {
                    continue;
                }

                var slug = GetStringValue(modelNode, "slug");
                if (string.IsNullOrEmpty(slug))
                {
                    continue;
                }

                if (!modelGroups.TryGetValue(slug, out var value))
                {
                    value = ([]);
                    modelGroups[slug] = value;
                }

                value.Add(modelNode);
            }

            foreach (var modelGroup in modelGroups)
            {
                try
                {
                    var modelConfig = await CreateModelConfigFromGroupAsync(
                        modelGroup.Key,
                        modelGroup.Value,
                        cache,
                        cancellationToken
                    );
                    if (modelConfig != null)
                    {
                        modelConfigs.Add(modelConfig);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to create ModelConfig for model {ModelSlug}",
                        modelGroup.Key
                    );
                }
            }

            _logger.LogDebug("Converted {Count} models to ModelConfig objects", modelConfigs.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to convert cache data to ModelConfig objects");
        }

        return modelConfigs;
    }

    /// <summary>
    /// Creates a ModelConfig from a group of model nodes (same model, different endpoints/providers).
    /// </summary>
    private async Task<ModelConfig?> CreateModelConfigFromGroupAsync(
        string modelSlug,
        List<JsonNode> modelNodes,
        OpenRouterCache cache,
        CancellationToken cancellationToken
    )
    {
        if (modelNodes.Count == 0)
        {
            return null;
        }

        // Use the first model node for basic model information
        var primaryModelNode = modelNodes[0];

        var name = GetStringValue(primaryModelNode, "name");
        var contextLength = GetIntValue(primaryModelNode, "context_length");
        var inputModalities = GetStringArray(primaryModelNode, "input_modalities");
        var outputModalities = GetStringArray(primaryModelNode, "output_modalities");
        var hasTextOutput = GetBoolValue(primaryModelNode, "has_text_output", true);
        var group = GetStringValue(primaryModelNode, "group");
        var author = GetStringValue(primaryModelNode, "author");
        var description = GetStringValue(primaryModelNode, "description");
        var warningMessage = GetStringValue(primaryModelNode, "warning_message");
        var isHidden = GetBoolValue(primaryModelNode, "hidden");

        // Parse created date from ISO 8601 string
        var createdAtString = GetStringValue(primaryModelNode, "created_at");
        var createdDate = TryParseDateTime(createdAtString);

        // Check if model is reasoning-capable
        var isReasoning = OpenRouterModelService.CheckIfReasoningModel(primaryModelNode);

        // Create capabilities
        var capabilities = OpenRouterModelService.CreateModelCapabilities(
            primaryModelNode,
            inputModalities,
            outputModalities,
            contextLength
        );

        // Create providers from model details endpoints
        var providers = new List<ProviderConfig>();
        var openRouterSubProviders = new List<SubProviderConfig>();
        var specialProviders = new HashSet<string>
        {
            "gemini",
            "openai",
            "groq",
            "deepinfra",
            "anthropic",
        };

        // Get the permaslug for this model to look up details
        var modelPermaslug = GetStringValue(primaryModelNode, "permaslug");
        if (string.IsNullOrEmpty(modelPermaslug))
        {
            // Fallback to slug if permaslug is not available
            modelPermaslug = modelSlug;
        }

        // Look up model details using permaslug
        if (
            cache.ModelDetails != null
            && cache.ModelDetails.TryGetValue(modelPermaslug, out var modelDetailsNode)
        )
        {
            var endpointsArray = modelDetailsNode?["data"]?.AsArray();
            if (endpointsArray != null)
            {
                var providerPriority = 100; // Start with high priority, decrease for each provider

                foreach (var endpointNode in endpointsArray)
                {
                    if (endpointNode == null)
                    {
                        continue;
                    }

                    var providerName = GetStringValue(endpointNode, "provider_name")
                        ?.ToLowerInvariant();
                    var endpointId = GetStringValue(endpointNode, "id");
                    var providerDisplayName = GetStringValue(endpointNode, "provider_display_name");
                    var providerModelId = GetStringValue(endpointNode, "provider_model_id");
                    var isDisabled = GetBoolValue(endpointNode, "is_disabled");
                    var isEndpointHidden = GetBoolValue(endpointNode, "is_hidden");

                    if (
                        string.IsNullOrEmpty(providerName)
                        || string.IsNullOrEmpty(endpointId)
                        || isDisabled
                        || isEndpointHidden
                    )
                    {
                        continue;
                    }

                    // Create sub-provider entry for OpenRouter
                    var subProvider = CreateSubProviderFromEndpoint(endpointNode, modelSlug);
                    if (subProvider != null)
                    {
                        openRouterSubProviders.Add(subProvider);
                    }

                    // Create separate provider entry for special providers
                    if (specialProviders.Contains(providerName))
                    {
                        var specialProviderConfig = await CreateProviderConfigFromEndpointAsync(
                            endpointNode,
                            modelSlug,
                            cache,
                            providerPriority,
                            cancellationToken
                        );
                        if (specialProviderConfig != null)
                        {
                            providers.Add(specialProviderConfig);
                            providerPriority = Math.Max(1, providerPriority - 10);
                        }
                    }
                }
            }
        }

        // Fallback: If no model details found, try to use endpoint data from main model nodes (legacy support)
        if (openRouterSubProviders.Count == 0)
        {
            foreach (var modelNode in modelNodes)
            {
                var endpoint = modelNode["endpoint"];
                if (endpoint == null)
                {
                    continue;
                }

                var subProvider = CreateSubProviderFromEndpoint(endpoint, modelSlug);
                if (subProvider != null)
                {
                    openRouterSubProviders.Add(subProvider);
                }
            }
        }

        // Always create OpenRouter as the primary provider with all endpoints as sub-providers
        var openRouterProvider = new ProviderConfig
        {
            Name = "OpenRouter",
            ModelName = modelSlug,
            Priority = 1000, // Highest priority for OpenRouter
            Pricing = OpenRouterModelService.GetBestPricingFromSubProviders(openRouterSubProviders),
            SubProviders = openRouterSubProviders,
            Tags = ["openrouter", "aggregator"],
        };

        // Insert OpenRouter as the first provider
        providers.Insert(0, openRouterProvider);

        // If no providers were created, create a basic OpenRouter provider
        if (providers.Count == 0)
        {
            providers.Add(OpenRouterModelService.CreateFallbackProvider(modelSlug));
        }

        return new ModelConfig
        {
            Id = modelSlug,
            IsReasoning = isReasoning,
            CreatedDate = createdDate,
            Capabilities = capabilities,
            Providers = providers,
        };
    }

    /// <summary>
    /// Creates a ProviderConfig from an OpenRouter endpoint.
    /// </summary>
    private Task<ProviderConfig?> CreateProviderConfigFromEndpointAsync(
        JsonNode endpoint,
        string modelSlug,
        OpenRouterCache cache,
        int priority,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var endpointId = GetStringValue(endpoint, "id");
            var providerName = GetStringValue(endpoint, "provider_name");
            var providerDisplayName = GetStringValue(endpoint, "provider_display_name");
            var providerModelId = GetStringValue(endpoint, "provider_model_id");
            var modelVariantSlug = GetStringValue(endpoint, "model_variant_slug");
            var isFree = GetBoolValue(endpoint, "is_free");
            var isHidden = GetBoolValue(endpoint, "is_hidden");
            var isDisabled = GetBoolValue(endpoint, "is_disabled");
            var quantization = GetStringValue(endpoint, "quantization");
            var variant = GetStringValue(endpoint, "variant");

            if (string.IsNullOrEmpty(providerName) || string.IsNullOrEmpty(endpointId))
            {
                return Task.FromResult<ProviderConfig?>(null);
            }

            // Skip disabled or hidden endpoints
            if (isDisabled || isHidden)
            {
                return Task.FromResult<ProviderConfig?>(null);
            }

            // Get pricing information
            var pricing = OpenRouterModelService.CreatePricingConfig(endpoint);

            // Create provider tags
            var tags = OpenRouterModelService.CreateProviderTags(endpoint, isFree, quantization, variant);

            // Get provider info for additional metadata
            var providerInfo = endpoint["provider_info"];
            var subProviders = OpenRouterModelService.CreateSubProviders(providerInfo);

            return Task.FromResult<ProviderConfig?>(
                new ProviderConfig
                {
                    Name = providerDisplayName ?? providerName,
                    ModelName = providerModelId ?? modelVariantSlug ?? modelSlug,
                    Priority = priority,
                    Pricing = pricing,
                    SubProviders = subProviders,
                    Tags = tags,
                }
            );
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Failed to create ProviderConfig from endpoint");
            return Task.FromResult<ProviderConfig?>(null);
        }
    }

    /// <summary>
    /// Creates pricing configuration from endpoint data.
    /// </summary>
    private static PricingConfig CreatePricingConfig(JsonNode endpoint)
    {
        var pricing = endpoint["pricing"];
        if (pricing == null)
        {
            return new PricingConfig { PromptPerMillion = 0.0, CompletionPerMillion = 0.0 };
        }

        // OpenRouter pricing is per token, convert to per million
        var promptPrice = GetStringValue(pricing, "prompt");
        var completionPrice = GetStringValue(pricing, "completion");

        var promptPerMillion = 0.0;
        var completionPerMillion = 0.0;

        if (double.TryParse(promptPrice, out var promptPriceValue))
        {
            promptPerMillion = promptPriceValue * 1_000_000; // Convert from per-token to per-million
        }

        if (double.TryParse(completionPrice, out var completionPriceValue))
        {
            completionPerMillion = completionPriceValue * 1_000_000; // Convert from per-token to per-million
        }

        return new PricingConfig
        {
            PromptPerMillion = promptPerMillion,
            CompletionPerMillion = completionPerMillion,
        };
    }

    /// <summary>
    /// Creates provider tags based on endpoint characteristics.
    /// </summary>
    private static IReadOnlyList<string> CreateProviderTags(
        JsonNode endpoint,
        bool isFree,
        string? quantization,
        string? variant
    )
    {
        var tags = new List<string> { "openrouter" };

        if (isFree)
        {
            tags.Add("free");
        }
        else
        {
            tags.Add("paid");
        }

        if (!string.IsNullOrEmpty(quantization))
        {
            tags.Add($"quantization-{quantization.ToLowerInvariant()}");
        }

        if (!string.IsNullOrEmpty(variant) && variant != "standard")
        {
            tags.Add($"variant-{variant.ToLowerInvariant()}");
        }

        // Add capability-based tags
        var supportsTools = GetBoolValue(endpoint, "supports_tool_parameters");
        var supportsReasoning = GetBoolValue(endpoint, "supports_reasoning");
        var supportsMultipart = GetBoolValue(endpoint, "supports_multipart");
        var supportedParams = GetStringArray(endpoint, "supported_parameters");

        if (supportsTools)
        {
            tags.Add("tools");
        }

        if (supportsReasoning)
        {
            tags.Add("reasoning");
        }

        if (supportsMultipart)
        {
            tags.Add("multimodal");
        }

        // Add structured output tags
        if (supportedParams.Contains("response_format"))
        {
            tags.Add("json-mode");
        }

        if (supportedParams.Contains("structured_outputs"))
        {
            tags.Add("structured-outputs");
        }

        // Add performance-based tags based on limits
        var limitRpm = GetIntValue(endpoint, "limit_rpm");
        if (limitRpm > 0)
        {
            if (limitRpm >= 100)
            {
                tags.Add("high-throughput");
            }
            else if (limitRpm <= 10)
            {
                tags.Add("low-throughput");
            }
        }

        return tags;
    }

    /// <summary>
    /// Creates sub-providers from provider info (for aggregators like OpenRouter).
    /// </summary>
    private static IReadOnlyList<SubProviderConfig>? CreateSubProviders(JsonNode? providerInfo)
    {
        // For OpenRouter, we don't typically have sub-providers since OpenRouter itself is the aggregator
        // This could be extended in the future if needed
        return null;
    }

    /// <summary>
    /// Creates a SubProviderConfig from an OpenRouter endpoint.
    /// </summary>
    private SubProviderConfig? CreateSubProviderFromEndpoint(JsonNode endpoint, string modelSlug)
    {
        try
        {
            var endpointId = GetStringValue(endpoint, "id");
            var providerName = GetStringValue(endpoint, "provider_name");
            var providerDisplayName = GetStringValue(endpoint, "provider_display_name");
            var providerModelId = GetStringValue(endpoint, "provider_model_id");
            var modelVariantSlug = GetStringValue(endpoint, "model_variant_slug");
            var isFree = GetBoolValue(endpoint, "is_free");
            var isDisabled = GetBoolValue(endpoint, "is_disabled");
            var isHidden = GetBoolValue(endpoint, "is_hidden");
            var quantization = GetStringValue(endpoint, "quantization");
            var variant = GetStringValue(endpoint, "variant");

            if (string.IsNullOrEmpty(providerName) || string.IsNullOrEmpty(endpointId))
            {
                return null;
            }

            // Skip disabled or hidden endpoints
            if (isDisabled || isHidden)
            {
                return null;
            }

            // Get pricing information
            var pricing = OpenRouterModelService.CreatePricingConfig(endpoint);

            return new SubProviderConfig
            {
                Name = providerDisplayName ?? providerName,
                ModelName = providerModelId ?? modelVariantSlug ?? modelSlug,
                Priority = 1, // Default priority for sub-providers
                Pricing = pricing,
            };
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Failed to create SubProviderConfig from endpoint");
            return null;
        }
    }

    /// <summary>
    /// Gets the best pricing from a list of sub-providers (typically the cheapest).
    /// </summary>
    private static PricingConfig GetBestPricingFromSubProviders(
        IReadOnlyList<SubProviderConfig> subProviders
    )
    {
        if (subProviders == null || subProviders.Count == 0)
        {
            return new PricingConfig { PromptPerMillion = 0.0, CompletionPerMillion = 0.0 };
        }

        // Find the cheapest pricing (lowest total cost for a typical request)
        var bestPricing = subProviders
            .Select(sp => sp.Pricing)
            .Where(p => p != null)
            .OrderBy(p => p.PromptPerMillion + p.CompletionPerMillion)
            .FirstOrDefault();

        return bestPricing
            ?? new PricingConfig { PromptPerMillion = 0.0, CompletionPerMillion = 0.0 };
    }

    /// <summary>
    /// Creates model capabilities from OpenRouter model data.
    /// </summary>
    private static ModelCapabilities CreateModelCapabilities(
        JsonNode modelNode,
        string[] inputModalities,
        string[] outputModalities,
        int contextLength
    )
    {
        // Create token limits
        var tokenLimits = new TokenLimits
        {
            MaxContextTokens = contextLength,
            MaxOutputTokens = contextLength / 4, // Reasonable default
            SupportsTokenCounting = true,
        };

        // Create multimodal capabilities
        MultimodalCapability? multimodal = null;
        if (
            inputModalities.Length > 1
            || inputModalities.Contains("image")
            || inputModalities.Contains("audio")
            || inputModalities.Contains("video")
        )
        {
            multimodal = new MultimodalCapability
            {
                SupportsImages = inputModalities.Contains("image"),
                SupportsAudio = inputModalities.Contains("audio"),
                SupportsVideo = inputModalities.Contains("video"),
                SupportedImageFormats = inputModalities.Contains("image")
                    ? new[] { "jpeg", "png", "webp", "gif" }
                    : [],
                SupportedAudioFormats = inputModalities.Contains("audio")
                    ? new[] { "mp3", "wav", "m4a" }
                    : [],
                SupportedVideoFormats = inputModalities.Contains("video")
                    ? new[] { "mp4", "avi", "mov" }
                    : [],
            };
        }

        // Create thinking capabilities
        ThinkingCapability? thinking = null;
        var reasoningConfig = modelNode["reasoning_config"];
        if (reasoningConfig != null || OpenRouterModelService.CheckIfReasoningModel(modelNode))
        {
            thinking = new ThinkingCapability
            {
                Type = OpenRouterModelService.DetermineThinkingType(modelNode),
                IsBuiltIn = true, // Most OpenRouter reasoning models have built-in thinking
                IsExposed = true,
            };
        }

        // Create function calling capabilities
        FunctionCallingCapability? functionCalling = null;
        var endpoint = modelNode["endpoint"];
        if (endpoint != null)
        {
            var supportsTools = GetBoolValue(endpoint, "supports_tool_parameters");
            if (supportsTools)
            {
                // Enhanced tool choice detection from features
                var toolChoiceSupport = new Dictionary<string, bool>
                {
                    ["literal_none"] = true,
                    ["literal_auto"] = true,
                    ["literal_required"] = true,
                    ["type_function"] = true,
                };

                var features = endpoint["features"];
                if (features != null)
                {
                    var supportsToolChoice = features["supports_tool_choice"];
                    if (supportsToolChoice != null)
                    {
                        toolChoiceSupport["literal_none"] = GetBoolValue(
                            supportsToolChoice,
                            "literal_none",
                            true
                        );
                        toolChoiceSupport["literal_auto"] = GetBoolValue(
                            supportsToolChoice,
                            "literal_auto",
                            true
                        );
                        toolChoiceSupport["literal_required"] = GetBoolValue(
                            supportsToolChoice,
                            "literal_required",
                            true
                        );
                        toolChoiceSupport["type_function"] = GetBoolValue(
                            supportsToolChoice,
                            "type_function",
                            true
                        );
                    }
                }

                functionCalling = new FunctionCallingCapability
                {
                    SupportsTools = true,
                    SupportsToolChoice =
                        toolChoiceSupport["literal_auto"] || toolChoiceSupport["literal_required"],
                    SupportsStructuredParameters = true,
                    SupportedToolTypes = ["function"],
                };
            }
        }

        // Create response format capabilities
        ResponseFormatCapability? responseFormats = null;
        if (endpoint != null)
        {
            var supportedParams = GetStringArray(endpoint, "supported_parameters");
            var features = endpoint["features"];

            // Check basic support
            var hasResponseFormat = supportedParams.Contains("response_format");
            var hasStructuredOutputs = supportedParams.Contains("structured_outputs");

            // Enhanced detection from features object
            if (features != null)
            {
                var supportedParameters = features["supported_parameters"];
                if (supportedParameters != null)
                {
                    hasResponseFormat =
                        hasResponseFormat || GetBoolValue(supportedParameters, "response_format");
                    hasStructuredOutputs =
                        hasStructuredOutputs
                        || GetBoolValue(supportedParameters, "structured_outputs");
                }
            }

            if (hasResponseFormat)
            {
                responseFormats = new ResponseFormatCapability
                {
                    SupportsJsonMode = true,
                    SupportsStructuredOutput = hasStructuredOutputs,
                    SupportsJsonSchema = hasStructuredOutputs, // Structured outputs typically implies JSON schema support
                };
            }
        }

        // Determine supported features
        var supportedFeatures = new List<string>();
        if (multimodal != null)
        {
            supportedFeatures.Add("multimodal");
        }

        if (thinking != null)
        {
            supportedFeatures.Add("thinking");
        }

        if (functionCalling != null)
        {
            supportedFeatures.Add("function_calling");
        }

        if (responseFormats != null)
        {
            supportedFeatures.Add("structured_output");
        }

        return new ModelCapabilities
        {
            TokenLimits = tokenLimits,
            Multimodal = multimodal,
            Thinking = thinking,
            FunctionCalling = functionCalling,
            ResponseFormats = responseFormats,
            SupportsStreaming = true, // Most OpenRouter models support streaming
            SupportedFeatures = supportedFeatures,
            IsPreview =
                GetStringValue(modelNode, "name")?.ToLowerInvariant().Contains("preview", StringComparison.InvariantCultureIgnoreCase) ?? false,
            IsDeprecated = !string.IsNullOrEmpty(GetStringValue(modelNode, "warning_message")),
        };
    }

    /// <summary>
    /// Checks if a model has reasoning capabilities.
    /// </summary>
    private static bool CheckIfReasoningModel(JsonNode modelNode)
    {
        var name = GetStringValue(modelNode, "name")?.ToLowerInvariant() ?? "";
        var slug = GetStringValue(modelNode, "slug")?.ToLowerInvariant() ?? "";
        var reasoningConfig = modelNode["reasoning_config"];

        // Check for explicit reasoning config
        if (reasoningConfig != null)
        {
            return true;
        }

        // Check for reasoning indicators in name/slug
        var reasoningIndicators = new[] { "o1", "reasoning", "think", "deepseek-r1", "qwq", "r1" };
        return reasoningIndicators.Any(indicator =>
            name.Contains(indicator) || slug.Contains(indicator)
        );
    }

    /// <summary>
    /// Determines the thinking type based on model characteristics.
    /// </summary>
    private static ThinkingType DetermineThinkingType(JsonNode modelNode)
    {
        var name = GetStringValue(modelNode, "name")?.ToLowerInvariant() ?? "";
        var slug = GetStringValue(modelNode, "slug")?.ToLowerInvariant() ?? "";
        var author = GetStringValue(modelNode, "author")?.ToLowerInvariant() ?? "";

        if (name.Contains("o1") || slug.Contains("o1") || author.Contains("openai"))
        {
            return ThinkingType.OpenAI;
        }

        return name.Contains("deepseek") || slug.Contains("deepseek") || author.Contains("deepseek")
            ? ThinkingType.DeepSeek
            : author.Contains("anthropic") ? ThinkingType.Anthropic : ThinkingType.Custom;
    }

    /// <summary>
    /// Creates a fallback provider when no endpoints are available.
    /// </summary>
    private static ProviderConfig CreateFallbackProvider(string modelSlug)
    {
        return new ProviderConfig
        {
            Name = "OpenRouter",
            ModelName = modelSlug,
            Priority = 1,
            Pricing = new PricingConfig { PromptPerMillion = 0.0, CompletionPerMillion = 0.0 },
            Tags = ["openrouter", "fallback"],
        };
    }

    /// <summary>
    /// Gets cache file information for monitoring and diagnostics.
    /// </summary>
    public CacheInfo GetCacheInfo()
    {
        try
        {
            if (!File.Exists(_cacheFilePath))
            {
                return new CacheInfo { Exists = false, FilePath = _cacheFilePath };
            }

            var fileInfo = new FileInfo(_cacheFilePath);
            return new CacheInfo
            {
                Exists = true,
                FilePath = _cacheFilePath,
                SizeBytes = fileInfo.Length,
                LastModified = fileInfo.LastWriteTimeUtc,
                IsValid =
                    File.GetLastWriteTimeUtc(_cacheFilePath)
                    > DateTime.UtcNow.Subtract(CacheExpiration),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get cache info for {CacheFilePath}", _cacheFilePath);
            return new CacheInfo
            {
                Exists = false,
                FilePath = _cacheFilePath,
                Error = ex.Message,
            };
        }
    }

    /// <summary>
    /// Clears the cache by deleting the cache file.
    /// </summary>
    public async Task ClearCacheAsync()
    {
        await _cacheSemaphore.WaitAsync();
        try
        {
            if (File.Exists(_cacheFilePath))
            {
                File.Delete(_cacheFilePath);
                _logger.LogDebug("Cache cleared: {CacheFilePath}", _cacheFilePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear cache: {CacheFilePath}", _cacheFilePath);
            throw;
        }
        finally
        {
            _cacheSemaphore.Release();
        }
    }

    /// <summary>
    /// Disposes resources.
    /// </summary>
    public void Dispose()
    {
        _cacheSemaphore?.Dispose();
        _backgroundRefreshSemaphore?.Dispose();
    }

    /// <summary>
    /// Information about the cache file.
    /// </summary>
    public record CacheInfo
    {
        public bool Exists { get; init; }
        public string FilePath { get; init; } = string.Empty;
        public long SizeBytes { get; init; }
        public DateTime LastModified { get; init; }
        public bool IsValid { get; init; }
        public string? Error { get; init; }

        public string SizeFormatted => Exists ? FormatBytes(SizeBytes) : "N/A";

        private static string FormatBytes(long bytes)
        {
            string[] suffixes = ["B", "KB", "MB", "GB"];
            int counter = 0;
            decimal number = bytes;
            while (Math.Round(number / 1024) >= 1)
            {
                number /= 1024;
                counter++;
            }
            return $"{number:n1} {suffixes[counter]}";
        }
    }
}
