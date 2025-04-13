using AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
using AchieveAi.LmDotnetTools.AnthropicProvider.Models;
using AchieveAi.LmDotnetTools.TestUtils;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Agents;

public class AnthropicClientWrapperTests
{
    [Fact]
    public async Task CreateChatCompletionsAsync_RecordsInteraction()
    {
        // Arrange
        var mockClient = new MockAnthropicClient();
        var testDataPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".json");
        var wrapper = new AnthropicClientWrapper(mockClient, testDataPath);

        try
        {
            var request = new AnthropicRequest
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
                                Text = "Hello, world!"
                            }
                        }
                    }
                }
            };

            // Act
            var response = await wrapper.CreateChatCompletionsAsync(request);

            // Assert
            Assert.NotNull(response);
            Assert.Equal("mockId", response.Id);
            Assert.Equal("message", response.Type);
            Assert.Equal("assistant", response.Role);
            Assert.Single(response.Content);
            Assert.Equal("text", response.Content[0].Type);
            Assert.Equal("Hello from mock Anthropic client!", response.Content[0].Text);
            
            // Verify test data file was created
            Assert.True(File.Exists(testDataPath));
        }
        finally
        {
            // Clean up
            if (File.Exists(testDataPath))
            {
                File.Delete(testDataPath);
            }
        }
    }

    [Fact]
    public async Task StreamingChatCompletionsAsync_RecordsInteraction()
    {
        // Arrange
        var mockClient = new MockAnthropicClient();
        var testDataPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".json");
        var wrapper = new AnthropicClientWrapper(mockClient, testDataPath);

        try
        {
            var request = new AnthropicRequest
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
                                Text = "Tell me a story"
                            }
                        }
                    }
                },
                Stream = true
            };

            // Act
            var events = new List<AnthropicStreamEvent>();
            await foreach (var streamEvent in wrapper.StreamingChatCompletionsAsync(request))
            {
                events.Add(streamEvent);
            }

            // Assert
            Assert.Equal(3, events.Count);
            Assert.Equal("content_block_start", events[0].Type);
            Assert.Equal("content_block_delta", events[1].Type);
            Assert.Equal("content_block_stop", events[2].Type);
            
            // Verify test data file was created
            Assert.True(File.Exists(testDataPath));
        }
        finally
        {
            // Clean up
            if (File.Exists(testDataPath))
            {
                File.Delete(testDataPath);
            }
        }
    }

    private class MockAnthropicClient : IAnthropicClient
    {
        public Task<AnthropicResponse> CreateChatCompletionsAsync(
            AnthropicRequest request,
            CancellationToken cancellationToken = default)
        {
            var response = new AnthropicResponse
            {
                Id = "mockId",
                Type = "message",
                Role = "assistant",
                Model = request.Model,
                Content = new List<AnthropicContent>
                {
                    new AnthropicContent
                    {
                        Type = "text",
                        Text = "Hello from mock Anthropic client!"
                    }
                },
                StopReason = "end_turn",
                Usage = new AnthropicUsage
                {
                    InputTokens = 10,
                    OutputTokens = 20
                }
            };

            return Task.FromResult(response);
        }

        public async IAsyncEnumerable<AnthropicStreamEvent> StreamingChatCompletionsAsync(
            AnthropicRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // First event - content block start
            yield return new AnthropicStreamEvent
            {
                Type = "content_block_start",
                Index = 0
            };

            await Task.Delay(1, cancellationToken);

            // Second event - content delta
            yield return new AnthropicStreamEvent
            {
                Type = "content_block_delta",
                Index = 0,
                Delta = new AnthropicDelta
                {
                    Type = "text_delta",
                    Text = "Once upon a time..."
                }
            };

            await Task.Delay(1, cancellationToken);

            // Third event - content block stop
            yield return new AnthropicStreamEvent
            {
                Type = "content_block_stop",
                Index = 0
            };
        }

        public void Dispose()
        {
            // Nothing to dispose
        }
    }
} 