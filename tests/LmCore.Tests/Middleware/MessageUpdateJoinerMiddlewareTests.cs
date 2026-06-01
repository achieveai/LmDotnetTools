using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Models;
using Microsoft.Extensions.Logging;
using Serilog;
namespace AchieveAi.LmDotnetTools.LmCore.Tests.Middleware;

public class MessageUpdateJoinerMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_ShouldPassThrough_ForNonStreamingRequests()
    {
        // Arrange
        var middleware = new MessageUpdateJoinerMiddleware();
        var cancellationToken = CancellationToken.None;

        // Create a regular non-streaming message
        var message = new TextMessage { Text = "This is a non-streaming response" };

        // Mock the agent to return our test message
        var mockAgent = new Mock<IAgent>();
        _ = mockAgent
            .Setup(a =>
                a.GenerateReplyAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([message]);

        // Create context with empty messages
        var context = new MiddlewareContext([], new GenerateReplyOptions());

        // Act
        var result = await middleware.InvokeAsync(context, mockAgent.Object, cancellationToken);

        // Assert
        Assert.NotNull(result);
        var firstMessage = result.FirstOrDefault();
        Assert.NotNull(firstMessage);
        Assert.Equal(message.Text, ((ICanGetText)firstMessage).GetText());

        // Verify the agent was called exactly once
        mockAgent.Verify(
            a =>
                a.GenerateReplyAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task InvokeStreamingAsync_JoinTextMessages()
    {
        // Arrange
        var testString = "This is a test";
        // Default behavior is to not preserve update messages
        var middleware = new MessageUpdateJoinerMiddleware();
        var cancellationToken = CancellationToken.None;

        // Create updates from the test string
        var updateMessages = CreateTextUpdateMessages(SplitStringPreservingSpaces(testString));

        // Set up mock streaming agent to return our updates as an async enumerable
        var mockStreamingAgent = new Mock<IStreamingAgent>();
        _ = mockStreamingAgent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(updateMessages.ToAsyncEnumerable());

        // Create context with empty messages
        var context = new MiddlewareContext([], new GenerateReplyOptions());

        // Act - Get the stream from the middleware
        var resultStream = await middleware.InvokeStreamingAsync(context, mockStreamingAgent.Object, cancellationToken);

        // Manually collect all messages from the stream
        var results = new List<IMessage>();
        await foreach (var message in resultStream)
        {
            results.Add(message);
        }

        // Assert - With current implementation of ProcessTextUpdate, no update messages should be emitted
        // since it just returns the original message and preserveUpdateMessages is false
        _ = Assert.Single(results);

        Assert.Equal(testString, ((ICanGetText)results[0]).GetText());

        // Verify the streaming agent was called exactly once
        mockStreamingAgent.Verify(
            a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task InvokeStreaminAsync_ValidateUsage()
    {
        // Arrange
        var testString = "This is a test";
        // Set preserveUpdateMessages to true to see all messages
        var middleware = new MessageUpdateJoinerMiddleware();
        var cancellationToken = CancellationToken.None;

        // Create updates from the test string
        var textUpdates = CreateTextUpdateMessages(SplitStringPreservingSpaces(testString));

        // Add a UsageMessage at the end
        var usage = new Usage
        {
            PromptTokens = 10,
            CompletionTokens = 10,
            TotalTokens = 20,
            OutputTokenDetails = null,
        };

        var usageMessage = new UsageMessage
        {
            Usage = usage,
            FromAgent = "test-agent",
            Role = Role.Assistant,
        };

        var updateMessages = new List<IMessage>(textUpdates) { usageMessage };

        // Set up mock streaming agent to return our updates as an async enumerable
        var mockStreamingAgent = new Mock<IStreamingAgent>();
        _ = mockStreamingAgent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(updateMessages.ToAsyncEnumerable());

        // Create context with empty messages
        var context = new MiddlewareContext([], new GenerateReplyOptions());

        // Act - Get the stream from the middleware
        var resultStream = await middleware.InvokeStreamingAsync(context, mockStreamingAgent.Object, cancellationToken);

        // Manually collect all messages from the stream
        var results = new List<IMessage>();
        await foreach (var message in resultStream)
        {
            results.Add(message);
        }

        // Assert - Now we expect two messages: the text message and a separate usage message
        Assert.Equal(2, results.Count);

        // Check that the first message is the text message with the complete text
        var textMessage = results[0];
        _ = Assert.IsType<TextMessage>(textMessage);
        Assert.NotNull(textMessage);
        Assert.Equal(testString, ((ICanGetText)textMessage).GetText());

        // Verify that the text message doesn't have usage metadata
        Assert.Null(textMessage.Metadata);

        // Check that the second message is a usage message
        var usageMessageResult = results[1];
        _ = Assert.IsType<UsageMessage>(usageMessageResult);
        var typedUsageMessage = (UsageMessage)usageMessageResult;

        // Verify the usage data is correct
        Assert.NotNull(typedUsageMessage.Usage);
        Assert.Equal(10, typedUsageMessage.Usage.PromptTokens);
        Assert.Equal(10, typedUsageMessage.Usage.CompletionTokens);
        Assert.Equal(20, typedUsageMessage.Usage.TotalTokens);

        // Verify the streaming agent was called exactly once
        mockStreamingAgent.Verify(
            a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task InvokeStreamingAsync_TextDeltasFollowedByFinalizingTextMessage_YieldsSingleTextMessage()
    {
        // Regression (GPT-5.5 / Copilot Responses duplicate bubble): a provider streams text deltas and
        // then emits its OWN finalizing complete TextMessage. The joiner must NOT also emit the
        // synthesized "built" copy — otherwise the same answer is persisted/forwarded twice, which
        // renders as two identical assistant bubbles and bloats the next turn's prompt.
        // Arrange
        var middleware = new MessageUpdateJoinerMiddleware();
        var stream = new List<IMessage>
        {
            new TextUpdateMessage { Text = "Hello", Role = Role.Assistant, GenerationId = "gen1" },
            new TextUpdateMessage { Text = " World", Role = Role.Assistant, GenerationId = "gen1" },
            new TextMessage { Text = "Hello World", Role = Role.Assistant, GenerationId = "gen1" },
        };
        var mockStreamingAgent = new Mock<IStreamingAgent>();
        _ = mockStreamingAgent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(stream.ToAsyncEnumerable());
        var context = new MiddlewareContext([], new GenerateReplyOptions());

        // Act
        var resultStream = await middleware.InvokeStreamingAsync(context, mockStreamingAgent.Object);
        var results = new List<IMessage>();
        await foreach (var message in resultStream)
        {
            results.Add(message);
        }

        // Assert — exactly ONE text message (the provider's finalizing message), not two.
        var textMessages = results.OfType<TextMessage>().ToList();
        _ = Assert.Single(textMessages);
        Assert.Equal("Hello World", ((ICanGetText)textMessages[0]).GetText());
    }

    [Fact]
    public async Task InvokeStreamingAsync_TextDeltasFollowedByDifferentKindComplete_StillEmitsBuiltText()
    {
        // Guard against over-suppression: when text deltas are followed by a complete message of a
        // DIFFERENT kind, the joiner must still emit the built TextMessage — the deltas were not
        // superseded by a same-kind finalizer, so dropping them would lose the streamed text.
        // Arrange
        var middleware = new MessageUpdateJoinerMiddleware();
        var stream = new List<IMessage>
        {
            new TextUpdateMessage { Text = "Hello", Role = Role.Assistant, GenerationId = "gen1" },
            new TextUpdateMessage { Text = " World", Role = Role.Assistant, GenerationId = "gen1" },
            new ReasoningMessage { Reasoning = "afterthought", Role = Role.Assistant, GenerationId = "gen1" },
        };
        var mockStreamingAgent = new Mock<IStreamingAgent>();
        _ = mockStreamingAgent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(stream.ToAsyncEnumerable());
        var context = new MiddlewareContext([], new GenerateReplyOptions());

        // Act
        var resultStream = await middleware.InvokeStreamingAsync(context, mockStreamingAgent.Object);
        var results = new List<IMessage>();
        await foreach (var message in resultStream)
        {
            results.Add(message);
        }

        // Assert — the built text message is preserved, alongside the reasoning message.
        var textMessages = results.OfType<TextMessage>().ToList();
        _ = Assert.Single(textMessages);
        Assert.Equal("Hello World", ((ICanGetText)textMessages[0]).GetText());
        _ = Assert.Single(results.OfType<ReasoningMessage>());
    }

    [Fact]
    public async Task DuplicateBubbleRegression_DeltasPlusFinalizingText_ThroughTransformationAndJoiner_YieldsSingleMergedText()
    {
        // Simplest end-to-end-at-unit-level reproduction of the GPT-5.5 / Copilot "duplicate assistant
        // bubble" bug. A provider streams text deltas and then its OWN finalizing TextMessage (same
        // generationId). Run that through the real downstream "for history" pipeline the app assembles
        // — MessageTransformation (assigns messageOrderIdx) then MessageUpdateJoiner — and the result
        // MUST be exactly ONE TextMessage whose messageOrderIdx equals the deltas' (so the live UI
        // merges them by generationId+messageOrderIdx, and history persists the answer once).
        //
        // Before the fix this produced TWO TextMessages: a joiner-built copy AND the finalizing copy,
        // with mismatched messageOrderIdx (0 vs 1). The single assertion below catches both fixes:
        //   - Assert.Single  -> guards MessageUpdateJoinerMiddleware (no synthesized duplicate)
        //   - MessageOrderIdx -> guards MessageTransformationMiddleware (finalizer shares deltas' idx)
        // Arrange
        var agentMessages = new List<IMessage>
        {
            new TextUpdateMessage { Text = "The capital of France ", Role = Role.Assistant, GenerationId = "gen1" },
            new TextUpdateMessage { Text = "is Paris.", Role = Role.Assistant, GenerationId = "gen1" },
            new TextMessage { Text = "The capital of France is Paris.", Role = Role.Assistant, GenerationId = "gen1" },
        };
        var mockStreamingAgent = new Mock<IStreamingAgent>();
        _ = mockStreamingAgent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(agentMessages.ToAsyncEnumerable());

        // The same downstream history pipeline MultiTurnAgentLoop assembles.
        var pipeline = mockStreamingAgent.Object
            .WithMessageTransformation()
            .WithMiddleware(new MessageUpdateJoinerMiddleware());

        // Act
        var resultStream = await pipeline.GenerateReplyStreamingAsync([], new GenerateReplyOptions());
        var results = new List<IMessage>();
        await foreach (var message in resultStream)
        {
            results.Add(message);
        }

        // Assert — exactly one assistant text representation, carrying the deltas' messageOrderIdx (0).
        var textMessages = results.OfType<TextMessage>().ToList();
        _ = Assert.Single(textMessages);
        Assert.Equal("The capital of France is Paris.", ((ICanGetText)textMessages[0]).GetText());
        Assert.Equal(0, textMessages[0].MessageOrderIdx);
    }

    [Fact]
    public async Task DuplicateBubbleRegression_PipelineEmitsServerSideDedupDecisionLogs()
    {
        // Closes the server-side log-visibility gap: the middleware that prevents the duplicate now
        // LOGS its decision. Drive the repro through the pipeline with real loggers writing
        // DuckDB-queryable CompactJson JSONL, then assert (a) the behavior (one TextMessage) and
        // (b) that both decision logs are present — so a future regression is debuggable from logs,
        // not just from code-reading.
        // Arrange
        var logPath = Path.Combine(AppContext.BaseDirectory, "logs", $"dup-bubble-decisions-{Guid.NewGuid():N}.jsonl");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var serilog = new Serilog.LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(new Serilog.Formatting.Compact.CompactJsonFormatter(), logPath)
            .CreateLogger();
        using var loggerFactory = new Serilog.Extensions.Logging.SerilogLoggerFactory(serilog, dispose: false);

        var agentMessages = new List<IMessage>
        {
            new TextUpdateMessage { Text = "The capital of France ", Role = Role.Assistant, GenerationId = "gen1" },
            new TextUpdateMessage { Text = "is Paris.", Role = Role.Assistant, GenerationId = "gen1" },
            new TextMessage { Text = "The capital of France is Paris.", Role = Role.Assistant, GenerationId = "gen1" },
        };
        var mockStreamingAgent = new Mock<IStreamingAgent>();
        _ = mockStreamingAgent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(agentMessages.ToAsyncEnumerable());

        var pipeline = mockStreamingAgent.Object
            .WithMessageTransformation(loggerFactory.CreateLogger<MessageTransformationMiddleware>())
            .WithMiddleware(new MessageUpdateJoinerMiddleware(logger: loggerFactory.CreateLogger<MessageUpdateJoinerMiddleware>()));

        // Act
        var resultStream = await pipeline.GenerateReplyStreamingAsync([], new GenerateReplyOptions());
        var results = new List<IMessage>();
        await foreach (var message in resultStream)
        {
            results.Add(message);
        }
        serilog.Dispose(); // flush the JSONL to disk

        // Assert — behavior: a single merged text message.
        _ = Assert.Single(results.OfType<TextMessage>());

        // Assert — the de-dup decisions are now visible in the structured logs (DuckDB-queryable).
        var logText = await File.ReadAllTextAsync(logPath);
        Assert.Contains("Joiner suppressed synthesized", logText);
        Assert.Contains("Finalizing TextMessage reuses streamed messageOrderIdx", logText);
    }

    #region Helper Methods

    // Helper method to split string on spaces while including spaces in the parts
    private static List<string> SplitStringPreservingSpaces(string input)
    {
        var result = new List<string>();
        var words = input.Split(' ');

        // Add first word
        result.Add(words[0]);

        // Add remaining words with preceding space
        for (var i = 1; i < words.Length; i++)
        {
            result.Add(" " + words[i]);
        }

        return result;
    }

    // Create a series of text update messages
    public static List<IMessage> CreateTextUpdateMessages(IEnumerable<string> parts)
    {
        var messages = new List<IMessage>();

        foreach (var part in parts)
        {
            messages.Add(new TextUpdateMessage { Text = part, Role = Role.Assistant });
        }

        return messages;
    }

    #endregion
}
