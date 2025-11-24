using AchieveAi.LmDotnetTools.LmConfig.Agents;
using AchieveAi.LmDotnetTools.LmConfig.Models;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Integration;

/// <summary>
/// Integration tests for the comprehensive logging system across all agents and middleware.
/// Tests complete request flows with logging enabled at various levels.
/// </summary>
public class LoggingIntegrationTests : IDisposable
{
    private readonly TestLogger<UnifiedAgent> _unifiedAgentLogger;
    private readonly TestLogger<FunctionCallMiddleware> _middlewareLogger;
    private readonly TestLogger<OpenClient> _openClientLogger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ServiceProvider _serviceProvider;

    public LoggingIntegrationTests()
    {
        // Create test loggers for testing
        _unifiedAgentLogger = new TestLogger<UnifiedAgent>();
        _middlewareLogger = new TestLogger<FunctionCallMiddleware>();
        _openClientLogger = new TestLogger<OpenClient>();

        // Create a service collection with logging
        var services = new ServiceCollection();
        _ = services.AddLogging(builder =>
        {
            _ = builder.SetMinimumLevel(LogLevel.Trace);
            _ = builder.AddProvider(new TestLoggerProvider());
        });

        // Create logger factory
        var loggerFactory = new TestLoggerFactory();
        loggerFactory.AddLogger(_unifiedAgentLogger);
        loggerFactory.AddLogger(_middlewareLogger);
        loggerFactory.AddLogger(_openClientLogger);
        _loggerFactory = loggerFactory;

        _serviceProvider = services.BuildServiceProvider();
    }

    /* [Fact] // Disabled: Test relies on mocking non-virtual properties which is not supported
    public async Task EndToEndLogging_WithUnifiedAgent_LogsAtAllLevels()
    {
        // Arrange
        var mockModelResolver = new Mock<IModelResolver>();
        var mockAgentFactory = new Mock<IProviderAgentFactory>();
        var mockAgent = new Mock<IAgent>();

        var mockResolution = new Mock<ProviderResolution>();
        mockResolution.SetupGet(x => x.EffectiveProviderName).Returns("openai");
        mockResolution.SetupGet(x => x.EffectiveModelName).Returns("gpt-4");

        mockModelResolver.Setup(x => x.ResolveProviderAsync(It.IsAny<string>(), It.IsAny<ProviderSelectionCriteria>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockResolution.Object);

        mockAgentFactory.Setup(x => x.CreateAgent(It.IsAny<ProviderResolution>()))
            .Returns(mockAgent.Object);

        var responseMessage = new TextMessage { Text = "Hello, world!", Role = Role.Assistant };
        mockAgent.Setup(x => x.GenerateReplyAsync(It.IsAny<IEnumerable<IMessage>>(), It.IsAny<GenerateReplyOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { responseMessage });

        var unifiedAgent = new UnifiedAgent(mockModelResolver.Object, mockAgentFactory.Object, _unifiedAgentLogger);
        var messages = new[] { new TextMessage { Text = "Hello", Role = Role.User } };
        var options = new GenerateReplyOptions { ModelId = "gpt-4" };

        // Act
        var result = await unifiedAgent.GenerateReplyAsync(messages, options);

        // Assert - Verify logging at different levels
        Assert.NotEmpty(result);

        // Verify Info level logging for request initiation
        var initiationLogs = _unifiedAgentLogger.LogEntries
            .Where(log => log.EventId == LmConfigLogEventIds.AgentRequestInitiated && log.LogLevel == LogLevel.Information)
            .ToList();
        Assert.Single(initiationLogs);

        var initiationLog = initiationLogs.First();
        Assert.Contains("LLM request initiated", initiationLog.Message);
        Assert.Contains("gpt-4", initiationLog.Message);
        Assert.Contains("non-streaming", initiationLog.Message);

        // Verify Info level logging for request completion
        var completionLogs = _unifiedAgentLogger.LogEntries
            .Where(log => log.EventId == LmConfigLogEventIds.AgentRequestCompleted && log.LogLevel == LogLevel.Information)
            .ToList();
        Assert.Single(completionLogs);

        var completionLog = completionLogs.First();
        Assert.Contains("LLM request completed", completionLog.Message);
        Assert.Contains("gpt-4", completionLog.Message);
        Assert.Contains("openai", completionLog.Message);

        // Verify Debug level logging for agent caching
        var cacheLogs = _unifiedAgentLogger.LogEntries
            .Where(log => log.EventId == LmConfigLogEventIds.AgentCacheMiss && log.LogLevel == LogLevel.Debug)
            .ToList();
        Assert.Single(cacheLogs);

        var cacheLog = cacheLogs.First();
        Assert.Contains("Agent cache miss", cacheLog.Message);
        Assert.Contains("openai", cacheLog.Message);
        Assert.Contains("gpt-4", cacheLog.Message);
    } */

    [Fact]
    public async Task EndToEndLogging_WithFunctionCallMiddleware_LogsFunctionExecution()
    {
        // Arrange
        var testFunction = new FunctionContract
        {
            Name = "TestFunction",
            Description = "A test function",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "input",
                    ParameterType = JsonSchemaObject.String("Test input parameter"),
                    Description = "Test input",
                },
            ],
        };

        var functionMap = new Dictionary<string, Func<string, Task<string>>>
        {
            ["TestFunction"] = async (args) =>
            {
                await Task.Delay(10); // Simulate some work
                return "Test result";
            },
        };

        var middleware = new FunctionCallMiddleware(
            [testFunction],
            functionMap,
            "TestMiddleware",
            _middlewareLogger
        );

        var mockAgent = new Mock<IAgent>();
        var toolCall = new ToolCall { FunctionName = "TestFunction", FunctionArgs = "{\"input\":\"test\"}" };
        var toolCallMessage = new ToolsCallMessage
        {
            ToolCalls = [toolCall],
            Role = Role.Assistant,
            FromAgent = "test-agent",
        };

        _ = mockAgent
            .Setup(x =>
                x.GenerateReplyAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([toolCallMessage]);

        var context = new MiddlewareContext(
            [
                new TextMessage { Text = "Call test function", Role = Role.User },
            ],
            new GenerateReplyOptions()
        );

        // Act
        var result = await middleware.InvokeAsync(context, mockAgent.Object);

        // Assert - Verify middleware logging
        var logs = _middlewareLogger.LogEntries.ToList();

        // Verify middleware processing started
        var processingStartedLogs = logs.Where(log => log.Message.Contains("Middleware processing started")).ToList();
        _ = Assert.Single(processingStartedLogs);
        Assert.Contains("TestMiddleware", processingStartedLogs.First().Message);

        // Verify function execution logging
        var functionExecutionLogs = logs.Where(log => log.Message.Contains("Function executed")).ToList();
        _ = Assert.Single(functionExecutionLogs);

        var functionLog = functionExecutionLogs.First();
        Assert.Contains("TestFunction", functionLog.Message);
        Assert.Contains("Success=True", functionLog.Message);

        // Verify middleware processing completed
        var processingCompletedLogs = logs.Where(log => log.Message.Contains("Middleware processing completed"))
            .ToList();
        _ = Assert.Single(processingCompletedLogs);

        // Verify the result contains the expected aggregate message
        var resultList = result.ToList();
        _ = Assert.Single(resultList);
        _ = Assert.IsType<ToolsCallAggregateMessage>(resultList.First());
    }

    /* [Fact] // Disabled: Test relies on mocking non-virtual properties which is not supported
    public async Task EndToEndLogging_WithStreamingAgent_LogsStreamingMetrics()
    {
        // Arrange
        var mockModelResolver = new Mock<IModelResolver>();
        var mockAgentFactory = new Mock<IProviderAgentFactory>();
        var mockStreamingAgent = new Mock<IStreamingAgent>();

        var mockResolution = new Mock<ProviderResolution>();
        mockResolution.SetupGet(x => x.EffectiveProviderName).Returns("openai");
        mockResolution.SetupGet(x => x.EffectiveModelName).Returns("gpt-4");

        mockModelResolver.Setup(x => x.ResolveProviderAsync(It.IsAny<string>(), It.IsAny<ProviderSelectionCriteria>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockResolution.Object);

        mockAgentFactory.Setup(x => x.CreateStreamingAgent(It.IsAny<ProviderResolution>()))
            .Returns(mockStreamingAgent.Object);

        // Create a streaming response
        var streamingMessages = new[]
        {
            new TextUpdateMessage { Text = "Hello", Role = Role.Assistant },
            new TextUpdateMessage { Text = " world", Role = Role.Assistant },
            new TextUpdateMessage { Text = "!", Role = Role.Assistant }
        };

        mockStreamingAgent.Setup(x => x.GenerateReplyStreamingAsync(It.IsAny<IEnumerable<IMessage>>(), It.IsAny<GenerateReplyOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(CreateAsyncEnumerable(streamingMessages.Cast<IMessage>())));

        var unifiedAgent = new UnifiedAgent(mockModelResolver.Object, mockAgentFactory.Object, _unifiedAgentLogger);
        var messages = new[] { new TextMessage { Text = "Hello", Role = Role.User } };
        var options = new GenerateReplyOptions { ModelId = "gpt-4" };

        // Act
        var streamingResult = await unifiedAgent.GenerateReplyStreamingAsync(messages, options);
        var resultMessages = new List<IMessage>();
        await foreach (var message in streamingResult)
        {
            resultMessages.Add(message);
        }

        // Assert - Verify streaming logging
        var logs = _unifiedAgentLogger.LogEntries.ToList();

        // Verify streaming request initiation
        var initiationLogs = logs.Where(log => log.EventId == LmConfigLogEventIds.AgentRequestInitiated && log.Message.Contains("streaming")).ToList();
        Assert.Single(initiationLogs);
        Assert.Contains("Type=streaming", initiationLogs.First().Message);

        // Verify streaming request completion
        var completionLogs = logs.Where(log => log.EventId == LmConfigLogEventIds.AgentRequestCompleted && log.Message.Contains("streaming")).ToList();
        Assert.Single(completionLogs);

        // Verify we got the expected streaming messages
        Assert.Equal(3, resultMessages.Count);
        Assert.All(resultMessages, msg => Assert.IsType<TextUpdateMessage>(msg));
    } */

    /* [Fact] // Disabled: Test relies on mocking non-virtual properties which is not supported
    public async Task EndToEndLogging_WithErrorScenario_LogsErrorsCorrectly()
    {
        // Arrange
        var mockModelResolver = new Mock<IModelResolver>();
        var mockAgentFactory = new Mock<IProviderAgentFactory>();
        var mockAgent = new Mock<IAgent>();

        var mockResolution = new Mock<ProviderResolution>();
        mockResolution.SetupGet(x => x.EffectiveProviderName).Returns("openai");
        mockResolution.SetupGet(x => x.EffectiveModelName).Returns("gpt-4");

        mockModelResolver.Setup(x => x.ResolveProviderAsync(It.IsAny<string>(), It.IsAny<ProviderSelectionCriteria>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockResolution.Object);

        mockAgentFactory.Setup(x => x.CreateAgent(It.IsAny<ProviderResolution>()))
            .Returns(mockAgent.Object);

        // Setup agent to throw an exception
        var expectedException = new InvalidOperationException("Test error");
        mockAgent.Setup(x => x.GenerateReplyAsync(It.IsAny<IEnumerable<IMessage>>(), It.IsAny<GenerateReplyOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(expectedException);

        var unifiedAgent = new UnifiedAgent(mockModelResolver.Object, mockAgentFactory.Object, _unifiedAgentLogger);
        var messages = new[] { new TextMessage { Text = "Hello", Role = Role.User } };
        var options = new GenerateReplyOptions { ModelId = "gpt-4" };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => unifiedAgent.GenerateReplyAsync(messages, options));
        Assert.Equal("Test error", exception.Message);

        // Verify error logging
        var errorLogs = _unifiedAgentLogger.LogEntries
            .Where(log => log.EventId == LmConfigLogEventIds.AgentRequestFailed && log.LogLevel == LogLevel.Error)
            .ToList();
        Assert.Single(errorLogs);

        var errorLog = errorLogs.First();
        Assert.Contains("LLM request failed", errorLog.Message);
        Assert.Contains("gpt-4", errorLog.Message);
        Assert.Contains("openai", errorLog.Message);
        Assert.NotNull(errorLog.Exception);
        Assert.Equal("Test error", errorLog.Exception.Message);
    } */

    /* [Fact] // Disabled: Test relies on test-specific logger factory that doesn't match actual implementation
    public void LoggerFactory_Integration_CreatesTypedLoggers()
    {
        // Arrange & Act
        var unifiedAgentLogger = _loggerFactory.CreateLogger<UnifiedAgent>();
        var middlewareLogger = _loggerFactory.CreateLogger<FunctionCallMiddleware>();
        var openClientLogger = _loggerFactory.CreateLogger<OpenClient>();

        // Assert
        Assert.NotNull(unifiedAgentLogger);
        Assert.NotNull(middlewareLogger);
        Assert.NotNull(openClientLogger);

        // Verify logger categories
        Assert.IsType<TestLogger<UnifiedAgent>>(unifiedAgentLogger);
        Assert.IsType<TestLogger<FunctionCallMiddleware>>(middlewareLogger);
        Assert.IsType<TestLogger<OpenClient>>(openClientLogger);
    } */

    [Fact]
    public async Task DependencyInjection_Integration_LoggerFactoryPropagation()
    {
        // Arrange
        var services = new ServiceCollection();
        _ = services.AddLogging(builder =>
        {
            _ = builder.SetMinimumLevel(LogLevel.Debug);
            _ = builder.AddProvider(new TestLoggerProvider());
        });

        // Register components with logger factory
        _ = services.AddSingleton<ILoggerFactory>(provider => _loggerFactory);
        _ = services.AddTransient<UnifiedAgent>(provider => new UnifiedAgent(
            Mock.Of<IModelResolver>(),
            Mock.Of<IProviderAgentFactory>(),
            provider.GetService<ILogger<UnifiedAgent>>()
        ));

        var serviceProvider = services.BuildServiceProvider();

        // Act
        var unifiedAgent = serviceProvider.GetRequiredService<UnifiedAgent>();

        // Assert
        Assert.NotNull(unifiedAgent);

        // Verify that the logger was injected by checking if logging works
        var mockModelResolver = new Mock<IModelResolver>();
        _ = mockModelResolver
            .Setup(x =>
                x.ResolveProviderAsync(
                    It.IsAny<string>(),
                    It.IsAny<ProviderSelectionCriteria>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(new InvalidOperationException("No provider found"));

        // This should trigger error logging
        var messages = new[]
        {
            new TextMessage { Text = "Hello", Role = Role.User },
        };
        var options = new GenerateReplyOptions { ModelId = "invalid-model" };

        try
        {
            // We expect this to fail, but we want to verify logging occurs
            _ = await unifiedAgent.GenerateReplyAsync(messages, options);
        }
        catch
        {
            // Expected to fail
        }

        // Verify that logging occurred (this confirms DI integration works)
        var logs = _unifiedAgentLogger.LogEntries.ToList();
        Assert.NotEmpty(logs);
    }

    /* [Fact] // Disabled: Test relies on test-specific logger that doesn't match actual implementation
    public void LogOutput_Format_MeetsRequirements()
    {
        // Arrange
        var logger = _loggerFactory.CreateLogger<UnifiedAgent>();

        // Act - Log a structured message
        logger.LogInformation(LmConfigLogEventIds.AgentRequestInitiated,
            "LLM request initiated: Model={ModelId}, MessageCount={MessageCount}, Type={RequestType}",
            "gpt-4", 2, "non-streaming");

        // Assert - Verify structured logging format
        var logs = ((TestLogger<UnifiedAgent>)logger).LogEntries.ToList();
        Assert.Single(logs);

        var log = logs.First();
        Assert.Equal(LmConfigLogEventIds.AgentRequestInitiated, log.EventId);
        Assert.Equal(LogLevel.Information, log.LogLevel);
        Assert.Contains("LLM request initiated", log.Message);
        Assert.Contains("Model=gpt-4", log.Message);
        Assert.Contains("MessageCount=2", log.Message);
        Assert.Contains("Type=non-streaming", log.Message);

        // Verify structured parameters are available
        Assert.NotNull(log.State);
        var state = log.State as IReadOnlyList<KeyValuePair<string, object>>;
        Assert.NotNull(state);

        var modelParam = state.FirstOrDefault(kvp => kvp.Key == "ModelId");
        Assert.Equal("gpt-4", modelParam.Value);

        var messageCountParam = state.FirstOrDefault(kvp => kvp.Key == "MessageCount");
        Assert.Equal(2, messageCountParam.Value);

        var typeParam = state.FirstOrDefault(kvp => kvp.Key == "RequestType");
        Assert.Equal("non-streaming", typeParam.Value);
    } */

    /* [Fact] // Disabled: Test relies on mocking non-virtual properties which is not supported
    public async Task PerformanceLogging_MinimalOverhead_WhenDisabled()
    {
        // Arrange
        var disabledLogger = new Mock<ILogger<UnifiedAgent>>();
        disabledLogger.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(false);

        var mockModelResolver = new Mock<IModelResolver>();
        var mockAgentFactory = new Mock<IProviderAgentFactory>();
        var mockAgent = new Mock<IAgent>();

        var mockResolution = new Mock<ProviderResolution>();
        mockResolution.SetupGet(x => x.EffectiveProviderName).Returns("openai");
        mockResolution.SetupGet(x => x.EffectiveModelName).Returns("gpt-4");

        mockModelResolver.Setup(x => x.ResolveProviderAsync(It.IsAny<string>(), It.IsAny<ProviderSelectionCriteria>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockResolution.Object);

        mockAgentFactory.Setup(x => x.CreateAgent(It.IsAny<ProviderResolution>()))
            .Returns(mockAgent.Object);

        var responseMessage = new TextMessage { Text = "Hello, world!", Role = Role.Assistant };
        mockAgent.Setup(x => x.GenerateReplyAsync(It.IsAny<IEnumerable<IMessage>>(), It.IsAny<GenerateReplyOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { responseMessage });

        var unifiedAgent = new UnifiedAgent(mockModelResolver.Object, mockAgentFactory.Object, disabledLogger.Object);
        var messages = new[] { new TextMessage { Text = "Hello", Role = Role.User } };
        var options = new GenerateReplyOptions { ModelId = "gpt-4" };

        // Act
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await unifiedAgent.GenerateReplyAsync(messages, options);
        stopwatch.Stop();

        // Assert
        Assert.NotEmpty(result);

        // Verify that no logging methods were called when disabled
        disabledLogger.Verify(x => x.Log(
            It.IsAny<LogLevel>(),
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception, string>>()), Times.Never);

        // Performance should be reasonable (this is a basic sanity check)
        Assert.True(stopwatch.ElapsedMilliseconds < 1000, "Operation took too long, suggesting logging overhead");
    } */

    private static async IAsyncEnumerable<IMessage> CreateAsyncEnumerable(IEnumerable<IMessage> messages)
    {
        foreach (var message in messages)
        {
            yield return message;
            await Task.Delay(1); // Small delay to simulate streaming
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _serviceProvider?.Dispose();
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Helper classes for testing logging functionality
/// </summary>
public class TestLoggerFactory : ILoggerFactory
{
    private readonly Dictionary<string, ILogger> _loggers = [];

    public void AddLogger<T>(TestLogger<T> logger)
    {
        _loggers[typeof(T).FullName!] = logger;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.TryGetValue(categoryName, out var logger) ? logger : new TestLogger();
    }

    public void AddProvider(ILoggerProvider provider) { }

    public void Dispose() { }
}

public class TestLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName)
    {
        return new TestLogger();
    }

    public void Dispose() { }
}

public class TestLogger : ILogger
{
    public List<LogEntry> LogEntries { get; } = [];

    IDisposable ILogger.BeginScope<TState>(TState state)
    {
        return new TestScope();
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    )
    {
        var message = formatter(state, exception);
        LogEntries.Add(
            new LogEntry
            {
                LogLevel = logLevel,
                EventId = eventId,
                State = state,
                Exception = exception,
                Message = message,
            }
        );
    }
}

public class TestLogger<T> : TestLogger, ILogger<T> { }

public class LogEntry
{
    public LogLevel LogLevel { get; set; }
    public EventId EventId { get; set; }
    public object? State { get; set; }
    public Exception? Exception { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class TestScope : IDisposable
{
    public void Dispose() { }
}
