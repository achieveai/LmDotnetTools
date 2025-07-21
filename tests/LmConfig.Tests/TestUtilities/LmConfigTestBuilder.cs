using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AchieveAi.LmDotnetTools.LmConfig.Agents;
using AchieveAi.LmDotnetTools.LmConfig.Models;
using AchieveAi.LmDotnetTools.LmConfig.Capabilities;

namespace LmConfig.Tests.TestUtilities;

/// <summary>
/// Builder class for creating test configurations and services, eliminating duplication across test classes.
/// </summary>
public class LmConfigTestBuilder
{
    private readonly List<ModelConfig> _models = new();
    private readonly Dictionary<string, ProviderConnectionInfo> _providerRegistry = new();

    /// <summary>
    /// Creates a new test builder instance.
    /// </summary>
    public static LmConfigTestBuilder Create() => new();

    /// <summary>
    /// Adds a model with basic configuration suitable for most tests.
    /// </summary>
    public LmConfigTestBuilder WithModel(
        string modelId,
        string providerName = "TestProvider",
        string? modelName = null,
        int priority = 1,
        double promptCost = 1.0,
        double completionCost = 2.0,
        string[]? tags = null)
    {
        modelName ??= $"{modelId}-v1";
        tags ??= new[] { "test" };

        var model = new ModelConfig
        {
            Id = modelId,
            Capabilities = CreateDefaultCapabilities(),
            Providers = new[]
            {
                new ProviderConfig
                {
                    Name = providerName,
                    ModelName = modelName,
                    Priority = priority,
                    Pricing = new PricingConfig 
                    { 
                        PromptPerMillion = promptCost, 
                        CompletionPerMillion = completionCost 
                    },
                    Tags = tags
                }
            }
        };

        _models.Add(model);
        return this;
    }

    /// <summary>
    /// Adds a provider connection configuration.
    /// </summary>
    public LmConfigTestBuilder WithProvider(
        string providerName,
        string? endpointUrl = null,
        string? apiKeyEnvVar = null,
        string compatibility = "OpenAI")
    {
        endpointUrl ??= $"https://{providerName.ToLowerInvariant()}.example.com";
        apiKeyEnvVar ??= $"{providerName.ToUpperInvariant()}_API_KEY";

        _providerRegistry[providerName] = new ProviderConnectionInfo
        {
            EndpointUrl = endpointUrl,
            ApiKeyEnvironmentVariable = apiKeyEnvVar,
            Compatibility = compatibility
        };

        return this;
    }

    /// <summary>
    /// Adds a model with OpenRouter configuration for testing model ID resolution.
    /// </summary>
    public LmConfigTestBuilder WithOpenRouterModel(
        string modelId,
        string openRouterModelName,
        double promptCost = 3.0,
        double completionCost = 15.0)
    {
        var model = new ModelConfig
        {
            Id = modelId,
            Capabilities = CreateDefaultCapabilities(),
            Providers = new[]
            {
                new ProviderConfig
                {
                    Name = "OpenRouter",
                    ModelName = openRouterModelName, // This will be different from modelId
                    Priority = 1,
                    Pricing = new PricingConfig 
                    { 
                        PromptPerMillion = promptCost, 
                        CompletionPerMillion = completionCost 
                    },
                    Tags = new[] { "fallback", "openai-compatible" }
                }
            }
        };

        _models.Add(model);
        
        // Add OpenRouter provider if not already added
        if (!_providerRegistry.ContainsKey("OpenRouter"))
        {
            WithProvider("OpenRouter", "https://openrouter.ai/api/v1", "OPENROUTER_API_KEY");
        }

        return this;
    }

    /// <summary>
    /// Builds the AppConfig instance.
    /// </summary>
    public AppConfig BuildConfig()
    {
        // Add default provider if none specified
        if (!_providerRegistry.Any())
        {
            WithProvider("TestProvider");
        }

        return new AppConfig
        {
            Models = _models.ToArray(),
            ProviderRegistry = _providerRegistry
        };
    }

    /// <summary>
    /// Builds a ServiceCollection with the configured LmConfig services.
    /// </summary>
    public ServiceCollection BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(BuildConfig()));
        services.AddSingleton<IModelResolver, ModelResolver>();
        services.AddSingleton<IHttpHandlerBuilder, HandlerBuilder>();
        services.AddSingleton<IProviderAgentFactory, ProviderAgentFactory>();
        services.AddScoped<UnifiedAgent>();
        
        return services;
    }

    /// <summary>
    /// Creates default capabilities suitable for most tests.
    /// </summary>
    private static ModelCapabilities CreateDefaultCapabilities()
    {
        return new ModelCapabilities
        {
            TokenLimits = new TokenLimits
            {
                MaxContextTokens = 4000,
                MaxOutputTokens = 1000,
                RecommendedMaxPromptTokens = 3000
            },
            SupportsStreaming = true
        };
    }
}

/// <summary>
/// Helper class for managing environment variables in tests.
/// </summary>
public class EnvironmentVariableScope : IDisposable
{
    private readonly Dictionary<string, string?> _originalValues = new();

    /// <summary>
    /// Sets an environment variable for the duration of the scope.
    /// </summary>
    public EnvironmentVariableScope Set(string name, string value)
    {
        if (!_originalValues.ContainsKey(name))
        {
            _originalValues[name] = Environment.GetEnvironmentVariable(name);
        }
        Environment.SetEnvironmentVariable(name, value);
        return this;
    }

    /// <summary>
    /// Creates a scope with a single environment variable set.
    /// </summary>
    public static EnvironmentVariableScope Create(string name, string value)
    {
        return new EnvironmentVariableScope().Set(name, value);
    }

    /// <summary>
    /// Restores all environment variables to their original values.
    /// </summary>
    public void Dispose()
    {
        foreach (var kvp in _originalValues)
        {
            Environment.SetEnvironmentVariable(kvp.Key, kvp.Value);
        }
    }
}

/// <summary>
/// Common test data and configurations.
/// </summary>
public static class TestData
{
    /// <summary>
    /// Standard test configuration JSON for use across multiple test classes.
    /// </summary>
    public static readonly string StandardConfigJson = """
        {
          "models": [
            {
              "id": "test-model",
              "capabilities": {
                "token_limits": {
                  "max_context_tokens": 4096,
                  "max_output_tokens": 1024
                },
                "supports_streaming": true
              },
              "providers": [
                {
                  "name": "TestProvider",
                  "model_name": "test-model-v1",
                  "priority": 1,
                  "pricing": {
                    "prompt_per_million": 1.0,
                    "completion_per_million": 2.0
                  },
                  "tags": ["test", "fast"]
                }
              ]
            }
          ],
          "provider_registry": {
            "TestProvider": {
              "endpoint_url": "https://api.test.com/v1",
              "api_key_environment_variable": "TEST_API_KEY",
              "compatibility": "OpenAI",
              "timeout": "00:01:00",
              "max_retries": 3
            }
          }
        }
        """;

    /// <summary>
    /// Creates a simple test model configuration.
    /// </summary>
    public static ModelConfig CreateTestModel(string id = "test-model") =>
        LmConfigTestBuilder.Create().WithModel(id).BuildConfig().Models.First();

    /// <summary>
    /// Creates a test provider connection.
    /// </summary>
    public static ProviderConnectionInfo CreateTestProvider(string name = "TestProvider") =>
        LmConfigTestBuilder.Create().WithProvider(name).BuildConfig().ProviderRegistry[name];
} 