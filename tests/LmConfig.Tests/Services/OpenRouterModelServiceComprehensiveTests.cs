using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using AchieveAi.LmDotnetTools.LmConfig.Services;
using AchieveAi.LmDotnetTools.LmConfig.Models;
using AchieveAi.LmDotnetTools.LmConfig.Capabilities;

namespace AchieveAi.LmDotnetTools.LmConfig.Tests.Services;

/// <summary>
/// Comprehensive tests for OpenRouterModelService focusing on mapping logic and cache validation.
/// Implements requirement 6.1: Write unit tests for mapping logic and cache validation.
/// </summary>
public class OpenRouterModelServiceComprehensiveTests : IDisposable
{
    private readonly Mock<ILogger<OpenRouterModelService>> _mockLogger;
    private readonly Mock<HttpMessageHandler> _mockHttpHandler;
    private readonly HttpClient _httpClient;
    private readonly string _tempCacheDir;
    private readonly string _tempCacheFile;

    public OpenRouterModelServiceComprehensiveTests()
    {
        _mockLogger = new Mock<ILogger<OpenRouterModelService>>();
        _mockHttpHandler = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_mockHttpHandler.Object);
        
        // Create temporary directory for cache testing
        _tempCacheDir = Path.Combine(Path.GetTempPath(), "LmDotnetTools_ComprehensiveTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempCacheDir);
        _tempCacheFile = Path.Combine(_tempCacheDir, "openrouter-cache.json");
    }

    #region Mapping Logic Tests

    [Fact]
    public async Task MappingLogic_WithComplexModelData_ShouldMapAllFields()
    {
        // Arrange
        var cache = CreateComplexModelCache();
        var service = CreateService();

        // Use reflection to access the private method for testing
        var method = typeof(OpenRouterModelService).GetMethod("ConvertToModelConfigsAsync", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        // Act
        var result = await (Task<IReadOnlyList<ModelConfig>>)method!.Invoke(service, new object[] { cache, CancellationToken.None })!;

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        
        var modelConfig = result.First();
        Assert.Equal("anthropic/claude-3-sonnet", modelConfig.Id);
        Assert.False(modelConfig.IsReasoning);
        
        // Verify capabilities mapping
        Assert.NotNull(modelConfig.Capabilities);
        Assert.Equal(200000, modelConfig.Capabilities.TokenLimits.MaxContextTokens);
        Assert.True(modelConfig.Capabilities.Multimodal?.SupportsImages);
        Assert.True(modelConfig.Capabilities.FunctionCalling?.SupportsTools);
        Assert.Contains("multimodal", modelConfig.Capabilities.SupportedFeatures);
        Assert.Contains("function_calling", modelConfig.Capabilities.SupportedFeatures);
        
        // Verify provider mapping
        Assert.NotEmpty(modelConfig.Providers);
        var provider = modelConfig.Providers.First();
        Assert.Equal("Anthropic", provider.Name);
        Assert.Equal("claude-3-sonnet-20240229", provider.ModelName);
        Assert.Equal(15.0, provider.Pricing.PromptPerMillion);
        Assert.Equal(75.0, provider.Pricing.CompletionPerMillion);
        Assert.Contains("openrouter", provider.Tags!);
        Assert.Contains("paid", provider.Tags!);
        Assert.Contains("tools", provider.Tags!);
        Assert.Contains("multimodal", provider.Tags!);
    }

    [Fact]
    public async Task MappingLogic_WithReasoningModel_ShouldSetReasoningCapabilities()
    {
        // Arrange
        var cache = CreateReasoningModelCache();
        var service = CreateService();

        // Use reflection to access the private method for testing
        var method = typeof(OpenRouterModelService).GetMethod("ConvertToModelConfigsAsync", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        // Act
        var result = await (Task<IReadOnlyList<ModelConfig>>)method!.Invoke(service, new object[] { cache, CancellationToken.None })!;

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        
        var modelConfig = result.First();
        Assert.True(modelConfig.IsReasoning);
        Assert.NotNull(modelConfig.Capabilities?.Thinking);
        Assert.Equal(ThinkingType.OpenAI, modelConfig.Capabilities.Thinking.Type);
        Assert.Contains("thinking", modelConfig.Capabilities.SupportedFeatures);
    }

    [Fact]
    public async Task MappingLogic_WithMultipleProviders_ShouldCreateSeparateProviderConfigs()
    {
        // Arrange
        var cache = CreateMultiProviderModelCache();
        var service = CreateService();

        // Use reflection to access the private method for testing
        var method = typeof(OpenRouterModelService).GetMethod("ConvertToModelConfigsAsync", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        // Act
        var result = await (Task<IReadOnlyList<ModelConfig>>)method!.Invoke(service, new object[] { cache, CancellationToken.None })!;

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        
        var modelConfig = result.First();
        Assert.Equal(3, modelConfig.Providers.Count);
        
        // Verify provider priorities are set correctly
        var providers = modelConfig.Providers.OrderByDescending(p => p.Priority).ToList();
        Assert.Equal("OpenAI", providers[0].Name);
        Assert.Equal(100, providers[0].Priority);
        Assert.Equal("Azure", providers[1].Name);
        Assert.Equal(90, providers[1].Priority);
        Assert.Equal("AWS", providers[2].Name);
        Assert.Equal(80, providers[2].Priority);
    }

    [Fact]
    public async Task MappingLogic_WithFreeAndPaidProviders_ShouldTagCorrectly()
    {
        // Arrange
        var cache = CreateFreeAndPaidProviderCache();
        var service = CreateService();

        // Use reflection to access the private method for testing
        var method = typeof(OpenRouterModelService).GetMethod("ConvertToModelConfigsAsync", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        // Act
        var result = await (Task<IReadOnlyList<ModelConfig>>)method!.Invoke(service, new object[] { cache, CancellationToken.None })!;

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        
        var modelConfig = result.First();
        Assert.Equal(2, modelConfig.Providers.Count);
        
        var freeProvider = modelConfig.Providers.FirstOrDefault(p => p.Tags?.Contains("free") == true);
        var paidProvider = modelConfig.Providers.FirstOrDefault(p => p.Tags?.Contains("paid") == true);
        
        Assert.NotNull(freeProvider);
        Assert.NotNull(paidProvider);
        
        Assert.Equal(0.0, freeProvider.Pricing.PromptPerMillion);
        Assert.Equal(0.0, freeProvider.Pricing.CompletionPerMillion);
        
        Assert.True(paidProvider.Pricing.PromptPerMillion > 0);
        Assert.True(paidProvider.Pricing.CompletionPerMillion > 0);
    }

    [Fact]
    public async Task MappingLogic_WithQuantizedModel_ShouldAddQuantizationTags()
    {
        // Arrange
        var cache = CreateQuantizedModelCache();
        var service = CreateService();

        // Use reflection to access the private method for testing
        var method = typeof(OpenRouterModelService).GetMethod("ConvertToModelConfigsAsync", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        // Act
        var result = await (Task<IReadOnlyList<ModelConfig>>)method!.Invoke(service, new object[] { cache, CancellationToken.None })!;

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        
        var modelConfig = result.First();
        var provider = modelConfig.Providers.First();
        
        Assert.Contains("quantization-int8", provider.Tags!);
        Assert.Contains("variant-extended", provider.Tags!);
    }

    #endregion

    #region Cache Validation Tests

    [Fact]
    public void CacheValidation_WithValidCache_ShouldReturnTrue()
    {
        // Arrange
        var validCache = CreateValidCache();
        var service = CreateService();

        // Use reflection to access the private method for testing
        var method = typeof(OpenRouterModelService).GetMethod("ValidateCacheIntegrity", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        // Act
        var result = (bool)method!.Invoke(service, new object[] { validCache })!;

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void CacheValidation_WithNullModelsData_ShouldReturnFalse()
    {
        // Arrange
        var invalidCache = new OpenRouterCache
        {
            CachedAt = DateTime.UtcNow.AddHours(-1),
            ModelsData = null,
            ModelDetails = new Dictionary<string, JsonNode>()
        };
        var service = CreateService();

        // Use reflection to access the private method for testing
        var method = typeof(OpenRouterModelService).GetMethod("ValidateCacheIntegrity", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        // Act
        var result = (bool)method!.Invoke(service, new object[] { invalidCache })!;

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void CacheValidation_WithEmptyModelsArray_ShouldReturnFalse()
    {
        // Arrange
        var invalidCache = new OpenRouterCache
        {
            CachedAt = DateTime.UtcNow.AddHours(-1),
            ModelsData = JsonNode.Parse("""{"data": []}"""),
            ModelDetails = new Dictionary<string, JsonNode>()
        };
        var service = CreateService();

        // Use reflection to access the private method for testing
        var method = typeof(OpenRouterModelService).GetMethod("ValidateCacheIntegrity", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        // Act
        var result = (bool)method!.Invoke(service, new object[] { invalidCache })!;

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void CacheValidation_WithFutureTimestamp_ShouldReturnFalse()
    {
        // Arrange
        var invalidCache = new OpenRouterCache
        {
            CachedAt = DateTime.UtcNow.AddHours(1), // Future timestamp
            ModelsData = JsonNode.Parse("""{"data": [{"slug": "test", "name": "Test"}]}"""),
            ModelDetails = new Dictionary<string, JsonNode>()
        };
        var service = CreateService();

        // Use reflection to access the private method for testing
        var method = typeof(OpenRouterModelService).GetMethod("ValidateCacheIntegrity", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        // Act
        var result = (bool)method!.Invoke(service, new object[] { invalidCache })!;

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void CacheValidation_WithMalformedModelsData_ShouldReturnFalse()
    {
        // Arrange
        var invalidCache = new OpenRouterCache
        {
            CachedAt = DateTime.UtcNow.AddHours(-1),
            ModelsData = JsonNode.Parse("""{"data": [{"invalid": "structure"}]}"""), // Missing required fields
            ModelDetails = new Dictionary<string, JsonNode>()
        };
        var service = CreateService();

        // Use reflection to access the private method for testing
        var method = typeof(OpenRouterModelService).GetMethod("ValidateCacheIntegrity", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        // Act
        var result = (bool)method!.Invoke(service, new object[] { invalidCache })!;

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void CacheValidation_WithNullModelDetails_ShouldReturnFalse()
    {
        // Arrange
        var invalidCache = new OpenRouterCache
        {
            CachedAt = DateTime.UtcNow.AddHours(-1),
            ModelsData = JsonNode.Parse("""{"data": [{"slug": "test", "name": "Test"}]}"""),
            ModelDetails = null!
        };
        var service = CreateService();

        // Use reflection to access the private method for testing
        var method = typeof(OpenRouterModelService).GetMethod("ValidateCacheIntegrity", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        // Act
        var result = (bool)method!.Invoke(service, new object[] { invalidCache })!;

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void CacheValidation_PerformanceRequirement_CompletesUnder100Ms()
    {
        // Arrange
        var cache = CreateLargeCacheForPerformanceTest();
        var service = CreateService();

        // Use reflection to access the private method for testing
        var method = typeof(OpenRouterModelService).GetMethod("ValidateCacheIntegrity", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        // Act
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = (bool)method!.Invoke(service, new object[] { cache })!;
        stopwatch.Stop();

        // Assert
        Assert.True(result);
        Assert.True(stopwatch.ElapsedMilliseconds < 100, 
            $"Cache validation took {stopwatch.ElapsedMilliseconds}ms, should be under 100ms for performance requirement 6.1");
    }

    #endregion

    #region Helper Methods

    private OpenRouterModelService CreateService()
    {
        return new OpenRouterModelService(_httpClient, _mockLogger.Object, _tempCacheFile);
    }

    private OpenRouterCache CreateComplexModelCache()
    {
        var modelsData = JsonNode.Parse("""
        {
            "data": [
                {
                    "slug": "anthropic/claude-3-sonnet",
                    "name": "Claude 3 Sonnet",
                    "context_length": 200000,
                    "input_modalities": ["text", "image"],
                    "output_modalities": ["text"],
                    "has_text_output": true,
                    "group": "Claude",
                    "author": "anthropic",
                    "description": "Claude 3 Sonnet is a powerful multimodal model",
                    "warning_message": null,
                    "hidden": false,
                    "endpoint": {
                        "id": "claude-3-sonnet-endpoint",
                        "provider_name": "Anthropic",
                        "provider_display_name": "Anthropic",
                        "provider_model_id": "claude-3-sonnet-20240229",
                        "model_variant_slug": "anthropic/claude-3-sonnet",
                        "is_free": false,
                        "is_hidden": false,
                        "is_disabled": false,
                        "quantization": null,
                        "variant": "standard",
                        "pricing": {
                            "prompt": "0.000015",
                            "completion": "0.000075"
                        },
                        "supported_parameters": ["max_tokens", "temperature", "tools", "response_format"],
                        "supports_tool_parameters": true,
                        "supports_reasoning": false,
                        "supports_multipart": true,
                        "limit_rpm": 60,
                        "provider_info": {
                            "name": "Anthropic",
                            "displayName": "Anthropic",
                            "slug": "anthropic",
                            "baseUrl": "https://api.anthropic.com"
                        }
                    }
                }
            ]
        }
        """);

        return new OpenRouterCache
        {
            CachedAt = DateTime.UtcNow.AddHours(-1),
            ModelsData = modelsData,
            ModelDetails = new Dictionary<string, JsonNode>
            {
                ["anthropic/claude-3-sonnet"] = JsonNode.Parse("""
                    {
                        "data": [
                            {
                                "id": "claude-3-sonnet-endpoint",
                                "provider_name": "Anthropic",
                                "additional_stats": {
                                    "usage_count": 1000,
                                    "success_rate": 0.99
                                }
                            }
                        ]
                    }
                    """)!
            }
        };
    }

    private OpenRouterCache CreateReasoningModelCache()
    {
        var modelsData = JsonNode.Parse("""
        {
            "data": [
                {
                    "slug": "openai/o1-mini",
                    "name": "OpenAI O1 Mini",
                    "context_length": 128000,
                    "input_modalities": ["text"],
                    "output_modalities": ["text"],
                    "has_text_output": true,
                    "group": "GPT",
                    "author": "openai",
                    "description": "O1 Mini is a reasoning model",
                    "endpoint": {
                        "id": "o1-mini-endpoint",
                        "provider_name": "OpenAI",
                        "provider_display_name": "OpenAI",
                        "provider_model_id": "o1-mini",
                        "model_variant_slug": "openai/o1-mini",
                        "is_free": false,
                        "is_hidden": false,
                        "is_disabled": false,
                        "pricing": {
                            "prompt": "0.000003",
                            "completion": "0.000012"
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
            CachedAt = DateTime.UtcNow.AddHours(-1),
            ModelsData = modelsData,
            ModelDetails = new Dictionary<string, JsonNode>()
        };
    }

    private OpenRouterCache CreateMultiProviderModelCache()
    {
        var modelsData = JsonNode.Parse("""
        {
            "data": [
                {
                    "slug": "openai/gpt-4",
                    "name": "GPT-4",
                    "context_length": 8192,
                    "input_modalities": ["text"],
                    "output_modalities": ["text"],
                    "has_text_output": true,
                    "group": "GPT",
                    "author": "openai",
                    "description": "GPT-4 model",
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
                        "supported_parameters": ["max_tokens", "temperature"],
                        "supports_tool_parameters": false,
                        "supports_reasoning": false,
                        "supports_multipart": false
                    }
                },
                {
                    "slug": "openai/gpt-4",
                    "name": "GPT-4",
                    "context_length": 8192,
                    "input_modalities": ["text"],
                    "output_modalities": ["text"],
                    "has_text_output": true,
                    "group": "GPT",
                    "author": "openai",
                    "description": "GPT-4 model",
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
                        "supported_parameters": ["max_tokens", "temperature"],
                        "supports_tool_parameters": false,
                        "supports_reasoning": false,
                        "supports_multipart": false
                    }
                },
                {
                    "slug": "openai/gpt-4",
                    "name": "GPT-4",
                    "context_length": 8192,
                    "input_modalities": ["text"],
                    "output_modalities": ["text"],
                    "has_text_output": true,
                    "group": "GPT",
                    "author": "openai",
                    "description": "GPT-4 model",
                    "endpoint": {
                        "id": "gpt4-aws-endpoint",
                        "provider_name": "AWS",
                        "provider_display_name": "AWS",
                        "provider_model_id": "gpt-4",
                        "model_variant_slug": "openai/gpt-4",
                        "is_free": false,
                        "is_hidden": false,
                        "is_disabled": false,
                        "pricing": {
                            "prompt": "0.00003",
                            "completion": "0.00006"
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
            CachedAt = DateTime.UtcNow.AddHours(-1),
            ModelsData = modelsData,
            ModelDetails = new Dictionary<string, JsonNode>()
        };
    }

    private OpenRouterCache CreateFreeAndPaidProviderCache()
    {
        var modelsData = JsonNode.Parse("""
        {
            "data": [
                {
                    "slug": "meta-llama/llama-3-8b-instruct",
                    "name": "Llama 3 8B Instruct",
                    "context_length": 8192,
                    "input_modalities": ["text"],
                    "output_modalities": ["text"],
                    "has_text_output": true,
                    "group": "Llama",
                    "author": "meta",
                    "description": "Llama 3 8B Instruct model",
                    "endpoint": {
                        "id": "llama-3-free-endpoint",
                        "provider_name": "Together",
                        "provider_display_name": "Together (Free)",
                        "provider_model_id": "meta-llama/Llama-3-8b-chat-hf",
                        "model_variant_slug": "meta-llama/llama-3-8b-instruct:free",
                        "is_free": true,
                        "is_hidden": false,
                        "is_disabled": false,
                        "pricing": {
                            "prompt": "0",
                            "completion": "0"
                        },
                        "supported_parameters": ["max_tokens", "temperature"],
                        "supports_tool_parameters": false,
                        "supports_reasoning": false,
                        "supports_multipart": false
                    }
                },
                {
                    "slug": "meta-llama/llama-3-8b-instruct",
                    "name": "Llama 3 8B Instruct",
                    "context_length": 8192,
                    "input_modalities": ["text"],
                    "output_modalities": ["text"],
                    "has_text_output": true,
                    "group": "Llama",
                    "author": "meta",
                    "description": "Llama 3 8B Instruct model",
                    "endpoint": {
                        "id": "llama-3-paid-endpoint",
                        "provider_name": "Together",
                        "provider_display_name": "Together",
                        "provider_model_id": "meta-llama/Llama-3-8b-chat-hf",
                        "model_variant_slug": "meta-llama/llama-3-8b-instruct",
                        "is_free": false,
                        "is_hidden": false,
                        "is_disabled": false,
                        "pricing": {
                            "prompt": "0.0000002",
                            "completion": "0.0000002"
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
            CachedAt = DateTime.UtcNow.AddHours(-1),
            ModelsData = modelsData,
            ModelDetails = new Dictionary<string, JsonNode>()
        };
    }

    private OpenRouterCache CreateQuantizedModelCache()
    {
        var modelsData = JsonNode.Parse("""
        {
            "data": [
                {
                    "slug": "microsoft/wizardlm-2-8x22b",
                    "name": "WizardLM-2 8x22B",
                    "context_length": 65536,
                    "input_modalities": ["text"],
                    "output_modalities": ["text"],
                    "has_text_output": true,
                    "group": "WizardLM",
                    "author": "microsoft",
                    "description": "WizardLM-2 8x22B model",
                    "endpoint": {
                        "id": "wizardlm-quantized-endpoint",
                        "provider_name": "Together",
                        "provider_display_name": "Together",
                        "provider_model_id": "microsoft/WizardLM-2-8x22B",
                        "model_variant_slug": "microsoft/wizardlm-2-8x22b:extended",
                        "is_free": false,
                        "is_hidden": false,
                        "is_disabled": false,
                        "quantization": "int8",
                        "variant": "extended",
                        "pricing": {
                            "prompt": "0.0000012",
                            "completion": "0.0000012"
                        },
                        "supported_parameters": ["max_tokens", "temperature"],
                        "supports_tool_parameters": false,
                        "supports_reasoning": false,
                        "supports_multipart": false,
                        "limit_rpm": 20
                    }
                }
            ]
        }
        """);

        return new OpenRouterCache
        {
            CachedAt = DateTime.UtcNow.AddHours(-1),
            ModelsData = modelsData,
            ModelDetails = new Dictionary<string, JsonNode>()
        };
    }

    private OpenRouterCache CreateValidCache()
    {
        var modelsData = JsonNode.Parse("""
        {
            "data": [
                {
                    "slug": "test-model",
                    "name": "Test Model",
                    "context_length": 4096,
                    "input_modalities": ["text"],
                    "output_modalities": ["text"],
                    "has_text_output": true,
                    "group": "Test",
                    "author": "test"
                }
            ]
        }
        """);

        return new OpenRouterCache
        {
            CachedAt = DateTime.UtcNow.AddHours(-1),
            ModelsData = modelsData,
            ModelDetails = new Dictionary<string, JsonNode>
            {
                ["test-model"] = JsonNode.Parse("""
                    {
                        "data": [
                            {
                                "id": "test-endpoint",
                                "provider_name": "TestProvider"
                            }
                        ]
                    }
                    """)!
            }
        };
    }

    private OpenRouterCache CreateLargeCacheForPerformanceTest()
    {
        // Create a cache with many models to test performance
        var models = new List<object>();
        for (int i = 0; i < 100; i++)
        {
            models.Add(new
            {
                slug = $"test-model-{i}",
                name = $"Test Model {i}",
                context_length = 4096,
                input_modalities = new[] { "text" },
                output_modalities = new[] { "text" },
                has_text_output = true,
                group = "Test",
                author = "test"
            });
        }

        var modelsData = JsonNode.Parse(JsonSerializer.Serialize(new { data = models }));

        var modelDetails = new Dictionary<string, JsonNode>();
        for (int i = 0; i < 100; i++)
        {
            modelDetails[$"test-model-{i}"] = JsonNode.Parse($$"""
                {
                    "data": [
                        {
                            "id": "test-endpoint-{{i}}",
                            "provider_name": "TestProvider{{i}}"
                        }
                    ]
                }
                """)!;
        }

        return new OpenRouterCache
        {
            CachedAt = DateTime.UtcNow.AddHours(-1),
            ModelsData = modelsData,
            ModelDetails = modelDetails
        };
    }

    #endregion

    public void Dispose()
    {
        _httpClient?.Dispose();
        
        // Clean up temporary cache directory
        if (Directory.Exists(_tempCacheDir))
        {
            try
            {
                Directory.Delete(_tempCacheDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors in tests
            }
        }
    }
}