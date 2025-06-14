namespace AchieveAi.LmDotnetTools.OpenAIProvider.Tests.Models;

using System.Collections.Generic;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.OpenAIProvider.Utils;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Tests for validating that ChatCompletionRequest is serialized correctly.
/// </summary>
public class ChatCompletionRequestSerializationTests
{
    private readonly ITestOutputHelper _output;

    public ChatCompletionRequestSerializationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void ChatCompletionRequest_SerializesCorrectly()
    {
        // Arrange
        var options = OpenAIJsonSerializerOptionsFactory.CreateForTesting();

        var messages = new List<ChatMessage>
    {
      new ChatMessage {
        Role = RoleEnum.System,
        Content = new Union<string, Union<TextContent, ImageContent>[]>("You are a helpful assistant.")
      },
      new ChatMessage {
        Role = RoleEnum.User,
        Content = new Union<string, Union<TextContent, ImageContent>[]>("Hello!")
      }
    };

        var request = new ChatCompletionRequest(
          "gpt-4",
          messages,
          temperature: 0.7,
          maxTokens: 1000,
          additionalParameters: new Dictionary<string, object>
          {
              ["frequency_penalty"] = 0.5,
              ["custom_parameter"] = "custom_value"
          }
        )
        {
            FrequencyPenalty = 0.5  // Set this property directly so it's not null
        };

        // Act
        var json = JsonSerializer.Serialize(request, options);
        _output.WriteLine($"Serialized JSON: {json}");

        // Examine the JSON structure
        using var doc = JsonDocument.Parse(json);

        // Assert - verify that the standard properties are present and correctly set
        Assert.True(doc.RootElement.TryGetProperty("model", out var modelElement));
        Assert.Equal("gpt-4", modelElement.GetString());

        Assert.True(doc.RootElement.TryGetProperty("temperature", out var temperatureElement));
        Assert.Equal(0.7, temperatureElement.GetDouble());

        Assert.True(doc.RootElement.TryGetProperty("max_tokens", out var maxTokensElement));
        Assert.Equal(1000, maxTokensElement.GetInt32());

        // Verify the frequency_penalty property is serialized correctly
        Assert.True(doc.RootElement.TryGetProperty("frequency_penalty", out var frequencyPenaltyElement));
        Assert.Equal(0.5, frequencyPenaltyElement.GetDouble());

        // Verify that the messages property contains the expected messages
        Assert.True(doc.RootElement.TryGetProperty("messages", out var messagesElement));
        Assert.Equal(2, messagesElement.GetArrayLength());

        var systemMessage = messagesElement[0];
        Assert.Equal("system", systemMessage.GetProperty("role").GetString());
        Assert.Equal("You are a helpful assistant.", systemMessage.GetProperty("content").GetString());

        var userMessage = messagesElement[1];
        Assert.Equal("user", userMessage.GetProperty("role").GetString());
        Assert.Equal("Hello!", userMessage.GetProperty("content").GetString());
    }
}
