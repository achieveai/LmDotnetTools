using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmConfig.Models;

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
        ILogger<UnifiedAgent> logger)
    {
        _modelResolver = modelResolver ?? throw new ArgumentNullException(nameof(modelResolver));
        _agentFactory = agentFactory ?? throw new ArgumentNullException(nameof(agentFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IEnumerable<IMessage>> GenerateReplyAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var (messageList, resolution, agent, updatedOptions) = await PrepareForGenerationAsync(messages, options, cancellationToken);
        
        try
        {
            _logger.LogDebug("Delegating GenerateReplyAsync to {AgentType} for model {ModelId} (effective: {EffectiveModelName})", 
                agent.GetType().Name, options?.ModelId ?? "default", resolution.EffectiveModelName);

            return await agent.GenerateReplyAsync(messageList, updatedOptions, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating reply for model {ModelId}", options?.ModelId ?? "default");
            throw;
        }
    }

    public async Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var (messageList, resolution, _, updatedOptions) = await PrepareForGenerationAsync(messages, options, cancellationToken);
        var streamingAgent = await ResolveStreamingAgentAsync(options, cancellationToken);

        try
        {
            _logger.LogDebug("Delegating GenerateReplyStreamingAsync to {AgentType} for model {ModelId} (effective: {EffectiveModelName})", 
                streamingAgent.GetType().Name, options?.ModelId ?? "default", resolution.EffectiveModelName);

            return await streamingAgent.GenerateReplyStreamingAsync(messageList, updatedOptions, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating streaming reply for model {ModelId}", options?.ModelId ?? "default");
            throw;
        }
    }

    /// <summary>
    /// Common preparation logic for both generation methods.
    /// </summary>
    private async Task<(List<IMessage> messageList, ProviderResolution resolution, IAgent agent, GenerateReplyOptions updatedOptions)> 
        PrepareForGenerationAsync(IEnumerable<IMessage> messages, GenerateReplyOptions? options, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var messageList = ValidateMessages(messages);
        
        var resolution = await ResolveProviderAsync(options, cancellationToken);
        var agent = await ResolveAgentAsync(options, cancellationToken);
        var updatedOptions = CreateUpdatedOptions(options, resolution);
        
        return (messageList, resolution, agent, updatedOptions);
    }

    /// <summary>
    /// Validates messages and returns them as a list.
    /// </summary>
    private static List<IMessage> ValidateMessages(IEnumerable<IMessage> messages)
    {
        if (messages == null)
            throw new ArgumentNullException(nameof(messages));

        var messageList = messages.ToList();
        if (!messageList.Any())
            throw new ArgumentException("Messages cannot be empty", nameof(messages));

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
        CancellationToken cancellationToken = default)
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
        CancellationToken cancellationToken = default)
    {
        return (IStreamingAgent)await ResolveAgentInternalAsync<IStreamingAgent>(options, cancellationToken, true);
    }

    /// <summary>
    /// Generic agent resolution logic to eliminate duplication.
    /// </summary>
    private async Task<IAgent> ResolveAgentInternalAsync<T>(
        GenerateReplyOptions? options,
        CancellationToken cancellationToken,
        bool isStreaming) where T : IAgent
    {
        var resolution = await ResolveProviderAsync(options, cancellationToken);
        var cacheKey = isStreaming ? GetStreamingCacheKey(resolution) : GetCacheKey(resolution);

        if (!_agentCache.TryGetValue(cacheKey, out var agent))
        {
            agent = isStreaming 
                ? _agentFactory.CreateStreamingAgent(resolution)
                : _agentFactory.CreateAgent(resolution);
            _agentCache[cacheKey] = agent;
            
            var agentType = isStreaming ? "streaming agent" : "agent";
            _logger.LogDebug("Created and cached {AgentType} for {Resolution}", agentType, resolution.ToString());
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
        CancellationToken cancellationToken = default)
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
        CancellationToken cancellationToken = default)
    {
        return await _modelResolver.GetAvailableProvidersAsync(modelId, criteria, cancellationToken);
    }

    private async Task<ProviderResolution> ResolveProviderAsync(
        GenerateReplyOptions? options,
        CancellationToken cancellationToken)
    {
        // Extract model ID from options
        var modelId = options?.ModelId;
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new InvalidOperationException(
                "Model ID must be specified in GenerateReplyOptions.ModelId");
        }

        // Create selection criteria from options if needed
        var criteria = CreateSelectionCriteriaFromOptions(options);

        // Resolve the provider
        var resolution = await _modelResolver.ResolveProviderAsync(modelId, criteria, cancellationToken);
        if (resolution == null)
        {
            throw new InvalidOperationException(
                $"No suitable provider found for model '{modelId}'. " +
                "Check that the model is configured and at least one provider is available.");
        }

        return resolution;
    }

    private ProviderSelectionCriteria? CreateSelectionCriteriaFromOptions(GenerateReplyOptions? options)
    {
        if (options?.ExtraProperties == null || !options.ExtraProperties.Any())
            return null;

        var criteria = new ProviderSelectionCriteria();

        // Extract provider preferences from extra properties
        if (options.ExtraProperties.TryGetValue("preferred_providers", out var preferredProviders) &&
            preferredProviders is IEnumerable<string> providers)
        {
            criteria = criteria with { IncludeOnlyProviders = providers.ToList() };
        }

        if (options.ExtraProperties.TryGetValue("excluded_providers", out var excludedProviders) &&
            excludedProviders is IEnumerable<string> excluded)
        {
            criteria = criteria with { ExcludeProviders = excluded.ToList() };
        }

        if (options.ExtraProperties.TryGetValue("prefer_lower_cost", out var preferCost) &&
            preferCost is bool preferLowerCost)
        {
            criteria = criteria with { PreferLowerCost = preferLowerCost };
        }

        if (options.ExtraProperties.TryGetValue("prefer_higher_performance", out var preferPerf) &&
            preferPerf is bool preferHigherPerformance)
        {
            criteria = criteria with { PreferHigherPerformance = preferHigherPerformance };
        }

        if (options.ExtraProperties.TryGetValue("required_tags", out var reqTags) &&
            reqTags is IEnumerable<string> requiredTags)
        {
            criteria = criteria with { RequiredTags = requiredTags.ToList() };
        }

        if (options.ExtraProperties.TryGetValue("preferred_tags", out var prefTags) &&
            prefTags is IEnumerable<string> preferredTags)
        {
            criteria = criteria with { PreferredTags = preferredTags.ToList() };
        }

        return criteria;
    }

    private string GetCacheKey(ProviderResolution resolution)
    {
        return $"agent_{resolution.EffectiveProviderName}_{resolution.EffectiveModelName}";
    }

    private string GetStreamingCacheKey(ProviderResolution resolution)
    {
        return $"streaming_agent_{resolution.EffectiveProviderName}_{resolution.EffectiveModelName}";
    }

    /// <summary>
    /// Creates updated GenerateReplyOptions with the correct ModelId for the resolved provider.
    /// </summary>
    /// <param name="originalOptions">The original options from the user.</param>
    /// <param name="resolution">The provider resolution containing the effective model name.</param>
    /// <returns>Updated options with the correct ModelId for the provider.</returns>
    private GenerateReplyOptions CreateUpdatedOptions(GenerateReplyOptions? originalOptions, ProviderResolution resolution)
    {
        // If no original options, create new ones with just the effective model name
        if (originalOptions == null)
        {
            return new GenerateReplyOptions
            {
                ModelId = resolution.EffectiveModelName
            };
        }

        // Create a copy of the original options with the updated ModelId
        // This ensures the provider agent gets the correct model name (e.g., "openai/gpt-4.1" for OpenRouter)
        return originalOptions with
        {
            ModelId = resolution.EffectiveModelName
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
                        _logger.LogWarning(ex, "Error disposing agent {AgentType}", agent.GetType().Name);
                    }
                }
            }

            _agentCache.Clear();
            _disposed = true;
        }
    }
} 