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
        // Even though UsageJsonConverter is applied via attribute, this makes the test more explicit
        _options = new JsonSerializerOptions
        {
            Converters = { new UsageJsonConverter() }
        };
    }

    [Fact]
    public void Usage_ShouldNotSerializeDefaultValues()
    {
        // Arrange
        var usage = new Usage
        {
            PromptTokens = 0,
            CompletionTokens = 0,
            TotalTokens = 0,
            CompletionTokenDetails = null
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
            CompletionTokens = 200,
            TotalTokens = 300,
            CompletionTokenDetails = null
        };

        // Act
        var json = JsonSerializer.Serialize(usage, _options);
        _output.WriteLine($"Serialized JSON: {json}");

        // Assert
        Assert.Contains("\"prompt_tokens\":100", json);
        Assert.Contains("\"completion_tokens\":200", json);
        Assert.Contains("\"total_tokens\":300", json);
        Assert.DoesNotContain("\"completion_token_details\"", json);
    }

    [Fact]
    public void Usage_ShouldSerializeCompletionTokenDetailsWithNonDefaultValues()
    {
        // Arrange
        var usage = new Usage
        {
            PromptTokens = 100,
            CompletionTokens = 200,
            TotalTokens = 300,
            CompletionTokenDetails = new CompletionTokenDetails
            {
                ReasoningTokens = 50
            }
        };

        // Act
        var json = JsonSerializer.Serialize(usage, _options);
        _output.WriteLine($"Serialized JSON: {json}");

        // Assert
        Assert.Contains("\"prompt_tokens\":100", json);
        Assert.Contains("\"completion_tokens\":200", json);
        Assert.Contains("\"total_tokens\":300", json);
        Assert.Contains("\"completion_token_details\"", json);
        Assert.Contains("\"reasoning_tokens\":50", json);
    }

    [Fact]
    public void Usage_ShouldSkipCompletionTokenDetailsWithDefaultValues()
    {
        // Arrange
        var usage = new Usage
        {
            PromptTokens = 100,
            CompletionTokens = 200,
            TotalTokens = 300,
            CompletionTokenDetails = new CompletionTokenDetails
            {
                ReasoningTokens = 0
            }
        };

        // Act
        var json = JsonSerializer.Serialize(usage, _options);
        _output.WriteLine($"Serialized JSON: {json}");

        // Assert
        Assert.Contains("\"prompt_tokens\":100", json);
        Assert.Contains("\"completion_tokens\":200", json);
        Assert.Contains("\"total_tokens\":300", json);
        Assert.DoesNotContain("\"completion_token_details\"", json);
    }
} 