using System.Diagnostics;
using AchieveAi.LmDotnetTools.LmConfig.Logging;
using AchieveAi.LmDotnetTools.LmConfig.Models;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmConfig.Agents;

/// <summary>
/// Unified agent that can work with any model/provider combination by using
/// ModelResolver to pick the best provider and delegating to appropriate provider-specific agents.
/// </summary>
public class UnifiedAgent : IStreamingAgent, IDisposable
{
    private readonly IModelResolver _modelResolver;
    private readonly IProviderAgentFactory _agentFactory;
    private readonly ILogger<UnifiedAgent> _logger;
    private readonly Dictionary<string, IAgent> _agentCache = new();
    private bool _disposed = false;

    public UnifiedAgent(
        IModelResolver modelResolver,
        IProviderAgentFactory agentFactory,
        ILogger<UnifiedAgent>? logger = null
    )
    {
        _modelResolver = modelResolver ?? throw new ArgumentNullException(nameof(modelResolver));
        _agentFactory = agentFactory ?? throw new ArgumentNullException(nameof(agentFactory));
        _logger = logger ?? NullLogger<UnifiedAgent>.Instance;
    }

    public async Task<IEnumerable<IMessage>> GenerateReplyAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var stopwatch = Stopwatch.StartNew();
        var messageList = ValidateMessages(messages);

        _logger.LogInformation(
            LogEventIds.AgentRequestInitiated,
            "LLM request initiated: Model={ModelId}, MessageCount={MessageCount}, Type={RequestType}",
            options?.ModelId ?? "default",
            messageList.Count,
            "non-streaming"
        );

        var (_, resolution, agent, updatedOptions) = await PrepareForGenerationAsync(
            messages,
            options,
            cancellationToken
        );

        try
        {
            _logger.LogInformation(
                LogEventIds.AgentDelegation,
                "Delegating to agent: AgentType={AgentType}, Model={ModelId}, EffectiveModel={EffectiveModelName}, Provider={ProviderName}",
                agent.GetType().Name,
                options?.ModelId ?? "default",
                resolution.EffectiveModelName,
                resolution.EffectiveProviderName
            );

            var result = await agent.GenerateReplyAsync(
                messageList,
                updatedOptions,
                cancellationToken
            );

            stopwatch.Stop();
            _logger.LogInformation(
                LogEventIds.AgentRequestCompleted,
                "LLM request completed: Model={ModelId}, Duration={Duration}ms, Provider={ProviderName}",
                options?.ModelId ?? "default",
                stopwatch.ElapsedMilliseconds,
                resolution.EffectiveProviderName
            );

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(
                LogEventIds.AgentRequestFailed,
                ex,
                "LLM request failed: Model={ModelId}, Duration={Duration}ms, Provider={ProviderName}",
                options?.ModelId ?? "default",
                stopwatch.ElapsedMilliseconds,
                resolution.EffectiveProviderName
            );
            throw;
        }
    }

    public async Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var stopwatch = Stopwatch.StartNew();
        var messageList = ValidateMessages(messages);

        _logger.LogInformation(
            LogEventIds.AgentRequestInitiated,
            "LLM request initiated: Model={ModelId}, MessageCount={MessageCount}, Type={RequestType}",
            options?.ModelId ?? "default",
            messageList.Count,
            "streaming"
        );

        var (_, resolution, _, updatedOptions) = await PrepareForGenerationAsync(
            messages,
            options,
            cancellationToken
        );
        var streamingAgent = await ResolveStreamingAgentAsync(options, cancellationToken);

        try
        {
            _logger.LogInformation(
                LogEventIds.AgentDelegation,
                "Delegating to streaming agent: AgentType={AgentType}, Model={ModelId}, EffectiveModel={EffectiveModelName}, Provider={ProviderName}",
                streamingAgent.GetType().Name,
                options?.ModelId ?? "default",
                resolution.EffectiveModelName,
                resolution.EffectiveProviderName
            );

            var result = await streamingAgent.GenerateReplyStreamingAsync(
                messageList,
                updatedOptions,
                cancellationToken
            );

            stopwatch.Stop();
            _logger.LogInformation(
                LogEventIds.AgentRequestCompleted,
                "LLM streaming request initiated: Model={ModelId}, Duration={Duration}ms, Provider={ProviderName}",
                options?.ModelId ?? "default",
                stopwatch.ElapsedMilliseconds,
                resolution.EffectiveProviderName
            );

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(
                LogEventIds.AgentRequestFailed,
                ex,
                "LLM streaming request failed: Model={ModelId}, Duration={Duration}ms, Provider={ProviderName}",
                options?.ModelId ?? "default",
                stopwatch.ElapsedMilliseconds,
                resolution.EffectiveProviderName
            );
            throw;
        }
    }

    /// <summary>
    /// Common preparation logic for both generation methods.
    /// </summary>
    private async Task<(
        List<IMessage> messageList,
        ProviderResolution resolution,
        IAgent agent,
        GenerateReplyOptions updatedOptions
    )> PrepareForGenerationAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options,
        CancellationToken cancellationToken
    )
    {
        ThrowIfDisposed();
        var messageList = ValidateMessages(messages);

        var resolution = await ResolveProviderAsync(options, cancellationToken);
        var agent = await ResolveAgentAsync(options, cancellationToken);
        var updatedOptions = UnifiedAgent.CreateUpdatedOptions(options, resolution);

        return (messageList, resolution, agent, updatedOptions);
    }

    /// <summary>
    /// Validates messages and returns them as a list.
    /// </summary>
    private List<IMessage> ValidateMessages(IEnumerable<IMessage> messages)
    {
        if (messages == null)
        {
            _logger.LogError("Message validation failed: Messages parameter is null");
            throw new ArgumentNullException(nameof(messages));
        }

        var messageList = messages.ToList();
        if (messageList.Count == 0)
        {
            _logger.LogError("Message validation failed: Messages collection is empty");
            throw new ArgumentException("Messages cannot be empty", nameof(messages));
        }

        _logger.LogDebug(
            "Message validation successful: MessageCount={MessageCount}",
            messageList.Count
        );
        return messageList;
    }

    /// <summary>
    /// Resolves the best provider for the given options and returns a configured agent.
    /// </summary>
    /// <param name="options">Generation options containing model ID and other preferences.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A configured agent for the resolved provider.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no suitable provider can be found.</exception>
    public async Task<IAgent> ResolveAgentAsync(
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        return await ResolveAgentInternalAsync<IAgent>(options, cancellationToken, false);
    }

    /// <summary>
    /// Resolves the best provider for the given options and returns a configured streaming agent.
    /// </summary>
    /// <param name="options">Generation options containing model ID and other preferences.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A configured streaming agent for the resolved provider.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no suitable provider can be found.</exception>
    public async Task<IStreamingAgent> ResolveStreamingAgentAsync(
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        return (IStreamingAgent)
            await ResolveAgentInternalAsync<IStreamingAgent>(options, cancellationToken, true);
    }

    /// <summary>
    /// Generic agent resolution logic to eliminate duplication.
    /// </summary>
    private async Task<IAgent> ResolveAgentInternalAsync<T>(
        GenerateReplyOptions? options,
        CancellationToken cancellationToken,
        bool isStreaming
    )
        where T : IAgent
    {
        var resolution = await ResolveProviderAsync(options, cancellationToken);
        var cacheKey = isStreaming ? UnifiedAgent.GetStreamingCacheKey(resolution) : UnifiedAgent.GetCacheKey(resolution);

        if (!_agentCache.TryGetValue(cacheKey, out var agent))
        {
            _logger.LogDebug(
                LogEventIds.AgentCacheMiss,
                "Agent cache miss: CacheKey={CacheKey}, Provider={ProviderName}, Model={EffectiveModelName}, IsStreaming={IsStreaming}",
                cacheKey,
                resolution.EffectiveProviderName,
                resolution.EffectiveModelName,
                isStreaming
            );

            try
            {
                agent = isStreaming
                    ? _agentFactory.CreateStreamingAgent(resolution)
                    : _agentFactory.CreateAgent(resolution);
                _agentCache[cacheKey] = agent;

                var agentType = isStreaming ? "streaming agent" : "agent";
                _logger.LogDebug(
                    "Created and cached {AgentType} for Provider={ProviderName}, Model={EffectiveModelName}",
                    agentType,
                    resolution.EffectiveProviderName,
                    resolution.EffectiveModelName
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    LogEventIds.AgentRequestFailed,
                    ex,
                    "Agent creation failed: Provider={ProviderName}, Model={EffectiveModelName}, IsStreaming={IsStreaming}, CacheKey={CacheKey}",
                    resolution.EffectiveProviderName,
                    resolution.EffectiveModelName,
                    isStreaming,
                    cacheKey
                );
                throw;
            }
        }
        else
        {
            _logger.LogDebug(
                LogEventIds.AgentCacheHit,
                "Agent cache hit: CacheKey={CacheKey}, Provider={ProviderName}, Model={EffectiveModelName}, IsStreaming={IsStreaming}",
                cacheKey,
                resolution.EffectiveProviderName,
                resolution.EffectiveModelName,
                isStreaming
            );
        }

        return agent;
    }

    /// <summary>
    /// Gets information about the resolved provider for the given options.
    /// </summary>
    /// <param name="options">Generation options containing model ID and other preferences.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Provider resolution information.</returns>
    public async Task<ProviderResolution> GetProviderResolutionAsync(
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        return await ResolveProviderAsync(options, cancellationToken);
    }

    /// <summary>
    /// Gets all available providers for the given model ID.
    /// </summary>
    /// <param name="modelId">The model ID to get providers for.</param>
    /// <param name="criteria">Optional selection criteria.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of available provider resolutions.</returns>
    public async Task<IReadOnlyList<ProviderResolution>> GetAvailableProvidersAsync(
        string modelId,
        ProviderSelectionCriteria? criteria = null,
        CancellationToken cancellationToken = default
    )
    {
        var availableProviders = await _modelResolver.GetAvailableProvidersAsync(
            modelId,
            criteria,
            cancellationToken
        );

        _logger.LogDebug(
            LogEventIds.AvailableProvidersEvaluated,
            "Available providers evaluated: ModelId={ModelId}, ProviderCount={ProviderCount}, Providers={Providers}",
            modelId,
            availableProviders.Count,
            string.Join(",", availableProviders.Select(p => p.EffectiveProviderName))
        );

        return availableProviders;
    }

    private async Task<ProviderResolution> ResolveProviderAsync(
        GenerateReplyOptions? options,
        CancellationToken cancellationToken
    )
    {
        var stopwatch = Stopwatch.StartNew();

        // Extract model ID from options
        var modelId = options?.ModelId;
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new InvalidOperationException(
                "Model ID must be specified in GenerateReplyOptions.ModelId"
            );
        }

        // Create selection criteria from options if needed
        var criteria = CreateSelectionCriteriaFromOptions(options);

        if (criteria != null)
        {
            _logger.LogDebug(
                LogEventIds.ProviderSelectionCriteria,
                "Provider selection criteria: ModelId={ModelId}, PreferLowerCost={PreferLowerCost}, PreferHigherPerformance={PreferHigherPerformance}, IncludeOnlyProviders={IncludeOnlyProviders}, ExcludeProviders={ExcludeProviders}",
                modelId,
                criteria.PreferLowerCost,
                criteria.PreferHigherPerformance,
                criteria.IncludeOnlyProviders != null
                    ? string.Join(",", criteria.IncludeOnlyProviders)
                    : "none",
                criteria.ExcludeProviders != null
                    ? string.Join(",", criteria.ExcludeProviders)
                    : "none"
            );
        }
        else
        {
            _logger.LogDebug(
                LogEventIds.ProviderSelectionCriteria,
                "Provider selection criteria: ModelId={ModelId}, Criteria={Criteria}",
                modelId,
                "default"
            );
        }

        try
        {
            // Resolve the provider
            var resolution = await _modelResolver.ResolveProviderAsync(
                modelId,
                criteria,
                cancellationToken
            );
            if (resolution == null)
            {
                stopwatch.Stop();
                _logger.LogError(
                    LogEventIds.ProviderResolutionFailed,
                    "Provider resolution failed: ModelId={ModelId}, Duration={Duration}ms, Reason={Reason}",
                    modelId,
                    stopwatch.ElapsedMilliseconds,
                    "No suitable provider found"
                );

                throw new InvalidOperationException(
                    $"No suitable provider found for model '{modelId}'. "
                        + "Check that the model is configured and at least one provider is available."
                );
            }

            stopwatch.Stop();
            _logger.LogInformation(
                LogEventIds.ProviderResolved,
                "Provider resolved: ModelId={ModelId}, Provider={ProviderName}, EffectiveModel={EffectiveModelName}, Duration={Duration}ms",
                modelId,
                resolution.EffectiveProviderName,
                resolution.EffectiveModelName,
                stopwatch.ElapsedMilliseconds
            );

            return resolution;
        }
        catch (Exception ex) when (!(ex is InvalidOperationException))
        {
            stopwatch.Stop();
            _logger.LogError(
                LogEventIds.ProviderResolutionFailed,
                ex,
                "Provider resolution failed: ModelId={ModelId}, Duration={Duration}ms",
                modelId,
                stopwatch.ElapsedMilliseconds
            );
            throw;
        }
    }

    private ProviderSelectionCriteria? CreateSelectionCriteriaFromOptions(
        GenerateReplyOptions? options
    )
    {
        if (options?.ExtraProperties == null || options.ExtraProperties.IsEmpty)
        {
            _logger.LogDebug(
                "Configuration resolution: No extra properties found, using default criteria"
            );
            return null;
        }

        _logger.LogDebug(
            "Configuration resolution: Processing {PropertyCount} extra properties: {Properties}",
            options.ExtraProperties.Count,
            string.Join(",", options.ExtraProperties.Keys)
        );

        var criteria = new ProviderSelectionCriteria();

        // Extract provider preferences from extra properties
        if (
            options.ExtraProperties.TryGetValue("preferred_providers", out var preferredProviders)
            && preferredProviders is IEnumerable<string> providers
        )
        {
            criteria = criteria with { IncludeOnlyProviders = providers.ToList() };
        }

        if (
            options.ExtraProperties.TryGetValue("excluded_providers", out var excludedProviders)
            && excludedProviders is IEnumerable<string> excluded
        )
        {
            criteria = criteria with { ExcludeProviders = excluded.ToList() };
        }

        if (
            options.ExtraProperties.TryGetValue("prefer_lower_cost", out var preferCost)
            && preferCost is bool preferLowerCost
        )
        {
            criteria = criteria with { PreferLowerCost = preferLowerCost };
        }

        if (
            options.ExtraProperties.TryGetValue("prefer_higher_performance", out var preferPerf)
            && preferPerf is bool preferHigherPerformance
        )
        {
            criteria = criteria with { PreferHigherPerformance = preferHigherPerformance };
        }

        if (
            options.ExtraProperties.TryGetValue("required_tags", out var reqTags)
            && reqTags is IEnumerable<string> requiredTags
        )
        {
            criteria = criteria with { RequiredTags = requiredTags.ToList() };
        }

        if (
            options.ExtraProperties.TryGetValue("preferred_tags", out var prefTags)
            && prefTags is IEnumerable<string> preferredTags
        )
        {
            criteria = criteria with { PreferredTags = preferredTags.ToList() };
        }

        return criteria;
    }

    private static string GetCacheKey(ProviderResolution resolution)
    {
        return $"agent_{resolution.EffectiveProviderName}_{resolution.EffectiveModelName}";
    }

    private static string GetStreamingCacheKey(ProviderResolution resolution)
    {
        return $"streaming_agent_{resolution.EffectiveProviderName}_{resolution.EffectiveModelName}";
    }

    /// <summary>
    /// Creates updated GenerateReplyOptions with the correct ModelId for the resolved provider.
    /// </summary>
    /// <param name="originalOptions">The original options from the user.</param>
    /// <param name="resolution">The provider resolution containing the effective model name.</param>
    /// <returns>Updated options with the correct ModelId for the provider.</returns>
    private static GenerateReplyOptions CreateUpdatedOptions(
        GenerateReplyOptions? originalOptions,
        ProviderResolution resolution
    )
    {
        // If no original options, create new ones with just the effective model name
        if (originalOptions == null)
        {
            return new GenerateReplyOptions { ModelId = resolution.EffectiveModelName };
        }

        // Create a copy of the original options with the updated ModelId
        // This ensures the provider agent gets the correct model name (e.g., "openai/gpt-4.1" for OpenRouter)
        return originalOptions with
        {
            ModelId = resolution.EffectiveModelName,
        };
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(UnifiedAgent));
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            // Dispose all cached agents
            foreach (var agent in _agentCache.Values)
            {
                if (agent is IDisposable disposableAgent)
                {
                    try
                    {
                        disposableAgent.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Error disposing agent: AgentType={AgentType}, CacheKey={CacheKey}",
                            agent.GetType().Name,
                            _agentCache.FirstOrDefault(kvp => kvp.Value == agent).Key ?? "unknown"
                        );
                    }
                }
            }

            _agentCache.Clear();
            _disposed = true;
        }
    }
}
