using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmLifecycle.Payloads;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using FluentAssertions;
using LmMultiTurn.Tests.Lifecycle;
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
        act.Should().Throw<ArgumentNullException>().WithParameterName("providerAgent");
    }

    [Fact]
    public void Constructor_ThrowsOnNullFunctionRegistry()
    {
        // Arrange & Act & Assert
        var act = () => new MultiTurnAgentLoop(_mockAgent.Object, null!, "thread-1");
        act.Should().Throw<ArgumentNullException>().WithParameterName("functionRegistry");
    }

    [Fact]
    public void Constructor_ThrowsOnNullThreadId()
    {
        // Arrange
        var registry = new FunctionRegistry();

        // Act & Assert
        var act = () => new MultiTurnAgentLoop(_mockAgent.Object, registry, null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("threadId");
    }

    [Fact]
    public async Task ExecuteRunAsync_ProcessesSimpleTextResponse()
    {
        // Arrange
        var responseMessage = new TextMessage { Text = "Hello! How can I help you?", Role = Role.Assistant };

        SetupMockAgentResponse([responseMessage]);

        var registry = new FunctionRegistry();
        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            registry,
            "test-thread",
            logger: _loggerMock.Object
        );

        using var cts = new CancellationTokenSource();
        var runTask = loop.RunAsync(cts.Token);

        // Act
        var userInput = new UserInput([new TextMessage { Text = "Hi", Role = Role.User }], InputId: "test-input");

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

        var finalMessage = new TextMessage { Text = "The weather in Seattle is sunny!", Role = Role.Assistant };

        // First call returns tool call, second call returns final message
        var callCount = 0;
        _mockAgent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (_, _, ct) =>
                {
                    callCount++;
                    if (callCount == 1)
                    {
                        return Task.FromResult(ToAsyncEnumerable([toolCallMessage]));
                    }

                    return Task.FromResult(ToAsyncEnumerable([finalMessage]));
                }
            );

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
        registry.AddFunction(
            weatherContract,
            (_, _, _) =>
                Task.FromResult<ToolHandlerResult>(
                    ToolHandlerResult.FromText("{\"temperature\": \"72F\", \"condition\": \"sunny\"}")
                )
        );

        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            registry,
            "test-thread",
            logger: _loggerMock.Object
        );

        using var cts = new CancellationTokenSource();
        var runTask = loop.RunAsync(cts.Token);

        // Act
        var userInput = new UserInput([new TextMessage { Text = "What's the weather in Seattle?", Role = Role.User }]);

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

    // BUG H3b: tool_call_result messages must carry a non-null MessageOrderIdx consistent with
    // their ordering, so the client merge/order logic (keyed partly on messageOrderIdx) does not
    // drop them. The loop publishes locally-executed tool results out-of-band (not through the
    // MessageTransformation middleware that stamps ordering), so without an explicit stamp they
    // reach subscribers with MessageOrderIdx == null.
    [Fact]
    public async Task ExecuteRunAsync_LocalToolResult_CarriesMessageOrderIdx()
    {
        // Arrange
        // GenerationId present so the pipeline assigns ordering, as it does for a real run.
        var toolCallMessage = new ToolCallMessage
        {
            FunctionName = "get_weather",
            FunctionArgs = "{\"location\": \"Seattle\"}",
            ToolCallId = "call_123",
            Role = Role.Assistant,
            GenerationId = "gen1",
        };
        var finalMessage = new TextMessage
        {
            Text = "Done!",
            Role = Role.Assistant,
            GenerationId = "gen1",
        };

        var callCount = 0;
        _mockAgent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (_, _, _) =>
                {
                    callCount++;
                    return Task.FromResult(
                        callCount == 1 ? ToAsyncEnumerable([toolCallMessage]) : ToAsyncEnumerable([finalMessage])
                    );
                }
            );

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
        registry.AddFunction(
            weatherContract,
            (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("{\"temperature\": \"72F\"}"))
        );

        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            registry,
            "test-thread",
            logger: _loggerMock.Object
        );

        using var cts = new CancellationTokenSource();
        var runTask = loop.RunAsync(cts.Token);

        var userInput = new UserInput([new TextMessage { Text = "What's the weather in Seattle?", Role = Role.User }]);

        var messages = new List<IMessage>();
        await foreach (var msg in loop.ExecuteRunAsync(userInput, cts.Token))
        {
            messages.Add(msg);
        }

        // Assert — the published tool result carries a non-null MessageOrderIdx.
        var results = messages.OfType<ToolCallResultMessage>().ToList();
        results.Should().NotBeEmpty();
        results.Should().OnlyContain(r => r.MessageOrderIdx != null);

        await cts.CancelAsync();
    }

    // Multi-turn message identity: each agentic TURN within a run must advertise a DISTINCT
    // generationId via options.GenerationId. The client merge key is
    // kind-runId-generationId-messageOrderIdx and messageOrderIdx resets every turn (a fresh
    // OrderingState per streaming invocation), so a run-scoped generationId makes turn N and turn N+1
    // collide — later turns' reasoning/text collapse onto the first block (#105/H1 over-corrected
    // from per-message to per-run; per-turn is the correct middle). A per-turn generationId stays
    // consistent WITHIN a turn (so a turn's tool_call + tool_call_result still share an id — the
    // #105 grouping requirement) while keeping turns distinct.
    [Fact]
    public async Task ExecuteRunAsync_MultiTurn_AssignsDistinctGenerationIdPerTurn()
    {
        // Arrange: turn 1 emits a tool call (forces a 2nd turn); turn 2 emits final text.
        var toolCallMessage = new ToolCallMessage
        {
            FunctionName = "get_weather",
            FunctionArgs = "{\"location\": \"Seattle\"}",
            ToolCallId = "call_123",
            Role = Role.Assistant,
        };
        var finalMessage = new TextMessage { Text = "Done!", Role = Role.Assistant };

        var capturedGenerationIds = new List<string?>();
        var callCount = 0;
        _mockAgent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (_, options, _) =>
                {
                    capturedGenerationIds.Add(options.GenerationId);
                    callCount++;
                    return Task.FromResult(
                        callCount == 1 ? ToAsyncEnumerable([toolCallMessage]) : ToAsyncEnumerable([finalMessage])
                    );
                }
            );

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
        registry.AddFunction(
            weatherContract,
            (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("{\"temperature\": \"72F\"}"))
        );

        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            registry,
            "test-thread",
            logger: _loggerMock.Object
        );

        using var cts = new CancellationTokenSource();
        var runTask = loop.RunAsync(cts.Token);

        var userInput = new UserInput([new TextMessage { Text = "What's the weather in Seattle?", Role = Role.User }]);

        var messages = new List<IMessage>();
        await foreach (var msg in loop.ExecuteRunAsync(userInput, cts.Token))
        {
            messages.Add(msg);
        }

        // Assert — two turns ran, each advertised a non-empty generationId, and they DIFFER.
        capturedGenerationIds.Should().HaveCount(2, "the tool call should force a second turn");
        capturedGenerationIds
            .Should()
            .OnlyContain(
                g => !string.IsNullOrEmpty(g),
                "every turn must advertise a run generationId so WithIds can stamp it onto messages"
            );
        capturedGenerationIds[0]
            .Should()
            .NotBe(
                capturedGenerationIds[1],
                "each agentic turn must advertise a DISTINCT generationId so the client merge key "
                    + "(kind-runId-generationId-messageOrderIdx) stays unique across turns when messageOrderIdx resets"
            );

        await cts.CancelAsync();
    }

    [Fact]
    public async Task ExecuteRunAsync_WithProviderServerToolCall_DoesNotExecuteLocalToolHandler()
    {
        var providerToolCall = new ToolCallMessage
        {
            FunctionName = "web_search",
            FunctionArgs = """{"query":"latest ai news"}""",
            ToolCallId = "srvtoolu_123",
            ExecutionTarget = ExecutionTarget.ProviderServer,
            Role = Role.Assistant,
        };

        var finalMessage = new TextMessage
        {
            Text = "I searched the web and found the latest updates.",
            Role = Role.Assistant,
        };

        _mockAgent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(Task.FromResult(ToAsyncEnumerable([providerToolCall, finalMessage])));

        var registry = new FunctionRegistry();
        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            registry,
            "test-thread",
            logger: _loggerMock.Object
        );

        using var cts = new CancellationTokenSource();
        var runTask = loop.RunAsync(cts.Token);

        var userInput = new UserInput([new TextMessage { Text = "Find latest AI news", Role = Role.User }]);

        var messages = new List<IMessage>();
        await foreach (var msg in loop.ExecuteRunAsync(userInput, cts.Token))
        {
            messages.Add(msg);
        }

        messages
            .OfType<ToolCallMessage>()
            .Should()
            .ContainSingle(tc =>
                tc.ToolCallId == "srvtoolu_123" && tc.ExecutionTarget == ExecutionTarget.ProviderServer
            );
        messages.OfType<ToolCallResultMessage>().Should().BeEmpty();
        messages
            .OfType<TextMessage>()
            .Should()
            .Contain(m => m.Text == "I searched the web and found the latest updates.");

        _mockAgent.Verify(
            a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );

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
            logger: _loggerMock.Object
        );

        using var cts = new CancellationTokenSource();
        var runTask = loop.RunAsync(cts.Token);

        // Act
        var messages = new List<IMessage>
        {
            new TextMessage { Text = "Hello", Role = Role.User },
        };
        var receipt = await loop.SendAsync(messages, "my-input-id");

        // Assert - SendAsync returns SendReceipt (fire-and-forget), not RunAssignment
        receipt.Should().NotBeNull();
        receipt.ReceiptId.Should().NotBeNullOrEmpty();
        receipt.InputId.Should().Be("my-input-id");

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
            logger: _loggerMock.Object
        );

        using var cts = new CancellationTokenSource();
        var runTask = loop.RunAsync(cts.Token);

        // A concurrent collection, not a List: the subscriber loop below appends from a thread-pool
        // thread while the assertions read here, and List<T> is not safe across that boundary.
        var receivedMessages = new System.Collections.Concurrent.ConcurrentQueue<IMessage>();
        var subscribeTask = Task.Run(async () =>
        {
            await foreach (var msg in loop.SubscribeAsync(cts.Token))
            {
                receivedMessages.Enqueue(msg);
            }
        });

        // Give time for subscription
        await Task.Delay(100);

        // Act
        await loop.SendAsync([new TextMessage { Text = "Hi", Role = Role.User }]);

        // Poll for the run's completion rather than sleeping a fixed 300 ms and hoping. That fixed
        // wait is exactly what failed on CI: RunAssignment and the assistant text had both arrived,
        // and only RunCompletedMessage had not — the run was still finishing, not broken.
        IMessage[] received = [];
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            received = [.. receivedMessages];
            if (received.OfType<RunCompletedMessage>().Any())
            {
                break;
            }

            await Task.Delay(25);
        }

        // Assert
        received.Should().NotBeEmpty();
        received.OfType<RunAssignmentMessage>().Should().NotBeEmpty();
        received.OfType<TextMessage>().Should().NotBeEmpty();
        received.OfType<RunCompletedMessage>().Should().NotBeEmpty();

        // Cleanup
        await cts.CancelAsync();
    }

    // Wrap-up on turn-cap hit: when a run exhausts its turn budget while the model is still
    // calling tools, the loop must run one final synthesizing turn so the run ends on an assistant
    // status message instead of a bare tool result left mid-stream. Here the wrap-up turn's model
    // call DOES produce text, so that text becomes the final message.
    [Fact]
    public async Task ExecuteRunAsync_CapHit_RunsWrapUpTurn_WithModelSummary()
    {
        // Arrange: a tool call the loop will execute, and a wrap-up summary the model returns once
        // it is told not to call more tools (the turn AFTER the cap is hit).
        var toolCallMessage = new ToolCallMessage
        {
            FunctionName = "get_weather",
            FunctionArgs = "{\"location\": \"Seattle\"}",
            ToolCallId = "call_loop",
            Role = Role.Assistant,
        };
        var wrapUpSummary = new TextMessage
        {
            Text = "I gathered the weather but ran out of turns; here is the final status.",
            Role = Role.Assistant,
        };

        // Every normal turn returns a tool call (so the run never completes naturally and burns the
        // whole budget). The wrap-up turn is the extra call AFTER the cap; return the summary there.
        var callCount = 0;
        _mockAgent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (msgs, _, _) =>
                {
                    callCount++;
                    // The wrap-up turn is identifiable by its injected instruction (a trailing user
                    // message telling the model not to call more tools).
                    var isWrapUp = msgs.OfType<TextMessage>()
                        .Any(m => m.Role == Role.User && m.Text.Contains("maximum number of tool-use turns"));
                    return Task.FromResult(
                        isWrapUp ? ToAsyncEnumerable([wrapUpSummary]) : ToAsyncEnumerable([toolCallMessage])
                    );
                }
            );

        var registry = new FunctionRegistry();
        registry.AddFunction(
            new FunctionContract
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
            },
            (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("{\"temperature\": \"72F\"}"))
        );

        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            registry,
            "test-thread",
            maxTurnsPerRun: 2,
            logger: _loggerMock.Object
        );

        using var cts = new CancellationTokenSource();
        var runTask = loop.RunAsync(cts.Token);

        var userInput = new UserInput([new TextMessage { Text = "What's the weather in Seattle?", Role = Role.User }]);

        // Act
        var messages = new List<IMessage>();
        await foreach (var msg in loop.ExecuteRunAsync(userInput, cts.Token))
        {
            messages.Add(msg);
        }

        // Assert: the run ends on the model's wrap-up text (not a bare tool result), and completes.
        messages.OfType<TextMessage>().Should().Contain(m => m.Text == wrapUpSummary.Text);
        messages.OfType<RunCompletedMessage>().Should().NotBeEmpty();

        // The last content message before completion is assistant text, not a tool result.
        var lastContent = messages.LastOrDefault(m => m is TextMessage or ToolCallResultMessage);
        lastContent.Should().BeOfType<TextMessage>();

        await cts.CancelAsync();
    }

    [Fact]
    public async Task ExecuteRunAsync_CapHit_ReportsWrapUpAsLifecycleTurn()
    {
        var toolCall = new ToolCallMessage
        {
            FunctionName = "get_weather",
            FunctionArgs = "{\"location\":\"Seattle\"}",
            ToolCallId = "call_lifecycle",
            Role = Role.Assistant,
        };
        var callCount = 0;
        _mockAgent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (messages, _, _) =>
                {
                    callCount++;
                    var wrapUp = messages
                        .OfType<TextMessage>()
                        .Any(m => m.Role == Role.User && m.Text.Contains("maximum number of tool-use turns"));
                    List<IMessage> reply = wrapUp
                        ? [new TextMessage { Text = "final", Role = Role.Assistant }]
                        : [toolCall];
                    return Task.FromResult(ToAsyncEnumerable(reply));
                }
            );

        var registry = new FunctionRegistry();
        registry.AddFunction(
            new FunctionContract
            {
                Name = "get_weather",
                Description = "weather",
                Parameters =
                [
                    new FunctionParameterContract
                    {
                        Name = "location",
                        ParameterType = JsonSchemaObject.String(),
                        IsRequired = true,
                    },
                ],
            },
            (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("ok"))
        );
        var publisher = new RecordingLifecyclePublisher();
        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            registry,
            "wrap-lifecycle-thread",
            maxTurnsPerRun: 1,
            lifecycleServices: new MultiTurnLifecycleServices { Publisher = publisher }
        );
        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        await foreach (
            var _ in loop.ExecuteRunAsync(new UserInput([new TextMessage { Text = "go", Role = Role.User }]), cts.Token)
        ) { }

        publisher.EventTypes.Count(t => t == LifecycleEventTypes.TurnCompleted).Should().Be(2);
        var wrapUpTurn = publisher.Payloads<TurnCompletedPayload>(LifecycleEventTypes.TurnCompleted)[1];
        wrapUpTurn.MessageCount.Should().Be(1);
        wrapUpTurn.ToolCallCount.Should().Be(0);
        publisher
            .Payloads<RunCompletedPayload>(LifecycleEventTypes.RunCompleted)
            .Should()
            .ContainSingle()
            .Which.TurnCount.Should()
            .Be(2);
        callCount.Should().Be(2);
        await cts.CancelAsync();
    }

    [Fact]
    public async Task ExecuteRunAsync_CapHit_WrapUpProviderFailureReportsErrorTurn()
    {
        var toolCall = new ToolCallMessage
        {
            FunctionName = "get_weather",
            FunctionArgs = "{\"location\":\"Seattle\"}",
            ToolCallId = "call_error",
            Role = Role.Assistant,
        };
        var callCount = 0;
        _mockAgent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (_, _, _) =>
                {
                    if (Interlocked.Increment(ref callCount) == 2)
                    {
                        throw new InvalidOperationException("wrap-up failed");
                    }

                    return Task.FromResult(ToAsyncEnumerable([toolCall]));
                }
            );
        var registry = new FunctionRegistry();
        registry.AddFunction(
            new FunctionContract
            {
                Name = "get_weather",
                Description = "weather",
                Parameters =
                [
                    new FunctionParameterContract
                    {
                        Name = "location",
                        ParameterType = JsonSchemaObject.String(),
                        IsRequired = true,
                    },
                ],
            },
            (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("ok"))
        );
        var publisher = new RecordingLifecyclePublisher();
        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            registry,
            "wrap-error-thread",
            maxTurnsPerRun: 1,
            lifecycleServices: new MultiTurnLifecycleServices { Publisher = publisher }
        );
        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        await foreach (
            var _ in loop.ExecuteRunAsync(new UserInput([new TextMessage { Text = "go", Role = Role.User }]), cts.Token)
        ) { }

        var wrapUpTurn = publisher.Payloads<TurnCompletedPayload>(LifecycleEventTypes.TurnCompleted)[1];
        wrapUpTurn.Outcome.Should().Be(LifecycleTurnOutcomes.Error);
        wrapUpTurn.MessageCount.Should().Be(1, "the deterministic fallback is still observed");
        await cts.CancelAsync();
    }

    // Wrap-up on turn-cap hit, fallback path: if the model keeps emitting tool calls even in the
    // wrap-up turn (ignoring the instruction), the loop must NOT execute them and must still close
    // the run on a deterministic assistant status message so it never dead-ends on a tool result.
    [Fact]
    public async Task ExecuteRunAsync_CapHit_WrapUpModelStillCallsTools_PublishesFallbackStatus()
    {
        // Arrange: EVERY turn (including the wrap-up turn) returns only a tool call.
        var toolCallMessage = new ToolCallMessage
        {
            FunctionName = "get_weather",
            FunctionArgs = "{\"location\": \"Seattle\"}",
            ToolCallId = "call_loop",
            Role = Role.Assistant,
        };

        var toolExecutions = 0;
        _mockAgent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(Task.FromResult(ToAsyncEnumerable([toolCallMessage])));

        var registry = new FunctionRegistry();
        registry.AddFunction(
            new FunctionContract
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
            },
            (_, _, _) =>
            {
                Interlocked.Increment(ref toolExecutions);
                return Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("{\"temperature\": \"72F\"}"));
            }
        );

        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            registry,
            "test-thread",
            maxTurnsPerRun: 2,
            logger: _loggerMock.Object
        );

        using var cts = new CancellationTokenSource();
        var runTask = loop.RunAsync(cts.Token);

        var userInput = new UserInput([new TextMessage { Text = "What's the weather in Seattle?", Role = Role.User }]);

        // Act
        var messages = new List<IMessage>();
        await foreach (var msg in loop.ExecuteRunAsync(userInput, cts.Token))
        {
            messages.Add(msg);
        }

        // Assert: a deterministic assistant status message closes the run.
        var finalText = messages.OfType<TextMessage>().LastOrDefault(m => m.Role == Role.Assistant);
        finalText.Should().NotBeNull();
        finalText!.Text.Should().Contain("maximum number of tool-use turns");
        messages.OfType<RunCompletedMessage>().Should().NotBeEmpty();

        // The last content message is the fallback assistant status, not a tool result.
        var lastContent = messages.LastOrDefault(m => m is TextMessage or ToolCallResultMessage);
        lastContent.Should().BeOfType<TextMessage>();

        // The wrap-up turn's tool call must NOT have been executed: exactly the budgeted turns ran
        // a tool (maxTurnsPerRun = 2), and the wrap-up turn added none.
        toolExecutions.Should().Be(2);

        await cts.CancelAsync();
    }

    private void SetupMockAgentResponse(List<IMessage> messages)
    {
        _mockAgent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(Task.FromResult(ToAsyncEnumerable(messages)));
    }

    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
        List<IMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        foreach (var msg in messages)
        {
            ct.ThrowIfCancellationRequested();
            yield return msg;
            await Task.Yield();
        }
    }
}
