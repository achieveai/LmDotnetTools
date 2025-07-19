using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models.OpenRouter;
using AchieveAi.LmDotnetTools.OpenAIProvider.Utils;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Middleware;

/// <summary>
/// Middleware for automatically tracking usage data from OpenRouter API calls.
/// Injects usage tracking flags and enriches responses with usage information.
/// </summary>
public class OpenRouterUsageMiddleware : IStreamingMiddleware, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenRouterUsageMiddleware> _logger;
    private readonly UsageCache _usageCache;
    private const int MaxRetryCount = 6;
    private const int RetryDelayMs = 500;
    private const int StreamingTimeoutMs = 3000;
    private const int SyncTimeoutMs = 5000;

    /// <summary>
    /// Initializes a new instance of the OpenRouterUsageMiddleware.
    /// </summary>
    /// <param name="openRouterApiKey">OpenRouter API key for usage lookup</param>
    /// <param name="logger">Logger for structured logging</param>
    /// <param name="httpClient">Optional HttpClient for testing</param>
    /// <param name="usageCache">Optional UsageCache for testing</param>
    /// <param name="cacheTtlSeconds">Cache TTL in seconds (default: 300, can be overridden by USAGE_CACHE_TTL_SEC env var)</param>
    public OpenRouterUsageMiddleware(
        string openRouterApiKey, 
        ILogger<OpenRouterUsageMiddleware> logger, 
        HttpClient? httpClient = null,
        UsageCache? usageCache = null,
        int cacheTtlSeconds = 300)
    {
        if (string.IsNullOrEmpty(openRouterApiKey))
            throw new ArgumentException("OpenRouter API key cannot be null or empty", nameof(openRouterApiKey));

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", openRouterApiKey);
        
        // Read cache TTL from environment variable or use parameter/default (Requirement 7.2)
        var envCacheTtl = EnvironmentVariableHelper.GetEnvironmentVariableWithFallback("USAGE_CACHE_TTL_SEC", null, cacheTtlSeconds.ToString());
        if (int.TryParse(envCacheTtl, out var parsedTtl) && parsedTtl > 0)
        {
            cacheTtlSeconds = parsedTtl;
        }
        
        _usageCache = usageCache ?? new UsageCache(cacheTtlSeconds);
        
        _logger.LogDebug("OpenRouterUsageMiddleware initialized with cache TTL: {CacheTtlSeconds} seconds", cacheTtlSeconds);
    }

    /// <summary>
    /// Gets the name of the middleware.
    /// </summary>
    public string? Name => "OpenRouterUsageMiddleware";

    /// <summary>
    /// Invokes the middleware for synchronous scenarios.
    /// </summary>
    public async Task<IEnumerable<IMessage>> InvokeAsync(
        MiddlewareContext context,
        IAgent agent,
        CancellationToken cancellationToken = default)
    {
        // Inject usage tracking into options
        var modifiedOptions = InjectUsageTracking(context.Options);
        var modifiedContext = new MiddlewareContext(context.Messages, modifiedOptions);

        // Generate reply with usage tracking enabled
        var messages = await agent.GenerateReplyAsync(
            modifiedContext.Messages,
            modifiedContext.Options,
            cancellationToken);

        return await ProcessMessagesAsync(messages, isStreaming: false, cancellationToken);
    }

    /// <summary>
    /// Invokes the middleware for streaming scenarios.
    /// </summary>
    public async Task<IAsyncEnumerable<IMessage>> InvokeStreamingAsync(
        MiddlewareContext context,
        IStreamingAgent agent,
        CancellationToken cancellationToken = default)
    {
        // Inject usage tracking into options
        var modifiedOptions = InjectUsageTracking(context.Options);
        var modifiedContext = new MiddlewareContext(context.Messages, modifiedOptions);

        // Generate streaming reply with usage tracking enabled
        var messageStream = await agent.GenerateReplyStreamingAsync(
            modifiedContext.Messages,
            modifiedContext.Options,
            cancellationToken);

        return ProcessStreamingMessagesAsync(messageStream, cancellationToken);
    }

    /// <summary>
    /// Injects usage tracking configuration into the request options.
    /// </summary>
    private GenerateReplyOptions InjectUsageTracking(GenerateReplyOptions? options)
    {
        var baseOptions = options ?? new GenerateReplyOptions();
        
        // Create usage tracking configuration
        var usageConfig = new Dictionary<string, object?>
        {
            ["include"] = true
        };

        // Inject into extra properties
        var usageOptions = new GenerateReplyOptions
        {
            ExtraProperties = ImmutableDictionary<string, object?>.Empty.Add("usage", usageConfig)
        };

        return baseOptions.Merge(usageOptions);
    }

    /// <summary>
    /// Processes messages for synchronous scenarios, holding back UsageMessage objects and emitting a single enhanced UsageMessage at the end.
    /// </summary>
    private async Task<IEnumerable<IMessage>> ProcessMessagesAsync(
        IEnumerable<IMessage> messages,
        bool isStreaming,
        CancellationToken cancellationToken)
    {
        var messageList = messages.ToList();
        if (!messageList.Any())
            return messageList;

        // Separate UsageMessage objects from other messages
        var nonUsageMessages = messageList.Where(m => !(m is UsageMessage)).ToList();
        var usageMessages = messageList.OfType<UsageMessage>().ToList();

        // Start with non-usage messages
        var result = new List<IMessage>(nonUsageMessages);

        // Create the final enhanced UsageMessage
        UsageMessage? finalUsageMessage = null;

        if (usageMessages.Any())
        {
            // We have UsageMessage(s) from the response - enhance the last one
            var lastUsageMessage = usageMessages.Last();
            _logger.LogDebug("Processing existing UsageMessage with {PromptTokens} prompt tokens and {CompletionTokens} completion tokens", 
                lastUsageMessage.Usage.PromptTokens, lastUsageMessage.Usage.CompletionTokens);
            finalUsageMessage = await CreateUsageMessageAsync(lastUsageMessage, isStreaming, cancellationToken);
        }
        else if (nonUsageMessages.Any())
        {
            // No UsageMessage in response - try to create one from the last non-usage message
            var lastMessage = nonUsageMessages.Last();
            finalUsageMessage = await CreateUsageMessageAsync(lastMessage, isStreaming, cancellationToken);
        }

        // Add the final enhanced UsageMessage
        if (finalUsageMessage != null)
        {
            result.Add(finalUsageMessage);
        }

        return result;
    }

    /// <summary>
    /// Processes streaming messages, holding back UsageMessage objects and emitting a single enhanced UsageMessage at the end.
    /// </summary>
    private async IAsyncEnumerable<IMessage> ProcessStreamingMessagesAsync(
        IAsyncEnumerable<IMessage> messageStream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IMessage? lastNonUsageMessage = null;
        UsageMessage? bufferedUsageMessage = null;

        await foreach (var message in messageStream.WithCancellation(cancellationToken))
        {
            if (message is UsageMessage usageMsg)
            {
                // Hold back UsageMessage - don't emit it yet
                bufferedUsageMessage = usageMsg;
                _logger.LogDebug("Buffered UsageMessage with {PromptTokens} prompt tokens and {CompletionTokens} completion tokens", 
                    usageMsg.Usage.PromptTokens, usageMsg.Usage.CompletionTokens);
            }
            else
            {
                // Forward non-UsageMessage immediately
                yield return message;
                lastNonUsageMessage = message;
            }
        }

        // Now create the final enhanced UsageMessage
        UsageMessage? finalUsageMessage = null;

        if (bufferedUsageMessage != null)
        {
            // We have a UsageMessage from the stream - enhance it
            finalUsageMessage = await CreateUsageMessageAsync(bufferedUsageMessage, isStreaming: true, cancellationToken);
        }
        else if (lastNonUsageMessage != null)
        {
            // No UsageMessage in stream - try to create one from the last message
            finalUsageMessage = await CreateUsageMessageAsync(lastNonUsageMessage, isStreaming: true, cancellationToken);
        }

        // Emit the final enhanced UsageMessage
        if (finalUsageMessage != null)
        {
            yield return finalUsageMessage;
        }
    }

    /// <summary>
    /// Creates a UsageMessage with usage information if available.
    /// </summary>
    private async Task<UsageMessage?> CreateUsageMessageAsync(
        IMessage message,
        bool isStreaming,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("[DIAGNOSTIC] CreateUsageMessageAsync called with message type: {MessageType}", message.GetType().Name);
        
        // Extract completion ID from metadata
        var completionId = GetCompletionId(message);
        _logger.LogDebug("[DIAGNOSTIC] Completion ID extracted: {CompletionId}", completionId ?? "NULL");
        
        if (string.IsNullOrEmpty(completionId))
            return null;

        // Check for inline usage data first (Requirement 2.2 & 2.3)
        var inlineUsage = GetInlineUsageFromMessage(message);
        _logger.LogDebug("[DIAGNOSTIC] Inline usage found: {HasInlineUsage}, TotalTokens: {TotalTokens}", 
            inlineUsage != null, inlineUsage?.TotalTokens ?? 0);
            
        if (inlineUsage != null && inlineUsage.TotalTokens > 0)
        {
            _logger.LogDebug("[DIAGNOSTIC] Using inline usage data - skipping enhancement");
            
            // Log successful inline usage enrichment with structured format (Requirement 11.1)
            _logger.LogInformation("Usage data enriched: {{completionId: {CompletionId}, model: {Model}, promptTokens: {PromptTokens}, completionTokens: {CompletionTokens}, totalCost: {TotalCost:F6}, cached: {Cached}}}",
                completionId, 
                inlineUsage.ExtraProperties?.GetValueOrDefault("model")?.ToString() ?? "unknown",
                inlineUsage.PromptTokens,
                inlineUsage.CompletionTokens,
                inlineUsage.TotalCost ?? 0.0,
                "false"); // inline usage is never cached

            // Create UsageMessage with inline usage data (Requirement 5.2)
            return CreateUsageMessage(message, inlineUsage, completionId);
        }

        // Check if message already has usage data from other sources (e.g., existing UsageMessage)
        var existingUsage = GetUsageFromMessage(message);
        _logger.LogDebug("[DIAGNOSTIC] Existing usage found: {HasExistingUsage}, TotalTokens: {TotalTokens}, TotalCost: {TotalCost}", 
            existingUsage != null, existingUsage?.TotalTokens ?? 0, existingUsage?.TotalCost);
            
        if (existingUsage != null && existingUsage.TotalTokens > 0)
        {
            // Check if we need to enhance with OpenRouter cost data
            // We should enhance if:
            // 1. No cost information is available (TotalCost is null or 0), OR
            // 2. This is a UsageMessage (indicating it came from upstream middleware) and we want to add OpenRouter-specific cost data
            bool shouldEnhanceWithOpenRouter = 
                (existingUsage.TotalCost == null || existingUsage.TotalCost == 0.0) ||
                (message is UsageMessage);

            _logger.LogDebug("[DIAGNOSTIC] Enhancement check: TotalCost={TotalCost}, IsUsageMessage={IsUsageMessage}, ShouldEnhance={ShouldEnhance}", 
                existingUsage.TotalCost, message is UsageMessage, shouldEnhanceWithOpenRouter);

            if (shouldEnhanceWithOpenRouter)
            {
                _logger.LogDebug("[DIAGNOSTIC] Attempting OpenRouter enhancement for completion {CompletionId}", completionId);
                
                // Try to get enhanced usage from OpenRouter generation endpoint
                var openRouterUsage = await GetCostStatsWithRetryAsync(completionId, isStreaming, cancellationToken);
                
                _logger.LogDebug("[DIAGNOSTIC] OpenRouter API result: {HasResult}, PromptTokens: {PromptTokens}, TotalCost: {TotalCost}", 
                    openRouterUsage != null, openRouterUsage?.PromptTokens ?? 0, openRouterUsage?.TotalCost);
                
                if (openRouterUsage != null)
                {
                    _logger.LogDebug("[DIAGNOSTIC] Merging usage data - existing vs OpenRouter");
                    
                    // Merge existing usage with OpenRouter cost data
                    var enhancedUsage = MergeUsageData(existingUsage, openRouterUsage);
                    
                    _logger.LogDebug("[DIAGNOSTIC] Merged result: PromptTokens: {PromptTokens}, TotalCost: {TotalCost}", 
                        enhancedUsage.PromptTokens, enhancedUsage.TotalCost);
                    
                    // Log successful usage enhancement
                    _logger.LogInformation("Usage data enhanced with OpenRouter cost: {{completionId: {CompletionId}, model: {Model}, promptTokens: {PromptTokens}, completionTokens: {CompletionTokens}, originalCost: {OriginalCost:F6}, enhancedCost: {EnhancedCost:F6}, cached: {Cached}}}",
                        completionId, 
                        enhancedUsage.ExtraProperties?.GetValueOrDefault("model")?.ToString() ?? "unknown",
                        enhancedUsage.PromptTokens,
                        enhancedUsage.CompletionTokens,
                        existingUsage.TotalCost ?? 0.0,
                        enhancedUsage.TotalCost ?? 0.0,
                        enhancedUsage.ExtraProperties?.GetValueOrDefault("is_cached")?.ToString() ?? "false");

                    return CreateUsageMessage(message, enhancedUsage, completionId);
                }
                else
                {
                    _logger.LogDebug("[DIAGNOSTIC] OpenRouter API returned no data - using existing usage");
                }
            }
            else
            {
                _logger.LogDebug("[DIAGNOSTIC] Enhancement not needed - using existing usage as-is");
            }

            // Return existing usage if no enhancement was needed or possible
            return CreateUsageMessage(message, existingUsage, completionId);
        }

        _logger.LogDebug("[DIAGNOSTIC] No existing usage found - attempting fallback to OpenRouter API");

        // Try to get usage from OpenRouter generation endpoint as fallback
        var fallbackUsage = await GetCostStatsWithRetryAsync(completionId, isStreaming, cancellationToken);
        if (fallbackUsage == null)
            return null;

        // Log successful usage enrichment with structured format (Requirement 11.1)
        _logger.LogInformation("Usage data enriched: {{completionId: {CompletionId}, model: {Model}, promptTokens: {PromptTokens}, completionTokens: {CompletionTokens}, totalCost: {TotalCost:F6}, cached: {Cached}}}",
            completionId, 
            fallbackUsage.ExtraProperties?.GetValueOrDefault("model")?.ToString() ?? "unknown",
            fallbackUsage.PromptTokens,
            fallbackUsage.CompletionTokens,
            fallbackUsage.TotalCost ?? 0.0,
            fallbackUsage.ExtraProperties?.GetValueOrDefault("is_cached")?.ToString() ?? "false");

        // Create UsageMessage with fallback usage data
        return CreateUsageMessage(message, fallbackUsage, completionId);
    }

    /// <summary>
    /// Extracts completion ID from message metadata.
    /// </summary>
    private static string? GetCompletionId(IMessage message)
    {
        return message.GenerationId ?? 
               message.Metadata?.GetValueOrDefault("completion_id") as string ??
               message.Metadata?.GetValueOrDefault("id") as string;
    }

    /// <summary>
    /// Extracts inline usage information from a message response (Requirement 2.2).
    /// This looks for usage data that came directly from the OpenRouter API response.
    /// </summary>
    private static Usage? GetInlineUsageFromMessage(IMessage message)
    {
        // Check for inline usage in message metadata with "inline_usage" key
        if (message.Metadata?.ContainsKey("inline_usage") == true)
        {
            var usageData = message.Metadata["inline_usage"];
            if (usageData is Usage usage)
                return usage;
            
            // Handle case where usage is a raw dictionary from JSON deserialization
            if (usageData is Dictionary<string, object?> usageDict)
            {
                return ParseUsageFromDictionary(usageDict);
            }
        }

        // Also check standard "usage" field but prefer inline_usage
        if (message.Metadata?.ContainsKey("usage") == true)
        {
            var usageData = message.Metadata["usage"];
            if (usageData is Usage usage)
                return usage;
                
            // Handle case where usage is a raw dictionary from JSON deserialization
            if (usageData is Dictionary<string, object?> usageDict)
            {
                return ParseUsageFromDictionary(usageDict);
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts usage information from a message (for non-inline sources).
    /// </summary>
    private static Usage? GetUsageFromMessage(IMessage message)
    {
        if (message is UsageMessage usageMsg)
            return usageMsg.Usage;

        if (message.Metadata?.ContainsKey("usage") == true)
        {
            var usageData = message.Metadata["usage"];
            if (usageData is Usage usage)
                return usage;
        }

        return null;
    }

    /// <summary>
    /// Parses usage from a dictionary (typically from JSON deserialization).
    /// </summary>
    private static Usage? ParseUsageFromDictionary(Dictionary<string, object?> usageDict)
    {
        try
        {
            var promptTokens = GetIntValue(usageDict, "prompt_tokens");
            var completionTokens = GetIntValue(usageDict, "completion_tokens");
            var totalTokens = GetIntValue(usageDict, "total_tokens");
            var totalCost = GetDoubleValue(usageDict, "total_cost");

            return new Usage
            {
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                TotalTokens = totalTokens > 0 ? totalTokens : promptTokens + completionTokens,
                TotalCost = totalCost,
                ExtraProperties = ImmutableDictionary<string, object?>.Empty
                    .Add("source", "inline")
            };
        }
        catch
        {
            return null;
        }
    }

    private static int GetIntValue(Dictionary<string, object?> dict, string key)
    {
        if (dict.TryGetValue(key, out var value))
        {
            return value switch
            {
                int intVal => intVal,
                long longVal => (int)longVal,
                double doubleVal => (int)doubleVal,
                string strVal when int.TryParse(strVal, out var parsed) => parsed,
                _ => 0
            };
        }
        return 0;
    }

    private static double? GetDoubleValue(Dictionary<string, object?> dict, string key)
    {
        if (dict.TryGetValue(key, out var value))
        {
            return value switch
            {
                double doubleVal => doubleVal,
                float floatVal => floatVal,
                int intVal => intVal,
                long longVal => longVal,
                string strVal when double.TryParse(strVal, out var parsed) => parsed,
                _ => null
            };
        }
        return null;
    }

    /// <summary>
    /// Gets cost stats from OpenRouter generation endpoint with retry logic (Requirement 3.1-3.4).
    /// Checks cache first, then falls back to API calls with retry logic.
    /// </summary>
    private async Task<Usage?> GetCostStatsWithRetryAsync(
        string completionId,
        bool isStreaming,
        CancellationToken cancellationToken)
    {
        // Check cache first (Requirement 7.3)
        var cachedUsage = _usageCache.TryGetUsage(completionId);
        if (cachedUsage != null)
        {
            _logger.LogDebug("Cache hit for completion {CompletionId}", completionId);
            
            // Log successful cached usage enrichment with structured format (Requirement 11.1)
            _logger.LogInformation("Usage data enriched: {{completionId: {CompletionId}, model: {Model}, promptTokens: {PromptTokens}, completionTokens: {CompletionTokens}, totalCost: {TotalCost:F6}, cached: {Cached}}}",
                completionId, 
                cachedUsage.ExtraProperties?.GetValueOrDefault("model")?.ToString() ?? "unknown",
                cachedUsage.PromptTokens,
                cachedUsage.CompletionTokens,
                cachedUsage.TotalCost ?? 0.0,
                "true"); // cached usage is always marked as cached
                
            return cachedUsage;
        }

        // Cache miss - proceed with API fallback
        _logger.LogDebug("Cache miss for completion {CompletionId}, attempting API fallback", completionId);

        for (int attempt = 0; attempt <= MaxRetryCount; attempt++)
        {
            try
            {
                var usage = await GetUsageFromGenerationEndpointAsync(completionId, isStreaming, cancellationToken);
                if (usage != null)
                {
                    // Cache successful response (Requirement 7.1)
                    _usageCache.SetUsage(completionId, usage);
                    _logger.LogDebug("Cached usage for completion {CompletionId}", completionId);
                    return usage;
                }
            }
            catch (Exception ex)
            {
                // Log warning for retry attempts (Requirement 9.1-9.2)
                _logger.LogWarning("Attempt {Attempt}/{MaxRetries} failed for completion {CompletionId}: {ErrorMessage}",
                    attempt + 1, MaxRetryCount + 1, completionId, ex.Message);
            }

            if (attempt < MaxRetryCount)
            {
                await Task.Delay(RetryDelayMs, cancellationToken);
            }
        }

        // Log final failure after all retries exhausted (Requirement 11.2)
        _logger.LogWarning("Usage middleware failure: all {MaxRetries} retries exhausted for completion {CompletionId}",
            MaxRetryCount + 1, completionId);

        // Increment usage_middleware_failure counter via structured logging (Requirement 11.2)
        _logger.LogWarning("Counter increment: usage_middleware_failure for completion {CompletionId} - reason: retry_exhaustion", 
            completionId);

        return null;
    }

    /// <summary>
    /// Calls OpenRouter generation endpoint to get usage data.
    /// </summary>
    private async Task<Usage?> GetUsageFromGenerationEndpointAsync(
        string completionId,
        bool isStreaming,
        CancellationToken cancellationToken)
    {
        var timeout = isStreaming ? StreamingTimeoutMs : SyncTimeoutMs;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            var response = await _httpClient.GetAsync(
                $"https://openrouter.ai/api/v1/generation?id={completionId}",
                timeoutCts.Token);

            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            var statsResponse = JsonSerializer.Deserialize<OpenRouterStatsResponse>(json);

            if (statsResponse?.Data == null)
                return null;

            return MapStatsToUsage(statsResponse.Data);
        }
        catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Timeout occurred
            return null;
        }
        catch (Exception)
        {
            // Network or other errors
            return null;
        }
    }

    /// <summary>
    /// Maps OpenRouter stats data to Usage object.
    /// </summary>
    private static Usage MapStatsToUsage(OpenRouterStatsData stats)
    {
        return new Usage
        {
            PromptTokens = stats.TokensPrompt,
            CompletionTokens = stats.TokensCompletion,
            TotalTokens = stats.TokensPrompt + stats.TokensCompletion,
            TotalCost = stats.TotalCost,
            ExtraProperties = ImmutableDictionary<string, object?>.Empty
                .Add("model", stats.Model)
                .Add("generation_time", stats.GenerationTime)
                .Add("streamed", stats.Streamed)
                .Add("created_at", stats.CreatedAt)
                .Add("is_cached", false)
        };
    }

    /// <summary>
    /// Maps OpenRouter stats data to OpenUsage object (Requirement 4.1-4.2).
    /// </summary>
    private static OpenUsage MapStatsToOpenUsage(OpenRouterStatsData stats)
    {
        return new OpenUsage
        {
            ModelId = stats.Model,
            PromptTokens = stats.TokensPrompt,
            CompletionTokens = stats.TokensCompletion,
            TotalCost = stats.TotalCost,
            IsCached = false
        };
    }

    /// <summary>
    /// Creates a UsageMessage with usage information (Requirement 5.2).
    /// </summary>
    private static UsageMessage CreateUsageMessage(IMessage originalMessage, Usage usage, string completionId)
    {
        return new UsageMessage
        {
            Usage = usage,
            Role = Role.Assistant,
            FromAgent = originalMessage.FromAgent,
            GenerationId = completionId,
            Metadata = ImmutableDictionary<string, object>.Empty
                .Add("source", "openrouter_middleware")
        };
    }

    /// <summary>
    /// Creates enriched messages with both Usage and OpenUsage information (Requirement 4.1-4.2, 5.2).
    /// Returns a collection that includes the original enriched message plus a UsageMessage.
    /// </summary>
    private static IEnumerable<IMessage> CreateEnrichedMessages(IMessage originalMessage, OpenRouterStatsData stats)
    {
        var usage = MapStatsToUsage(stats);
        var openUsage = MapStatsToOpenUsage(stats);
        var completionId = GetCompletionId(originalMessage);

        // First, yield the original message unchanged
        yield return originalMessage;

        // Then, yield a dedicated UsageMessage following LmDotnet patterns
        yield return new UsageMessage
        {
            Usage = usage,
            Role = Role.Assistant,
            FromAgent = originalMessage.FromAgent,
            GenerationId = completionId ?? originalMessage.GenerationId,
            Metadata = ImmutableDictionary<string, object>.Empty
                .Add("open_usage", openUsage)
                .Add("source", "openrouter_fallback")
        };
    }

    /// <summary>
    /// Merges two Usage objects, prioritizing the 'new' usage for fields that are not zero or null.
    /// Handles cases where OpenRouter provides revised token counts by logging warnings and using the updated values.
    /// </summary>
    private Usage MergeUsageData(Usage existingUsage, Usage newUsage)
    {
        // Check for token count discrepancies and log warnings if found
        bool hasTokenDiscrepancies = false;
        
        if (existingUsage.PromptTokens != 0 && newUsage.PromptTokens != 0 && existingUsage.PromptTokens != newUsage.PromptTokens)
        {
            _logger.LogWarning("Token count discrepancy detected: Existing PromptTokens={ExistingPromptTokens}, OpenRouter PromptTokens={NewPromptTokens}. Using OpenRouter values as they are typically more accurate.",
                existingUsage.PromptTokens, newUsage.PromptTokens);
            hasTokenDiscrepancies = true;
        }
        
        if (existingUsage.CompletionTokens != 0 && newUsage.CompletionTokens != 0 && existingUsage.CompletionTokens != newUsage.CompletionTokens)
        {
            _logger.LogWarning("Token count discrepancy detected: Existing CompletionTokens={ExistingCompletionTokens}, OpenRouter CompletionTokens={NewCompletionTokens}. Using OpenRouter values as they are typically more accurate.",
                existingUsage.CompletionTokens, newUsage.CompletionTokens);
            hasTokenDiscrepancies = true;
        }
        
        if (existingUsage.TotalTokens != 0 && newUsage.TotalTokens != 0 && existingUsage.TotalTokens != newUsage.TotalTokens)
        {
            _logger.LogWarning("Token count discrepancy detected: Existing TotalTokens={ExistingTotalTokens}, OpenRouter TotalTokens={NewTotalTokens}. Using OpenRouter values as they are typically more accurate.",
                existingUsage.TotalTokens, newUsage.TotalTokens);
            hasTokenDiscrepancies = true;
        }

        // Determine which values to use - prioritize OpenRouter data when available and non-zero
        var finalPromptTokens = newUsage.PromptTokens > 0 ? newUsage.PromptTokens : existingUsage.PromptTokens;
        var finalCompletionTokens = newUsage.CompletionTokens > 0 ? newUsage.CompletionTokens : existingUsage.CompletionTokens;
        var finalTotalTokens = newUsage.TotalTokens > 0 ? newUsage.TotalTokens : existingUsage.TotalTokens;
        
        // If we used OpenRouter token counts, recalculate total tokens to ensure consistency
        if (newUsage.PromptTokens > 0 && newUsage.CompletionTokens > 0)
        {
            finalTotalTokens = finalPromptTokens + finalCompletionTokens;
        }

        return new Usage
        {
            PromptTokens = finalPromptTokens,
            CompletionTokens = finalCompletionTokens,
            TotalTokens = finalTotalTokens,
            TotalCost = newUsage.TotalCost ?? existingUsage.TotalCost,
            ExtraProperties = MergeExtraProperties(existingUsage.ExtraProperties, newUsage.ExtraProperties, hasTokenDiscrepancies)
        };
    }

    /// <summary>
    /// Merges extra properties from two Usage objects, prioritizing new values.
    /// </summary>
    private static ImmutableDictionary<string, object?> MergeExtraProperties(
        ImmutableDictionary<string, object?>? existing, 
        ImmutableDictionary<string, object?>? newProps,
        bool hasTokenDiscrepancies = false)
    {
        var result = existing ?? ImmutableDictionary<string, object?>.Empty;
        
        if (newProps != null)
        {
            result = result.AddRange(newProps);
        }
        
        // Always mark that this was enhanced by the OpenRouter middleware
        result = result.SetItem("enhanced_by", "openrouter_middleware");
        
        // Add a flag if token discrepancies were found and resolved
        if (hasTokenDiscrepancies)
        {
            result = result.SetItem("token_discrepancies_resolved", true);
            result = result.SetItem("resolution_strategy", "used_openrouter_values");
        }
        
        return result;
    }

    /// <summary>
    /// Disposes of the HttpClient and UsageCache.
    /// </summary>
    public void Dispose()
    {
        _httpClient?.Dispose();
        _usageCache?.Dispose();
    }
} 