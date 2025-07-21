using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmConfig.Models;
using AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAIProvider.Middleware;
using AchieveAi.LmDotnetTools.OpenAIProvider.Configuration;
using AchieveAi.LmDotnetTools.LmConfig.Http;

namespace AchieveAi.LmDotnetTools.LmConfig.Agents;

/// <summary>
/// Factory implementation that creates appropriate agents for resolved providers.
/// Supports Anthropic, OpenAI, and OpenAI-compatible providers.
/// </summary>
public class ProviderAgentFactory : IProviderAgentFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ProviderAgentFactory> _logger;
    private readonly IHttpHandlerBuilder _handlerBuilder;

    // Mapping of provider names to their compatibility types
    private static readonly Dictionary<string, string> ProviderCompatibility = new()
    {
        { "OpenAI", "OpenAI" },
        { "Anthropic", "Anthropic" },
        { "OpenRouter", "OpenAI" },
        { "DeepInfra", "OpenAI" },
        { "Groq", "OpenAI" },
        { "Cerebras", "OpenAI" },
        { "GoogleGemini", "OpenAI" },
        { "DeepSeek", "OpenAI" },
        { "Alibaba Cloud", "OpenAI" },
        { "Together AI", "OpenAI" },
        { "Fireworks AI", "OpenAI" },
        { "Hyperbolic", "OpenAI" },
        { "NovitaAI", "OpenAI" },
        { "Chutes", "OpenAI" },
        { "Replicate", "Replicate" }
    };

    public ProviderAgentFactory(IServiceProvider serviceProvider, ILogger<ProviderAgentFactory> logger, IHttpHandlerBuilder handlerBuilder)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _handlerBuilder = handlerBuilder ?? throw new ArgumentNullException(nameof(handlerBuilder));
    }

    public IAgent CreateAgent(ProviderResolution resolution)
    {
        if (resolution == null)
            throw new ArgumentNullException(nameof(resolution));

        var compatibilityType = GetCompatibilityType(resolution);
        
        _logger.LogDebug("Creating agent for provider {Provider} with compatibility {Compatibility}", 
            resolution.EffectiveProviderName, compatibilityType);

        return compatibilityType switch
        {
            "Anthropic" => CreateAnthropicAgent(resolution),
            "OpenAI" => CreateOpenAIAgent(resolution),
            "Replicate" => throw new NotSupportedException("Replicate provider is not yet supported"),
            _ => throw new NotSupportedException($"Provider compatibility type '{compatibilityType}' is not supported")
        };
    }

    public IStreamingAgent CreateStreamingAgent(ProviderResolution resolution)
    {
        var agent = CreateAgent(resolution);
        
        if (agent is IStreamingAgent streamingAgent)
            return streamingAgent;

        throw new NotSupportedException(
            $"Provider '{resolution.EffectiveProviderName}' does not support streaming agents");
    }

    public bool CanCreateAgent(string providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
            return false;

        return ProviderCompatibility.ContainsKey(providerName) && 
               ProviderCompatibility[providerName] != "Replicate"; // Replicate not supported yet
    }

    public bool CanCreateStreamingAgent(string providerName)
    {
        // All supported providers currently support streaming
        return CanCreateAgent(providerName);
    }

    public IReadOnlyList<string> GetSupportedProviders()
    {
        return ProviderCompatibility.Keys
            .Where(provider => ProviderCompatibility[provider] != "Replicate")
            .ToList();
    }

    public ProviderCapabilityInfo? GetProviderCapabilities(string providerName)
    {
        if (!ProviderCompatibility.TryGetValue(providerName, out var compatibility))
            return null;

        return new ProviderCapabilityInfo
        {
            Name = providerName,
            SupportsBasicAgent = compatibility != "Replicate",
            SupportsStreamingAgent = compatibility != "Replicate",
            CompatibilityType = compatibility,
            Notes = compatibility == "Replicate" ? "Not yet supported" : null
        };
    }

    private string GetCompatibilityType(ProviderResolution resolution)
    {
        // First check if the connection info specifies compatibility
        if (!string.IsNullOrWhiteSpace(resolution.Connection.Compatibility))
            return resolution.Connection.Compatibility;

        // Fall back to provider name mapping
        if (ProviderCompatibility.TryGetValue(resolution.EffectiveProviderName, out var compatibility))
            return compatibility;

        // Default to OpenAI compatibility for unknown providers
        _logger.LogWarning("Unknown provider {Provider}, defaulting to OpenAI compatibility", 
            resolution.EffectiveProviderName);
        return "OpenAI";
    }

    private IAgent CreateAnthropicAgent(ProviderResolution resolution)
    {
        try
        {
            // Create Anthropic client
            var client = CreateAnthropicClient(resolution);
            
            // Create agent with the resolved model name
            var agentName = $"{resolution.EffectiveProviderName}-{resolution.EffectiveModelName}";
            return new AnthropicAgent(agentName, client);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create Anthropic agent for {Provider}", resolution.EffectiveProviderName);
            throw new InvalidOperationException(
                $"Failed to create Anthropic agent for provider '{resolution.EffectiveProviderName}': {ex.Message}", ex);
        }
    }

    private IAgent CreateOpenAIAgent(ProviderResolution resolution)
    {
        try
        {
            // Create OpenAI client
            var client = CreateOpenAIClient(resolution);
            
            // Create agent with the resolved model name
            var agentName = $"{resolution.EffectiveProviderName}-{resolution.EffectiveModelName}";
            var agent = new OpenClientAgent(agentName, client) as IAgent;

            // Automatically inject OpenRouter usage middleware if this is an OpenRouter provider
            // and the middleware is enabled (Requirement 12.1-12.2)
            if (string.Equals(resolution.EffectiveProviderName, "OpenRouter", StringComparison.OrdinalIgnoreCase))
            {
                agent = InjectOpenRouterUsageMiddlewareIfEnabled(agent);
            }

            return agent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create OpenAI agent for {Provider}", resolution.EffectiveProviderName);
            throw new InvalidOperationException(
                $"Failed to create OpenAI agent for provider '{resolution.EffectiveProviderName}': {ex.Message}", ex);
        }
    }

    private IAnthropicClient CreateAnthropicClient(ProviderResolution resolution)
    {
        var apiKey = resolution.Connection.GetApiKey() ?? throw new InvalidOperationException("API key not found.");
        var providerCfg = new Http.ProviderConfig(apiKey, resolution.Connection.EndpointUrl, Http.ProviderType.Anthropic);
        var httpClient = Http.HttpClientFactory.Create(providerCfg, _handlerBuilder, resolution.Connection.Timeout, resolution.Connection.Headers, _logger);
        return new AnthropicClient(httpClient);
    }

    private IOpenClient CreateOpenAIClient(ProviderResolution resolution)
    {
        var apiKey = resolution.Connection.GetApiKey() ?? throw new InvalidOperationException("API key not found.");
        var providerCfg = new Http.ProviderConfig(apiKey, resolution.Connection.EndpointUrl, Http.ProviderType.OpenAI);
        var httpClient = Http.HttpClientFactory.Create(providerCfg, _handlerBuilder, resolution.Connection.Timeout, resolution.Connection.Headers, _logger);
        return new OpenClient(httpClient, resolution.Connection.EndpointUrl);
    }

    /// <summary>
    /// Injects OpenRouter usage middleware if enabled via configuration (Requirement 12.1-12.2).
    /// </summary>
    /// <param name="agent">The base agent to wrap with middleware</param>
    /// <returns>The agent, optionally wrapped with OpenRouter usage middleware</returns>
    private IAgent InjectOpenRouterUsageMiddlewareIfEnabled(IAgent agent)
    {
        try
        {
            // Get configuration from DI
            var configuration = _serviceProvider.GetService<IConfiguration>();
            if (configuration == null)
            {
                _logger.LogWarning("IConfiguration not available in DI container. Skipping OpenRouter usage middleware injection.");
                return agent;
            }

            // Check if usage middleware is enabled
            var enableUsageMiddleware = EnvironmentVariables.GetEnableUsageMiddleware(configuration);
            if (!enableUsageMiddleware)
            {
                _logger.LogDebug("OpenRouter usage middleware is disabled");
                return agent;
            }

            // Get OpenRouter API key for usage lookup
            var openRouterApiKey = EnvironmentVariables.GetOpenRouterApiKey(configuration);
            if (string.IsNullOrWhiteSpace(openRouterApiKey))
            {
                _logger.LogWarning("OpenRouter usage middleware is enabled but OPENROUTER_API_KEY is missing. " +
                                 "Skipping middleware injection. Set ENABLE_USAGE_MIDDLEWARE=false to disable this warning.");
                return agent;
            }

            // Create and inject the usage middleware
            var middlewareLogger = _serviceProvider.GetService<ILogger<OpenRouterUsageMiddleware>>() ??
                                 throw new InvalidOperationException("Logger<OpenRouterUsageMiddleware> not found in DI container");

            var usageMiddleware = new OpenRouterUsageMiddleware(
                openRouterApiKey: openRouterApiKey,
                logger: middlewareLogger);

            _logger.LogDebug("Injecting OpenRouter usage middleware for agent");

            // Wrap the agent with middleware using the extension method
            if (agent is IStreamingAgent streamingAgent)
            {
                return streamingAgent.WithMiddleware(usageMiddleware);
            }

            throw new InvalidOperationException("OpenRouter usage middleware requires a streaming agent");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to inject OpenRouter usage middleware. Continuing without middleware.");
            return agent;
        }
    }
} 