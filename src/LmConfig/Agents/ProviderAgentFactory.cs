using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmConfig.Models;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;

namespace AchieveAi.LmDotnetTools.LmConfig.Agents;

/// <summary>
/// Factory implementation that creates appropriate agents for resolved providers.
/// Supports Anthropic, OpenAI, and OpenAI-compatible providers.
/// </summary>
public class ProviderAgentFactory : IProviderAgentFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ProviderAgentFactory> _logger;

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

    public ProviderAgentFactory(IServiceProvider serviceProvider, ILogger<ProviderAgentFactory> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
            return new OpenClientAgent(agentName, client);
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
        // Get API key from environment
        var apiKey = resolution.Connection.GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"API key not found for provider '{resolution.EffectiveProviderName}'. " +
                $"Please set the environment variable '{resolution.Connection.ApiKeyEnvironmentVariable}'.");
        }

        // Create HTTP client using shared factory
        var httpClient = HttpClientFactory.CreateForAnthropic(
            apiKey,
            resolution.Connection.EndpointUrl,
            resolution.Connection.Timeout,
            resolution.Connection.Headers);

        // Create Anthropic client
        return new AnthropicClient(httpClient);
    }

    private IOpenClient CreateOpenAIClient(ProviderResolution resolution)
    {
        // Get API key from environment
        var apiKey = resolution.Connection.GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"API key not found for provider '{resolution.EffectiveProviderName}'. " +
                $"Please set the environment variable '{resolution.Connection.ApiKeyEnvironmentVariable}'.");
        }

        // Create HTTP client using shared factory
        var httpClient = HttpClientFactory.CreateForOpenAI(
            apiKey,
            resolution.Connection.EndpointUrl,
            resolution.Connection.Timeout,
            resolution.Connection.Headers);

        // Create OpenAI client
        return new OpenClient(httpClient, resolution.Connection.EndpointUrl);
    }
} 