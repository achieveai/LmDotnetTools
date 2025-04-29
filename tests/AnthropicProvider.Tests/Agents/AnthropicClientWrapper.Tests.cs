using System.Runtime.CompilerServices;

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

            var typedContent = Assert.IsType<AnthropicResponseTextContent>(response.Content[0]);
            Assert.Equal("Hello from mock Anthropic client!", typedContent.Text);

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
            var streamEvents = await wrapper.StreamingChatCompletionsAsync(request);
            await foreach (var streamEvent in streamEvents)
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
                Content = new List<AnthropicResponseContent>
                {
                    new AnthropicResponseTextContent
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

        public Task<IAsyncEnumerable<AnthropicStreamEvent>> StreamingChatCompletionsAsync(
            AnthropicRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IAsyncEnumerable<AnthropicStreamEvent>>(
                GetStreamEvents(request, cancellationToken));
        }

        private async IAsyncEnumerable<AnthropicStreamEvent> GetStreamEvents(
            AnthropicRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // First event - content block start
            yield return new AnthropicContentBlockStartEvent
            {
                Index = 0,
                ContentBlock = new AnthropicResponseTextContent
                {
                    Type = "text",
                    Text = ""
                }
            };

            await Task.Delay(1, cancellationToken);

            // Second event - content delta
            yield return new AnthropicContentBlockDeltaEvent
            {
                Index = 0,
                Delta = new AnthropicTextDelta
                {
                    Text = "Once upon a time..."
                }
            };

            await Task.Delay(1, cancellationToken);

            // Third event - content block stop
            yield return new AnthropicContentBlockStopEvent
            {
                Index = 0
            };
        }

        public void Dispose()
        {
            // Nothing to dispose
        }
    }
}