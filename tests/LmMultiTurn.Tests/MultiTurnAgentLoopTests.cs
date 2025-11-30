using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Tests for MultiTurnAgentLoop (raw LLM implementation).
/// </summary>
public class MultiTurnAgentLoopTests
{
    private readonly Mock<IStreamingAgent> _mockAgent = new();
    private readonly Mock<ILogger<MultiTurnAgentLoop>> _loggerMock = new();

    [Fact]
    public void Constructor_ThrowsOnNullProviderAgent()
    {
        // Arrange
        var registry = new FunctionRegistry();

        // Act & Assert
        var act = () => new MultiTurnAgentLoop(null!, registry, "thread-1");
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("providerAgent");
    }

    [Fact]
    public void Constructor_ThrowsOnNullFunctionRegistry()
    {
        // Arrange & Act & Assert
        var act = () => new MultiTurnAgentLoop(_mockAgent.Object, null!, "thread-1");
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("functionRegistry");
    }

    [Fact]
    public void Constructor_ThrowsOnNullThreadId()
    {
        // Arrange
        var registry = new FunctionRegistry();

        // Act & Assert
        var act = () => new MultiTurnAgentLoop(_mockAgent.Object, registry, null!);
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("threadId");
    }

    [Fact]
    public async Task ExecuteRunAsync_ProcessesSimpleTextResponse()
    {
        // Arrange
        var responseMessage = new TextMessage
        {
            Text = "Hello! How can I help you?",
            Role = Role.Assistant,
        };

        SetupMockAgentResponse([responseMessage]);

        var registry = new FunctionRegistry();
        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            registry,
            "test-thread",
            logger: _loggerMock.Object);

        using var cts = new CancellationTokenSource();
        var runTask = loop.RunAsync(cts.Token);

        // Act
        var userInput = new UserInput(
            [new TextMessage { Text = "Hi", Role = Role.User }],
            InputId: "test-input");

        var messages = new List<IMessage>();
        await foreach (var msg in loop.ExecuteRunAsync(userInput, cts.Token))
        {
            messages.Add(msg);
        }

        // Assert
        messages.OfType<RunAssignmentMessage>().Should().NotBeEmpty();
        messages.OfType<TextMessage>().Should().Contain(m => m.Text == "Hello! How can I help you?");
        messages.OfType<RunCompletedMessage>().Should().NotBeEmpty();

        // Cleanup
        await cts.CancelAsync();
    }

    [Fact]
    public async Task ExecuteRunAsync_HandlesToolCalls()
    {
        // Arrange
        var toolCallMessage = new ToolCallMessage
        {
            FunctionName = "get_weather",
            FunctionArgs = "{\"location\": \"Seattle\"}",
            ToolCallId = "call_123",
            Role = Role.Assistant,
        };

        var finalMessage = new TextMessage
        {
            Text = "The weather in Seattle is sunny!",
            Role = Role.Assistant,
        };

        // First call returns tool call, second call returns final message
        var callCount = 0;
        _mockAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>((_, _, ct) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return Task.FromResult(ToAsyncEnumerable([toolCallMessage]));
                }

                return Task.FromResult(ToAsyncEnumerable([finalMessage]));
            });

        var registry = new FunctionRegistry();
        var weatherContract = new FunctionContract
        {
            Name = "get_weather",
            Description = "Get weather for a location",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "location",
                    Description = "The location to get weather for",
                    ParameterType = new JsonSchemaObject { Type = JsonSchemaTypeHelper.ToType("string") },
                    IsRequired = true,
                },
            ],
        };
        registry.AddFunction(weatherContract, _ =>
            Task.FromResult("{\"temperature\": \"72F\", \"condition\": \"sunny\"}"));

        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            registry,
            "test-thread",
            logger: _loggerMock.Object);

        using var cts = new CancellationTokenSource();
        var runTask = loop.RunAsync(cts.Token);

        // Act
        var userInput = new UserInput(
            [new TextMessage { Text = "What's the weather in Seattle?", Role = Role.User }]);

        var messages = new List<IMessage>();
        await foreach (var msg in loop.ExecuteRunAsync(userInput, cts.Token))
        {
            messages.Add(msg);
        }

        // Assert
        messages.OfType<RunAssignmentMessage>().Should().NotBeEmpty();
        messages.OfType<ToolCallMessage>().Should().Contain(tc => tc.FunctionName == "get_weather");
        messages.OfType<ToolCallResultMessage>().Should().NotBeEmpty();
        messages.OfType<TextMessage>().Should().Contain(m => m.Text == "The weather in Seattle is sunny!");
        messages.OfType<RunCompletedMessage>().Should().NotBeEmpty();

        // Cleanup
        await cts.CancelAsync();
    }

    [Fact]
    public async Task SendAsync_ReturnsRunAssignment()
    {
        // Arrange
        SetupMockAgentResponse([new TextMessage { Text = "OK", Role = Role.Assistant }]);

        var registry = new FunctionRegistry();
        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            registry,
            "test-thread",
            logger: _loggerMock.Object);

        using var cts = new CancellationTokenSource();
        var runTask = loop.RunAsync(cts.Token);

        // Act
        var messages = new List<IMessage>
        {
            new TextMessage { Text = "Hello", Role = Role.User },
        };
        var assignment = await loop.SendAsync(messages, "my-input-id");

        // Assert
        assignment.Should().NotBeNull();
        assignment.RunId.Should().NotBeNullOrEmpty();
        assignment.GenerationId.Should().NotBeNullOrEmpty();
        assignment.InputId.Should().Be("my-input-id");

        // Wait for processing
        await Task.Delay(200);

        // Cleanup
        await cts.CancelAsync();
    }

    [Fact]
    public async Task SubscribeAsync_ReceivesAllMessages()
    {
        // Arrange
        var responseMessage = new TextMessage { Text = "Response", Role = Role.Assistant };
        SetupMockAgentResponse([responseMessage]);

        var registry = new FunctionRegistry();
        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            registry,
            "test-thread",
            logger: _loggerMock.Object);

        using var cts = new CancellationTokenSource();
        var runTask = loop.RunAsync(cts.Token);

        var receivedMessages = new List<IMessage>();
        var subscribeTask = Task.Run(async () =>
        {
            await foreach (var msg in loop.SubscribeAsync(cts.Token))
            {
                receivedMessages.Add(msg);
            }
        });

        // Give time for subscription
        await Task.Delay(100);

        // Act
        await loop.SendAsync([new TextMessage { Text = "Hi", Role = Role.User }]);

        // Wait for processing
        await Task.Delay(300);

        // Assert
        receivedMessages.Should().NotBeEmpty();
        receivedMessages.OfType<RunAssignmentMessage>().Should().NotBeEmpty();
        receivedMessages.OfType<TextMessage>().Should().NotBeEmpty();
        receivedMessages.OfType<RunCompletedMessage>().Should().NotBeEmpty();

        // Cleanup
        await cts.CancelAsync();
    }

    private void SetupMockAgentResponse(List<IMessage> messages)
    {
        _mockAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(ToAsyncEnumerable(messages)));
    }

    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
        List<IMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var msg in messages)
        {
            ct.ThrowIfCancellationRequested();
            yield return msg;
            await Task.Yield();
        }
    }
}
