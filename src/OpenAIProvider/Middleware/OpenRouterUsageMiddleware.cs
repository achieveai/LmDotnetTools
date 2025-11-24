using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models.OpenRouter;
using AchieveAi.LmDotnetTools.OpenAIProvider.Utils;
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
        int cacheTtlSeconds = 300
    )
    {
        if (string.IsNullOrEmpty(openRouterApiKey))
        {
            throw new ArgumentException("OpenRouter API key cannot be null or empty", nameof(openRouterApiKey));
        }

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", openRouterApiKey);

        // Read cache TTL from environment variable or use parameter/default (Requirement 7.2)
        var envCacheTtl = EnvironmentVariableHelper.GetEnvironmentVariableWithFallback(
            "USAGE_CACHE_TTL_SEC",
            null,
            cacheTtlSeconds.ToString()
        );
        if (int.TryParse(envCacheTtl, out var parsedTtl) && parsedTtl > 0)
        {
            cacheTtlSeconds = parsedTtl;
        }

        _usageCache = usageCache ?? new UsageCache(cacheTtlSeconds);

        _logger.LogInformation(
            "OpenRouterUsageMiddleware initialized: CacheTtl={CacheTtlSeconds}s, ApiKeyConfigured={ApiKeyConfigured}",
            cacheTtlSeconds,
            !string.IsNullOrEmpty(openRouterApiKey)
        );
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
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(agent);

        // Inject usage tracking into options
        var modifiedOptions = InjectUsageTracking(context.Options);
        var modifiedContext = new MiddlewareContext(context.Messages, modifiedOptions);

        // Generate reply with usage tracking enabled
        var messages = await agent.GenerateReplyAsync(
            modifiedContext.Messages,
            modifiedContext.Options,
            cancellationToken
        );

        return await ProcessMessagesAsync(messages, isStreaming: false, cancellationToken);
    }

    /// <summary>
    /// Invokes the middleware for streaming scenarios.
    /// </summary>
    public async Task<IAsyncEnumerable<IMessage>> InvokeStreamingAsync(
        MiddlewareContext context,
        IStreamingAgent agent,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(agent);

        // Inject usage tracking into options
        var modifiedOptions = InjectUsageTracking(context.Options);
        var modifiedContext = new MiddlewareContext(context.Messages, modifiedOptions);

        // Generate streaming reply with usage tracking enabled
        var messageStream = await agent.GenerateReplyStreamingAsync(
            modifiedContext.Messages,
            modifiedContext.Options,
            cancellationToken
        );

        return ProcessStreamingMessagesAsync(messageStream, cancellationToken);
    }

    /// <summary>
    /// Injects usage tracking configuration into the request options.
    /// </summary>
    private static GenerateReplyOptions InjectUsageTracking(GenerateReplyOptions? options)
    {
        var baseOptions = options ?? new GenerateReplyOptions();

        // Create usage tracking configuration
        var usageConfig = new Dictionary<string, object?> { ["include"] = true };

        // Inject into extra properties
        var usageOptions = new GenerateReplyOptions
        {
            ExtraProperties = ImmutableDictionary<string, object?>.Empty.Add("usage", usageConfig),
        };

        return baseOptions.Merge(usageOptions);
    }

    /// <summary>
    /// Processes messages for synchronous scenarios, holding back UsageMessage objects and emitting a single enhanced UsageMessage at the end.
    /// </summary>
    private async Task<IEnumerable<IMessage>> ProcessMessagesAsync(
        IEnumerable<IMessage> messages,
        bool isStreaming,
        CancellationToken cancellationToken
    )
    {
        var messageList = messages.ToList();
        if (messageList.Count == 0)
        {
            return messageList;
        }

        // Separate UsageMessage objects from other messages
        var nonUsageMessages = messageList.Where(m => m is not UsageMessage).ToList();
        var usageMessages = messageList.OfType<UsageMessage>().ToList();

        _logger.LogDebug(
            "Processing messages: Total={TotalMessages}, NonUsage={NonUsageMessages}, Usage={UsageMessages}, IsStreaming={IsStreaming}",
            messageList.Count,
            nonUsageMessages.Count,
            usageMessages.Count,
            isStreaming
        );

        // Start with non-usage messages
        var result = new List<IMessage>(nonUsageMessages);

        // Create the final enhanced UsageMessage
        UsageMessage? finalUsageMessage = null;

        if (usageMessages.Count != 0)
        {
            // We have UsageMessage(s) from the response - enhance the last one
            var lastUsageMessage = usageMessages.Last();
            _logger.LogDebug(
                "Processing existing UsageMessage: PromptTokens={PromptTokens}, CompletionTokens={CompletionTokens}, TotalCost={TotalCost}",
                lastUsageMessage.Usage.PromptTokens,
                lastUsageMessage.Usage.CompletionTokens,
                lastUsageMessage.Usage.TotalCost
            );
            finalUsageMessage = await CreateUsageMessageAsync(lastUsageMessage, isStreaming, cancellationToken);
        }
        else if (nonUsageMessages.Count != 0)
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
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        IMessage? lastNonUsageMessage = null;
        UsageMessage? bufferedUsageMessage = null;

        await foreach (var message in messageStream.WithCancellation(cancellationToken))
        {
            if (message is UsageMessage usageMsg)
            {
                // Hold back UsageMessage - don't emit it yet
                bufferedUsageMessage = usageMsg;
                _logger.LogDebug(
                    "Buffered UsageMessage: PromptTokens={PromptTokens}, CompletionTokens={CompletionTokens}, TotalCost={TotalCost}",
                    usageMsg.Usage.PromptTokens,
                    usageMsg.Usage.CompletionTokens,
                    usageMsg.Usage.TotalCost
                );
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
            finalUsageMessage = await CreateUsageMessageAsync(
                bufferedUsageMessage,
                isStreaming: true,
                cancellationToken
            );
        }
        else if (lastNonUsageMessage != null)
        {
            // No UsageMessage in stream - try to create one from the last message
            finalUsageMessage = await CreateUsageMessageAsync(
                lastNonUsageMessage,
                isStreaming: true,
                cancellationToken
            );
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
        CancellationToken cancellationToken
    )
    {
        var startTime = DateTimeOffset.UtcNow;
        _logger.LogDebug(
            "Creating usage message: MessageType={MessageType}, IsStreaming={IsStreaming}",
            message.GetType().Name,
            isStreaming
        );

        // Extract completion ID from metadata
        var completionId = GetCompletionId(message);
        if (string.IsNullOrEmpty(completionId))
        {
            _logger.LogDebug(
                "No completion ID found in message metadata: Duration={TotalDurationMs}ms",
                (DateTimeOffset.UtcNow - startTime).TotalMilliseconds
            );
            return null;
        }

        _logger.LogDebug("Processing usage for completion: CompletionId={CompletionId}", completionId);
        // Check for inline usage data first (Requirement 2.2 & 2.3)
        var inlineUsage = GetInlineUsageFromMessage(message);
        _logger.LogDebug(
            "Inline usage check: Found={HasInlineUsage}, TotalTokens={TotalTokens}",
            inlineUsage != null,
            inlineUsage?.TotalTokens ?? 0
        );

        if (inlineUsage != null && inlineUsage.TotalTokens > 0)
        {
            _logger.LogDebug(
                "Using inline usage data: PromptTokens={PromptTokens}, CompletionTokens={CompletionTokens}, TotalCost={TotalCost}",
                inlineUsage.PromptTokens,
                inlineUsage.CompletionTokens,
                inlineUsage.TotalCost
            );

            // Log successful inline usage enrichment with structured format (Requirement 11.1)
            _logger.LogInformation(
                "Usage data enriched: {{completionId: {CompletionId}, model: {Model}, promptTokens: {PromptTokens}, completionTokens: {CompletionTokens}, totalCost: {TotalCost:F6}, cached: {Cached}}}",
                completionId,
                inlineUsage.ExtraProperties?.GetValueOrDefault("model")?.ToString() ?? "unknown",
                inlineUsage.PromptTokens,
                inlineUsage.CompletionTokens,
                inlineUsage.TotalCost ?? 0.0,
                "false"
            ); // inline usage is never cached

            // Create UsageMessage with inline usage data (Requirement 5.2)
            var inlineResult = CreateUsageMessage(message, inlineUsage, completionId);

            _logger.LogDebug(
                "Usage message creation complete: CompletionId={CompletionId}, TotalDuration={TotalDurationMs}ms, Source=inline",
                completionId,
                (DateTimeOffset.UtcNow - startTime).TotalMilliseconds
            );

            return inlineResult;
        }

        // Check if message already has usage data from other sources (e.g., existing UsageMessage)
        var existingUsage = GetUsageFromMessage(message);
        _logger.LogDebug(
            "Existing usage check: Found={HasExistingUsage}, TotalTokens={TotalTokens}, TotalCost={TotalCost}",
            existingUsage != null,
            existingUsage?.TotalTokens ?? 0,
            existingUsage?.TotalCost
        );

        if (existingUsage != null && existingUsage.TotalTokens > 0)
        {
            // Check if we need to enhance with OpenRouter cost data
            // We should enhance if:
            // 1. No cost information is available (TotalCost is null or 0), OR
            // 2. This is a UsageMessage (indicating it came from upstream middleware) and we want to add OpenRouter-specific cost data
            var shouldEnhanceWithOpenRouter =
                existingUsage.TotalCost == null || existingUsage.TotalCost == 0.0 || (message is UsageMessage);

            _logger.LogDebug(
                "Enhancement evaluation: TotalCost={TotalCost}, IsUsageMessage={IsUsageMessage}, ShouldEnhance={ShouldEnhance}",
                existingUsage.TotalCost,
                message is UsageMessage,
                shouldEnhanceWithOpenRouter
            );

            if (shouldEnhanceWithOpenRouter)
            {
                _logger.LogDebug("Attempting OpenRouter enhancement: CompletionId={CompletionId}", completionId);

                // Try to get enhanced usage from OpenRouter generation endpoint
                var openRouterUsage = await GetCostStatsWithRetryAsync(completionId, isStreaming, cancellationToken);

                _logger.LogDebug(
                    "OpenRouter API response: Success={HasResult}, PromptTokens={PromptTokens}, TotalCost={TotalCost}",
                    openRouterUsage != null,
                    openRouterUsage?.PromptTokens ?? 0,
                    openRouterUsage?.TotalCost
                );

                if (openRouterUsage != null)
                {
                    _logger.LogDebug(
                        "Merging usage data: ExistingTokens={ExistingTokens}, OpenRouterTokens={OpenRouterTokens}",
                        existingUsage.TotalTokens,
                        openRouterUsage.TotalTokens
                    );

                    // Merge existing usage with OpenRouter cost data
                    var enhancedUsage = MergeUsageData(existingUsage, openRouterUsage);

                    _logger.LogDebug(
                        "Usage merge complete: FinalPromptTokens={PromptTokens}, FinalTotalCost={TotalCost}",
                        enhancedUsage.PromptTokens,
                        enhancedUsage.TotalCost
                    );

                    // Log successful usage enhancement
                    _logger.LogInformation(
                        "Usage data enhanced with OpenRouter cost: {{completionId: {CompletionId}, model: {Model}, promptTokens: {PromptTokens}, completionTokens: {CompletionTokens}, originalCost: {OriginalCost:F6}, enhancedCost: {EnhancedCost:F6}, cached: {Cached}}}",
                        completionId,
                        enhancedUsage.ExtraProperties?.GetValueOrDefault("model")?.ToString() ?? "unknown",
                        enhancedUsage.PromptTokens,
                        enhancedUsage.CompletionTokens,
                        existingUsage.TotalCost ?? 0.0,
                        enhancedUsage.TotalCost ?? 0.0,
                        enhancedUsage.ExtraProperties?.GetValueOrDefault("is_cached")?.ToString() ?? "false"
                    );

                    var enhancedResult = CreateUsageMessage(message, enhancedUsage, completionId);

                    var enhancedElapsed = DateTimeOffset.UtcNow - startTime;
                    _logger.LogDebug(
                        "Usage message creation complete: CompletionId={CompletionId}, TotalDuration={TotalDurationMs}ms, Source=enhanced",
                        completionId,
                        enhancedElapsed.TotalMilliseconds
                    );

                    return enhancedResult;
                }
                else
                {
                    _logger.LogDebug("OpenRouter API returned no data, using existing usage");
                }
            }
            else
            {
                _logger.LogDebug("Enhancement not needed, using existing usage");
            }

            // Return existing usage if no enhancement was needed or possible
            var existingResult = CreateUsageMessage(message, existingUsage, completionId);

            var existingElapsed = DateTimeOffset.UtcNow - startTime;
            _logger.LogDebug(
                "Usage message creation complete: CompletionId={CompletionId}, TotalDuration={TotalDurationMs}ms, Source=existing",
                completionId,
                existingElapsed.TotalMilliseconds
            );

            return existingResult;
        }

        _logger.LogDebug("No existing usage found, attempting OpenRouter API fallback");

        // Try to get usage from OpenRouter generation endpoint as fallback
        var fallbackUsage = await GetCostStatsWithRetryAsync(completionId, isStreaming, cancellationToken);
        if (fallbackUsage == null)
        {
            var fallbackElapsed = DateTimeOffset.UtcNow - startTime;
            _logger.LogDebug(
                "OpenRouter API fallback returned no data: CompletionId={CompletionId}, TotalDuration={TotalDurationMs}ms",
                completionId,
                fallbackElapsed.TotalMilliseconds
            );
            return null;
        }

        // Log successful usage enrichment with structured format (Requirement 11.1)
        _logger.LogInformation(
            "Usage data enriched: {{completionId: {CompletionId}, model: {Model}, promptTokens: {PromptTokens}, completionTokens: {CompletionTokens}, totalCost: {TotalCost:F6}, cached: {Cached}}}",
            completionId,
            fallbackUsage.ExtraProperties?.GetValueOrDefault("model")?.ToString() ?? "unknown",
            fallbackUsage.PromptTokens,
            fallbackUsage.CompletionTokens,
            fallbackUsage.TotalCost ?? 0.0,
            fallbackUsage.ExtraProperties?.GetValueOrDefault("is_cached")?.ToString() ?? "false"
        );

        // Create UsageMessage with fallback usage data
        var fallbackResult = CreateUsageMessage(message, fallbackUsage, completionId);

        var finalElapsed = DateTimeOffset.UtcNow - startTime;
        _logger.LogDebug(
            "Usage message creation complete: CompletionId={CompletionId}, TotalDuration={TotalDurationMs}ms, Source=fallback",
            completionId,
            finalElapsed.TotalMilliseconds
        );

        return fallbackResult;
    }

    /// <summary>
    /// Extracts completion ID from message metadata.
    /// </summary>
    private static string? GetCompletionId(IMessage message)
    {
        return message.GenerationId
            ?? message.Metadata?.GetValueOrDefault("completion_id") as string
            ?? message.Metadata?.GetValueOrDefault("id") as string;
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
            {
                return usage;
            }

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
            {
                return usage;
            }

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
        {
            return usageMsg.Usage;
        }

        if (message.Metadata?.ContainsKey("usage") == true)
        {
            var usageData = message.Metadata["usage"];
            if (usageData is Usage usage)
            {
                return usage;
            }
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
                ExtraProperties = ImmutableDictionary<string, object?>.Empty.Add("source", "inline"),
            };
        }
        catch
        {
            return null;
        }
    }

    private static int GetIntValue(Dictionary<string, object?> dict, string key)
    {
        return dict.TryGetValue(key, out var value)
            ? value switch
            {
                int intVal => intVal,
                long longVal => (int)longVal,
                double doubleVal => (int)doubleVal,
                string strVal when int.TryParse(strVal, out var parsed) => parsed,
                _ => 0,
            }
            : 0;
    }

    private static double? GetDoubleValue(Dictionary<string, object?> dict, string key)
    {
        return dict.TryGetValue(key, out var value)
            ? value switch
            {
                double doubleVal => doubleVal,
                float floatVal => floatVal,
                int intVal => intVal,
                long longVal => longVal,
                string strVal when double.TryParse(strVal, out var parsed) => parsed,
                _ => null,
            }
            : null;
    }

    /// <summary>
    /// Gets cost stats from OpenRouter generation endpoint with retry logic (Requirement 3.1-3.4).
    /// Checks cache first, then falls back to API calls with retry logic.
    /// </summary>
    private async Task<Usage?> GetCostStatsWithRetryAsync(
        string completionId,
        bool isStreaming,
        CancellationToken cancellationToken
    )
    {
        // Check cache first (Requirement 7.3)
        var cacheStartTime = DateTimeOffset.UtcNow;
        var cachedUsage = _usageCache.TryGetUsage(completionId);
        var cacheElapsed = DateTimeOffset.UtcNow - cacheStartTime;

        if (cachedUsage != null)
        {
            _logger.LogDebug(
                "Cache hit: CompletionId={CompletionId}, PromptTokens={PromptTokens}, CompletionTokens={CompletionTokens}, TotalCost={TotalCost}, CacheLookupDuration={CacheDurationMs}ms",
                completionId,
                cachedUsage.PromptTokens,
                cachedUsage.CompletionTokens,
                cachedUsage.TotalCost,
                cacheElapsed.TotalMilliseconds
            );

            // Log successful cached usage enrichment with structured format (Requirement 11.1)
            _logger.LogInformation(
                "Usage data enriched: {{completionId: {CompletionId}, model: {Model}, promptTokens: {PromptTokens}, completionTokens: {CompletionTokens}, totalCost: {TotalCost:F6}, cached: {Cached}}}",
                completionId,
                cachedUsage.ExtraProperties?.GetValueOrDefault("model")?.ToString() ?? "unknown",
                cachedUsage.PromptTokens,
                cachedUsage.CompletionTokens,
                cachedUsage.TotalCost ?? 0.0,
                "true"
            ); // cached usage is always marked as cached

            return cachedUsage;
        }

        // Cache miss - proceed with API fallback
        _logger.LogDebug(
            "Cache miss: CompletionId={CompletionId}, CacheLookupDuration={CacheDurationMs}ms, attempting API fallback",
            completionId,
            cacheElapsed.TotalMilliseconds
        );

        var totalStartTime = DateTimeOffset.UtcNow;

        for (var attempt = 0; attempt <= MaxRetryCount; attempt++)
        {
            var attemptStartTime = DateTimeOffset.UtcNow;

            try
            {
                var usage = await GetUsageFromGenerationEndpointAsync(completionId, isStreaming, cancellationToken);
                if (usage != null)
                {
                    var totalElapsed = DateTimeOffset.UtcNow - totalStartTime;
                    var attemptElapsed = DateTimeOffset.UtcNow - attemptStartTime;

                    _logger.LogDebug(
                        "API call performance: CompletionId={CompletionId}, Attempt={Attempt}, AttemptDuration={AttemptDurationMs}ms, TotalDuration={TotalDurationMs}ms",
                        completionId,
                        attempt + 1,
                        attemptElapsed.TotalMilliseconds,
                        totalElapsed.TotalMilliseconds
                    );

                    // Cache successful response (Requirement 7.1)
                    _usageCache.SetUsage(completionId, usage);
                    _logger.LogDebug(
                        "Cached usage data: CompletionId={CompletionId}, PromptTokens={PromptTokens}, CompletionTokens={CompletionTokens}, TotalCost={TotalCost}",
                        completionId,
                        usage.PromptTokens,
                        usage.CompletionTokens,
                        usage.TotalCost
                    );
                    return usage;
                }

                var failedAttemptElapsed = DateTimeOffset.UtcNow - attemptStartTime;
                _logger.LogDebug(
                    "API call returned no data: CompletionId={CompletionId}, Attempt={Attempt}, Duration={DurationMs}ms",
                    completionId,
                    attempt + 1,
                    failedAttemptElapsed.TotalMilliseconds
                );
            }
            catch (Exception ex)
            {
                var failedAttemptElapsed = DateTimeOffset.UtcNow - attemptStartTime;

                // Log warning for retry attempts (Requirement 9.1-9.2)
                _logger.LogWarning(
                    "Attempt {Attempt}/{MaxRetries} failed for completion {CompletionId}: {ErrorMessage}, Duration={DurationMs}ms",
                    attempt + 1,
                    MaxRetryCount + 1,
                    completionId,
                    ex.Message,
                    failedAttemptElapsed.TotalMilliseconds
                );
            }

            if (attempt < MaxRetryCount)
            {
                _logger.LogDebug(
                    "Retry delay: CompletionId={CompletionId}, Attempt={Attempt}, DelayMs={DelayMs}ms",
                    completionId,
                    attempt + 1,
                    RetryDelayMs
                );
                await Task.Delay(RetryDelayMs, cancellationToken);
            }
        }

        var totalFailedElapsed = DateTimeOffset.UtcNow - totalStartTime;
        _logger.LogWarning(
            "All retry attempts failed: CompletionId={CompletionId}, TotalAttempts={TotalAttempts}, TotalDuration={TotalDurationMs}ms",
            completionId,
            MaxRetryCount + 1,
            totalFailedElapsed.TotalMilliseconds
        );

        // Log final failure after all retries exhausted (Requirement 11.2)
        _logger.LogWarning(
            "Usage middleware failure: all {MaxRetries} retries exhausted for completion {CompletionId}",
            MaxRetryCount + 1,
            completionId
        );

        // Increment usage_middleware_failure counter via structured logging (Requirement 11.2)
        _logger.LogWarning(
            "Counter increment: usage_middleware_failure for completion {CompletionId} - reason: retry_exhaustion",
            completionId
        );

        return null;
    }

    /// <summary>
    /// Calls OpenRouter generation endpoint to get usage data.
    /// </summary>
    private async Task<Usage?> GetUsageFromGenerationEndpointAsync(
        string completionId,
        bool isStreaming,
        CancellationToken cancellationToken
    )
    {
        var timeout = isStreaming ? StreamingTimeoutMs : SyncTimeoutMs;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        _logger.LogDebug(
            "Calling OpenRouter API: CompletionId={CompletionId}, Timeout={TimeoutMs}ms, IsStreaming={IsStreaming}",
            completionId,
            timeout,
            isStreaming
        );

        try
        {
            var response = await _httpClient.GetAsync(
                $"https://openrouter.ai/api/v1/generation?id={completionId}",
                timeoutCts.Token
            );

            _logger.LogDebug(
                "OpenRouter API response: CompletionId={CompletionId}, StatusCode={StatusCode}, IsSuccess={IsSuccess}",
                completionId,
                (int)response.StatusCode,
                response.IsSuccessStatusCode
            );

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var readStartTime = DateTimeOffset.UtcNow;
            var json = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            var readElapsed = DateTimeOffset.UtcNow - readStartTime;

            _logger.LogDebug(
                "OpenRouter API response data: CompletionId={CompletionId}, ResponseLength={ResponseLength}, ReadDuration={ReadDurationMs}ms",
                completionId,
                json?.Length ?? 0,
                readElapsed.TotalMilliseconds
            );

            var deserializeStartTime = DateTimeOffset.UtcNow;
            var statsResponse = !string.IsNullOrEmpty(json)
                ? JsonSerializer.Deserialize<OpenRouterStatsResponse>(json)
                : null;
            var deserializeElapsed = DateTimeOffset.UtcNow - deserializeStartTime;

            if (statsResponse?.Data == null)
            {
                _logger.LogDebug(
                    "OpenRouter API response parsing: CompletionId={CompletionId}, HasStatsResponse={HasStats}, HasData={HasData}, DeserializeDuration={DeserializeDurationMs}ms",
                    completionId,
                    statsResponse != null,
                    false,
                    deserializeElapsed.TotalMilliseconds
                );
                return null;
            }

            _logger.LogDebug(
                "JSON deserialization performance: CompletionId={CompletionId}, DeserializeDuration={DeserializeDurationMs}ms",
                completionId,
                deserializeElapsed.TotalMilliseconds
            );

            _logger.LogDebug(
                "OpenRouter API data extraction: CompletionId={CompletionId}, Model={Model}, PromptTokens={PromptTokens}, CompletionTokens={CompletionTokens}, TotalCost={TotalCost}",
                completionId,
                statsResponse.Data.Model,
                statsResponse.Data.TokensPrompt,
                statsResponse.Data.TokensCompletion,
                statsResponse.Data.TotalCost
            );

            return MapStatsToUsage(statsResponse.Data);
        }
        catch (OperationCanceledException)
            when (timeoutCts.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
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
            ExtraProperties = ImmutableDictionary<string, object?>
                .Empty.Add("model", stats.Model)
                .Add("generation_time", stats.GenerationTime)
                .Add("streamed", stats.Streamed)
                .Add("created_at", stats.CreatedAt)
                .Add("is_cached", false),
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
            IsCached = false,
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
            Metadata = ImmutableDictionary<string, object>.Empty.Add("source", "openrouter_middleware"),
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
            Metadata = ImmutableDictionary<string, object>
                .Empty.Add("open_usage", openUsage)
                .Add("source", "openrouter_fallback"),
        };
    }

    /// <summary>
    /// Merges two Usage objects, prioritizing the 'new' usage for fields that are not zero or null.
    /// Handles cases where OpenRouter provides revised token counts by logging warnings and using the updated values.
    /// </summary>
    private Usage MergeUsageData(Usage existingUsage, Usage newUsage)
    {
        _logger.LogDebug(
            "Merging usage data: ExistingPrompt={ExistingPrompt}, ExistingCompletion={ExistingCompletion}, ExistingCost={ExistingCost}, NewPrompt={NewPrompt}, NewCompletion={NewCompletion}, NewCost={NewCost}",
            existingUsage.PromptTokens,
            existingUsage.CompletionTokens,
            existingUsage.TotalCost,
            newUsage.PromptTokens,
            newUsage.CompletionTokens,
            newUsage.TotalCost
        );

        // Check for token count discrepancies and log warnings if found
        var hasTokenDiscrepancies = false;

        if (
            existingUsage.PromptTokens != 0
            && newUsage.PromptTokens != 0
            && existingUsage.PromptTokens != newUsage.PromptTokens
        )
        {
            _logger.LogWarning(
                "Token count discrepancy detected: Existing PromptTokens={ExistingPromptTokens}, OpenRouter PromptTokens={NewPromptTokens}. Using OpenRouter values as they are typically more accurate.",
                existingUsage.PromptTokens,
                newUsage.PromptTokens
            );
            hasTokenDiscrepancies = true;
        }

        if (
            existingUsage.CompletionTokens != 0
            && newUsage.CompletionTokens != 0
            && existingUsage.CompletionTokens != newUsage.CompletionTokens
        )
        {
            _logger.LogWarning(
                "Token count discrepancy detected: Existing CompletionTokens={ExistingCompletionTokens}, OpenRouter CompletionTokens={NewCompletionTokens}. Using OpenRouter values as they are typically more accurate.",
                existingUsage.CompletionTokens,
                newUsage.CompletionTokens
            );
            hasTokenDiscrepancies = true;
        }

        if (
            existingUsage.TotalTokens != 0
            && newUsage.TotalTokens != 0
            && existingUsage.TotalTokens != newUsage.TotalTokens
        )
        {
            _logger.LogWarning(
                "Token count discrepancy detected: Existing TotalTokens={ExistingTotalTokens}, OpenRouter TotalTokens={NewTotalTokens}. Using OpenRouter values as they are typically more accurate.",
                existingUsage.TotalTokens,
                newUsage.TotalTokens
            );
            hasTokenDiscrepancies = true;
        }

        // Determine which values to use - prioritize OpenRouter data when available and non-zero
        var finalPromptTokens = newUsage.PromptTokens > 0 ? newUsage.PromptTokens : existingUsage.PromptTokens;
        var finalCompletionTokens =
            newUsage.CompletionTokens > 0 ? newUsage.CompletionTokens : existingUsage.CompletionTokens;
        var finalTotalTokens = newUsage.TotalTokens > 0 ? newUsage.TotalTokens : existingUsage.TotalTokens;

        // If we used OpenRouter token counts, recalculate total tokens to ensure consistency
        if (newUsage.PromptTokens > 0 && newUsage.CompletionTokens > 0)
        {
            finalTotalTokens = finalPromptTokens + finalCompletionTokens;
        }

        var finalCost = newUsage.TotalCost ?? existingUsage.TotalCost;

        _logger.LogDebug(
            "Usage merge result: FinalPrompt={FinalPrompt}, FinalCompletion={FinalCompletion}, FinalTotal={FinalTotal}, FinalCost={FinalCost}, HasDiscrepancies={HasDiscrepancies}",
            finalPromptTokens,
            finalCompletionTokens,
            finalTotalTokens,
            finalCost,
            hasTokenDiscrepancies
        );

        return new Usage
        {
            PromptTokens = finalPromptTokens,
            CompletionTokens = finalCompletionTokens,
            TotalTokens = finalTotalTokens,
            TotalCost = finalCost,
            ExtraProperties = MergeExtraProperties(
                existingUsage.ExtraProperties,
                newUsage.ExtraProperties,
                hasTokenDiscrepancies
            ),
        };
    }

    /// <summary>
    /// Merges extra properties from two Usage objects, prioritizing new values.
    /// </summary>
    private static ImmutableDictionary<string, object?> MergeExtraProperties(
        ImmutableDictionary<string, object?>? existing,
        ImmutableDictionary<string, object?>? newProps,
        bool hasTokenDiscrepancies = false
    )
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
