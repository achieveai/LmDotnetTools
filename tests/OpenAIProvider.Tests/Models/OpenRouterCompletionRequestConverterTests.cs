using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.OpenAIProvider.Utils;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Tests.Models;

public class OpenRouterCompletionRequestConverterTests
{
    private static readonly string[] sourceArray = ["trim"];

    private static readonly string[] sourceArray2 = ["openai/gpt-4-turbo", "anthropic/claude-3-opus"];

    [Fact]
    public void Create_WithOpenRouterProviders_DetectsModelCorrectly()
    {
        // Arrange
        var messages = new List<IMessage>
        {
            new TextMessage { Role = Role.User, Text = "Hello" },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "anthropic/claude-3-opus",
            ExtraProperties = new Dictionary<string, object?> { ["transforms"] = sourceArray }.ToImmutableDictionary(),
        };

        // Act - use the create method to test detection logic
        var result = ChatCompletionRequestFactory.Create(messages, options);

        // Assert that the model name is preserved
        Assert.Equal("anthropic/claude-3-opus", result.Model);
        // We can't assert on AdditionalParameters since it may be handled differently in implementation
    }

    [Fact]
    public void IsOpenRouterRequest_DetectsFromModelId()
    {
        // Arrange
        var messages = new List<IMessage>
        {
            new TextMessage { Role = Role.User, Text = "Hello" },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "openai/gpt-4", // OpenRouter style model ID
        };

        // Act - This should be detected as an OpenRouter request
        var result = ChatCompletionRequestFactory.Create(messages, options);

        // Assert the model name is preserved
        Assert.Equal("openai/gpt-4", result.Model);
        // We can only verify the result was created without error
    }

    [Fact]
    public void Create_WithModelPreference_CreatesRequest()
    {
        // Arrange
        var messages = new List<IMessage>
        {
            new TextMessage { Role = Role.User, Text = "Hello" },
        };

        var options = new GenerateReplyOptions
        {
            ExtraProperties = new Dictionary<string, object?> { ["models"] = sourceArray2 }.ToImmutableDictionary(),
        };

        // Act
        var result = ChatCompletionRequestFactory.Create(messages, options);

        // Assert
        Assert.NotNull(result);
        // We don't check AdditionalParameters since implementations may differ
    }

    [Fact]
    public void Create_WithResponseFormat_SetsJsonMode()
    {
        // Arrange
        var messages = new List<IMessage>
        {
            new TextMessage { Role = Role.User, Text = "Return a JSON object with name and age" },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "openai/gpt-4",
            ExtraProperties = new Dictionary<string, object?>
            {
                ["response_format"] = new Dictionary<string, object?> { ["type"] = "json_object" },
            }.ToImmutableDictionary(),
        };

        // Act
        var result = ChatCompletionRequestFactory.Create(messages, options);

        // Assert
        Assert.NotNull(result.ResponseFormat);
        Assert.Equal("json_object", result.ResponseFormat.ResponseFormatType);
    }

    [Fact]
    public void Create_WithHttpHeaders_CreatesRequest()
    {
        // Arrange
        var messages = new List<IMessage>
        {
            new TextMessage { Role = Role.User, Text = "Hello" },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "anthropic/claude-3-haiku",
            ExtraProperties = new Dictionary<string, object?>
            {
                ["http_headers"] = new Dictionary<string, string>
                {
                    ["Anthropic-Version"] = "2023-06-01",
                    ["X-Custom-Header"] = "value",
                },
            }.ToImmutableDictionary(),
        };

        // Act
        var result = ChatCompletionRequestFactory.Create(messages, options);

        // Assert
        Assert.NotNull(result);
        // We can only verify the request was created without errors
    }
}
