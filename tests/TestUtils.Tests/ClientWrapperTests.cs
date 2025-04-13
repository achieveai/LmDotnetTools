using AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
using AchieveAi.LmDotnetTools.AnthropicProvider.Models;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;

namespace AchieveAi.LmDotnetTools.TestUtils.Tests;

public class ClientWrapperTests
{
    [Fact]
    public async Task BothWrappers_RecordAndReplayInteractions_Consistently()
    {
        // Arrange
        var testName = $"ConsistencyTest_{Guid.NewGuid()}";
        
        // Create mock clients for both providers
        var mockOpenAiClient = new MockOpenAIClient();
        var mockAnthropicClient = new MockAnthropicClient();
        
        // Create wrappers
        var openAiWrapper = ClientWrapperFactory.CreateOpenAIClientWrapper(mockOpenAiClient, testName, true);
        var anthropicWrapper = ClientWrapperFactory.CreateAnthropicClientWrapper(mockAnthropicClient, testName, true);
        
        try
        {
            // Act & Assert - Record interactions
            
            // Make OpenAI request
            var openAiRequest = new ChatCompletionRequest
            {
                Model = "gpt-4",
                Messages = new List<ChatMessage>
                {
                    new ChatMessage { Role = RoleEnum.User, Content = "Hello" }
                },
                Temperature = 0.7f,
                MaxTokens = 150
            };
            
            var openAiResponse = await openAiWrapper.CreateChatCompletionsAsync(openAiRequest);
            
            Assert.NotNull(openAiResponse);
            Assert.Equal("Hello from mock OpenAI", openAiResponse.Choices[0].Message.Content.ToString());
            
            // Make Anthropic request
            var anthropicRequest = new AnthropicRequest
            {
                Model = "claude-3-sonnet-20240229",
                Messages = new List<AnthropicMessage>
                {
                    new AnthropicMessage
                    {
                        Role = "user",
                        Content = new List<AnthropicContent>
                        {
                            new AnthropicContent
                            {
                                Type = "text",
                                Text = "Hello"
                            }
                        }
                    }
                },
                Temperature = 0.7f,
                MaxTokens = 150
            };
            
            var anthropicResponse = await anthropicWrapper.CreateChatCompletionsAsync(anthropicRequest);
            
            Assert.NotNull(anthropicResponse);
            Assert.Equal("Hello from mock Anthropic", anthropicResponse.Content[0].Text);
            
            // Create new wrappers to replay the interactions
            var replayOpenAiWrapper = ClientWrapperFactory.CreateOpenAIClientWrapper(mockOpenAiClient, testName, false);
            var replayAnthropicWrapper = ClientWrapperFactory.CreateAnthropicClientWrapper(mockAnthropicClient, testName, false);
            
            // Replay OpenAI interaction
            var replayOpenAiResponse = await replayOpenAiWrapper.CreateChatCompletionsAsync(openAiRequest);
            
            Assert.NotNull(replayOpenAiResponse);
            Assert.Equal("Hello from mock OpenAI", replayOpenAiResponse.Choices[0].Message.Content.ToString());
            
            // Replay Anthropic interaction
            var replayAnthropicResponse = await replayAnthropicWrapper.CreateChatCompletionsAsync(anthropicRequest);
            
            Assert.NotNull(replayAnthropicResponse);
            Assert.Equal("Hello from mock Anthropic", replayAnthropicResponse.Content[0].Text);
        }
        finally
        {
            // Clean up
            string openAiPath = Path.Combine(Environment.GetEnvironmentVariable("TEST_DIRECTORY") ?? "../TestData", "OpenAI", $"{testName}.json");
            string anthropicPath = Path.Combine(Environment.GetEnvironmentVariable("TEST_DIRECTORY") ?? "../TestData", "Anthropic", $"{testName}.json");
            
            if (File.Exists(openAiPath))
            {
                File.Delete(openAiPath);
            }
            
            if (File.Exists(anthropicPath))
            {
                File.Delete(anthropicPath);
            }
            
            openAiWrapper.Dispose();
            anthropicWrapper.Dispose();
        }
    }

    /// <summary>
    /// Mock OpenAI client for testing.
    /// </summary>
    private class MockOpenAIClient : IOpenClient
    {
        public Task<ChatCompletionResponse> CreateChatCompletionsAsync(
            ChatCompletionRequest chatCompletionRequest,
            CancellationToken cancellationToken = default)
        {
            var response = new ChatCompletionResponse
            {
                Id = "mock-id",
                Model = chatCompletionRequest.Model,
                Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Choices = new List<ChatCompletionResponseChoice>
                {
                    new ChatCompletionResponseChoice
                    {
                        Message = new ChatMessage
                        {
                            Role = RoleEnum.Assistant,
                            Content = "Hello from mock OpenAI"
                        },
                        Index = 0,
                        FinishReason = "stop"
                    }
                },
                Usage = new CompletionUsage
                {
                    PromptTokens = 10,
                    CompletionTokens = 5,
                    TotalTokens = 15
                }
            };

            return Task.FromResult(response);
        }

        public async IAsyncEnumerable<ChatCompletionResponse> StreamingChatCompletionsAsync(
            ChatCompletionRequest chatCompletionRequest,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatCompletionResponse
            {
                Id = "mock-id",
                Model = chatCompletionRequest.Model,
                Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Choices = new List<ChatCompletionResponseChoice>
                {
                    new ChatCompletionResponseChoice
                    {
                        Delta = new ChatMessage
                        {
                            Role = RoleEnum.Assistant,
                            Content = "Hello"
                        },
                        Index = 0
                    }
                }
            };

            await Task.Delay(1, cancellationToken);

            yield return new ChatCompletionResponse
            {
                Id = "mock-id",
                Model = chatCompletionRequest.Model,
                Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Choices = new List<ChatCompletionResponseChoice>
                {
                    new ChatCompletionResponseChoice
                    {
                        Delta = new ChatMessage
                        {
                            Content = " from mock OpenAI"
                        },
                        Index = 0
                    }
                }
            };

            await Task.Delay(1, cancellationToken);

            yield return new ChatCompletionResponse
            {
                Id = "mock-id",
                Model = chatCompletionRequest.Model,
                Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Choices = new List<ChatCompletionResponseChoice>
                {
                    new ChatCompletionResponseChoice
                    {
                        Delta = new ChatMessage(),
                        Index = 0,
                        FinishReason = "stop"
                    }
                }
            };
        }

        public void Dispose()
        {
            // Nothing to dispose
        }
    }

    /// <summary>
    /// Mock Anthropic client for testing.
    /// </summary>
    private class MockAnthropicClient : IAnthropicClient
    {
        public Task<AnthropicResponse> CreateChatCompletionsAsync(
            AnthropicRequest request,
            CancellationToken cancellationToken = default)
        {
            var response = new AnthropicResponse
            {
                Id = "mock-id",
                Type = "message",
                Role = "assistant",
                Model = request.Model,
                Content = new List<AnthropicContent>
                {
                    new AnthropicContent
                    {
                        Type = "text",
                        Text = "Hello from mock Anthropic"
                    }
                },
                StopReason = "end_turn",
                Usage = new AnthropicUsage
                {
                    InputTokens = 10,
                    OutputTokens = 5
                }
            };

            return Task.FromResult(response);
        }

        public async IAsyncEnumerable<AnthropicStreamEvent> StreamingChatCompletionsAsync(
            AnthropicRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new AnthropicStreamEvent
            {
                Type = "content_block_start",
                Index = 0,
                Message = new AnthropicResponse
                {
                    Id = "mock-id",
                    Type = "message",
                    Role = "assistant",
                    Model = request.Model
                }
            };

            await Task.Delay(1, cancellationToken);

            yield return new AnthropicStreamEvent
            {
                Type = "content_block_delta",
                Index = 0,
                Delta = new AnthropicDelta
                {
                    Type = "text_delta",
                    Text = "Hello from mock Anthropic"
                }
            };

            await Task.Delay(1, cancellationToken);

            yield return new AnthropicStreamEvent
            {
                Type = "content_block_stop",
                Index = 0
            };

            await Task.Delay(1, cancellationToken);

            yield return new AnthropicStreamEvent
            {
                Type = "message_stop",
                Message = new AnthropicResponse
                {
                    Id = "mock-id",
                    Type = "message",
                    Role = "assistant",
                    Model = request.Model,
                    StopReason = "end_turn"
                },
                Usage = new AnthropicUsage
                {
                    InputTokens = 10,
                    OutputTokens = 5
                }
            };
        }

        public void Dispose()
        {
            // Nothing to dispose
        }
    }
} 