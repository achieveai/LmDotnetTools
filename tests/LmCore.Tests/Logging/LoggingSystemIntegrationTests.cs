using System.Diagnostics;
using AchieveAi.LmDotnetTools.LmCore.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
namespace AchieveAi.LmDotnetTools.LmCore.Tests.Logging;

/// <summary>
///     Integration tests for the comprehensive logging system to verify that logging works correctly
///     across agents, middleware, and factories with proper null handling and performance.
/// </summary>
public class LoggingSystemIntegrationTests
{
    #region Performance and Overhead Tests

    [Fact]
    public async Task FunctionCallMiddleware_WithDisabledLogging_HasMinimalOverhead()
    {
        // Arrange
        var mockLogger = new Mock<ILogger<FunctionCallMiddleware>>();
        _ = mockLogger.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(false);

        var functions = new List<FunctionContract>
        {
            new()
            {
                Name = "perf_test_function",
                Description = "Function for performance testing",
                Parameters = [],
            },
        };
        var functionMap = new Dictionary<string, Func<string, Task<string>>>
        {
            { "perf_test_function", args => Task.FromResult("perf result") },
        };

        var messages = new List<IMessage>
        {
            new TextMessage { Text = "Performance test", Role = Role.User },
        };
        var context = new MiddlewareContext(messages);
        var mockAgent = new Mock<IAgent>();

        _ = mockAgent
            .Setup(x =>
                x.GenerateReplyAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([new TextMessage { Text = "Response", Role = Role.Assistant }]);

        var middleware = new FunctionCallMiddleware(functions, functionMap, "perf-test", mockLogger.Object);

        // Act - Measure performance
        const int iterations = 100;
        var stopwatch = Stopwatch.StartNew();

        for (var i = 0; i < iterations; i++)
        {
            _ = await middleware.InvokeAsync(context, mockAgent.Object);
        }

        stopwatch.Stop();

        // Assert - Should complete quickly even with logging infrastructure
        var timePerIteration = (double)stopwatch.ElapsedMilliseconds / iterations;
        Assert.True(
            timePerIteration < 50.0, // Less than 50ms per iteration
            $"Middleware with disabled logging is too slow: {timePerIteration:F2}ms per iteration"
        );

        // Verify IsEnabled was called (showing logging checks are working)
        mockLogger.Verify(x => x.IsEnabled(It.IsAny<LogLevel>()), Times.AtLeast(iterations));
    }

    #endregion

    #region Error Handling and Context Tests

    [Fact]
    public async Task FunctionCallMiddleware_WithFailingFunction_LogsErrorWithContext()
    {
        // Arrange
        var mockLogger = new Mock<ILogger<FunctionCallMiddleware>>();
        var testException = new InvalidOperationException("Test function failure");

        var functions = new List<FunctionContract>
        {
            new()
            {
                Name = "failing_function",
                Description = "Function that fails for testing",
                Parameters = [],
            },
        };
        var functionMap = new Dictionary<string, Func<string, Task<string>>>
        {
            { "failing_function", args => throw testException },
        };

        var toolCall = new ToolCall
        {
            ToolCallId = "error-test-call-id",
            FunctionName = "failing_function",
            FunctionArgs = "{\"test\": \"data\"}",
        };

        var toolCallMessage = new ToolsCallMessage
        {
            ToolCalls = [toolCall],
            Role = Role.Assistant,
            FromAgent = "error-test-agent",
        };

        var messages = new List<IMessage> { toolCallMessage };
        var context = new MiddlewareContext(messages);

        var middleware = new FunctionCallMiddleware(functions, functionMap, "error-test-middleware", mockLogger.Object);

        // Act
        _ = await middleware.InvokeAsync(context, new Mock<IAgent>().Object);

        // Assert - Verify that error was logged with appropriate context
        mockLogger.Verify(
            x =>
                x.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>(
                        (v, t) =>
                            v.ToString()!.Contains("Function execution failed")
                            && v.ToString()!.Contains("failing_function")
                    ),
                    testException,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()
                ),
            Times.Once
        );
    }

    #endregion

    #region Agent Logging Integration Tests

    [Fact]
    public void FunctionCallMiddleware_WithNullLogger_HandlesGracefully()
    {
        // Arrange
        var functions = new List<FunctionContract>
        {
            new()
            {
                Name = "test_function",
                Description = "Test function for logging",
                Parameters = [],
            },
        };
        var functionMap = new Dictionary<string, Func<string, Task<string>>>
        {
            { "test_function", args => Task.FromResult("test result") },
        };

        // Act & Assert - Should not throw with null logger
        var middleware = new FunctionCallMiddleware(functions, functionMap, "test-middleware");
        Assert.NotNull(middleware);
        Assert.Equal("test-middleware", middleware.Name);
    }

    [Fact]
    public async Task FunctionCallMiddleware_WithLogger_LogsCorrectly()
    {
        // Arrange
        var mockLogger = new Mock<ILogger<FunctionCallMiddleware>>();
        var functions = new List<FunctionContract>
        {
            new()
            {
                Name = "test_function",
                Description = "Test function for logging",
                Parameters = [],
            },
        };
        var functionMap = new Dictionary<string, Func<string, Task<string>>>
        {
            { "test_function", args => Task.FromResult("test result") },
        };

        var messages = new List<IMessage>
        {
            new TextMessage { Text = "Hello", Role = Role.User },
        };
        var context = new MiddlewareContext(messages);
        var mockAgent = new Mock<IAgent>();

        _ = mockAgent
            .Setup(x =>
                x.GenerateReplyAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([new TextMessage { Text = "Response", Role = Role.Assistant }]);

        var middleware = new FunctionCallMiddleware(functions, functionMap, "test-middleware", mockLogger.Object);

        // Act
        _ = await middleware.InvokeAsync(context, mockAgent.Object);

        // Assert - Verify that logging was called
        mockLogger.Verify(
            x =>
                x.Log(
                    It.IsAny<LogLevel>(),
                    It.IsAny<EventId>(),
                    It.IsAny<It.IsAnyType>(),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()
                ),
            Times.AtLeastOnce
        );
    }

    [Fact]
    public async Task FunctionCallMiddleware_WithToolCall_LogsFunctionExecution()
    {
        // Arrange
        var mockLogger = new Mock<ILogger<FunctionCallMiddleware>>();
        var functions = new List<FunctionContract>
        {
            new()
            {
                Name = "logging_test_function",
                Description = "Function to test logging",
                Parameters = [],
            },
        };
        var functionMap = new Dictionary<string, Func<string, Task<string>>>
        {
            { "logging_test_function", args => Task.FromResult("logging test result") },
        };

        var toolCall = new ToolCall
        {
            ToolCallId = "test-call-id",
            FunctionName = "logging_test_function",
            FunctionArgs = "{}",
        };

        var toolCallMessage = new ToolsCallMessage
        {
            ToolCalls = [toolCall],
            Role = Role.Assistant,
            FromAgent = "test-agent",
        };

        var messages = new List<IMessage> { toolCallMessage };
        var context = new MiddlewareContext(messages);

        var middleware = new FunctionCallMiddleware(
            functions,
            functionMap,
            "logging-test-middleware",
            mockLogger.Object
        );

        // Act
        _ = await middleware.InvokeAsync(context, new Mock<IAgent>().Object);

        // Assert - Verify that function execution was logged
        mockLogger.Verify(
            x =>
                x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Function executed")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()
                ),
            Times.AtLeastOnce
        );
    }

    #endregion

    #region Structured Logging Tests

    [Fact]
    public void LogEventIds_AreProperlyStructured()
    {
        // Assert - Verify that all LogEventIds follow the proper structure
        var agentEvents = new[]
        {
            LogEventIds.AgentRequestInitiated,
            LogEventIds.AgentRequestCompleted,
            LogEventIds.AgentRequestFailed,
            LogEventIds.AgentCacheHit,
            LogEventIds.AgentCacheMiss,
        };

        var middlewareEvents = new[]
        {
            LogEventIds.MiddlewareProcessing,
            LogEventIds.MiddlewareProcessingCompleted,
            LogEventIds.MiddlewareProcessingFailed,
        };

        var providerEvents = new[] { LogEventIds.ProviderResolved, LogEventIds.ProviderResolutionFailed };

        // Verify agent events are in the 1000-1999 range
        foreach (var eventId in agentEvents)
        {
            Assert.True(
                eventId.Id is >= 1000 and < 2000,
                $"Agent event {eventId.Name} has ID {eventId.Id} outside expected range 1000-1999"
            );
        }

        // Verify middleware events are in the 2000-2999 range
        foreach (var eventId in middlewareEvents)
        {
            Assert.True(
                eventId.Id is >= 2000 and < 3000,
                $"Middleware event {eventId.Name} has ID {eventId.Id} outside expected range 2000-2999"
            );
        }

        // Verify provider events are in the 3000-3999 range
        foreach (var eventId in providerEvents)
        {
            Assert.True(
                eventId.Id is >= 3000 and < 4000,
                $"Provider event {eventId.Name} has ID {eventId.Id} outside expected range 3000-3999"
            );
        }
    }

    [Fact]
    public void StructuredLogging_WithComplexParameters_WorksCorrectly()
    {
        // Arrange
        var mockLogger = new Mock<ILogger<FunctionCallMiddleware>>();
        var complexData = new
        {
            RequestId = "test-123",
            ModelId = "gpt-4",
            Parameters = new { Temperature = 0.7, MaxTokens = 1000 },
            Metadata = new Dictionary<string, object>
            {
                { "source", "test" },
                { "timestamp", DateTime.UtcNow },
                { "version", "1.0" },
            },
        };

        // Act - Simulate structured logging call
        mockLogger.Object.LogInformation(
            LogEventIds.AgentRequestInitiated,
            "Request initiated: RequestId={RequestId}, Model={ModelId}, Params={Parameters}, Meta={Metadata}",
            complexData.RequestId,
            complexData.ModelId,
            complexData.Parameters,
            complexData.Metadata
        );

        // Assert - Verify structured logging was called correctly
        mockLogger.Verify(
            x =>
                x.Log(
                    LogLevel.Information,
                    LogEventIds.AgentRequestInitiated,
                    It.Is<It.IsAnyType>(
                        (v, t) => v.ToString()!.Contains("test-123") && v.ToString()!.Contains("gpt-4")
                    ),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()
                ),
            Times.Once
        );
    }

    #endregion

    #region Null Safety and Backward Compatibility Tests

    [Fact]
    public void LoggingComponents_WithNullLoggers_DoNotThrow()
    {
        // Arrange & Act & Assert - All should work with null loggers
        var functions = new List<FunctionContract>
        {
            new()
            {
                Name = "null_safe_function",
                Description = "Function for null safety testing",
                Parameters = [],
            },
        };
        var functionMap = new Dictionary<string, Func<string, Task<string>>>
        {
            { "null_safe_function", args => Task.FromResult("null safe result") },
        };

        // Should not throw with null logger
        var middleware = new FunctionCallMiddleware(functions, functionMap, "null-safe-test");
        Assert.NotNull(middleware);
        Assert.Equal("null-safe-test", middleware.Name);
    }

    [Fact]
    public void LoggingComponents_WithNullLoggerFactory_HandleGracefully()
    {
        // Arrange
        ILoggerFactory? nullLoggerFactory = null;

        // Act
        var logger =
            nullLoggerFactory?.CreateLogger<LoggingSystemIntegrationTests>()
            ?? NullLogger<LoggingSystemIntegrationTests>.Instance;

        // Assert
        Assert.NotNull(logger);
        _ = Assert.IsType<NullLogger<LoggingSystemIntegrationTests>>(logger);

        // Should not throw when logging
        logger.LogInformation("Test message with null factory");
        logger.LogError(new Exception("Test"), "Error with null factory");
    }

    #endregion
}
