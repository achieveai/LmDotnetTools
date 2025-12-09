using AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
using AchieveAi.LmDotnetTools.LmConfig.Http;
using AchieveAi.LmDotnetTools.LmConfig.Models;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAIProvider.Configuration;
using AchieveAi.LmDotnetTools.OpenAIProvider.Middleware;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProviderConfig = AchieveAi.LmDotnetTools.LmConfig.Http.ProviderConfig;

namespace AchieveAi.LmDotnetTools.LmConfig.Agents;

/// <summary>
///     Factory implementation that creates appropriate agents for resolved providers.
///     Supports Anthropic, OpenAI, and OpenAI-compatible providers.
/// </summary>
public class ProviderAgentFactory : IProviderAgentFactory
{
    // Mapping of provider names to their compatibility types
    private static readonly Dictionary<string, string> ProviderCompatibility = new()
    {
        { "OpenAI", "OpenAI" },
        { "Anthropic", "Anthropic" },
        { "ClaudeAgentSDK", "ClaudeAgentSDK" },
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
        { "Replicate", "Replicate" },
    };

    private readonly IHttpHandlerBuilder _handlerBuilder;
    private readonly ILogger<ProviderAgentFactory> _logger;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly IServiceProvider _serviceProvider;

    public ProviderAgentFactory(
        IServiceProvider serviceProvider,
        IHttpHandlerBuilder handlerBuilder,
        ILoggerFactory? loggerFactory = null
    )
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _handlerBuilder = handlerBuilder ?? throw new ArgumentNullException(nameof(handlerBuilder));
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<ProviderAgentFactory>() ?? NullLogger<ProviderAgentFactory>.Instance;
    }

    public IAgent CreateAgent(ProviderResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        var compatibilityType = GetCompatibilityType(resolution);

        _logger.LogDebug(
            "Provider resolution and agent type selection: Provider={Provider}, Model={Model}, Compatibility={Compatibility}, EndpointUrl={EndpointUrl}, HasApiKey={HasApiKey}",
            resolution.EffectiveProviderName,
            resolution.EffectiveModelName,
            compatibilityType,
            resolution.Connection.EndpointUrl,
            !string.IsNullOrEmpty(resolution.Connection.GetApiKey())
        );

        return compatibilityType switch
        {
            "Anthropic" => CreateAnthropicAgent(resolution),
            "OpenAI" => CreateOpenAIAgent(resolution),
            "ClaudeAgentSDK" => CreateClaudeAgentSdkAgent(resolution),
            "Replicate" => throw new NotSupportedException("Replicate provider is not yet supported"),
            _ => throw new NotSupportedException($"Provider compatibility type '{compatibilityType}' is not supported"),
        };
    }

    public IStreamingAgent CreateStreamingAgent(ProviderResolution resolution)
    {
        var agent = CreateAgent(resolution);

        return agent is IStreamingAgent streamingAgent
            ? streamingAgent
            : throw new NotSupportedException(
                $"Provider '{resolution.EffectiveProviderName}' does not support streaming agents"
            );
    }

    public bool CanCreateAgent(string providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            return false;
        }

        return ProviderCompatibility.ContainsKey(providerName) && ProviderCompatibility[providerName] != "Replicate"; // Replicate not supported yet
    }

    public bool CanCreateStreamingAgent(string providerName)
    {
        // All supported providers currently support streaming
        return CanCreateAgent(providerName);
    }

    public IReadOnlyList<string> GetSupportedProviders()
    {
        return [.. ProviderCompatibility.Keys.Where(provider => ProviderCompatibility[provider] != "Replicate")];
    }

    public ProviderCapabilityInfo? GetProviderCapabilities(string providerName)
    {
        return !ProviderCompatibility.TryGetValue(providerName, out var compatibility)
            ? null
            : new ProviderCapabilityInfo
            {
                Name = providerName,
                SupportsBasicAgent = compatibility != "Replicate",
                SupportsStreamingAgent = compatibility != "Replicate",
                CompatibilityType = compatibility,
                Notes = compatibility == "Replicate" ? "Not yet supported" : null,
            };
    }

    private string GetCompatibilityType(ProviderResolution resolution)
    {
        // First check if the connection info specifies compatibility
        if (!string.IsNullOrWhiteSpace(resolution.Connection.Compatibility))
        {
            _logger.LogDebug(
                "Using explicit compatibility from connection: Provider={Provider}, Compatibility={Compatibility}",
                resolution.EffectiveProviderName,
                resolution.Connection.Compatibility
            );
            return resolution.Connection.Compatibility;
        }

        // Fall back to provider name mapping
        if (ProviderCompatibility.TryGetValue(resolution.EffectiveProviderName, out var compatibility))
        {
            _logger.LogDebug(
                "Using mapped compatibility: Provider={Provider}, Compatibility={Compatibility}",
                resolution.EffectiveProviderName,
                compatibility
            );
            return compatibility;
        }

        // Default to OpenAI compatibility for unknown providers
        _logger.LogWarning(
            "Unknown provider {Provider}, defaulting to OpenAI compatibility",
            resolution.EffectiveProviderName
        );
        return "OpenAI";
    }

    private IAgent CreateAnthropicAgent(ProviderResolution resolution)
    {
        try
        {
            // Create Anthropic client
            var client = CreateAnthropicClient(resolution);

            // Create agent with the resolved model name and logger
            var agentName = $"{resolution.EffectiveProviderName}-{resolution.EffectiveModelName}";
            var agentLogger = _loggerFactory?.CreateLogger<AnthropicAgent>();

            _logger.LogDebug(
                "Creating Anthropic agent: Provider={Provider}, Model={Model}, AgentType={AgentType}",
                resolution.EffectiveProviderName,
                resolution.EffectiveModelName,
                "AnthropicAgent"
            );

            return new AnthropicAgent(agentName, client, agentLogger);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create Anthropic agent for {Provider}", resolution.EffectiveProviderName);
            throw new InvalidOperationException(
                $"Failed to create Anthropic agent for provider '{resolution.EffectiveProviderName}': {ex.Message}",
                ex
            );
        }
    }

    private IAgent CreateOpenAIAgent(ProviderResolution resolution)
    {
        try
        {
            // Create OpenAI client
            var client = CreateOpenAIClient(resolution);

            // Create agent with the resolved model name and logger
            var agentName = $"{resolution.EffectiveProviderName}-{resolution.EffectiveModelName}";
            var agentLogger = _loggerFactory?.CreateLogger<OpenClientAgent>();

            _logger.LogDebug(
                "Creating OpenAI agent: Provider={Provider}, Model={Model}, AgentType={AgentType}",
                resolution.EffectiveProviderName,
                resolution.EffectiveModelName,
                "OpenClientAgent"
            );

            var agent = new OpenClientAgent(agentName, client, agentLogger) as IAgent;

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
                $"Failed to create OpenAI agent for provider '{resolution.EffectiveProviderName}': {ex.Message}",
                ex
            );
        }
    }

    private IAnthropicClient CreateAnthropicClient(ProviderResolution resolution)
    {
        var apiKey = resolution.Connection.GetApiKey() ?? throw new InvalidOperationException("API key not found.");
        var providerCfg = new ProviderConfig(apiKey, resolution.Connection.EndpointUrl, ProviderType.Anthropic);
        var httpClient = HttpClientFactory.Create(
            providerCfg,
            _handlerBuilder,
            resolution.Connection.Timeout,
            resolution.Connection.Headers,
            _logger
        );
        return new AnthropicClient(httpClient);
    }

    private IOpenClient CreateOpenAIClient(ProviderResolution resolution)
    {
        var apiKey = resolution.Connection.GetApiKey() ?? throw new InvalidOperationException("API key not found.");
        var providerCfg = new ProviderConfig(apiKey, resolution.Connection.EndpointUrl);
        var httpClient = HttpClientFactory.Create(
            providerCfg,
            _handlerBuilder,
            resolution.Connection.Timeout,
            resolution.Connection.Headers,
            _logger
        );
        return new OpenClient(httpClient, resolution.Connection.EndpointUrl);
    }

    private IAgent CreateClaudeAgentSdkAgent(ProviderResolution resolution)
    {
        // ClaudeAgentSDK is not compatible with the IAgent interface.
        // It requires using ClaudeAgentLoop (MultiTurnAgentBase) directly for multi-turn agentic workflows.
        throw new NotSupportedException(
            $"ClaudeAgentSDK provider '{resolution.EffectiveProviderName}' cannot be used via ProviderAgentFactory. " +
            "ClaudeAgentSDK is designed for multi-turn agentic workflows and requires using ClaudeAgentLoop directly. " +
            "See LmMultiTurn.ClaudeAgentLoop for the correct usage pattern."
        );
    }

    /// <summary>
    ///     Injects OpenRouter usage middleware if enabled via configuration (Requirement 12.1-12.2).
    /// </summary>
    /// <param name="agent">The base agent to wrap with middleware</param>
    /// <returns>The agent, optionally wrapped with OpenRouter usage middleware</returns>
    private IAgent InjectOpenRouterUsageMiddlewareIfEnabled(IAgent agent)
    {
        try
        {
            _logger.LogDebug("Factory configuration interaction: Checking service provider for IConfiguration");

            // Get configuration from DI
            var configuration = _serviceProvider.GetService<IConfiguration>();
            if (configuration == null)
            {
                _logger.LogWarning(
                    "IConfiguration not available in DI container. Skipping OpenRouter usage middleware injection."
                );
                return agent;
            }

            _logger.LogDebug("Factory configuration interaction: IConfiguration found, checking middleware settings");

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
                _logger.LogWarning(
                    "OpenRouter usage middleware is enabled but OPENROUTER_API_KEY is missing. "
                        + "Skipping middleware injection. Set ENABLE_USAGE_MIDDLEWARE=false to disable this warning."
                );
                return agent;
            }

            _logger.LogDebug("Factory configuration interaction: Retrieving middleware logger from service provider");

            // Create and inject the usage middleware
            var middlewareLogger =
                _serviceProvider.GetService<ILogger<OpenRouterUsageMiddleware>>()
                ?? throw new InvalidOperationException("Logger<OpenRouterUsageMiddleware> not found in DI container");

            var usageMiddleware = new OpenRouterUsageMiddleware(openRouterApiKey, middlewareLogger);

            _logger.LogDebug("Injecting OpenRouter usage middleware for agent");

            // Wrap the agent with middleware using the extension method
            return agent is IStreamingAgent streamingAgent
                ? (IAgent)streamingAgent.WithMiddleware(usageMiddleware)
                : throw new InvalidOperationException("OpenRouter usage middleware requires a streaming agent");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to inject OpenRouter usage middleware. Continuing without middleware.");
            return agent;
        }
    }
}
