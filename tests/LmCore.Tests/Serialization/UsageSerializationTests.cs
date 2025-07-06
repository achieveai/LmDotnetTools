using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Core;
using Xunit.Abstractions;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Serialization;

public class UsageSerializationTests
{
    private readonly ITestOutputHelper _output;
    private readonly JsonSerializerOptions _options;

    public UsageSerializationTests(ITestOutputHelper output)
    {
        _output = output;
        // Set up the options to match how the Usage class would be serialized in production
        // Even though UsageShadowPropertiesJsonConverter is applied via attribute, this makes the test more explicit
        _options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };
    }

    [Fact]
    public void Usage_ShouldSerializeToEmptyJsonWhenAllValuesAreDefault()
    {
        // Arrange
        var usage = new Usage
        {
            PromptTokens = 0,
            CompletionTokens = 0,
            TotalTokens = 0,
            TotalCost = null,
            InputTokenDetails = null,
            OutputTokenDetails = null
        };

        // Act
        var json = JsonSerializer.Serialize(usage, _options);
        _output.WriteLine($"Serialized JSON: {json}");

        // Assert
        Assert.Equal("{}", json);
    }

    [Fact]
    public void Usage_ShouldSerializeNonDefaultValues()
    {
        // Arrange
        var usage = new Usage
        {
            PromptTokens = 100,
            CompletionTokens = 50,
            TotalTokens = 150,
            TotalCost = 0.05,
            InputTokenDetails = null,
            OutputTokenDetails = null
        };

        // Act
        var json = JsonSerializer.Serialize(usage, _options);
        _output.WriteLine($"Serialized JSON: {json}");

        // Assert
        var expectedJson = """{"prompt_tokens":100,"completion_tokens":50,"total_tokens":150,"total_cost":0.05}""";
        Assert.Equal(expectedJson, json);
    }

    [Fact]
    public void Usage_ShouldSerializeOutputTokenDetailsWithNonDefaultValues()
    {
        // Arrange
        var usage = new Usage
        {
            PromptTokens = 100,
            CompletionTokens = 50,
            TotalTokens = 150,
            OutputTokenDetails = new OutputTokenDetails
            {
                ReasoningTokens = 25
            }
        };

        // Act
        var json = JsonSerializer.Serialize(usage, _options);
        _output.WriteLine($"Serialized JSON: {json}");

        // Assert
        var expectedJson = """{"prompt_tokens":100,"completion_tokens":50,"total_tokens":150,"output_tokens_details":{"reasoning_tokens":25}}""";
        Assert.Equal(expectedJson, json);
    }

    [Fact]
    public void Usage_ShouldSkipOutputTokenDetailsWithDefaultValues()
    {
        // Arrange
        var usage = new Usage
        {
            PromptTokens = 100,
            CompletionTokens = 50,
            TotalTokens = 150,
            OutputTokenDetails = new OutputTokenDetails
            {
                ReasoningTokens = 0 // Default value
            }
        };

        // Act
        var json = JsonSerializer.Serialize(usage, _options);
        _output.WriteLine($"Serialized JSON: {json}");

        // Assert
        // With ShadowPropertiesJsonConverter, the empty OutputTokenDetails object is included
        // because the object itself is not null, even though its properties have default values
        var expectedJson = """{"prompt_tokens":100,"completion_tokens":50,"total_tokens":150,"output_tokens_details":{}}""";
        Assert.Equal(expectedJson, json);
        // The OutputTokenDetails will contain an empty object or the reasoning_tokens with value 0
    }

    [Fact]
    public void Usage_ShouldDeserializeFromJson()
    {
        // Arrange
        var json = """{"prompt_tokens":100,"completion_tokens":50,"total_tokens":150,"total_cost":0.05}""";

        // Act
        var usage = JsonSerializer.Deserialize<Usage>(json, _options);
        _output.WriteLine($"Deserialized Usage: {usage}");

        // Assert
        Assert.NotNull(usage);
        Assert.Equal(100, usage.PromptTokens);
        Assert.Equal(50, usage.CompletionTokens);
        Assert.Equal(150, usage.TotalTokens);
        Assert.Equal(0.05, usage.TotalCost);
    }

    [Fact]
    public void Usage_ShouldDeserializeWithOutputTokenDetails()
    {
        // Arrange
        var json = """{"prompt_tokens":100,"completion_tokens":50,"total_tokens":150,"output_tokens_details":{"reasoning_tokens":25}}""";

        // Act
        var usage = JsonSerializer.Deserialize<Usage>(json, _options);
        _output.WriteLine($"Deserialized Usage: {usage}");

        // Assert
        Assert.NotNull(usage);
        Assert.Equal(100, usage.PromptTokens);
        Assert.Equal(50, usage.CompletionTokens);
        Assert.Equal(150, usage.TotalTokens);
        Assert.NotNull(usage.OutputTokenDetails);
        Assert.Equal(25, usage.OutputTokenDetails.ReasoningTokens);
        Assert.Equal(25, usage.TotalReasoningTokens);
    }

    [Fact]
    public void Usage_ShouldDeserializeWithInputTokenDetails()
    {
        // Arrange
        var json = """{"prompt_tokens":100,"completion_tokens":50,"total_tokens":150,"input_tokens_details":{"cached_tokens":30}}""";

        // Act
        var usage = JsonSerializer.Deserialize<Usage>(json, _options);
        _output.WriteLine($"Deserialized Usage: {usage}");

        // Assert
        Assert.NotNull(usage);
        Assert.Equal(100, usage.PromptTokens);
        Assert.Equal(50, usage.CompletionTokens);
        Assert.Equal(150, usage.TotalTokens);
        Assert.NotNull(usage.InputTokenDetails);
        Assert.Equal(30, usage.InputTokenDetails.CachedTokens);
        Assert.Equal(30, usage.TotalCachedTokens);
    }

    [Fact]
    public void Usage_ShouldDeserializeWithBothTokenDetails()
    {
        // Arrange
        var json = """{"prompt_tokens":100,"completion_tokens":50,"total_tokens":150,"input_tokens_details":{"cached_tokens":30},"output_tokens_details":{"reasoning_tokens":25}}""";

        // Act
        var usage = JsonSerializer.Deserialize<Usage>(json, _options);
        _output.WriteLine($"Deserialized Usage: {usage}");

        // Assert
        Assert.NotNull(usage);
        Assert.Equal(100, usage.PromptTokens);
        Assert.Equal(50, usage.CompletionTokens);
        Assert.Equal(150, usage.TotalTokens);
        Assert.NotNull(usage.InputTokenDetails);
        Assert.Equal(30, usage.InputTokenDetails.CachedTokens);
        Assert.NotNull(usage.OutputTokenDetails);
        Assert.Equal(25, usage.OutputTokenDetails.ReasoningTokens);
        Assert.Equal(30, usage.TotalCachedTokens);
        Assert.Equal(25, usage.TotalReasoningTokens);
    }

    [Fact]
    public void Usage_ShouldHandleOpenAiInputTokensField()
    {
        // Arrange - OpenAI uses "input_tokens" instead of "prompt_tokens"
        var json = """{"input_tokens":100,"completion_tokens":50,"total_tokens":150}""";

        // Act
        var usage = JsonSerializer.Deserialize<Usage>(json, _options);
        _output.WriteLine($"Deserialized Usage: {usage}");

        // Assert
        Assert.NotNull(usage);
        Assert.Equal(100, usage.PromptTokens); // Should map to PromptTokens
        Assert.Equal(50, usage.CompletionTokens);
        Assert.Equal(150, usage.TotalTokens);
    }

    [Fact]
    public void Usage_ShouldHandleOpenAiOutputTokensField()
    {
        // Arrange - OpenAI uses "output_tokens" instead of "completion_tokens"
        var json = """{"prompt_tokens":100,"output_tokens":50,"total_tokens":150}""";

        // Act
        var usage = JsonSerializer.Deserialize<Usage>(json, _options);
        _output.WriteLine($"Deserialized Usage: {usage}");

        // Assert
        Assert.NotNull(usage);
        Assert.Equal(100, usage.PromptTokens);
        Assert.Equal(50, usage.CompletionTokens); // Should map to CompletionTokens
        Assert.Equal(150, usage.TotalTokens);
    }
}