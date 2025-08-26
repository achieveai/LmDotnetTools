using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmConfig.Capabilities;
using AchieveAi.LmDotnetTools.LmConfig.Models;
using AchieveAi.LmDotnetTools.LmConfig.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmConfig.Tests.Services;

/// <summary>
/// Integration tests for OpenRouter model service mapping with realistic data structures.
/// </summary>
public class OpenRouterModelServiceIntegrationTests
{
    private readonly Mock<ILogger<OpenRouterModelService>> _mockLogger;
    private readonly Mock<HttpClient> _mockHttpClient;

    public OpenRouterModelServiceIntegrationTests()
    {
        _mockLogger = new Mock<ILogger<OpenRouterModelService>>();
        _mockHttpClient = new Mock<HttpClient>();
    }

    [Fact]
    public async Task ConvertToModelConfigsAsync_WithComplexOpenRouterData_ShouldMapCorrectly()
    {
        // Arrange
        var cache = CreateComplexOpenRouterCache();
        var service = new OpenRouterModelService(_mockHttpClient.Object, _mockLogger.Object);

        // Use reflection to access the private method for testing
        var method = typeof(OpenRouterModelService).GetMethod(
            "ConvertToModelConfigsAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
        );

        // Act
        var result = await (Task<IReadOnlyList<ModelConfig>>)
            method!.Invoke(service, new object[] { cache, CancellationToken.None })!;

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count); // Should have 2 unique models

        // Test GPT-4 model
        var gpt4Model = result.FirstOrDefault(m => m.Id == "openai/gpt-4");
        Assert.NotNull(gpt4Model);
        Assert.Equal("openai/gpt-4", gpt4Model.Id);
        Assert.False(gpt4Model.IsReasoning);
        Assert.NotNull(gpt4Model.Capabilities);
        Assert.Equal(8192, gpt4Model.Capabilities.TokenLimits.MaxContextTokens);
        Assert.True(gpt4Model.Capabilities.Multimodal?.SupportsImages);
        Assert.True(gpt4Model.Capabilities.FunctionCalling?.SupportsTools);
        Assert.Contains("multimodal", gpt4Model.Capabilities.SupportedFeatures);
        Assert.Contains("function_calling", gpt4Model.Capabilities.SupportedFeatures);

        // Should have multiple providers for GPT-4
        Assert.True(gpt4Model.Providers.Count >= 2);
        var openAiProvider = gpt4Model.Providers.FirstOrDefault(p => p.Name == "OpenAI");
        var azureProvider = gpt4Model.Providers.FirstOrDefault(p => p.Name == "Azure");

        Assert.NotNull(openAiProvider);
        Assert.NotNull(azureProvider);
        Assert.True(openAiProvider.Priority > azureProvider.Priority); // OpenAI should have higher priority
        Assert.Equal(30.0, openAiProvider.Pricing.PromptPerMillion);
        Assert.Equal(60.0, openAiProvider.Pricing.CompletionPerMillion);

        // Test O1 reasoning model
        var o1Model = result.FirstOrDefault(m => m.Id == "openai/o1-preview");
        Assert.NotNull(o1Model);
        Assert.True(o1Model.IsReasoning);
        Assert.NotNull(o1Model.Capabilities?.Thinking);
        Assert.Equal(ThinkingType.OpenAI, o1Model.Capabilities.Thinking.Type);
        Assert.Contains("thinking", o1Model.Capabilities.SupportedFeatures);
    }

    [Fact]
    public async Task ConvertToModelConfigsAsync_WithProviderFiltering_ShouldSkipDisabledProviders()
    {
        // Arrange
        var cache = CreateCacheWithDisabledProviders();
        var service = new OpenRouterModelService(_mockHttpClient.Object, _mockLogger.Object);

        // Use reflection to access the private method for testing
        var method = typeof(OpenRouterModelService).GetMethod(
            "ConvertToModelConfigsAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
        );

        // Act
        var result = await (Task<IReadOnlyList<ModelConfig>>)
            method!.Invoke(service, new object[] { cache, CancellationToken.None })!;

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);

        var model = result.First();
        // Should only have the enabled provider, not the disabled one
        Assert.Single(model.Providers);
        Assert.Equal("EnabledProvider", model.Providers.First().Name);
    }

    [Fact]
    public async Task ConvertToModelConfigsAsync_WithPricingVariations_ShouldMapCorrectly()
    {
        // Arrange
        var cache = CreateCacheWithPricingVariations();
        var service = new OpenRouterModelService(_mockHttpClient.Object, _mockLogger.Object);

        // Use reflection to access the private method for testing
        var method = typeof(OpenRouterModelService).GetMethod(
            "ConvertToModelConfigsAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
        );

        // Act
        var result = await (Task<IReadOnlyList<ModelConfig>>)
            method!.Invoke(service, new object[] { cache, CancellationToken.None })!;

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);

        var model = result.First();
        Assert.Equal(2, model.Providers.Count);

        var freeProvider = model.Providers.FirstOrDefault(p => p.Tags?.Contains("free") == true);
        var paidProvider = model.Providers.FirstOrDefault(p => p.Tags?.Contains("paid") == true);

        Assert.NotNull(freeProvider);
        Assert.NotNull(paidProvider);

        // Free provider should have 0 cost
        Assert.Equal(0.0, freeProvider.Pricing.PromptPerMillion);
        Assert.Equal(0.0, freeProvider.Pricing.CompletionPerMillion);

        // Paid provider should have actual costs
        Assert.True(paidProvider.Pricing.PromptPerMillion > 0);
        Assert.True(paidProvider.Pricing.CompletionPerMillion > 0);
    }

    private OpenRouterCache CreateComplexOpenRouterCache()
    {
        var modelsData = JsonNode.Parse(
            """
            {
                "data": [
                    {
                        "slug": "openai/gpt-4",
                        "name": "OpenAI GPT-4",
                        "context_length": 8192,
                        "input_modalities": ["text", "image"],
                        "output_modalities": ["text"],
                        "has_text_output": true,
                        "group": "GPT",
                        "author": "openai",
                        "description": "GPT-4 is a large multimodal model",
                        "endpoint": {
                            "id": "gpt4-openai-endpoint",
                            "provider_name": "OpenAI",
                            "provider_display_name": "OpenAI",
                            "provider_model_id": "gpt-4",
                            "model_variant_slug": "openai/gpt-4",
                            "is_free": false,
                            "is_hidden": false,
                            "is_disabled": false,
                            "pricing": {
                                "prompt": "0.00003",
                                "completion": "0.00006"
                            },
                            "supported_parameters": ["max_tokens", "temperature", "tools", "response_format"],
                            "supports_tool_parameters": true,
                            "supports_reasoning": false,
                            "supports_multipart": true
                        }
                    },
                    {
                        "slug": "openai/gpt-4",
                        "name": "OpenAI GPT-4",
                        "context_length": 8192,
                        "input_modalities": ["text", "image"],
                        "output_modalities": ["text"],
                        "has_text_output": true,
                        "group": "GPT",
                        "author": "openai",
                        "description": "GPT-4 is a large multimodal model",
                        "endpoint": {
                            "id": "gpt4-azure-endpoint",
                            "provider_name": "Azure",
                            "provider_display_name": "Azure",
                            "provider_model_id": "gpt-4",
                            "model_variant_slug": "openai/gpt-4",
                            "is_free": false,
                            "is_hidden": false,
                            "is_disabled": false,
                            "pricing": {
                                "prompt": "0.00003",
                                "completion": "0.00006"
                            },
                            "supported_parameters": ["max_tokens", "temperature", "tools"],
                            "supports_tool_parameters": true,
                            "supports_reasoning": false,
                            "supports_multipart": true
                        }
                    },
                    {
                        "slug": "openai/o1-preview",
                        "name": "OpenAI O1 Preview",
                        "context_length": 128000,
                        "input_modalities": ["text"],
                        "output_modalities": ["text"],
                        "has_text_output": true,
                        "group": "GPT",
                        "author": "openai",
                        "description": "O1 is a reasoning model",
                        "endpoint": {
                            "id": "o1-openai-endpoint",
                            "provider_name": "OpenAI",
                            "provider_display_name": "OpenAI",
                            "provider_model_id": "o1-preview",
                            "model_variant_slug": "openai/o1-preview",
                            "is_free": false,
                            "is_hidden": false,
                            "is_disabled": false,
                            "pricing": {
                                "prompt": "0.000015",
                                "completion": "0.00006"
                            },
                            "supported_parameters": ["max_tokens"],
                            "supports_tool_parameters": false,
                            "supports_reasoning": true,
                            "supports_multipart": false
                        }
                    }
                ]
            }
            """
        );

        return new OpenRouterCache
        {
            CachedAt = DateTime.UtcNow,
            ModelsData = modelsData,
            ModelDetails = new Dictionary<string, JsonNode>(),
        };
    }

    private OpenRouterCache CreateCacheWithDisabledProviders()
    {
        var modelsData = JsonNode.Parse(
            """
            {
                "data": [
                    {
                        "slug": "test/model",
                        "name": "Test Model",
                        "context_length": 4096,
                        "input_modalities": ["text"],
                        "output_modalities": ["text"],
                        "has_text_output": true,
                        "group": "Test",
                        "author": "test",
                        "description": "A test model",
                        "endpoint": {
                            "id": "enabled-endpoint",
                            "provider_name": "EnabledProvider",
                            "provider_display_name": "EnabledProvider",
                            "provider_model_id": "test-model",
                            "model_variant_slug": "test/model",
                            "is_free": false,
                            "is_hidden": false,
                            "is_disabled": false,
                            "pricing": {
                                "prompt": "0.000001",
                                "completion": "0.000002"
                            },
                            "supported_parameters": ["max_tokens"],
                            "supports_tool_parameters": false,
                            "supports_reasoning": false,
                            "supports_multipart": false
                        }
                    },
                    {
                        "slug": "test/model",
                        "name": "Test Model",
                        "context_length": 4096,
                        "input_modalities": ["text"],
                        "output_modalities": ["text"],
                        "has_text_output": true,
                        "group": "Test",
                        "author": "test",
                        "description": "A test model",
                        "endpoint": {
                            "id": "disabled-endpoint",
                            "provider_name": "DisabledProvider",
                            "provider_display_name": "DisabledProvider",
                            "provider_model_id": "test-model",
                            "model_variant_slug": "test/model",
                            "is_free": false,
                            "is_hidden": false,
                            "is_disabled": true,
                            "pricing": {
                                "prompt": "0.000001",
                                "completion": "0.000002"
                            },
                            "supported_parameters": ["max_tokens"],
                            "supports_tool_parameters": false,
                            "supports_reasoning": false,
                            "supports_multipart": false
                        }
                    }
                ]
            }
            """
        );

        return new OpenRouterCache
        {
            CachedAt = DateTime.UtcNow,
            ModelsData = modelsData,
            ModelDetails = new Dictionary<string, JsonNode>(),
        };
    }

    private OpenRouterCache CreateCacheWithPricingVariations()
    {
        var modelsData = JsonNode.Parse(
            """
            {
                "data": [
                    {
                        "slug": "test/model",
                        "name": "Test Model",
                        "context_length": 4096,
                        "input_modalities": ["text"],
                        "output_modalities": ["text"],
                        "has_text_output": true,
                        "group": "Test",
                        "author": "test",
                        "description": "A test model",
                        "endpoint": {
                            "id": "free-endpoint",
                            "provider_name": "FreeProvider",
                            "provider_display_name": "FreeProvider",
                            "provider_model_id": "test-model-free",
                            "model_variant_slug": "test/model:free",
                            "is_free": true,
                            "is_hidden": false,
                            "is_disabled": false,
                            "pricing": {
                                "prompt": "0",
                                "completion": "0"
                            },
                            "supported_parameters": ["max_tokens"],
                            "supports_tool_parameters": false,
                            "supports_reasoning": false,
                            "supports_multipart": false
                        }
                    },
                    {
                        "slug": "test/model",
                        "name": "Test Model",
                        "context_length": 4096,
                        "input_modalities": ["text"],
                        "output_modalities": ["text"],
                        "has_text_output": true,
                        "group": "Test",
                        "author": "test",
                        "description": "A test model",
                        "endpoint": {
                            "id": "paid-endpoint",
                            "provider_name": "PaidProvider",
                            "provider_display_name": "PaidProvider",
                            "provider_model_id": "test-model-paid",
                            "model_variant_slug": "test/model",
                            "is_free": false,
                            "is_hidden": false,
                            "is_disabled": false,
                            "pricing": {
                                "prompt": "0.000001",
                                "completion": "0.000002"
                            },
                            "supported_parameters": ["max_tokens"],
                            "supports_tool_parameters": false,
                            "supports_reasoning": false,
                            "supports_multipart": false
                        }
                    }
                ]
            }
            """
        );

        return new OpenRouterCache
        {
            CachedAt = DateTime.UtcNow,
            ModelsData = modelsData,
            ModelDetails = new Dictionary<string, JsonNode>(),
        };
    }
}
