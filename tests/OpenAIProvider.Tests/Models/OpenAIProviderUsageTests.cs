using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;
using Xunit.Abstractions;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Tests.Models;

public class OpenAIProviderUsageTests
{
    private readonly ITestOutputHelper _output;
    private readonly JsonSerializerOptions _options;

    public OpenAIProviderUsageTests(ITestOutputHelper output)
    {
        _output = output;
        _options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    }

    [Fact]
    public void OpenAIProviderUsage_ShouldDeserializeOpenAiResponse()
    {
        // Arrange - Real OpenAI API response format
        var openAiJson = """
            {
                "prompt_tokens": 10,
                "completion_tokens": 148,
                "total_tokens": 158,
                "input_tokens_details": {
                    "cached_tokens": 5
                },
                "output_tokens_details": {
                    "reasoning_tokens": 128
                }
            }
            """;

        // Act
        var usage = JsonSerializer.Deserialize<OpenAIProviderUsage>(openAiJson, _options);
        _output.WriteLine($"Deserialized OpenAI Usage: {usage}");

        // Assert
        Assert.NotNull(usage);
        Assert.Equal(10, usage.PromptTokens);
        Assert.Equal(148, usage.CompletionTokens);
        Assert.Equal(158, usage.TotalTokens);
        Assert.NotNull(usage.InputTokenDetails);
        Assert.Equal(5, usage.InputTokenDetails.CachedTokens);
        Assert.NotNull(usage.OutputTokenDetails);
        Assert.Equal(128, usage.OutputTokenDetails.ReasoningTokens);

        // Test unified access
        Assert.Equal(128, usage.TotalReasoningTokens);
        Assert.Equal(5, usage.TotalCachedTokens);
    }

    [Fact]
    public void OpenAIProviderUsage_ShouldDeserializeOpenRouterResponse()
    {
        // Arrange - OpenRouter API response format with direct fields
        var openRouterJson = """
            {
                "prompt_tokens": 10,
                "completion_tokens": 148,
                "total_tokens": 158,
                "reasoning_tokens": 100,
                "cached_tokens": 8
            }
            """;

        // Act
        var usage = JsonSerializer.Deserialize<OpenAIProviderUsage>(openRouterJson, _options);
        _output.WriteLine($"Deserialized OpenRouter Usage: {usage}");

        // Assert
        Assert.NotNull(usage);
        Assert.Equal(10, usage.PromptTokens);
        Assert.Equal(148, usage.CompletionTokens);
        Assert.Equal(158, usage.TotalTokens);
        Assert.Equal(100, usage.ReasoningTokens);
        Assert.Equal(8, usage.CachedTokens);

        // Test unified access - OpenRouter direct fields take precedence
        Assert.Equal(100, usage.TotalReasoningTokens);
        Assert.Equal(8, usage.TotalCachedTokens);
    }

    [Fact]
    public void OpenAIProviderUsage_ShouldConvertToCoreUsage()
    {
        // Arrange
        var providerUsage = new OpenAIProviderUsage
        {
            PromptTokens = 10,
            CompletionTokens = 148,
            TotalTokens = 158,
            TotalCost = 0.05,
            InputTokenDetails = new OpenAIInputTokenDetails { CachedTokens = 5 },
            OutputTokenDetails = new OpenAIOutputTokenDetails { ReasoningTokens = 128 },
        };

        // Act
        var coreUsage = providerUsage.ToCoreUsage();
        _output.WriteLine($"Converted Core Usage: {coreUsage}");

        // Assert
        Assert.NotNull(coreUsage);
        Assert.Equal(10, coreUsage.PromptTokens);
        Assert.Equal(148, coreUsage.CompletionTokens);
        Assert.Equal(158, coreUsage.TotalTokens);
        Assert.Equal(0.05, coreUsage.TotalCost);
        Assert.NotNull(coreUsage.InputTokenDetails);
        Assert.Equal(5, coreUsage.InputTokenDetails.CachedTokens);
        Assert.NotNull(coreUsage.OutputTokenDetails);
        Assert.Equal(128, coreUsage.OutputTokenDetails.ReasoningTokens);
    }

    [Fact]
    public void OpenAIProviderUsage_ShouldCreateFromCoreUsage()
    {
        // Arrange
        var coreUsage = new Usage
        {
            PromptTokens = 10,
            CompletionTokens = 148,
            TotalTokens = 158,
            TotalCost = 0.05,
            InputTokenDetails = new InputTokenDetails { CachedTokens = 5 },
            OutputTokenDetails = new OutputTokenDetails { ReasoningTokens = 128 },
        };

        // Act
        var providerUsage = OpenAIProviderUsage.FromCoreUsage(coreUsage);
        _output.WriteLine($"Created Provider Usage: {providerUsage}");

        // Assert
        Assert.NotNull(providerUsage);
        Assert.Equal(10, providerUsage.PromptTokens);
        Assert.Equal(148, providerUsage.CompletionTokens);
        Assert.Equal(158, providerUsage.TotalTokens);
        Assert.Equal(0.05, providerUsage.TotalCost);
        Assert.NotNull(providerUsage.InputTokenDetails);
        Assert.Equal(5, providerUsage.InputTokenDetails.CachedTokens);
        Assert.NotNull(providerUsage.OutputTokenDetails);
        Assert.Equal(128, providerUsage.OutputTokenDetails.ReasoningTokens);
    }

    [Fact]
    public void OpenAIProviderUsage_TotalReasoningTokens_ShouldPreferOpenRouterDirectField()
    {
        // Arrange - Both OpenRouter direct field and OpenAI nested structure present
        var usage = new OpenAIProviderUsage
        {
            ReasoningTokens = 100, // OpenRouter direct field
            OutputTokenDetails = new OpenAIOutputTokenDetails { ReasoningTokens = 50 }, // OpenAI nested
        };

        // Act & Assert
        Assert.Equal(100, usage.TotalReasoningTokens); // Should prefer OpenRouter direct field
    }

    [Fact]
    public void OpenAIProviderUsage_TotalReasoningTokens_ShouldFallbackToOpenAiNested()
    {
        // Arrange - Only OpenAI nested structure present
        var usage = new OpenAIProviderUsage
        {
            ReasoningTokens = 0, // Default value
            OutputTokenDetails = new OpenAIOutputTokenDetails { ReasoningTokens = 75 },
        };

        // Act & Assert
        Assert.Equal(75, usage.TotalReasoningTokens); // Should use OpenAI nested structure
    }

    [Fact]
    public void OpenAIProviderUsage_TotalCachedTokens_ShouldPreferOpenRouterDirectField()
    {
        // Arrange - Both OpenRouter direct field and OpenAI nested structure present
        var usage = new OpenAIProviderUsage
        {
            CachedTokens = 20, // OpenRouter direct field
            InputTokenDetails = new OpenAIInputTokenDetails { CachedTokens = 10 }, // OpenAI nested
        };

        // Act & Assert
        Assert.Equal(20, usage.TotalCachedTokens); // Should prefer OpenRouter direct field
    }

    [Fact]
    public void OpenAIProviderUsage_TotalCachedTokens_ShouldFallbackToOpenAiNested()
    {
        // Arrange - Only OpenAI nested structure present
        var usage = new OpenAIProviderUsage
        {
            CachedTokens = 0, // Default value
            InputTokenDetails = new OpenAIInputTokenDetails { CachedTokens = 15 },
        };

        // Act & Assert
        Assert.Equal(15, usage.TotalCachedTokens); // Should use OpenAI nested structure
    }

    [Fact]
    public void OpenAIProviderUsage_ShouldSerializeToJson()
    {
        // Arrange
        var usage = new OpenAIProviderUsage
        {
            PromptTokens = 10,
            CompletionTokens = 148,
            TotalTokens = 158,
            OutputTokenDetails = new OpenAIOutputTokenDetails { ReasoningTokens = 128 },
        };

        // Act
        var json = JsonSerializer.Serialize(usage, _options);
        _output.WriteLine($"Serialized JSON: {json}");

        // Assert
        Assert.Contains("\"prompt_tokens\":10", json);
        Assert.Contains("\"completion_tokens\":148", json);
        Assert.Contains("\"total_tokens\":158", json);
        Assert.Contains("\"output_tokens_details\"", json);
        Assert.Contains("\"reasoning_tokens\":128", json);
    }
}
