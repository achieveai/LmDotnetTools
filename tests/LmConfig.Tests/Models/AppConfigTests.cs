using System.Text.Json;
using AchieveAi.LmDotnetTools.LmConfig.Models;

namespace LmConfig.Tests.Models;

/// <summary>
/// Tests for AppConfig with unified provider registry.
/// </summary>
public class AppConfigTests
{
    [Fact]
    public void AppConfig_GetProviderConnection_ReturnsCorrectProvider()
    {
        // Arrange
        System.Diagnostics.Debug.WriteLine("Testing AppConfig GetProviderConnection method");

        var providerRegistry = new Dictionary<string, ProviderConnectionInfo>
        {
            ["OpenAI"] = new ProviderConnectionInfo
            {
                EndpointUrl = "https://api.openai.com/v1",
                ApiKeyEnvironmentVariable = "OPENAI_API_KEY",
                Compatibility = "OpenAI",
                MaxRetries = 3,
                Description = "Official OpenAI API endpoint",
            },
            ["OpenRouter"] = new ProviderConnectionInfo
            {
                EndpointUrl = "https://openrouter.ai/api/v1",
                ApiKeyEnvironmentVariable = "OPENROUTER_API_KEY",
                Compatibility = "OpenAI",
                MaxRetries = 3,
                Description = "OpenRouter aggregator",
            },
        };

        var appConfig = new AppConfig
        {
            Models = new List<ModelConfig>(),
            ProviderRegistry = providerRegistry,
        };

        // Act
        var openAIConnection = appConfig.GetProviderConnection("OpenAI");
        var openRouterConnection = appConfig.GetProviderConnection("OpenRouter");
        var nonExistentConnection = appConfig.GetProviderConnection("NonExistent");

        System.Diagnostics.Debug.WriteLine($"OpenAI connection found: {openAIConnection != null}");
        System.Diagnostics.Debug.WriteLine(
            $"OpenRouter connection found: {openRouterConnection != null}"
        );
        System.Diagnostics.Debug.WriteLine(
            $"NonExistent connection found: {nonExistentConnection != null}"
        );

        // Assert
        Assert.NotNull(openAIConnection);
        Assert.Equal("https://api.openai.com/v1", openAIConnection.EndpointUrl);
        Assert.Equal("OPENAI_API_KEY", openAIConnection.ApiKeyEnvironmentVariable);

        Assert.NotNull(openRouterConnection);
        Assert.Equal("https://openrouter.ai/api/v1", openRouterConnection.EndpointUrl);

        Assert.Null(nonExistentConnection);
    }

    [Fact]
    public void AppConfig_GetRegisteredProviders_ReturnsAllProviderNames()
    {
        // Arrange
        System.Diagnostics.Debug.WriteLine("Testing AppConfig GetRegisteredProviders method");

        var providerRegistry = new Dictionary<string, ProviderConnectionInfo>
        {
            ["OpenAI"] = new ProviderConnectionInfo
            {
                EndpointUrl = "https://api.openai.com/v1",
                ApiKeyEnvironmentVariable = "OPENAI_API_KEY",
            },
            ["Anthropic"] = new ProviderConnectionInfo
            {
                EndpointUrl = "https://api.anthropic.com",
                ApiKeyEnvironmentVariable = "ANTHROPIC_API_KEY",
            },
            ["OpenRouter"] = new ProviderConnectionInfo
            {
                EndpointUrl = "https://openrouter.ai/api/v1",
                ApiKeyEnvironmentVariable = "OPENROUTER_API_KEY",
            },
        };

        var appConfig = new AppConfig
        {
            Models = new List<ModelConfig>(),
            ProviderRegistry = providerRegistry,
        };

        // Act
        var registeredProviders = appConfig.GetRegisteredProviders();

        System.Diagnostics.Debug.WriteLine(
            $"Registered providers count: {registeredProviders.Count}"
        );
        System.Diagnostics.Debug.WriteLine($"Providers: {string.Join(", ", registeredProviders)}");

        // Assert
        Assert.Equal(3, registeredProviders.Count);
        Assert.Contains("OpenAI", registeredProviders);
        Assert.Contains("Anthropic", registeredProviders);
        Assert.Contains("OpenRouter", registeredProviders);
    }

    [Fact]
    public void AppConfig_IsProviderRegistered_ReturnsCorrectStatus()
    {
        // Arrange
        System.Diagnostics.Debug.WriteLine("Testing AppConfig IsProviderRegistered method");

        var providerRegistry = new Dictionary<string, ProviderConnectionInfo>
        {
            ["OpenAI"] = new ProviderConnectionInfo
            {
                EndpointUrl = "https://api.openai.com/v1",
                ApiKeyEnvironmentVariable = "OPENAI_API_KEY",
            },
        };

        var appConfig = new AppConfig
        {
            Models = new List<ModelConfig>(),
            ProviderRegistry = providerRegistry,
        };

        // Act & Assert
        Assert.True(appConfig.IsProviderRegistered("OpenAI"));
        Assert.False(appConfig.IsProviderRegistered("NonExistent"));
        Assert.False(appConfig.IsProviderRegistered("openai")); // Case sensitive

        System.Diagnostics.Debug.WriteLine("Provider registration checks completed successfully");
    }

    [Fact]
    public void AppConfig_WithNullProviderRegistry_HandlesGracefully()
    {
        // Arrange
        System.Diagnostics.Debug.WriteLine("Testing AppConfig with null ProviderRegistry");

        var appConfig = new AppConfig { Models = new List<ModelConfig>(), ProviderRegistry = null };

        // Act & Assert
        Assert.Null(appConfig.GetProviderConnection("OpenAI"));
        Assert.Empty(appConfig.GetRegisteredProviders());
        Assert.False(appConfig.IsProviderRegistered("OpenAI"));

        System.Diagnostics.Debug.WriteLine("Null provider registry handled gracefully");
    }

    [Fact]
    public void ProviderConnectionInfo_Validate_CatchesInvalidConfiguration()
    {
        // Arrange
        System.Diagnostics.Debug.WriteLine(
            "Testing ProviderConnectionInfo validation with invalid configuration"
        );

        var invalidConnection = new ProviderConnectionInfo
        {
            EndpointUrl = "invalid-url", // Invalid URL
            ApiKeyEnvironmentVariable = "", // Empty env var
            MaxRetries = -1, // Invalid retry count
        };

        // Act
        var validation = invalidConnection.Validate();

        System.Diagnostics.Debug.WriteLine(
            $"Validation result: IsValid={validation.IsValid}, Errors={validation.Errors.Count}"
        );

        // Assert
        Assert.False(validation.IsValid);
        Assert.Contains("EndpointUrl", validation.Errors.First(e => e.Contains("EndpointUrl")));
        Assert.Contains(
            "ApiKeyEnvironmentVariable",
            validation.Errors.First(e => e.Contains("ApiKeyEnvironmentVariable"))
        );
        Assert.Contains("MaxRetries", validation.Errors.First(e => e.Contains("MaxRetries")));
    }

    [Fact]
    public void ProviderConnectionInfo_Validate_PassesForValidConfiguration()
    {
        // Arrange
        System.Diagnostics.Debug.WriteLine(
            "Testing ProviderConnectionInfo validation with valid configuration"
        );

        Environment.SetEnvironmentVariable("TEST_API_KEY", "test-key-value");

        var validConnection = new ProviderConnectionInfo
        {
            EndpointUrl = "https://api.example.com/v1",
            ApiKeyEnvironmentVariable = "TEST_API_KEY",
            Compatibility = "OpenAI",
            MaxRetries = 3,
        };

        try
        {
            // Act
            var validation = validConnection.Validate();

            System.Diagnostics.Debug.WriteLine(
                $"Validation result: IsValid={validation.IsValid}, Errors={validation.Errors.Count}, Warnings={validation.Warnings.Count}"
            );

            // Assert
            Assert.True(validation.IsValid);
            Assert.Empty(validation.Errors);
            Assert.Empty(validation.Warnings);
        }
        finally
        {
            // Cleanup
            Environment.SetEnvironmentVariable("TEST_API_KEY", null);
        }
    }

    [Fact]
    public void ProviderConnectionInfo_GetApiKey_ReturnsEnvironmentVariable()
    {
        // Arrange
        System.Diagnostics.Debug.WriteLine("Testing ProviderConnectionInfo GetApiKey method");

        Environment.SetEnvironmentVariable("TEST_API_KEY", "test-key-value");

        var connection = new ProviderConnectionInfo
        {
            EndpointUrl = "https://api.example.com/v1",
            ApiKeyEnvironmentVariable = "TEST_API_KEY",
        };

        try
        {
            // Act
            var apiKey = connection.GetApiKey();

            System.Diagnostics.Debug.WriteLine($"Retrieved API key: {apiKey}");

            // Assert
            Assert.Equal("test-key-value", apiKey);
        }
        finally
        {
            // Cleanup
            Environment.SetEnvironmentVariable("TEST_API_KEY", null);
        }
    }

    [Fact]
    public void AppConfig_DeserializesFromJson_WithProviderRegistry()
    {
        // Arrange
        System.Diagnostics.Debug.WriteLine(
            "Testing AppConfig JSON deserialization with ProviderRegistry"
        );

        var json = """
            {
              "models": [
                {
                  "id": "gpt-4",
                  "is_reasoning": false,
                  "providers": [
                    {
                      "name": "OpenAI",
                      "model_name": "gpt-4",
                      "priority": 1,
                      "pricing": {
                        "prompt_per_million": 2.5,
                        "completion_per_million": 10.0
                      }
                    }
                  ]
                }
              ],
              "provider_registry": {
                "OpenAI": {
                  "endpoint_url": "https://api.openai.com/v1",
                  "api_key_environment_variable": "OPENAI_API_KEY",
                  "compatibility": "OpenAI",
                  "max_retries": 3,
                  "description": "Official OpenAI API endpoint"
                }
              }
            }
            """;

        // Act
        var appConfig = JsonSerializer.Deserialize<AppConfig>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        );

        System.Diagnostics.Debug.WriteLine(
            $"Deserialized AppConfig: Models={appConfig?.Models.Count}, ProviderRegistry={appConfig?.ProviderRegistry?.Count}"
        );

        // Assert
        Assert.NotNull(appConfig);
        Assert.Single(appConfig.Models);
        Assert.Equal("gpt-4", appConfig.Models[0].Id);

        Assert.NotNull(appConfig.ProviderRegistry);
        Assert.True(appConfig.ProviderRegistry.ContainsKey("OpenAI"));

        var openAIProvider = appConfig.ProviderRegistry["OpenAI"];
        Assert.Equal("https://api.openai.com/v1", openAIProvider.EndpointUrl);
        Assert.Equal("OPENAI_API_KEY", openAIProvider.ApiKeyEnvironmentVariable);
        Assert.Equal("OpenAI", openAIProvider.Compatibility);
        Assert.Equal(3, openAIProvider.MaxRetries);
        Assert.Equal("Official OpenAI API endpoint", openAIProvider.Description);
    }
}
