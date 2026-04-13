using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;

namespace AchieveAi.LmDotnetTools.TestUtils.Tests;

public class MockHttpHandlerBuilderRecordPlaybackIntegrationTests
{
    private static readonly string[] fallbackApiKeys = ["LLM_API_KEY"];

    private static readonly string[] fallbackBaseUrls = ["LLM_API_BASE_URL"];

    [Fact]
    public async Task MockHttpHandlerBuilder_RecordPlayback_ShouldHandleNewTestData()
    {
        // Arrange
        var testDataPath = Path.Combine(Path.GetTempPath(), $"test_data_{Guid.NewGuid()}.json");

        // Create MockHttpHandlerBuilder with record/playback and OpenAI response
        var handler = MockHttpHandlerBuilder
            .Create()
            .WithRecordPlayback(testDataPath, true)
            .ForwardToApi(
                EnvironmentHelper.GetApiBaseUrlFromEnv("LLM_API_BASE_URL"),
                EnvironmentHelper.GetApiKeyFromEnv("LLM_API_KEY")
            )
            .Build();

        // Create HttpClient with our mock handler
        var httpClient = new HttpClient(handler);
        var client = new OpenClient(httpClient, GetApiBaseUrlFromEnv());

        // Create a simple request
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "test message" },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "meta-llama/llama-4-maverick:free",
            Temperature = 0.7f,
            MaxToken = 100,
        };

        var request = ChatCompletionRequest.FromMessages(messages, options);

        // Act
        var response = await client.CreateChatCompletionsAsync(request);

        // Assert
        Assert.NotNull(response);
        Assert.Equal("meta-llama/llama-4-maverick:free", response.Model);
        Assert.NotNull(response.Choices);
        Assert.NotEmpty(response.Choices);
        var requestedContent = response.Choices[0].Message!.Content?.Get<string>();
        var realResponse = response;

        response = await client.CreateChatCompletionsAsync(request);
        Assert.NotNull(response);
        Assert.Equal("meta-llama/llama-4-maverick:free", response.Model);
        Assert.NotNull(response.Choices);
        Assert.NotEmpty(response.Choices);
        Assert.Equal(requestedContent, response.Choices[0].Message!.Content?.Get<string>());

        // Clean up
        if (File.Exists(testDataPath))
        {
            File.Delete(testDataPath);
        }
    }

    /// <summary>
    ///     Helper method to get API key from environment (using shared EnvironmentHelper)
    /// </summary>
    private static string GetApiKeyFromEnv()
    {
        return EnvironmentHelper.GetApiKeyFromEnv("OPENAI_API_KEY", fallbackApiKeys);
    }

    /// <summary>
    ///     Helper method to get API base URL from environment (using shared EnvironmentHelper)
    /// </summary>
    private static string GetApiBaseUrlFromEnv()
    {
        return EnvironmentHelper.GetApiBaseUrlFromEnv("OPENAI_API_URL", fallbackBaseUrls);
    }
}
