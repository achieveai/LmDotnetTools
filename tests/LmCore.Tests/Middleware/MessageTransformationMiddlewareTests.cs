using AchieveAi.LmDotnetTools.LmCore.Core;
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
            new UsageMessage { Usage = new AchieveAi.LmDotnetTools.LmCore.Models.Usage(), GenerationId = "gen1" }
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
            new ToolsCallMessage
            {
                ToolCalls = [new ToolCall { FunctionName = "test", FunctionArgs = "{}", ToolCallId = "call_1", ToolCallIdx = 0 }],
                GenerationId = "gen1"
            },
            new UsageMessage { Usage = new(), GenerationId = "gen1" }
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
        for (var i = 0; i < messages.Count; i++)
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
        _ = Assert.Single(agent.ReceivedMessages);
        _ = Assert.IsType<ToolsCallAggregateMessage>(agent.ReceivedMessages[0]);
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
                Usage = new AchieveAi.LmDotnetTools.LmCore.Models.Usage(),
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
        _ = Assert.Single(agent.ReceivedMessages);
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
        _ = Assert.Single(agent.ReceivedMessages);
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
                MessageOrderIdx = 0,
            },
            new ToolsCallResultMessage
            {
                ToolCallResults = [new ToolCallResult("call_1", "result")],
                GenerationId = "gen0",
                MessageOrderIdx = 1,
            },
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
        _ = Assert.Single(agent.ReceivedMessages);
        _ = Assert.IsType<ToolsCallAggregateMessage>(agent.ReceivedMessages[0]);
        // DOWNSTREAM: Output should be ordered
        Assert.Equal(2, outputMessages.Count);
        Assert.Equal(0, outputMessages[0].MessageOrderIdx);
        Assert.Equal(1, outputMessages[1].MessageOrderIdx);
    }
    #endregion
    #region ChunkIdx Assignment Tests
    [Fact]
    public async Task Downstream_AssignsChunkIdx_ToTextUpdateMessages()
    {
        // Arrange
        var middleware = new MessageTransformationMiddleware();
        var agent = new MockAgent(
            new TextUpdateMessage { Text = "Hello", GenerationId = "gen1" },
            new TextUpdateMessage { Text = " ", GenerationId = "gen1" },
            new TextUpdateMessage { Text = "World", GenerationId = "gen1" }
        );
        var context = new MiddlewareContext(Messages: [], Options: null);
        // Act
        var result = await middleware.InvokeAsync(context, agent);
        var messages = result.Cast<TextUpdateMessage>().ToList();
        // Assert
        Assert.Equal(3, messages.Count);
        // All text updates should have same messageOrderIdx but incrementing chunkIdx
        Assert.All(messages, m => Assert.Equal(0, m.MessageOrderIdx));
        Assert.Equal(0, messages[0].ChunkIdx);
        Assert.Equal(1, messages[1].ChunkIdx);
        Assert.Equal(2, messages[2].ChunkIdx);
    }
    [Fact]
    public async Task Downstream_AssignsChunkIdx_ToReasoningUpdateMessages()
    {
        // Arrange
        var middleware = new MessageTransformationMiddleware();
        var agent = new MockAgent(
            new ReasoningUpdateMessage { Reasoning = "Think", GenerationId = "gen1" },
            new ReasoningUpdateMessage { Reasoning = "ing", GenerationId = "gen1" },
            new ReasoningUpdateMessage { Reasoning = "...", GenerationId = "gen1" }
        );
        var context = new MiddlewareContext(Messages: [], Options: null);
        // Act
        var result = await middleware.InvokeAsync(context, agent);
        var messages = result.Cast<ReasoningUpdateMessage>().ToList();
        // Assert
        Assert.Equal(3, messages.Count);
        // All reasoning updates should have same messageOrderIdx but incrementing chunkIdx
        Assert.All(messages, m => Assert.Equal(0, m.MessageOrderIdx));
        Assert.Equal(0, messages[0].ChunkIdx);
        Assert.Equal(1, messages[1].ChunkIdx);
        Assert.Equal(2, messages[2].ChunkIdx);
    }
    [Fact]
    public async Task Downstream_ResetsChunkIdx_WhenMessageTypeChanges()
    {
        // Arrange
        var middleware = new MessageTransformationMiddleware();
        var agent = new MockAgent(
            new TextUpdateMessage { Text = "Hello", GenerationId = "gen1" },
            new TextUpdateMessage { Text = " World", GenerationId = "gen1" },
            new ReasoningUpdateMessage { Reasoning = "Thinking", GenerationId = "gen1" },
            new ReasoningUpdateMessage { Reasoning = "...", GenerationId = "gen1" }
        );
        var context = new MiddlewareContext(Messages: [], Options: null);
        // Act
        var result = await middleware.InvokeAsync(context, agent);
        var messages = result.ToList();
        // Assert
        Assert.Equal(4, messages.Count);
        // First two are TextUpdateMessages with same orderIdx
        var textUpdate1 = Assert.IsType<TextUpdateMessage>(messages[0]);
        var textUpdate2 = Assert.IsType<TextUpdateMessage>(messages[1]);
        Assert.Equal(0, textUpdate1.MessageOrderIdx);
        Assert.Equal(0, textUpdate1.ChunkIdx);
        Assert.Equal(0, textUpdate2.MessageOrderIdx);
        Assert.Equal(1, textUpdate2.ChunkIdx);
        // Last two are ReasoningUpdateMessages with new orderIdx, reset chunkIdx
        var reasoningUpdate1 = Assert.IsType<ReasoningUpdateMessage>(messages[2]);
        var reasoningUpdate2 = Assert.IsType<ReasoningUpdateMessage>(messages[3]);
        Assert.Equal(1, reasoningUpdate1.MessageOrderIdx);
        Assert.Equal(0, reasoningUpdate1.ChunkIdx); // Reset to 0
        Assert.Equal(1, reasoningUpdate2.MessageOrderIdx);
        Assert.Equal(1, reasoningUpdate2.ChunkIdx);
    }
    [Fact]
    public async Task Downstream_ResetsChunkIdx_WhenCompleteMessageInterrupts()
    {
        // Arrange
        var middleware = new MessageTransformationMiddleware();
        var agent = new MockAgent(
            new TextUpdateMessage { Text = "Hello", GenerationId = "gen1" },
            new TextUpdateMessage { Text = " ", GenerationId = "gen1" },
            new TextMessage { Text = "Complete message", GenerationId = "gen1" },
            new TextUpdateMessage { Text = "World", GenerationId = "gen1" }
        );
        var context = new MiddlewareContext(Messages: [], Options: null);
        // Act
        var result = await middleware.InvokeAsync(context, agent);
        var messages = result.ToList();
        // Assert
        Assert.Equal(4, messages.Count);
        // First two TextUpdateMessages
        var textUpdate1 = Assert.IsType<TextUpdateMessage>(messages[0]);
        var textUpdate2 = Assert.IsType<TextUpdateMessage>(messages[1]);
        Assert.Equal(0, textUpdate1.MessageOrderIdx);
        Assert.Equal(0, textUpdate1.ChunkIdx);
        Assert.Equal(0, textUpdate2.MessageOrderIdx);
        Assert.Equal(1, textUpdate2.ChunkIdx);
        // Complete message gets new orderIdx
        var textMessage = Assert.IsType<TextMessage>(messages[2]);
        Assert.Equal(1, textMessage.MessageOrderIdx);
        // Next TextUpdateMessage starts new message with reset chunkIdx
        var textUpdate3 = Assert.IsType<TextUpdateMessage>(messages[3]);
        Assert.Equal(2, textUpdate3.MessageOrderIdx);
        Assert.Equal(0, textUpdate3.ChunkIdx); // Reset to 0
    }
    #endregion
    #region Plural to Singular Conversion Tests
    [Fact]
    public async Task Downstream_ConvertsToolsCallMessage_ToIndividualToolCallMessages()
    {
        // Arrange
        var middleware = new MessageTransformationMiddleware();
        var agent = new MockAgent(
            new ToolsCallMessage
            {
                ToolCalls = [
                    new ToolCall { FunctionName = "func1", FunctionArgs = "{}", ToolCallId = "call_1", ToolCallIdx = 0 },
                    new ToolCall { FunctionName = "func2", FunctionArgs = "{}", ToolCallId = "call_2", ToolCallIdx = 1 }
                ],
                GenerationId = "gen1"
            }
        );
        var context = new MiddlewareContext(Messages: [], Options: null);
        // Act
        var result = await middleware.InvokeAsync(context, agent);
        var messages = result.ToList();
        // Assert
        Assert.Equal(2, messages.Count);
        var toolCall1 = Assert.IsType<ToolCallMessage>(messages[0]);
        var toolCall2 = Assert.IsType<ToolCallMessage>(messages[1]);
        Assert.Equal(0, toolCall1.MessageOrderIdx);
        Assert.Equal("func1", toolCall1.FunctionName);
        Assert.Equal("call_1", toolCall1.ToolCallId);
        Assert.Equal(1, toolCall2.MessageOrderIdx);
        Assert.Equal("func2", toolCall2.FunctionName);
        Assert.Equal("call_2", toolCall2.ToolCallId);
    }
    [Fact]
    public async Task Downstream_ConvertsToolsCallUpdateMessage_ToIndividualToolCallUpdateMessages()
    {
        // Arrange
        var middleware = new MessageTransformationMiddleware();
        var agent = new MockAgent(
            new ToolsCallUpdateMessage
            {
                ToolCallUpdates = [
                    new ToolCallUpdate { FunctionName = "func1", FunctionArgs = "{\"a\"", ToolCallId = "call_1" }
                ],
                GenerationId = "gen1"
            },
            new ToolsCallUpdateMessage
            {
                ToolCallUpdates = [
                    new ToolCallUpdate { FunctionArgs = ":1}", ToolCallId = "call_1" }
                ],
                GenerationId = "gen1"
            }
        );
        var context = new MiddlewareContext(Messages: [], Options: null);
        // Act
        var result = await middleware.InvokeAsync(context, agent);
        var messages = result.Cast<ToolCallUpdateMessage>().ToList();
        // Assert
        Assert.Equal(2, messages.Count);
        // Both updates for same tool call ID should have same messageOrderIdx but incrementing chunkIdx
        Assert.Equal(0, messages[0].MessageOrderIdx);
        Assert.Equal(0, messages[0].ChunkIdx);
        Assert.Equal("func1", messages[0].FunctionName);
        Assert.Equal(0, messages[1].MessageOrderIdx);
        Assert.Equal(1, messages[1].ChunkIdx);
        Assert.Null(messages[1].FunctionName);
    }
    [Fact]
    public async Task Downstream_ConvertsToolsCallResultMessage_ToIndividualToolCallResultMessages()
    {
        // Arrange
        var middleware = new MessageTransformationMiddleware();
        var agent = new MockAgent(
            new ToolsCallResultMessage
            {
                ToolCallResults = [
                    new ToolCallResult("call_1", "result1"),
                    new ToolCallResult("call_2", "result2")
                ],
                GenerationId = "gen1"
            }
        );
        var context = new MiddlewareContext(Messages: [], Options: null);
        // Act
        var result = await middleware.InvokeAsync(context, agent);
        var messages = result.ToList();
        // Assert
        Assert.Equal(2, messages.Count);
        var result1 = Assert.IsType<ToolCallResultMessage>(messages[0]);
        var result2 = Assert.IsType<ToolCallResultMessage>(messages[1]);
        Assert.Equal(0, result1.MessageOrderIdx);
        Assert.Equal("call_1", result1.ToolCallId);
        Assert.Equal("result1", result1.Result);
        Assert.Equal(1, result2.MessageOrderIdx);
        Assert.Equal("call_2", result2.ToolCallId);
        Assert.Equal("result2", result2.Result);
    }
    #endregion
    #region ToolCallUpdate Identity Tests
    [Fact]
    public async Task Downstream_GroupsToolCallUpdates_BySameToolCallId()
    {
        // Arrange
        var middleware = new MessageTransformationMiddleware();
        var agent = new MockAgent(
            new ToolsCallUpdateMessage
            {
                ToolCallUpdates = [
                    new ToolCallUpdate { FunctionName = "func1", ToolCallId = "call_1" }
                ],
                GenerationId = "gen1"
            },
            new ToolsCallUpdateMessage
            {
                ToolCallUpdates = [
                    new ToolCallUpdate { FunctionArgs = "{\"a\"", ToolCallId = "call_1" }
                ],
                GenerationId = "gen1"
            },
            new ToolsCallUpdateMessage
            {
                ToolCallUpdates = [
                    new ToolCallUpdate { FunctionArgs = ":1}", ToolCallId = "call_1" }
                ],
                GenerationId = "gen1"
            }
        );
        var context = new MiddlewareContext(Messages: [], Options: null);
        // Act
        var result = await middleware.InvokeAsync(context, agent);
        var messages = result.Cast<ToolCallUpdateMessage>().ToList();
        // Assert
        Assert.Equal(3, messages.Count);
        // All updates for same tool call should have same messageOrderIdx
        Assert.All(messages, m => Assert.Equal(0, m.MessageOrderIdx));
        // But incrementing chunkIdx
        Assert.Equal(0, messages[0].ChunkIdx);
        Assert.Equal(1, messages[1].ChunkIdx);
        Assert.Equal(2, messages[2].ChunkIdx);
    }
    [Fact]
    public async Task Downstream_StartsNewMessage_WhenToolCallIdChanges()
    {
        // Arrange
        var middleware = new MessageTransformationMiddleware();
        var agent = new MockAgent(
            new ToolsCallUpdateMessage
            {
                ToolCallUpdates = [
                    new ToolCallUpdate { FunctionName = "func1", ToolCallId = "call_1" }
                ],
                GenerationId = "gen1"
            },
            new ToolsCallUpdateMessage
            {
                ToolCallUpdates = [
                    new ToolCallUpdate { FunctionArgs = "args1", ToolCallId = "call_1" }
                ],
                GenerationId = "gen1"
            },
            new ToolsCallUpdateMessage
            {
                ToolCallUpdates = [
                    new ToolCallUpdate { FunctionName = "func2", ToolCallId = "call_2" }
                ],
                GenerationId = "gen1"
            },
            new ToolsCallUpdateMessage
            {
                ToolCallUpdates = [
                    new ToolCallUpdate { FunctionArgs = "args2", ToolCallId = "call_2" }
                ],
                GenerationId = "gen1"
            }
        );
        var context = new MiddlewareContext(Messages: [], Options: null);
        // Act
        var result = await middleware.InvokeAsync(context, agent);
        var messages = result.Cast<ToolCallUpdateMessage>().ToList();
        // Assert
        Assert.Equal(4, messages.Count);
        // First two updates for call_1
        Assert.Equal(0, messages[0].MessageOrderIdx);
        Assert.Equal(0, messages[0].ChunkIdx);
        Assert.Equal("call_1", messages[0].ToolCallId);
        Assert.Equal(0, messages[1].MessageOrderIdx);
        Assert.Equal(1, messages[1].ChunkIdx);
        Assert.Equal("call_1", messages[1].ToolCallId);
        // Next two updates for call_2 - new messageOrderIdx, reset chunkIdx
        Assert.Equal(1, messages[2].MessageOrderIdx);
        Assert.Equal(0, messages[2].ChunkIdx); // Reset to 0
        Assert.Equal("call_2", messages[2].ToolCallId);
        Assert.Equal(1, messages[3].MessageOrderIdx);
        Assert.Equal(1, messages[3].ChunkIdx);
        Assert.Equal("call_2", messages[3].ToolCallId);
    }
    [Fact]
    public async Task Downstream_HandlesMultipleToolCallUpdates_InSingleMessage()
    {
        // Arrange
        var middleware = new MessageTransformationMiddleware();
        var agent = new MockAgent(
            new ToolsCallUpdateMessage
            {
                ToolCallUpdates = [
                    new ToolCallUpdate { FunctionName = "func1", ToolCallId = "call_1" },
                    new ToolCallUpdate { FunctionName = "func2", ToolCallId = "call_2" }
                ],
                GenerationId = "gen1"
            }
        );
        var context = new MiddlewareContext(Messages: [], Options: null);
        // Act
        var result = await middleware.InvokeAsync(context, agent);
        var messages = result.Cast<ToolCallUpdateMessage>().ToList();
        // Assert
        Assert.Equal(2, messages.Count);
        // Two different tool calls should get different messageOrderIdx
        Assert.Equal(0, messages[0].MessageOrderIdx);
        Assert.Equal(0, messages[0].ChunkIdx);
        Assert.Equal("call_1", messages[0].ToolCallId);
        Assert.Equal(1, messages[1].MessageOrderIdx);
        Assert.Equal(0, messages[1].ChunkIdx); // Different tool call, so reset
        Assert.Equal("call_2", messages[1].ToolCallId);
    }
    #endregion
    #region Exception Tests
    [Fact]
    public async Task Downstream_ThrowsException_ForCompositeMessage()
    {
        // Arrange
        var middleware = new MessageTransformationMiddleware();
        var agent = new MockAgent(
            new CompositeMessage
            {
                Messages = [
                    new TextMessage { Text = "Test", GenerationId = "gen1" }
                ],
                GenerationId = "gen1"
            }
        );
        var context = new MiddlewareContext(Messages: [], Options: null);
        // Act & Assert
        // Exception is thrown during enumeration, not during InvokeAsync call
        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            async () =>
            {
                var result = await middleware.InvokeAsync(context, agent);
                _ = result.ToList(); // Force enumeration
            }
        );
        Assert.Contains("CompositeMessage should not appear when assigning message orderings", exception.Message);
    }
    [Fact]
    public async Task Downstream_ThrowsException_ForToolsCallAggregateMessage()
    {
        // Arrange
        var middleware = new MessageTransformationMiddleware();
        var toolCall = new ToolCall { FunctionName = "test", FunctionArgs = "{}", ToolCallId = "call_1", ToolCallIdx = 0 };
        var agent = new MockAgent(
            new ToolsCallAggregateMessage(
                new ToolsCallMessage { ToolCalls = [toolCall], GenerationId = "gen1" },
                new ToolsCallResultMessage { ToolCallResults = [new ToolCallResult("call_1", "result")], GenerationId = "gen1" }
            )
        );
        var context = new MiddlewareContext(Messages: [], Options: null);
        // Act & Assert
        // Exception is thrown during enumeration, not during InvokeAsync call
        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            async () =>
            {
                var result = await middleware.InvokeAsync(context, agent);
                _ = result.ToList(); // Force enumeration
            }
        );
        Assert.Contains("ToolsCallAggregateMessage should not appear when assigning message orderings", exception.Message);
    }
    #endregion
    #region Complex Transition Tests
    [Fact]
    public async Task Downstream_HandlesComplexMessageSequence_WithAllTypes()
    {
        // Arrange
        var middleware = new MessageTransformationMiddleware();
        var agent = new MockAgent(
            // Reasoning updates
            new ReasoningUpdateMessage { Reasoning = "Think", GenerationId = "gen1" },
            new ReasoningUpdateMessage { Reasoning = "ing", GenerationId = "gen1" },
            // Complete reasoning
            new ReasoningMessage { Reasoning = "Thinking", GenerationId = "gen1" },
            // Tool calls
            new ToolsCallMessage
            {
                ToolCalls = [
                    new ToolCall { FunctionName = "func1", ToolCallId = "call_1", ToolCallIdx = 0 }
                ],
                GenerationId = "gen1"
            },
            // Tool results
            new ToolsCallResultMessage
            {
                ToolCallResults = [new ToolCallResult("call_1", "result")],
                GenerationId = "gen1"
            },
            // Text updates
            new TextUpdateMessage { Text = "Hello", GenerationId = "gen1" },
            new TextUpdateMessage { Text = " World", GenerationId = "gen1" },
            // Usage
            new UsageMessage { Usage = new AchieveAi.LmDotnetTools.LmCore.Models.Usage(), GenerationId = "gen1" }
        );
        var context = new MiddlewareContext(Messages: [], Options: null);
        // Act
        var result = await middleware.InvokeAsync(context, agent);
        var messages = result.ToList();
        // Assert
        Assert.Equal(8, messages.Count);
        // Reasoning updates: orderIdx=0, chunkIdx increments
        var reasoningUpdate1 = Assert.IsType<ReasoningUpdateMessage>(messages[0]);
        var reasoningUpdate2 = Assert.IsType<ReasoningUpdateMessage>(messages[1]);
        Assert.Equal(0, reasoningUpdate1.MessageOrderIdx);
        Assert.Equal(0, reasoningUpdate1.ChunkIdx);
        Assert.Equal(0, reasoningUpdate2.MessageOrderIdx);
        Assert.Equal(1, reasoningUpdate2.ChunkIdx);
        // Complete reasoning: orderIdx=1
        var reasoning = Assert.IsType<ReasoningMessage>(messages[2]);
        Assert.Equal(1, reasoning.MessageOrderIdx);
        // Tool call: orderIdx=2
        var toolCall = Assert.IsType<ToolCallMessage>(messages[3]);
        Assert.Equal(2, toolCall.MessageOrderIdx);
        // Tool result: orderIdx=3
        var toolResult = Assert.IsType<ToolCallResultMessage>(messages[4]);
        Assert.Equal(3, toolResult.MessageOrderIdx);
        // Text updates: orderIdx=4, chunkIdx increments
        var textUpdate1 = Assert.IsType<TextUpdateMessage>(messages[5]);
        var textUpdate2 = Assert.IsType<TextUpdateMessage>(messages[6]);
        Assert.Equal(4, textUpdate1.MessageOrderIdx);
        Assert.Equal(0, textUpdate1.ChunkIdx);
        Assert.Equal(4, textUpdate2.MessageOrderIdx);
        Assert.Equal(1, textUpdate2.ChunkIdx);
        // Usage: orderIdx=5
        var usage = Assert.IsType<UsageMessage>(messages[7]);
        Assert.Equal(5, usage.MessageOrderIdx);
    }
    [Fact]
    public async Task Downstream_HandlesMultipleGenerations_Independently()
    {
        // Arrange
        var middleware = new MessageTransformationMiddleware();
        var agent = new MockAgent(
            // Generation 1
            new TextUpdateMessage { Text = "Gen1-1", GenerationId = "gen1" },
            new TextUpdateMessage { Text = "Gen1-2", GenerationId = "gen1" },
            // Generation 2
            new TextUpdateMessage { Text = "Gen2-1", GenerationId = "gen2" },
            new TextUpdateMessage { Text = "Gen2-2", GenerationId = "gen2" },
            // Back to Generation 1
            new TextUpdateMessage { Text = "Gen1-3", GenerationId = "gen1" }
        );
        var context = new MiddlewareContext(Messages: [], Options: null);
        // Act
        var result = await middleware.InvokeAsync(context, agent);
        var messages = result.Cast<TextUpdateMessage>().ToList();
        // Assert
        Assert.Equal(5, messages.Count);
        // Gen1 messages
        Assert.Equal("gen1", messages[0].GenerationId);
        Assert.Equal(0, messages[0].MessageOrderIdx);
        Assert.Equal(0, messages[0].ChunkIdx);
        Assert.Equal("gen1", messages[1].GenerationId);
        Assert.Equal(0, messages[1].MessageOrderIdx);
        Assert.Equal(1, messages[1].ChunkIdx);
        // Gen2 messages (separate tracking)
        Assert.Equal("gen2", messages[2].GenerationId);
        Assert.Equal(0, messages[2].MessageOrderIdx);
        Assert.Equal(0, messages[2].ChunkIdx);
        Assert.Equal("gen2", messages[3].GenerationId);
        Assert.Equal(0, messages[3].MessageOrderIdx);
        Assert.Equal(1, messages[3].ChunkIdx);
        // Back to Gen1 (continues from where it left off)
        Assert.Equal("gen1", messages[4].GenerationId);
        Assert.Equal(0, messages[4].MessageOrderIdx);
        Assert.Equal(2, messages[4].ChunkIdx);
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
            new TextUpdateMessage { Text = "World", GenerationId = "gen1" },
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
        // Both update messages of same type share same messageOrderIdx with different chunkIdx
        Assert.Equal(0, messages[0].MessageOrderIdx);
        Assert.Equal(0, messages[1].MessageOrderIdx);
    }
    [Fact]
    public async Task Downstream_Streaming_AssignsChunkIdx_ToUpdateMessages()
    {
        // Arrange
        var middleware = new MessageTransformationMiddleware();
        var streamingMessages = new List<IMessage>
        {
            new TextUpdateMessage { Text = "Hello", GenerationId = "gen1" },
            new TextUpdateMessage { Text = " ", GenerationId = "gen1" },
            new TextUpdateMessage { Text = "World", GenerationId = "gen1" }
        }.ToAsyncEnumerable();
        var agent = new MockStreamingAgent(streamingMessages);
        var context = new MiddlewareContext(Messages: [], Options: null);
        // Act
        var resultStream = await middleware.InvokeStreamingAsync(context, agent);
        var messages = (await resultStream.ToListAsync()).Cast<TextUpdateMessage>().ToList();
        // Assert
        Assert.Equal(3, messages.Count);
        Assert.All(messages, m => Assert.Equal(0, m.MessageOrderIdx));
        Assert.Equal(0, messages[0].ChunkIdx);
        Assert.Equal(1, messages[1].ChunkIdx);
        Assert.Equal(2, messages[2].ChunkIdx);
    }
    [Fact]
    public async Task Downstream_Streaming_HandlesIdentityChanges()
    {
        // Arrange
        var middleware = new MessageTransformationMiddleware();
        var streamingMessages = new List<IMessage>
        {
            new TextUpdateMessage { Text = "Text1", GenerationId = "gen1" },
            new TextUpdateMessage { Text = "Text2", GenerationId = "gen1" },
            new ReasoningUpdateMessage { Reasoning = "Think1", GenerationId = "gen1" },
            new ReasoningUpdateMessage { Reasoning = "Think2", GenerationId = "gen1" }
        }.ToAsyncEnumerable();
        var agent = new MockStreamingAgent(streamingMessages);
        var context = new MiddlewareContext(Messages: [], Options: null);
        // Act
        var resultStream = await middleware.InvokeStreamingAsync(context, agent);
        var messages = await resultStream.ToListAsync();
        // Assert
        Assert.Equal(4, messages.Count);
        // Text updates
        var textUpdate1 = Assert.IsType<TextUpdateMessage>(messages[0]);
        var textUpdate2 = Assert.IsType<TextUpdateMessage>(messages[1]);
        Assert.Equal(0, textUpdate1.MessageOrderIdx);
        Assert.Equal(0, textUpdate1.ChunkIdx);
        Assert.Equal(0, textUpdate2.MessageOrderIdx);
        Assert.Equal(1, textUpdate2.ChunkIdx);
        // Reasoning updates (new identity, new orderIdx, reset chunkIdx)
        var reasoningUpdate1 = Assert.IsType<ReasoningUpdateMessage>(messages[2]);
        var reasoningUpdate2 = Assert.IsType<ReasoningUpdateMessage>(messages[3]);
        Assert.Equal(1, reasoningUpdate1.MessageOrderIdx);
        Assert.Equal(0, reasoningUpdate1.ChunkIdx);
        Assert.Equal(1, reasoningUpdate2.MessageOrderIdx);
        Assert.Equal(1, reasoningUpdate2.ChunkIdx);
    }
    #endregion
    #region Upstream Tests (Aggregation)
    [Fact]
    public async Task Upstream_AggregatesMultipleToolCallMessages_IntoToolsCallMessage()
    {
        // Arrange
        var middleware = new MessageTransformationMiddleware();
        var inputMessages = new List<IMessage>
        {
            new ToolCallMessage
            {
                FunctionName = "func1",
                FunctionArgs = "{\"arg\":1}",
                ToolCallId = "call_1",
                Index = 0,
                ToolCallIdx = 0,
                GenerationId = "gen1",
                MessageOrderIdx = 0
            },
            new ToolCallMessage
            {
                FunctionName = "func2",
                FunctionArgs = "{\"arg\":2}",
                ToolCallId = "call_2",
                Index = 1,
                ToolCallIdx = 1,
                GenerationId = "gen1",
                MessageOrderIdx = 1
            },
            new ToolCallMessage
            {
                FunctionName = "func3",
                FunctionArgs = "{\"arg\":3}",
                ToolCallId = "call_3",
                Index = 2,
                ToolCallIdx = 2,
                GenerationId = "gen1",
                MessageOrderIdx = 2
            }
        };
        var agent = new MockAgent();
        var context = new MiddlewareContext(Messages: inputMessages, Options: null);
        // Act
        await middleware.InvokeAsync(context, agent);
        // Assert
        var receivedMessages = agent.ReceivedMessages;
        Assert.Single(receivedMessages);
        var toolsCallMessage = Assert.IsType<ToolsCallMessage>(receivedMessages[0]);
        Assert.Equal(3, toolsCallMessage.ToolCalls.Count);
        Assert.Equal("func1", toolsCallMessage.ToolCalls[0].FunctionName);
        Assert.Equal("func2", toolsCallMessage.ToolCalls[1].FunctionName);
        Assert.Equal("func3", toolsCallMessage.ToolCalls[2].FunctionName);
        Assert.Equal("gen1", toolsCallMessage.GenerationId);
        Assert.Equal(0, toolsCallMessage.MessageOrderIdx);
    }
    [Fact]
    public async Task Upstream_AggregatesMultipleToolCallResultMessages_IntoToolsCallResultMessage()
    {
        // Arrange
        var middleware = new MessageTransformationMiddleware();
        var inputMessages = new List<IMessage>
        {
            new ToolCallResultMessage
            {
                ToolCallId = "call_1",
                Result = "result1",
                GenerationId = "gen1",
                MessageOrderIdx = 0
            },
            new ToolCallResultMessage
            {
                ToolCallId = "call_2",
                Result = "result2",
                GenerationId = "gen1",
                MessageOrderIdx = 1
            },
            new ToolCallResultMessage
            {
                ToolCallId = "call_3",
                Result = "result3",
                GenerationId = "gen1",
                MessageOrderIdx = 2
            }
        };
        var agent = new MockAgent();
        var context = new MiddlewareContext(Messages: inputMessages, Options: null);
        // Act
        await middleware.InvokeAsync(context, agent);
        // Assert
        var receivedMessages = agent.ReceivedMessages;
        Assert.Single(receivedMessages);
        var toolsCallResultMessage = Assert.IsType<ToolsCallResultMessage>(receivedMessages[0]);
        Assert.Equal(3, toolsCallResultMessage.ToolCallResults.Count);
        Assert.Equal("call_1", toolsCallResultMessage.ToolCallResults[0].ToolCallId);
        Assert.Equal("result1", toolsCallResultMessage.ToolCallResults[0].Result);
        Assert.Equal("call_2", toolsCallResultMessage.ToolCallResults[1].ToolCallId);
        Assert.Equal("result2", toolsCallResultMessage.ToolCallResults[1].Result);
        Assert.Equal("call_3", toolsCallResultMessage.ToolCallResults[2].ToolCallId);
        Assert.Equal("result3", toolsCallResultMessage.ToolCallResults[2].Result);
        Assert.Equal("gen1", toolsCallResultMessage.GenerationId);
        Assert.Equal(0, toolsCallResultMessage.MessageOrderIdx);
    }
    [Fact]
    public async Task Upstream_CreatesToolsCallAggregateMessage_FromAggregatedMessages()
    {
        // Arrange
        var middleware = new MessageTransformationMiddleware();
        var inputMessages = new List<IMessage>
        {
            // Tool calls
            new ToolCallMessage
            {
                FunctionName = "func1",
                FunctionArgs = "{\"arg\":1}",
                ToolCallId = "call_1",
                Index = 0,
                ToolCallIdx = 0,
                GenerationId = "gen1",
                MessageOrderIdx = 0
            },
            new ToolCallMessage
            {
                FunctionName = "func2",
                FunctionArgs = "{\"arg\":2}",
                ToolCallId = "call_2",
                Index = 1,
                ToolCallIdx = 1,
                GenerationId = "gen1",
                MessageOrderIdx = 1
            },
            // Tool call results
            new ToolCallResultMessage
            {
                ToolCallId = "call_1",
                Result = "result1",
                GenerationId = "gen1",
                MessageOrderIdx = 2
            },
            new ToolCallResultMessage
            {
                ToolCallId = "call_2",
                Result = "result2",
                GenerationId = "gen1",
                MessageOrderIdx = 3
            }
        };
        var agent = new MockAgent();
        var context = new MiddlewareContext(Messages: inputMessages, Options: null);
        // Act
        await middleware.InvokeAsync(context, agent);
        // Assert
        var receivedMessages = agent.ReceivedMessages;
        Assert.Single(receivedMessages);
        var aggregate = Assert.IsType<ToolsCallAggregateMessage>(receivedMessages[0]);
        Assert.Equal(2, aggregate.ToolsCallMessage.ToolCalls.Count);
        Assert.Equal(2, aggregate.ToolsCallResult.ToolCallResults.Count);
        Assert.Equal("func1", aggregate.ToolsCallMessage.ToolCalls[0].FunctionName);
        Assert.Equal("func2", aggregate.ToolsCallMessage.ToolCalls[1].FunctionName);
        Assert.Equal("result1", aggregate.ToolsCallResult.ToolCallResults[0].Result);
        Assert.Equal("result2", aggregate.ToolsCallResult.ToolCallResults[1].Result);
    }
    [Fact]
    public async Task Upstream_PreservesMessageOrdering_WhenAggregating()
    {
        // Arrange
        var middleware = new MessageTransformationMiddleware();
        var inputMessages = new List<IMessage>
        {
            new TextMessage { Text = "Before", GenerationId = "gen1", MessageOrderIdx = 0 },
            new ToolCallMessage
            {
                FunctionName = "func1",
                FunctionArgs = "{}",
                ToolCallId = "call_1",
                ToolCallIdx = 0,
                GenerationId = "gen1",
                MessageOrderIdx = 1
            },
            new ToolCallMessage
            {
                FunctionName = "func2",
                FunctionArgs = "{}",
                ToolCallId = "call_2",
                ToolCallIdx = 1,
                GenerationId = "gen1",
                MessageOrderIdx = 2
            },
            new TextMessage { Text = "After", GenerationId = "gen1", MessageOrderIdx = 3 }
        };
        var agent = new MockAgent();
        var context = new MiddlewareContext(Messages: inputMessages, Options: null);
        // Act
        await middleware.InvokeAsync(context, agent);
        // Assert
        var receivedMessages = agent.ReceivedMessages.ToList();
        // Should be wrapped in CompositeMessage due to multiple messages with same GenerationId
        Assert.Single(receivedMessages);
        var composite = Assert.IsType<CompositeMessage>(receivedMessages[0]);
        Assert.Equal(3, composite.Messages.Count);
        // Verify order: TextMessage, ToolsCallMessage (aggregated), TextMessage
        Assert.IsType<TextMessage>(composite.Messages[0]);
        Assert.Equal("Before", ((TextMessage)composite.Messages[0]).Text);
        var toolsCall = Assert.IsType<ToolsCallMessage>(composite.Messages[1]);
        Assert.Equal(2, toolsCall.ToolCalls.Count);
        Assert.IsType<TextMessage>(composite.Messages[2]);
        Assert.Equal("After", ((TextMessage)composite.Messages[2]).Text);
    }
    [Fact]
    public async Task Upstream_HandlesMessagesWithoutGenerationId()
    {
        // Arrange
        var middleware = new MessageTransformationMiddleware();
        var inputMessages = new List<IMessage>
        {
            new TextMessage { Text = "No gen id" },
            new ToolCallMessage
            {
                FunctionName = "func1",
                FunctionArgs = "{}",
                ToolCallId = "call_1",
                ToolCallIdx = 0,
                GenerationId = "gen1",
                MessageOrderIdx = 0
            }
        };
        var agent = new MockAgent();
        var context = new MiddlewareContext(Messages: inputMessages, Options: null);
        // Act
        await middleware.InvokeAsync(context, agent);
        // Assert
        var receivedMessages = agent.ReceivedMessages.ToList();
        Assert.Equal(2, receivedMessages.Count);
        // First message without GenerationId passes through unchanged
        var textMessage = Assert.IsType<TextMessage>(receivedMessages[0]);
        Assert.Equal("No gen id", textMessage.Text);
        Assert.Null(textMessage.GenerationId);
        // Second message becomes ToolsCallMessage
        var toolsCall = Assert.IsType<ToolsCallMessage>(receivedMessages[1]);
        Assert.Single(toolsCall.ToolCalls);
    }
    [Fact]
    public async Task Upstream_HandlesMixedGenerationIds()
    {
        // Arrange
        var middleware = new MessageTransformationMiddleware();
        var inputMessages = new List<IMessage>
        {
            new ToolCallMessage
            {
                FunctionName = "func1",
                FunctionArgs = "{}",
                ToolCallId = "call_1",
                ToolCallIdx = 0,
                GenerationId = "gen1",
                MessageOrderIdx = 0
            },
            new ToolCallMessage
            {
                FunctionName = "func2",
                FunctionArgs = "{}",
                ToolCallId = "call_2",
                ToolCallIdx = 0,
                GenerationId = "gen2",
                MessageOrderIdx = 0
            }
        };
        var agent = new MockAgent();
        var context = new MiddlewareContext(Messages: inputMessages, Options: null);
        // Act
        await middleware.InvokeAsync(context, agent);
        // Assert
        var receivedMessages = agent.ReceivedMessages.ToList();
        Assert.Equal(2, receivedMessages.Count);
        // Each GenerationId should produce a separate ToolsCallMessage
        var toolsCall1 = Assert.IsType<ToolsCallMessage>(receivedMessages[0]);
        Assert.Single(toolsCall1.ToolCalls);
        Assert.Equal("gen1", toolsCall1.GenerationId);
        Assert.Equal("func1", toolsCall1.ToolCalls[0].FunctionName);
        var toolsCall2 = Assert.IsType<ToolsCallMessage>(receivedMessages[1]);
        Assert.Single(toolsCall2.ToolCalls);
        Assert.Equal("gen2", toolsCall2.GenerationId);
        Assert.Equal("func2", toolsCall2.ToolCalls[0].FunctionName);
    }
    [Fact]
    public async Task Upstream_HandlesSingleToolCallMessage()
    {
        // Arrange
        var middleware = new MessageTransformationMiddleware();
        var inputMessages = new List<IMessage>
        {
            new ToolCallMessage
            {
                FunctionName = "func1",
                FunctionArgs = "{}",
                ToolCallId = "call_1",
                ToolCallIdx = 0,
                GenerationId = "gen1",
                MessageOrderIdx = 0
            }
        };
        var agent = new MockAgent();
        var context = new MiddlewareContext(Messages: inputMessages, Options: null);
        // Act
        await middleware.InvokeAsync(context, agent);
        // Assert
        var receivedMessages = agent.ReceivedMessages.ToList();
        Assert.Single(receivedMessages);
        // Single ToolCallMessage should still be aggregated into ToolsCallMessage
        var toolsCall = Assert.IsType<ToolsCallMessage>(receivedMessages[0]);
        Assert.Single(toolsCall.ToolCalls);
        Assert.Equal("func1", toolsCall.ToolCalls[0].FunctionName);
    }
    #endregion
    #region Helper Classes
    private class MockAgent : IAgent
    {
        private readonly IMessage[] _responsesToReturn;
        public List<IMessage> ReceivedMessages { get; } = [];
        public static string Name => "MockAgent";
        public MockAgent(params IMessage[] responsesToReturn)
        {
            _responsesToReturn = responsesToReturn;
        }
        public Task<IEnumerable<IMessage>> GenerateReplyAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            ReceivedMessages.AddRange(messages);
            return Task.FromResult<IEnumerable<IMessage>>(_responsesToReturn);
        }
    }
    private class MockStreamingAgent : IStreamingAgent
    {
        private readonly IAsyncEnumerable<IMessage> _streamToReturn;
        public static string Name => "MockStreamingAgent";
        public MockStreamingAgent(IAsyncEnumerable<IMessage> streamToReturn)
        {
            _streamToReturn = streamToReturn;
        }
        public Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            return Task.FromResult(_streamToReturn);
        }
        public Task<IEnumerable<IMessage>> GenerateReplyAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        )
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
