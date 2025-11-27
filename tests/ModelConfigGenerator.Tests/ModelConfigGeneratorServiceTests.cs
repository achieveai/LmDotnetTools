using System.Reflection;
using AchieveAi.LmDotnetTools.LmConfig.Capabilities;
using AchieveAi.LmDotnetTools.LmConfig.Models;
using AchieveAi.LmDotnetTools.LmConfig.Services;
using AchieveAi.LmDotnetTools.ModelConfigGenerator.Configuration;
using AchieveAi.LmDotnetTools.ModelConfigGenerator.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace AchieveAi.LmDotnetTools.ModelConfigGenerator.Tests;

/// <summary>
///     Tests for ModelConfigGeneratorService with focus on family detection and filtering logic.
/// </summary>
public class ModelConfigGeneratorServiceTests
{
    private static ModelConfigGeneratorService CreateTestService()
    {
        var httpClient = new HttpClient();
        var logger = new Mock<ILogger<OpenRouterModelService>>().Object;
        var openRouterService = new OpenRouterModelService(httpClient, logger);
        var serviceLogger = new Mock<ILogger<ModelConfigGeneratorService>>().Object;
        return new ModelConfigGeneratorService(openRouterService, serviceLogger);
    }

    [Fact]
    public void GetSupportedFamilies_ShouldReturnExpectedFamilies()
    {
        // Act
        var families = ModelConfigGeneratorService.GetSupportedFamilies();

        // Assert
        Assert.NotEmpty(families);
        Assert.Contains("llama", families);
        Assert.Contains("claude", families);
        Assert.Contains("gpt", families);
        Assert.Contains("qwen", families);
        Assert.Contains("deepseek", families);
        Assert.Contains("kimi", families);
        Assert.Contains("mistral", families);
        Assert.Contains("cohere", families);
    }

    [Theory]
    [InlineData("meta-llama/llama-3.1-70b", "llama", true)]
    [InlineData("anthropic/claude-3-sonnet", "claude", true)]
    [InlineData("openai/gpt-4-turbo", "gpt", true)]
    [InlineData("qwen/qwen-2.5-72b", "qwen", true)]
    [InlineData("deepseek/deepseek-v2", "deepseek", true)]
    [InlineData("moonshot/kimi-chat", "kimi", true)]
    [InlineData("mistral/mistral-7b", "mistral", true)]
    [InlineData("cohere/command-r", "cohere", true)]
    [InlineData("meta-llama/llama-3.1-70b", "claude", false)]
    [InlineData("anthropic/claude-3-sonnet", "gpt", false)]
    [InlineData("random/model-name", "nonexistent", false)]
    public void ModelFamilyMatching_ShouldWorkCorrectly(string modelId, string family, bool shouldMatch)
    {
        // Arrange
        var model = new ModelConfig
        {
            Id = modelId,
            Capabilities = new ModelCapabilities
            {
                TokenLimits = new TokenLimits { MaxContextTokens = 4096, MaxOutputTokens = 1024 },
                SupportsStreaming = true,
            },
            Providers =
            [
                new ProviderConfig
                {
                    Name = "TestProvider",
                    ModelName = modelId,
                    Priority = 1,
                    Pricing = new PricingConfig { PromptPerMillion = 1.0, CompletionPerMillion = 2.0 },
                },
            ],
        };

        // Act
        var reflection = typeof(ModelConfigGeneratorService);
        var matchesFamilyMethod = reflection.GetMethod("MatchesFamily", BindingFlags.NonPublic | BindingFlags.Static);
        var result = (bool)matchesFamilyMethod!.Invoke(null, [model, family])!;

        // Assert
        Assert.Equal(shouldMatch, result);
    }

    [Fact]
    public void FilteringLogic_WithReasoningModels_ShouldWorkCorrectly()
    {
        // Arrange
        var models = CreateTestModels();
        var options = new GeneratorOptions { ReasoningOnly = true };

        // Act
        var reflection = typeof(ModelConfigGeneratorService);
        var applyFiltersMethod = reflection.GetMethod("ApplyFilters", BindingFlags.NonPublic | BindingFlags.Instance);

        var service = CreateTestService();

        var result = (IReadOnlyList<ModelConfig>)applyFiltersMethod!.Invoke(service, [models, options])!;

        // Assert
        Assert.All(result, model => Assert.True(model.IsReasoning || model.HasCapability("thinking")));
    }

    [Fact]
    public void FilteringLogic_WithMultimodalModels_ShouldWorkCorrectly()
    {
        // Arrange
        var models = CreateTestModels();
        var options = new GeneratorOptions { MultimodalOnly = true };

        // Act
        var reflection = typeof(ModelConfigGeneratorService);
        var applyFiltersMethod = reflection.GetMethod("ApplyFilters", BindingFlags.NonPublic | BindingFlags.Instance);

        var service = CreateTestService();

        var result = (IReadOnlyList<ModelConfig>)applyFiltersMethod!.Invoke(service, [models, options])!;

        // Assert
        Assert.All(result, model => Assert.True(model.HasCapability("multimodal")));
    }

    [Fact]
    public void FilteringLogic_WithMaxModels_ShouldLimitResults()
    {
        // Arrange
        var models = CreateTestModels();
        var options = new GeneratorOptions { MaxModels = 2 };

        // Act
        var reflection = typeof(ModelConfigGeneratorService);
        var applyFiltersMethod = reflection.GetMethod("ApplyFilters", BindingFlags.NonPublic | BindingFlags.Instance);

        var service = CreateTestService();

        var result = (IReadOnlyList<ModelConfig>)applyFiltersMethod!.Invoke(service, [models, options])!;

        // Assert
        Assert.True(result.Count <= 2);
    }

    [Fact]
    public void FilteringLogic_WithFamilyFilter_ShouldWorkCorrectly()
    {
        // Arrange
        var models = CreateTestModels();
        var options = new GeneratorOptions { ModelFamilies = ["llama"] };

        // Act
        var reflection = typeof(ModelConfigGeneratorService);
        var applyFiltersMethod = reflection.GetMethod("ApplyFilters", BindingFlags.NonPublic | BindingFlags.Instance);

        var service = CreateTestService();

        var result = (IReadOnlyList<ModelConfig>)applyFiltersMethod!.Invoke(service, [models, options])!;

        // Assert
        Assert.All(result, model => Assert.Contains("llama", model.Id.ToLowerInvariant()));
    }

    [Fact]
    public void FilteringLogic_WithModelUpdatedSince_ShouldWorkCorrectly()
    {
        // Arrange
        var models = CreateTestModelsWithDates();
        var options = new GeneratorOptions { ModelUpdatedSince = new DateTime(2024, 6, 1) };

        // Act
        var reflection = typeof(ModelConfigGeneratorService);
        var applyFiltersMethod = reflection.GetMethod("ApplyFilters", BindingFlags.NonPublic | BindingFlags.Instance);

        var service = CreateTestService();

        var result = (IReadOnlyList<ModelConfig>)applyFiltersMethod!.Invoke(service, [models, options])!;

        // Assert
        Assert.All(
            result,
            model =>
            {
                Assert.True(model.CreatedDate.HasValue);
                Assert.True(model.CreatedDate.Value.Date >= new DateTime(2024, 6, 1).Date);
            }
        );
        Assert.Equal(2, result.Count); // Should exclude models created before June 1, 2024
    }

    [Fact]
    public void FilteringLogic_WithModelUpdatedSinceAndOtherFilters_ShouldApplyAllFilters()
    {
        // Arrange
        var models = CreateTestModelsWithDates();
        var options = new GeneratorOptions { ModelUpdatedSince = new DateTime(2024, 1, 1), ReasoningOnly = true };

        // Act
        var reflection = typeof(ModelConfigGeneratorService);
        var applyFiltersMethod = reflection.GetMethod("ApplyFilters", BindingFlags.NonPublic | BindingFlags.Instance);

        var service = CreateTestService();

        var result = (IReadOnlyList<ModelConfig>)applyFiltersMethod!.Invoke(service, [models, options])!;

        // Assert
        Assert.All(
            result,
            model =>
            {
                Assert.True(model.CreatedDate.HasValue);
                Assert.True(model.CreatedDate.Value.Date >= new DateTime(2024, 1, 1).Date);
                Assert.True(model.IsReasoning || model.HasCapability("thinking"));
            }
        );
        _ = Assert.Single(result); // Should only include reasoning models from 2024 onwards
    }

    [Fact]
    public void FilteringLogic_WithModelUpdatedSinceExcludesModelsWithoutDates_ShouldWorkCorrectly()
    {
        // Arrange
        var models = CreateTestModelsWithMixedDates();
        var options = new GeneratorOptions { ModelUpdatedSince = new DateTime(2024, 1, 1) };

        // Act
        var reflection = typeof(ModelConfigGeneratorService);
        var applyFiltersMethod = reflection.GetMethod("ApplyFilters", BindingFlags.NonPublic | BindingFlags.Instance);

        var service = CreateTestService();

        var result = (IReadOnlyList<ModelConfig>)applyFiltersMethod!.Invoke(service, [models, options])!;

        // Assert
        Assert.All(result, model => Assert.True(model.CreatedDate.HasValue));
        Assert.Equal(1, result.Count); // Should exclude models without dates and models before 2024
    }

    [Fact]
    public void FilteringLogic_WithModelUpdatedSinceNoMatches_ShouldReturnEmpty()
    {
        // Arrange
        var models = CreateTestModelsWithDates();
        var options = new GeneratorOptions { ModelUpdatedSince = new DateTime(2025, 1, 1) }; // Future date

        // Act
        var reflection = typeof(ModelConfigGeneratorService);
        var applyFiltersMethod = reflection.GetMethod("ApplyFilters", BindingFlags.NonPublic | BindingFlags.Instance);

        var service = CreateTestService();

        var result = (IReadOnlyList<ModelConfig>)applyFiltersMethod!.Invoke(service, [models, options])!;

        // Assert
        Assert.Empty(result);
    }

    private static IReadOnlyList<ModelConfig> CreateTestModels()
    {
        return
        [
            new ModelConfig
            {
                Id = "meta-llama/llama-3.1-70b",
                IsReasoning = false,
                Capabilities = new ModelCapabilities
                {
                    TokenLimits = new TokenLimits { MaxContextTokens = 131072, MaxOutputTokens = 4096 },
                    SupportsStreaming = true,
                    SupportedFeatures = ["long-context"],
                },
                Providers =
                [
                    new ProviderConfig
                    {
                        Name = "OpenRouter",
                        ModelName = "meta-llama/llama-3.1-70b",
                        Priority = 1,
                        Pricing = new PricingConfig { PromptPerMillion = 0.5, CompletionPerMillion = 0.75 },
                    },
                ],
            },
            new ModelConfig
            {
                Id = "anthropic/claude-3-sonnet",
                IsReasoning = true,
                Capabilities = new ModelCapabilities
                {
                    Thinking = new ThinkingCapability
                    {
                        Type = ThinkingType.Anthropic,
                        SupportsBudgetTokens = true,
                        IsBuiltIn = false,
                        IsExposed = true,
                    },
                    Multimodal = new MultimodalCapability
                    {
                        SupportsImages = true,
                        SupportedImageFormats = ["jpeg", "png"],
                    },
                    TokenLimits = new TokenLimits { MaxContextTokens = 200000, MaxOutputTokens = 8192 },
                    SupportsStreaming = true,
                    SupportedFeatures = ["thinking", "multimodal"],
                },
                Providers =
                [
                    new ProviderConfig
                    {
                        Name = "Anthropic",
                        ModelName = "claude-3-sonnet-20240229",
                        Priority = 1,
                        Pricing = new PricingConfig { PromptPerMillion = 3.0, CompletionPerMillion = 15.0 },
                    },
                ],
            },
            new ModelConfig
            {
                Id = "openai/gpt-4-turbo",
                IsReasoning = false,
                Capabilities = new ModelCapabilities
                {
                    FunctionCalling = new FunctionCallingCapability
                    {
                        SupportsTools = true,
                        SupportsParallelCalls = true,
                        MaxToolsPerRequest = 128,
                    },
                    Multimodal = new MultimodalCapability
                    {
                        SupportsImages = true,
                        SupportedImageFormats = ["jpeg", "png", "webp"],
                    },
                    TokenLimits = new TokenLimits { MaxContextTokens = 128000, MaxOutputTokens = 4096 },
                    SupportsStreaming = true,
                    SupportedFeatures = ["multimodal", "function-calling"],
                },
                Providers =
                [
                    new ProviderConfig
                    {
                        Name = "OpenAI",
                        ModelName = "gpt-4-turbo",
                        Priority = 1,
                        Pricing = new PricingConfig { PromptPerMillion = 10.0, CompletionPerMillion = 30.0 },
                    },
                ],
            },
            new ModelConfig
            {
                Id = "qwen/qwen-2.5-72b",
                IsReasoning = false,
                Capabilities = new ModelCapabilities
                {
                    TokenLimits = new TokenLimits { MaxContextTokens = 32768, MaxOutputTokens = 8192 },
                    SupportsStreaming = true,
                    SupportedFeatures = ["chat"],
                },
                Providers =
                [
                    new ProviderConfig
                    {
                        Name = "OpenRouter",
                        ModelName = "qwen/qwen-2.5-72b",
                        Priority = 1,
                        Pricing = new PricingConfig { PromptPerMillion = 0.4, CompletionPerMillion = 1.2 },
                    },
                ],
            },
        ];
    }

    private static IReadOnlyList<ModelConfig> CreateTestModelsWithDates()
    {
        return
        [
            new ModelConfig
            {
                Id = "meta-llama/llama-3.1-70b",
                IsReasoning = false,
                CreatedDate = new DateTime(2024, 7, 15), // After June 1
                Capabilities = new ModelCapabilities
                {
                    TokenLimits = new TokenLimits { MaxContextTokens = 131072, MaxOutputTokens = 4096 },
                    SupportsStreaming = true,
                    SupportedFeatures = ["long-context"],
                },
                Providers =
                [
                    new ProviderConfig
                    {
                        Name = "OpenRouter",
                        ModelName = "meta-llama/llama-3.1-70b",
                        Priority = 1,
                        Pricing = new PricingConfig { PromptPerMillion = 0.5, CompletionPerMillion = 0.75 },
                    },
                ],
            },
            new ModelConfig
            {
                Id = "anthropic/claude-3-sonnet",
                IsReasoning = true,
                CreatedDate = new DateTime(2024, 8, 20), // After June 1
                Capabilities = new ModelCapabilities
                {
                    Thinking = new ThinkingCapability
                    {
                        Type = ThinkingType.Anthropic,
                        SupportsBudgetTokens = true,
                        IsBuiltIn = false,
                        IsExposed = true,
                    },
                    TokenLimits = new TokenLimits { MaxContextTokens = 200000, MaxOutputTokens = 8192 },
                    SupportsStreaming = true,
                    SupportedFeatures = ["thinking"],
                },
                Providers =
                [
                    new ProviderConfig
                    {
                        Name = "Anthropic",
                        ModelName = "claude-3-sonnet-20240229",
                        Priority = 1,
                        Pricing = new PricingConfig { PromptPerMillion = 3.0, CompletionPerMillion = 15.0 },
                    },
                ],
            },
            new ModelConfig
            {
                Id = "openai/gpt-4-turbo",
                IsReasoning = false,
                CreatedDate = new DateTime(2024, 3, 10), // Before June 1
                Capabilities = new ModelCapabilities
                {
                    TokenLimits = new TokenLimits { MaxContextTokens = 128000, MaxOutputTokens = 4096 },
                    SupportsStreaming = true,
                },
                Providers =
                [
                    new ProviderConfig
                    {
                        Name = "OpenAI",
                        ModelName = "gpt-4-turbo",
                        Priority = 1,
                        Pricing = new PricingConfig { PromptPerMillion = 10.0, CompletionPerMillion = 30.0 },
                    },
                ],
            },
            new ModelConfig
            {
                Id = "qwen/qwen-2.5-72b",
                IsReasoning = false,
                CreatedDate = new DateTime(2023, 12, 5), // Before June 1
                Capabilities = new ModelCapabilities
                {
                    TokenLimits = new TokenLimits { MaxContextTokens = 32768, MaxOutputTokens = 8192 },
                    SupportsStreaming = true,
                },
                Providers =
                [
                    new ProviderConfig
                    {
                        Name = "OpenRouter",
                        ModelName = "qwen/qwen-2.5-72b",
                        Priority = 1,
                        Pricing = new PricingConfig { PromptPerMillion = 0.4, CompletionPerMillion = 1.2 },
                    },
                ],
            },
        ];
    }

    private static IReadOnlyList<ModelConfig> CreateTestModelsWithMixedDates()
    {
        return
        [
            new ModelConfig
            {
                Id = "model-with-date",
                IsReasoning = false,
                CreatedDate = new DateTime(2024, 6, 15), // Has date after 2024
                Capabilities = new ModelCapabilities
                {
                    TokenLimits = new TokenLimits { MaxContextTokens = 4096, MaxOutputTokens = 1024 },
                },
                Providers =
                [
                    new ProviderConfig
                    {
                        Name = "TestProvider",
                        ModelName = "model-with-date",
                        Priority = 1,
                        Pricing = new PricingConfig { PromptPerMillion = 1.0, CompletionPerMillion = 2.0 },
                    },
                ],
            },
            new ModelConfig
            {
                Id = "model-without-date",
                IsReasoning = false,
                CreatedDate = null, // No date information
                Capabilities = new ModelCapabilities
                {
                    TokenLimits = new TokenLimits { MaxContextTokens = 4096, MaxOutputTokens = 1024 },
                },
                Providers =
                [
                    new ProviderConfig
                    {
                        Name = "TestProvider",
                        ModelName = "model-without-date",
                        Priority = 1,
                        Pricing = new PricingConfig { PromptPerMillion = 1.0, CompletionPerMillion = 2.0 },
                    },
                ],
            },
            new ModelConfig
            {
                Id = "old-model-with-date",
                IsReasoning = false,
                CreatedDate = new DateTime(2023, 5, 10), // Has date before 2024
                Capabilities = new ModelCapabilities
                {
                    TokenLimits = new TokenLimits { MaxContextTokens = 4096, MaxOutputTokens = 1024 },
                },
                Providers =
                [
                    new ProviderConfig
                    {
                        Name = "TestProvider",
                        ModelName = "old-model-with-date",
                        Priority = 1,
                        Pricing = new PricingConfig { PromptPerMillion = 1.0, CompletionPerMillion = 2.0 },
                    },
                ],
            },
        ];
    }
}
