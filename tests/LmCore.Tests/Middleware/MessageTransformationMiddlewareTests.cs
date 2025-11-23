using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Middleware;

public class MessageTransformationMiddlewareTests
{
    #region Downstream Tests (Ordering Assignment)

    [Fact]
    public async Task Downstream_AssignsMessageOrderIdx_ToMessagesWithSameGenerationId()
    {
        // Arrange
        var middleware = new MessageTransformationMiddleware();
        var agent = new MockAgent(
            new TextMessage { Text = "Hello", GenerationId = "gen1" },
            new TextMessage { Text = "World", GenerationId = "gen1" },
            new UsageMessage { Usage = new Core.Usage(), GenerationId = "gen1" }
        );
        var context = new MiddlewareContext(
            Messages: [],
            Options: null
        );

        // Act
        var result = await middleware.InvokeAsync(context, agent);
        var messages = result.ToList();

        // Assert
        Assert.Equal(3, messages.Count);
        Assert.Equal(0, messages[0].MessageOrderIdx);
        Assert.Equal(1, messages[1].MessageOrderIdx);
        Assert.Equal(2, messages[2].MessageOrderIdx);
    }

    [Fact]
    public async Task Downstream_RestartsOrderingAtZero_ForNewGenerationId()
    {
        // Arrange
        var middleware = new MessageTransformationMiddleware();
        var agent = new MockAgent(
            new TextMessage { Text = "First", GenerationId = "gen1" },
            new TextMessage { Text = "Second", GenerationId = "gen1" },
            new TextMessage { Text = "Third", GenerationId = "gen2" },
            new TextMessage { Text = "Fourth", GenerationId = "gen2" }
        );
        var context = new MiddlewareContext(
            Messages: [],
            Options: null
        );

        // Act
        var result = await middleware.InvokeAsync(context, agent);
        var messages = result.ToList();

        // Assert
        Assert.Equal(4, messages.Count);
        // First generation
        Assert.Equal(0, messages[0].MessageOrderIdx);
        Assert.Equal(1, messages[1].MessageOrderIdx);
        // Second generation - restarts at 0
        Assert.Equal(0, messages[2].MessageOrderIdx);
        Assert.Equal(1, messages[3].MessageOrderIdx);
    }

    [Fact]
    public async Task Downstream_PassesThrough_MessagesWithoutGenerationId()
    {
        // Arrange
        var middleware = new MessageTransformationMiddleware();
        var agent = new MockAgent(
            new TextMessage { Text = "No generation", GenerationId = null },
            new TextMessage { Text = "Has generation", GenerationId = "gen1" }
        );
        var context = new MiddlewareContext(
            Messages: [],
            Options: null
        );

        // Act
        var result = await middleware.InvokeAsync(context, agent);
        var messages = result.ToList();

        // Assert
        Assert.Equal(2, messages.Count);
        Assert.Null(messages[0].MessageOrderIdx);
        Assert.Equal(0, messages[1].MessageOrderIdx);
    }

    [Fact]
    public async Task Downstream_AssignsOrdering_ToAllMessageTypes()
    {
        // Arrange
        var middleware = new MessageTransformationMiddleware();
        var agent = new MockAgent(
            new TextMessage { Text = "Text", GenerationId = "gen1" },
            new ReasoningMessage { Reasoning = "Reason", GenerationId = "gen1" },
            new ToolsCallMessage { ToolCalls = [], GenerationId = "gen1" },
            new UsageMessage { Usage = new Core.Usage(), GenerationId = "gen1" }
        );
        var context = new MiddlewareContext(
            Messages: [],
            Options: null
        );

        // Act
        var result = await middleware.InvokeAsync(context, agent);
        var messages = result.ToList();

        // Assert
        Assert.Equal(4, messages.Count);
        Assert.All(messages, m => Assert.NotNull(m.MessageOrderIdx));
        for (int i = 0; i < messages.Count; i++)
        {
            Assert.Equal(i, messages[i].MessageOrderIdx);
        }
    }

    #endregion

    #region Upstream Tests (Aggregate Reconstruction)

    [Fact]
    public async Task Upstream_ReconstructsToolCallAggregate_FromOrderedMessages()
    {
        // Arrange
        var middleware = new MessageTransformationMiddleware();
        var toolCall = new ToolCall { FunctionName = "test_function", FunctionArgs = "{\"arg\":\"value\"}", ToolCallId = "call_1", ToolCallIdx = 0 };
        var agent = new MockAgent(
            new TextMessage { Text = "Response", GenerationId = "gen1" }
        );
        var inputMessages = new IMessage[]
        {
            new ToolsCallMessage
            {
                ToolCalls = [toolCall],
                GenerationId = "gen1",
                MessageOrderIdx = 0
            },
            new ToolsCallResultMessage
            {
                ToolCallResults = [new ToolCallResult("call_1", "result")],
                GenerationId = "gen1",
                MessageOrderIdx = 1
            }
        };
        var context = new MiddlewareContext(
            Messages: inputMessages,
            Options: null
        );

        // Act
        var result = await middleware.InvokeAsync(context, agent);

        // Assert - Agent should have received aggregated message
        Assert.Single(agent.ReceivedMessages);
        Assert.IsType<ToolsCallAggregateMessage>(agent.ReceivedMessages[0]);
    }

    [Fact]
    public async Task Upstream_ReconstructsCompositeMessage_FromMultipleMessagesWithSameGenerationId()
    {
        // Arrange
        var middleware = new MessageTransformationMiddleware();
        var agent = new MockAgent(
            new TextMessage { Text = "Response", GenerationId = "gen2" }
        );
        var inputMessages = new IMessage[]
        {
            new TextMessage
            {
                Text = "First",
                GenerationId = "gen1",
                MessageOrderIdx = 0
            },
            new TextMessage
            {
                Text = "Second",
                GenerationId = "gen1",
                MessageOrderIdx = 1
            },
            new UsageMessage
            {
                Usage = new Core.Usage(),
                GenerationId = "gen1",
                MessageOrderIdx = 2
            }
        };
        var context = new MiddlewareContext(
            Messages: inputMessages,
            Options: null
        );

        // Act
        var result = await middleware.InvokeAsync(context, agent);

        // Assert - Agent should have received composite message
        Assert.Single(agent.ReceivedMessages);
        var composite = Assert.IsType<CompositeMessage>(agent.ReceivedMessages[0]);
        Assert.Equal(3, composite.Messages.Count);
        Assert.Equal("First", ((TextMessage)composite.Messages[0]).Text);
        Assert.Equal("Second", ((TextMessage)composite.Messages[1]).Text);
    }

    [Fact]
    public async Task Upstream_PassesThrough_SingleMessagesWithGenerationId()
    {
        // Arrange
        var middleware = new MessageTransformationMiddleware();
        var agent = new MockAgent(
            new TextMessage { Text = "Response", GenerationId = "gen2" }
        );
        var inputMessages = new IMessage[]
        {
            new TextMessage
            {
                Text = "Single",
                GenerationId = "gen1",
                MessageOrderIdx = 0
            }
        };
        var context = new MiddlewareContext(
            Messages: inputMessages,
            Options: null
        );

        // Act
        var result = await middleware.InvokeAsync(context, agent);

        // Assert - Single message should pass through unchanged
        Assert.Single(agent.ReceivedMessages);
        var textMessage = Assert.IsType<TextMessage>(agent.ReceivedMessages[0]);
        Assert.Equal("Single", textMessage.Text);
    }

    #endregion

    #region Bidirectional Tests

    [Fact]
    public async Task Bidirectional_FullRoundTrip_OrderedToAggregatedToOrdered()
    {
        // Arrange
        var middleware = new MessageTransformationMiddleware();
        var toolCall = new ToolCall { FunctionName = "test_func", FunctionArgs = "{}", ToolCallId = "call_1", ToolCallIdx = 0 };

        // Mock agent that returns raw messages (simulating provider)
        var agent = new MockAgent(
            new ToolsCallMessage { ToolCalls = [toolCall], GenerationId = "gen1" },
            new TextMessage { Text = "Response", GenerationId = "gen1" }
        );

        // Input: Ordered messages (from application)
        var inputMessages = new IMessage[]
        {
            new ToolsCallMessage
            {
                ToolCalls = [toolCall],
                GenerationId = "gen0",
                MessageOrderIdx = 0
            },
            new ToolsCallResultMessage
            {
                ToolCallResults = [new ToolCallResult("call_1", "result")],
                GenerationId = "gen0",
                MessageOrderIdx = 1
            }
        };
        var context = new MiddlewareContext(
            Messages: inputMessages,
            Options: null
        );

        // Act
        var result = await middleware.InvokeAsync(context, agent);
        var outputMessages = result.ToList();

        // Assert
        // UPSTREAM: Input should be aggregated for agent
        Assert.Single(agent.ReceivedMessages);
        Assert.IsType<ToolsCallAggregateMessage>(agent.ReceivedMessages[0]);

        // DOWNSTREAM: Output should be ordered
        Assert.Equal(2, outputMessages.Count);
        Assert.Equal(0, outputMessages[0].MessageOrderIdx);
        Assert.Equal(1, outputMessages[1].MessageOrderIdx);
    }

    #endregion

    #region Streaming Tests

    [Fact]
    public async Task Downstream_Streaming_AssignsOrderingToStreamingMessages()
    {
        // Arrange
        var middleware = new MessageTransformationMiddleware();
        var streamingMessages = new List<IMessage>
        {
            new TextUpdateMessage { Text = "Hello ", GenerationId = "gen1" },
            new TextUpdateMessage { Text = "World", GenerationId = "gen1" }
        }.ToAsyncEnumerable();

        var agent = new MockStreamingAgent(streamingMessages);
        var context = new MiddlewareContext(
            Messages: [],
            Options: null
        );

        // Act
        var resultStream = await middleware.InvokeStreamingAsync(context, agent);
        var messages = await resultStream.ToListAsync();

        // Assert
        Assert.Equal(2, messages.Count);
        Assert.Equal(0, messages[0].MessageOrderIdx);
        Assert.Equal(1, messages[1].MessageOrderIdx);
    }

    #endregion

    #region Helper Classes

    private class MockAgent : IAgent
    {
        private readonly IMessage[] _responsesToReturn;
        public List<IMessage> ReceivedMessages { get; } = new();

        public string Name => "MockAgent";

        public MockAgent(params IMessage[] responsesToReturn)
        {
            _responsesToReturn = responsesToReturn;
        }

        public Task<IEnumerable<IMessage>> GenerateReplyAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ReceivedMessages.AddRange(messages);
            return Task.FromResult<IEnumerable<IMessage>>(_responsesToReturn);
        }
    }

    private class MockStreamingAgent : IStreamingAgent
    {
        private readonly IAsyncEnumerable<IMessage> _streamToReturn;

        public string Name => "MockStreamingAgent";

        public MockStreamingAgent(IAsyncEnumerable<IMessage> streamToReturn)
        {
            _streamToReturn = streamToReturn;
        }

        public Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_streamToReturn);
        }

        public Task<IEnumerable<IMessage>> GenerateReplyAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }

    #endregion
}

internal static class AsyncEnumerableExtensions
{
    public static async Task<List<T>> ToListAsync<T>(this IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }
        return list;
    }

    public static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(this IEnumerable<T> source)
    {
        foreach (var item in source)
        {
            await Task.Yield();
            yield return item;
        }
    }
}
