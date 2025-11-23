using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Integration;

/// <summary>
/// Integration tests for the agentic loop with the new simplified message flow.
/// Tests the interaction between MessageTransformationMiddleware, ToolCallInjectionMiddleware,
/// ToolCallExecutor, and the provider agents.
/// </summary>
public class AgenticLoopIntegrationTests
{
    #region Single-Turn Agentic Loop Tests

    [Fact]
    public async Task AgenticLoop_SingleTurn_WithToolCall_ShouldCompleteSuccessfully()
    {
        // Arrange
        var generationId1 = "gen1";
        var generationId2 = "gen2";

        // Simulate provider that returns tool call, then final response
        var mockProvider = new Mock​Agent(
            // First call: returns tool call
            new ToolsCallMessage
            {
                ToolCalls =
                [
                    new ToolCall { FunctionName = "get_weather", FunctionArgs = "{\"location\":\"San Francisco\"}", ToolCallId = "call_1", ToolCallIdx = 0 }
                ],
                Role = Role.Assistant,
                GenerationId = generationId1,
                FromAgent = "MockProvider"
            },
            new UsageMessage
            {
                Usage = new Core.Usage { TotalTokens = 100 },
                GenerationId = generationId1,
                FromAgent = "MockProvider"
            }
        );

        // Wrap provider with MessageTransformationMiddleware
        var middleware = new MessageTransformationMiddleware();
        var agent = new MiddlewareWrappingAgent(mockProvider, middleware);

        // Define tool function
        var functionMap = new Dictionary<string, Func<string, Task<string>>>
        {
            ["get_weather"] = args => Task.FromResult("{\"temperature\": 72, \"condition\": \"sunny\"}")
        };

        // Act
        // Step 1: Initial call to LLM
        var userMessage = new TextMessage
        {
            Text = "What's the weather in San Francisco?",
            Role = Role.User
        };

        var firstResponse = await agent.GenerateReplyAsync(new[] { userMessage });
        var firstMessages = firstResponse.ToList();

        // Assert first response has ordered messages
        Assert.Equal(2, firstMessages.Count);
        var toolCallMsg = Assert.IsType<ToolsCallMessage>(firstMessages[0]);
        var usageMsg = Assert.IsType<UsageMessage>(firstMessages[1]);

        // Verify ordering
        Assert.Equal(0, toolCallMsg.MessageOrderIdx);
        Assert.Equal(1, usageMsg.MessageOrderIdx);
        Assert.Equal(generationId1, toolCallMsg.GenerationId);

        // Step 2: Execute tool calls
        var toolResultMessage = await ToolCallExecutor.ExecuteAsync(
            toolCallMsg,
            functionMap
        );

        // Assert tool result has correct generation ID
        Assert.Equal(generationId1, toolResultMessage.GenerationId);
        Assert.Single(toolResultMessage.ToolCallResults);
        Assert.Contains("72", toolResultMessage.ToolCallResults[0].Result);

        // Step 3: Send tool results back to LLM (simulate second turn)
        // Update mock to return final response
        mockProvider.SetNextResponses(
            new TextMessage
            {
                Text = "The weather in San Francisco is 72°F and sunny.",
                Role = Role.Assistant,
                GenerationId = generationId2,
                FromAgent = "MockProvider"
            },
            new UsageMessage
            {
                Usage = new Core.Usage { TotalTokens = 50 },
                GenerationId = generationId2,
                FromAgent = "MockProvider"
            }
        );

        var conversationHistory = new List<IMessage>
        {
            userMessage,
            toolCallMsg,
            toolResultMessage
        };

        var finalResponse = await agent.GenerateReplyAsync(conversationHistory);
        var finalMessages = finalResponse.ToList();

        // Assert final response
        Assert.Equal(2, finalMessages.Count);
        var textMsg = Assert.IsType<TextMessage>(finalMessages[0]);
        var finalUsageMsg = Assert.IsType<UsageMessage>(finalMessages[1]);

        Assert.Contains("72°F", textMsg.Text);
        Assert.Equal(0, textMsg.MessageOrderIdx);
        Assert.Equal(1, finalUsageMsg.MessageOrderIdx);
        Assert.Equal(generationId2, textMsg.GenerationId);
    }

    #endregion

    #region Multi-Turn Agentic Loop Tests

    [Fact]
    public async Task AgenticLoop_MultiTurn_WithMultipleToolCalls_ShouldCompleteSuccessfully()
    {
        // Arrange
        var mockProvider = new MockSequentialAgent(
            // Turn 1: Request weather
            new IMessage[]
            {
                new ToolsCallMessage
                {
                    ToolCalls =
                    [
                        new ToolCall { FunctionName = "get_weather", FunctionArgs = "{\"location\":\"SF\"}", ToolCallId = "call_1", ToolCallIdx = 0 },
                        new ToolCall { FunctionName = "get_weather", FunctionArgs = "{\"location\":\"NYC\"}", ToolCallId = "call_2", ToolCallIdx = 1 }
                    ],
                    Role = Role.Assistant,
                    GenerationId = "gen1",
                    FromAgent = "MockProvider"
                }
            },
            // Turn 2: Final response
            new IMessage[]
            {
                new TextMessage
                {
                    Text = "SF: 72°F, NYC: 65°F",
                    Role = Role.Assistant,
                    GenerationId = "gen2",
                    FromAgent = "MockProvider"
                }
            }
        );

        var middleware = new MessageTransformationMiddleware();
        var agent = new MiddlewareWrappingAgent(mockProvider, middleware);

        var functionMap = new Dictionary<string, Func<string, Task<string>>>
        {
            ["get_weather"] = args =>
            {
                var location = args.Contains("SF") ? "SF" : "NYC";
                var temp = location == "SF" ? "72" : "65";
                return Task.FromResult($"{{\"temperature\": {temp}}}");
            }
        };

        // Act
        var userMessage = new TextMessage { Text = "Compare weather SF vs NYC", Role = Role.User };

        // Turn 1
        var response1 = await agent.GenerateReplyAsync(new[] { userMessage });
        var msg1 = response1.Single();
        var toolCallMsg = Assert.IsType<ToolsCallMessage>(msg1);

        Assert.Equal(2, toolCallMsg.ToolCalls.Count);
        Assert.Equal(0, toolCallMsg.ToolCalls[0].ToolCallIdx);
        Assert.Equal(1, toolCallMsg.ToolCalls[1].ToolCallIdx);

        // Execute tools
        var toolResult = await ToolCallExecutor.ExecuteAsync(toolCallMsg, functionMap);
        Assert.Equal(2, toolResult.ToolCallResults.Count);

        // Turn 2
        IMessage[] history = [userMessage, toolCallMsg, toolResult];
        var response2 = await agent.GenerateReplyAsync(history);
        var msg2 = response2.Single();
        var textMsg = Assert.IsType<TextMessage>(msg2);

        Assert.Contains("SF", textMsg.Text);
        Assert.Contains("NYC", textMsg.Text);
        Assert.Equal(0, textMsg.MessageOrderIdx);
    }

    #endregion

    #region Bidirectional Transformation Tests

    [Fact]
    public async Task AgenticLoop_MessageTransformation_BidirectionalFlow_WorksCorrectly()
    {
        // This test verifies that MessageTransformationMiddleware correctly:
        // 1. Aggregates ordered messages going TO provider (upstream)
        // 2. Assigns ordering to raw messages coming FROM provider (downstream)

        // Arrange
        var trackingProvider = new MessageTrackingAgent(
            new TextMessage
            {
                Text = "Response",
                Role = Role.Assistant,
                GenerationId = "gen1"
            }
        );

        var middleware = new MessageTransformationMiddleware();
        var agent = new MiddlewareWrappingAgent(trackingProvider, middleware);

        // Create ordered messages (as they would be in application)
        var inputMessages = new IMessage[]
        {
            new ToolsCallMessage
            {
                ToolCalls = [new ToolCall { FunctionName = "test", FunctionArgs = "{}", ToolCallId = "call_1", ToolCallIdx = 0 }],
                Role = Role.Assistant,
                GenerationId = "gen0",
                MessageOrderIdx = 0
            },
            new ToolsCallResultMessage
            {
                ToolCallResults = [new ToolCallResult("call_1", "result")],
                Role = Role.Tool,
                GenerationId = "gen0",
                MessageOrderIdx = 1
            }
        };

        // Act
        var response = await agent.GenerateReplyAsync(inputMessages);
        var responseMsg = response.Single();

        // Assert
        // Verify upstream transformation: Provider received aggregated message
        Assert.Single(trackingProvider.ReceivedMessages);
        var receivedMsg = trackingProvider.ReceivedMessages[0];
        Assert.IsType<ToolsCallAggregateMessage>(receivedMsg);

        // Verify downstream transformation: Response has ordering
        Assert.Equal(0, responseMsg.MessageOrderIdx);
        Assert.Equal("gen1", responseMsg.GenerationId);
    }

    #endregion

    #region Tool Injection Tests

    [Fact]
    public async Task AgenticLoop_ToolInjection_WithMessageTransformation_WorksCorrectly()
    {
        // Arrange
        var captureAgent = new OptionsCapturingAgent(
            new TextMessage { Text = "Response", Role = Role.Assistant, GenerationId = "gen1" }
        );

        var functions = new[]
        {
            new FunctionContract
            {
                Name = "test_function",
                Description = "Test function"
            }
        };

        // Create middleware pipeline: MessageTransformation → ToolInjection → Provider
        var transformationMiddleware = new MessageTransformationMiddleware();
        var injectionMiddleware = new ToolCallInjectionMiddleware(functions);

        var agent = new MiddlewareWrappingAgent(
            new MiddlewareWrappingAgent(captureAgent, injectionMiddleware),
            transformationMiddleware
        );

        // Act
        var response = await agent.GenerateReplyAsync(
            new[] { new TextMessage { Text = "Test", Role = Role.User } }
        );

        // Assert
        Assert.NotNull(captureAgent.CapturedOptions);
        Assert.NotNull(captureAgent.CapturedOptions.Functions);
        Assert.Single(captureAgent.CapturedOptions.Functions);
        Assert.Equal("test_function", captureAgent.CapturedOptions.Functions[0].Name);

        // Response should have ordering
        var msg = response.Single();
        Assert.Equal(0, msg.MessageOrderIdx);
    }

    #endregion

    #region Helper Classes

    private class MockAgent : IAgent
    {
        private readonly Queue<IMessage> _responsesToReturn;
        public List<IMessage> ReceivedMessages { get; } = new();

        public string Name => "MockAgent";

        public MockAgent(params IMessage[] responsesToReturn)
        {
            _responsesToReturn = new Queue<IMessage>(responsesToReturn);
        }

        public void SetNextResponses(params IMessage[] responses)
        {
            _responsesToReturn.Clear();
            foreach (var response in responses)
            {
                _responsesToReturn.Enqueue(response);
            }
        }

        public Task<IEnumerable<IMessage>> GenerateReplyAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ReceivedMessages.AddRange(messages);

            var responses = new List<IMessage>();
            while (_responsesToReturn.Count > 0)
            {
                responses.Add(_responsesToReturn.Dequeue());
            }

            return Task.FromResult<IEnumerable<IMessage>>(responses);
        }
    }

    private class MockSequentialAgent : IAgent
    {
        private readonly Queue<IEnumerable<IMessage>> _responseSequence;

        public string Name => "MockSequentialAgent";

        public MockSequentialAgent(params IEnumerable<IMessage>[] responseSequence)
        {
            _responseSequence = new Queue<IEnumerable<IMessage>>(responseSequence);
        }

        public Task<IEnumerable<IMessage>> GenerateReplyAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (_responseSequence.Count == 0)
            {
                return Task.FromResult<IEnumerable<IMessage>>([]);
            }

            return Task.FromResult(_responseSequence.Dequeue());
        }
    }

    private class MessageTrackingAgent : IAgent
    {
        private readonly IMessage _responseToReturn;
        public List<IMessage> ReceivedMessages { get; } = new();

        public string Name => "MessageTrackingAgent";

        public MessageTrackingAgent(IMessage responseToReturn)
        {
            _responseToReturn = responseToReturn;
        }

        public Task<IEnumerable<IMessage>> GenerateReplyAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ReceivedMessages.AddRange(messages);
            return Task.FromResult<IEnumerable<IMessage>>([_responseToReturn]);
        }
    }

    private class OptionsCapturingAgent : IAgent
    {
        private readonly IMessage _responseToReturn;
        public GenerateReplyOptions? CapturedOptions { get; private set; }

        public string Name => "OptionsCapturingAgent";

        public OptionsCapturingAgent(IMessage responseToReturn)
        {
            _responseToReturn = responseToReturn;
        }

        public Task<IEnumerable<IMessage>> GenerateReplyAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CapturedOptions = options;
            return Task.FromResult<IEnumerable<IMessage>>([_responseToReturn]);
        }
    }

    #endregion
}
