using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;

namespace AchieveAi.LmDotnetTools.TestUtils.Tests;

public class DatabasedClientWrapperTests
{
    [Fact]
    public async Task DatabasedClientWrapper_ShouldHandleNewTestData()
    {
        // Arrange
        string testDataPath = Path.Combine(Path.GetTempPath(), $"test_data_{Guid.NewGuid()}.json");

        // Create a mock OpenClient
        var mockClient = new MockOpenClient();

        // Create the wrapper
        var wrapper = new DatabasedClientWrapper(mockClient, testDataPath);

        // Create a simple request
        var messages = new IMessage[]
        {
      new TextMessage
      {
        Role = Role.User,
        Text = "test message"
      }
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "test-model",
            Temperature = 0.7f,
            MaxToken = 100
        };

        var request = ChatCompletionRequest.FromMessages(messages, options);

        // Act
        var response = await wrapper.CreateChatCompletionsAsync(request);

        // Assert
        Assert.NotNull(response);
        Assert.Equal("test-response-id", response.Id);

        // Clean up
        if (File.Exists(testDataPath))
        {
            File.Delete(testDataPath);
        }
    }

    // Simple mock client for testing
    private class MockOpenClient : IOpenClient
    {
        public Task<ChatCompletionResponse> CreateChatCompletionsAsync(
          ChatCompletionRequest chatCompletionRequest,
          System.Threading.CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ChatCompletionResponse
            {
                Id = "test-response-id",
                Model = "test-model",
                Created = 12345,
                Choices = new System.Collections.Generic.List<Choice>
        {
          new Choice
          {
            Index = 0,
            Message = new ChatMessage
            {
              Role = ChatMessage.ToRoleEnum(Role.Assistant),
              Content = ChatMessage.CreateContent("test response")
            }
          }
        }
            });
        }

        public System.Collections.Generic.IAsyncEnumerable<ChatCompletionResponse> StreamingChatCompletionsAsync(
          ChatCompletionRequest chatCompletionRequest,
          System.Threading.CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException("Streaming not implemented in mock");
        }

        public void Dispose()
        {
            // Nothing to dispose
        }
    }
}
