using AchieveAi.LmDotnetTools.LmCore.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Logging;

/// <summary>
/// Simple tests for logging functionality to verify that logging works correctly
/// and handles null loggers gracefully.
/// </summary>
public class SimpleLoggingTests
{
    #region Null Logger Handling Tests

    [Fact]
    public void NullLogger_ShouldNotThrow_WhenLogging()
    {
        // Arrange
        ILogger<SimpleLoggingTests>? nullLogger = null;
        var logger = nullLogger ?? NullLogger<SimpleLoggingTests>.Instance;

        // Act & Assert - Should not throw
        logger.LogInformation("Test message");
        logger.LogDebug("Debug message");
        logger.LogError("Error message");
        logger.LogWarning("Warning message");

        Assert.NotNull(logger);
    }

    [Fact]
    public void NullLoggerInstance_ShouldNotThrow_WhenLogging()
    {
        // Arrange
        var logger = NullLogger<SimpleLoggingTests>.Instance;

        // Act & Assert - Should not throw
        logger.LogInformation("Test message");
        logger.LogDebug("Debug message");
        logger.LogError("Error message");
        logger.LogWarning("Warning message");

        Assert.NotNull(logger);
    }

    #endregion

    #region LogEventIds Tests

    [Fact]
    public void LogEventIds_ShouldHaveCorrectValues()
    {
        // Assert - Verify that LogEventIds are properly defined
        Assert.Equal(1001, LogEventIds.AgentRequestInitiated.Id);
        Assert.Equal("AgentRequestInitiated", LogEventIds.AgentRequestInitiated.Name);

        Assert.Equal(1002, LogEventIds.AgentRequestCompleted.Id);
        Assert.Equal("AgentRequestCompleted", LogEventIds.AgentRequestCompleted.Name);

        Assert.Equal(1003, LogEventIds.AgentRequestFailed.Id);
        Assert.Equal("AgentRequestFailed", LogEventIds.AgentRequestFailed.Name);

        Assert.Equal(2001, LogEventIds.MiddlewareProcessing.Id);
        Assert.Equal("MiddlewareProcessing", LogEventIds.MiddlewareProcessing.Name);

        Assert.Equal(3001, LogEventIds.ProviderResolved.Id);
        Assert.Equal("ProviderResolved", LogEventIds.ProviderResolved.Name);
    }

    #endregion

    #region Logger State Tests

    [Fact]
    public void Logger_IsEnabled_ShouldWork()
    {
        // Arrange
        var mockLogger = new Mock<ILogger<SimpleLoggingTests>>();
        mockLogger.Setup(x => x.IsEnabled(LogLevel.Information)).Returns(true);
        mockLogger.Setup(x => x.IsEnabled(LogLevel.Debug)).Returns(false);

        // Act & Assert
        Assert.True(mockLogger.Object.IsEnabled(LogLevel.Information));
        Assert.False(mockLogger.Object.IsEnabled(LogLevel.Debug));
    }

    [Fact]
    public void NullLogger_IsEnabled_ShouldReturnFalse()
    {
        // Arrange
        var logger = NullLogger<SimpleLoggingTests>.Instance;

        // Act & Assert
        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.False(logger.IsEnabled(LogLevel.Debug));
        Assert.False(logger.IsEnabled(LogLevel.Warning));
        Assert.False(logger.IsEnabled(LogLevel.Error));
    }

    #endregion

    #region Structured Logging Tests

    [Fact]
    public void StructuredLogging_WithParameters_ShouldWork()
    {
        // Arrange
        var mockLogger = new Mock<ILogger<SimpleLoggingTests>>();
        var testMessage = "Test message with {Parameter1} and {Parameter2}";
        var param1 = "value1";
        var param2 = 42;

        // Act
        mockLogger.Object.LogInformation(testMessage, param1, param2);

        // Assert - Verify that Log was called with correct parameters
        mockLogger.Verify(
            x =>
                x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("value1") && v.ToString()!.Contains("42")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()
                ),
            Times.Once
        );
    }

    [Fact]
    public void StructuredLogging_WithEventId_ShouldWork()
    {
        // Arrange
        var mockLogger = new Mock<ILogger<SimpleLoggingTests>>();
        var eventId = LogEventIds.AgentRequestInitiated;
        var testMessage = "Agent request initiated: {ModelId}";
        var modelId = "test-model";

        // Act
        mockLogger.Object.LogInformation(eventId, testMessage, modelId);

        // Assert - Verify that Log was called with correct EventId
        mockLogger.Verify(
            x =>
                x.Log(
                    LogLevel.Information,
                    eventId,
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("test-model")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()
                ),
            Times.Once
        );
    }

    #endregion

    #region Performance Tests

    [Fact]
    public void NullLogger_Performance_ShouldBeMinimal()
    {
        // Arrange
        var logger = NullLogger<SimpleLoggingTests>.Instance;
        const int iterations = 1000;

        // Act - Measure performance
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            logger.LogInformation("Test message {Iteration}", i);
        }
        stopwatch.Stop();

        // Assert - Should complete quickly
        var timePerIteration = (double)stopwatch.ElapsedMilliseconds / iterations;
        Assert.True(
            timePerIteration < 1.0, // Less than 1ms per iteration
            $"NullLogger performance is too slow: {timePerIteration:F4}ms per iteration"
        );
    }

    [Fact]
    public void DisabledLogger_Performance_ShouldBeMinimal()
    {
        // Arrange
        var mockLogger = new Mock<ILogger<SimpleLoggingTests>>();
        mockLogger.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(false);
        const int iterations = 1000;

        // Act - Measure performance
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            mockLogger.Object.LogInformation("Test message {Iteration}", i);
        }
        stopwatch.Stop();

        // Assert - Should complete quickly
        var timePerIteration = (double)stopwatch.ElapsedMilliseconds / iterations;
        Assert.True(
            timePerIteration < 2.0, // Less than 2ms per iteration
            $"Disabled logger performance is too slow: {timePerIteration:F4}ms per iteration"
        );
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public void Logger_WithException_ShouldWork()
    {
        // Arrange
        var mockLogger = new Mock<ILogger<SimpleLoggingTests>>();
        var testException = new InvalidOperationException("Test exception");
        var testMessage = "An error occurred: {ErrorMessage}";

        // Act
        mockLogger.Object.LogError(testException, testMessage, testException.Message);

        // Assert - Verify that Log was called with exception
        mockLogger.Verify(
            x =>
                x.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Test exception")),
                    testException,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()
                ),
            Times.Once
        );
    }

    [Fact]
    public void NullLogger_WithException_ShouldNotThrow()
    {
        // Arrange
        var logger = NullLogger<SimpleLoggingTests>.Instance;
        var testException = new InvalidOperationException("Test exception");

        // Act & Assert - Should not throw
        logger.LogError(testException, "An error occurred");
        logger.LogWarning(testException, "A warning occurred");
        logger.LogInformation(testException, "Information with exception");

        Assert.NotNull(logger);
    }

    #endregion

    #region Logger Factory Tests

    [Fact]
    public void LoggerFactory_CreateLogger_ShouldWork()
    {
        // Arrange
        var mockLoggerFactory = new Mock<ILoggerFactory>();
        var mockLogger = new Mock<ILogger>();

        mockLoggerFactory.Setup(x => x.CreateLogger(typeof(SimpleLoggingTests).FullName!)).Returns(mockLogger.Object);

        // Act
        var logger = mockLoggerFactory.Object.CreateLogger(typeof(SimpleLoggingTests).FullName!);

        // Assert
        Assert.NotNull(logger);
        Assert.Same(mockLogger.Object, logger);
        mockLoggerFactory.Verify(x => x.CreateLogger(typeof(SimpleLoggingTests).FullName!), Times.Once);
    }

    [Fact]
    public void NullLoggerFactory_ShouldHandleGracefully()
    {
        // Arrange
        ILoggerFactory? nullLoggerFactory = null;

        // Act
        var logger = nullLoggerFactory?.CreateLogger<SimpleLoggingTests>() ?? NullLogger<SimpleLoggingTests>.Instance;

        // Assert
        Assert.NotNull(logger);
        Assert.IsType<NullLogger<SimpleLoggingTests>>(logger);
    }

    #endregion
}
