using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using AchieveAi.LmDotnetTools.LmConfig.Services;
using AchieveAi.LmDotnetTools.LmConfig.Models;
using AchieveAi.LmDotnetTools.LmConfig.Capabilities;

namespace AchieveAi.LmDotnetTools.LmConfig.Tests.Services;

/// <summary>
/// Tests for OpenRouter to ModelConfig mapping functionality.
/// </summary>
public class OpenRouterModelServiceMappingTests
{
    private readonly Mock<ILogger<OpenRouterModelService>> _mockLogger;
    private readonly Mock<HttpClient> _mockHttpClient;

    public OpenRouterModelServiceMappingTests()
    {
        _mockLogger = new Mock<ILogger<OpenRouterModelService>>();
        _mockHttpClient = new Mock<HttpClient>();
    }

    [Fact]
    public async Task ConvertToModelConfigsAsync_WithValidCache_ShouldCreateModelConfigs()
    {
        // Arrange
        var cache = CreateTestCache();
        var service = new OpenRouterModelService(_mockHttpClient.Object, _mockLogger.Object);

        // Use reflection to access the private method for testing
        var method = typeof(OpenRouterModelService).GetMethod("ConvertToModelConfigsAsync", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        // Act
        var result = await (Task<IReadOnlyList<ModelConfig>>)method!.Invoke(service, new object[] { cache, CancellationToken.None })!;

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result);
        
        var modelConfig = result.First();
        Assert.Equal("test/model", modelConfig.Id);
        Assert.NotNull(modelConfig.Capabilities);
        Assert.NotEmpty(modelConfig.Providers);
        
        var provider = modelConfig.Providers.First();
        Assert.Equal("TestProvider", provider.Name);
        Assert.Contains("openrouter", provider.Tags!);
    }

    [Fact]
    public async Task ConvertToModelConfigsAsync_WithReasoningModel_ShouldSetIsReasoning()
    {
        // Arrange
        var cache = CreateReasoningModelCache();
        var service = new OpenRouterModelService(_mockHttpClient.Object, _mockLogger.Object);

        // Use reflection to access the private method for testing
        var method = typeof(OpenRouterModelService).GetMethod("ConvertToModelConfigsAsync", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        // Act
        var result = await (Task<IReadOnlyList<ModelConfig>>)method!.Invoke(service, new object[] { cache, CancellationToken.None })!;

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result);
        
        var modelConfig = result.First();
        Assert.True(modelConfig.IsReasoning);
        Assert.NotNull(modelConfig.Capabilities?.Thinking);
        Assert.Equal(ThinkingType.OpenAI, modelConfig.Capabilities.Thinking.Type);
    }

    [Fact]
    public async Task ConvertToModelConfigsAsync_WithMultimodalModel_ShouldSetMultimodalCapabilities()
    {
        // Arrange
        var cache = CreateMultimodalModelCache();
        var service = new OpenRouterModelService(_mockHttpClient.Object, _mockLogger.Object);

        // Use reflection to access the private method for testing
        var method = typeof(OpenRouterModelService).GetMethod("ConvertToModelConfigsAsync", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        // Act
        var result = await (Task<IReadOnlyList<ModelConfig>>)method!.Invoke(service, new object[] { cache, CancellationToken.None })!;

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result);
        
        var modelConfig = result.First();
        Assert.NotNull(modelConfig.Capabilities?.Multimodal);
        Assert.True(modelConfig.Capabilities.Multimodal.SupportsImages);
        Assert.Contains("multimodal", modelConfig.Capabilities.SupportedFeatures);
    }

    private OpenRouterCache CreateTestCache()
    {
        var modelsData = JsonNode.Parse("""
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
                        "id": "test-endpoint-1",
                        "provider_name": "TestProvider",
                        "provider_display_name": "TestProvider",
                        "provider_model_id": "test-model-id",
                        "model_variant_slug": "test/model",
                        "is_free": false,
                        "is_hidden": false,
                        "is_disabled": false,
                        "pricing": {
                            "prompt": "0.000001",
                            "completion": "0.000002"
                        },
                        "supported_parameters": ["max_tokens", "temperature"],
                        "supports_tool_parameters": false,
                        "supports_reasoning": false,
                        "supports_multipart": false
                    }
                }
            ]
        }
        """);

        return new OpenRouterCache
        {
            CachedAt = DateTime.UtcNow,
            ModelsData = modelsData,
            ModelDetails = new Dictionary<string, JsonNode>()
        };
    }

    private OpenRouterCache CreateReasoningModelCache()
    {
        var modelsData = JsonNode.Parse("""
        {
            "data": [
                {
                    "slug": "openai/o1-preview",
                    "name": "OpenAI O1 Preview",
                    "context_length": 128000,
                    "input_modalities": ["text"],
                    "output_modalities": ["text"],
                    "has_text_output": true,
                    "group": "GPT",
                    "author": "openai",
                    "description": "A reasoning model",
                    "endpoint": {
                        "id": "reasoning-endpoint-1",
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
        """);

        return new OpenRouterCache
        {
            CachedAt = DateTime.UtcNow,
            ModelsData = modelsData,
            ModelDetails = new Dictionary<string, JsonNode>()
        };
    }

    private OpenRouterCache CreateMultimodalModelCache()
    {
        var modelsData = JsonNode.Parse("""
        {
            "data": [
                {
                    "slug": "google/gemini-pro-vision",
                    "name": "Google Gemini Pro Vision",
                    "context_length": 32768,
                    "input_modalities": ["text", "image"],
                    "output_modalities": ["text"],
                    "has_text_output": true,
                    "group": "Gemini",
                    "author": "google",
                    "description": "A multimodal model",
                    "endpoint": {
                        "id": "multimodal-endpoint-1",
                        "provider_name": "Google",
                        "provider_display_name": "Google",
                        "provider_model_id": "gemini-pro-vision",
                        "model_variant_slug": "google/gemini-pro-vision",
                        "is_free": false,
                        "is_hidden": false,
                        "is_disabled": false,
                        "pricing": {
                            "prompt": "0.000001",
                            "completion": "0.000002"
                        },
                        "supported_parameters": ["max_tokens", "temperature"],
                        "supports_tool_parameters": true,
                        "supports_reasoning": false,
                        "supports_multipart": true
                    }
                }
            ]
        }
        """);

        return new OpenRouterCache
        {
            CachedAt = DateTime.UtcNow,
            ModelsData = modelsData,
            ModelDetails = new Dictionary<string, JsonNode>()
        };
    }
}