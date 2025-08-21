using AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
using AchieveAi.LmDotnetTools.AnthropicProvider.Logging;
using AchieveAi.LmDotnetTools.AnthropicProvider.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnthropicProvider.Tests.Agents;

public class AnthropicAgentLoggingTests
{
    private class TestAnthropicClient : IAnthropicClient
    {
        public bool ThrowOnDispose { get; set; }

        public Task<AnthropicResponse> CreateChatCompletionsAsync(AnthropicRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<IAsyncEnumerable<AnthropicStreamEvent>> StreamingChatCompletionsAsync(AnthropicRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public void Dispose()
        {
            if (ThrowOnDispose)
                throw new InvalidOperationException("Test disposal error");
        }
    }

    private class TestLogger : ILogger<AnthropicAgent>
    {
        public List<LogEntry> LogEntries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            LogEntries.Add(new LogEntry
            {
                Level = logLevel,
                EventId = eventId,
                Message = formatter(state, exception),
                Exception = exception
            });
        }
    }

    private class LogEntry
    {
        public LogLevel Level { get; set; }
        public EventId EventId { get; set; }
        public string Message { get; set; } = string.Empty;
        public Exception? Exception { get; set; }
    }

    [Fact]
    public void Constructor_WithLogger_SetsLoggerCorrectly()
    {
        // Arrange
        var client = new TestAnthropicClient();
        var logger = new TestLogger();

        // Act
        var agent = new AnthropicAgent("test-agent", client, logger);

        // Assert
        Assert.Equal("test-agent", agent.Name);
    }

    [Fact]
    public void Constructor_WithoutLogger_UsesNullLogger()
    {
        // Arrange
        var client = new TestAnthropicClient();

        // Act & Assert - Should not throw
        var agent = new AnthropicAgent("test-agent", client);
        Assert.Equal("test-agent", agent.Name);
    }

    [Fact]
    public void Dispose_WithLoggingClient_LogsDisposalErrors()
    {
        // Arrange
        var client = new TestAnthropicClient { ThrowOnDispose = true };
        var logger = new TestLogger();
        var agent = new AnthropicAgent("test-agent", client, logger);

        // Act
        agent.Dispose();

        // Assert
        var errorLog = logger.LogEntries.FirstOrDefault(x => x.Level == LogLevel.Error);
        Assert.NotNull(errorLog);
        Assert.Equal(LogEventIds.ClientDisposalError.Id, errorLog.EventId.Id);
        Assert.Contains("Error disposing client", errorLog.Message);
        Assert.Contains("test-agent", errorLog.Message);
    }
}