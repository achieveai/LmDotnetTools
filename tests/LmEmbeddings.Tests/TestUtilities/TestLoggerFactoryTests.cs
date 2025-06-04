using Microsoft.Extensions.Logging;
using System.Diagnostics;
using Xunit;

namespace LmEmbeddings.Tests.TestUtilities;

/// <summary>
/// Tests for TestLoggerFactory shared utility
/// </summary>
public class TestLoggerFactoryTests
{
    [Fact]
    public void CreateLogger_ReturnsValidLogger()
    {
        Debug.WriteLine("Testing CreateLogger basic functionality");

        // Act
        var logger = TestLoggerFactory.CreateLogger<TestLoggerFactoryTests>();

        // Assert
        Assert.NotNull(logger);
        Assert.True(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Debug));
        Debug.WriteLine("✓ CreateLogger returned valid logger instance");
    }

    [Fact]
    public void CreateLogger_WithCustomOutputAction_UsesCustomAction()
    {
        Debug.WriteLine("Testing CreateLogger with custom output action");

        // Arrange
        var capturedMessages = new List<string>();
        var customAction = new Action<string>(message => capturedMessages.Add(message));

        // Act
        var logger = TestLoggerFactory.CreateLogger<TestLoggerFactoryTests>(customAction);
        logger.LogInformation("Test message");

        // Assert
        Assert.NotNull(logger);
        Assert.Single(capturedMessages);
        Assert.Contains("Test message", capturedMessages[0]);
        Assert.Contains("[Information]", capturedMessages[0]);
        Debug.WriteLine($"✓ Custom action captured: {capturedMessages[0]}");
    }

    [Fact]
    public void CreateSilentLogger_DoesNotOutput()
    {
        Debug.WriteLine("Testing CreateSilentLogger functionality");

        // Act
        var logger = TestLoggerFactory.CreateSilentLogger<TestLoggerFactoryTests>();

        // Assert
        Assert.NotNull(logger);
        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.False(logger.IsEnabled(LogLevel.Debug));
        Assert.False(logger.IsEnabled(LogLevel.Error));
        
        // Should not throw when logging
        logger.LogInformation("This should be silent");
        logger.LogError("This should also be silent");
        
        Debug.WriteLine("✓ Silent logger created and doesn't output");
    }

    [Theory]
    [InlineData(LogLevel.Trace)]
    [InlineData(LogLevel.Debug)]
    [InlineData(LogLevel.Information)]
    [InlineData(LogLevel.Warning)]
    [InlineData(LogLevel.Error)]
    [InlineData(LogLevel.Critical)]
    public void Logger_SupportsAllLogLevels(LogLevel logLevel)
    {
        Debug.WriteLine($"Testing log level: {logLevel}");

        // Arrange
        var capturedMessages = new List<string>();
        var logger = TestLoggerFactory.CreateLogger<TestLoggerFactoryTests>(message => capturedMessages.Add(message));

        // Act
        logger.Log(logLevel, "Test message for {LogLevel}", logLevel);

        // Assert
        Assert.Single(capturedMessages);
        Assert.Contains($"[{logLevel}]", capturedMessages[0]);
        Assert.Contains("Test message for", capturedMessages[0]);
        Debug.WriteLine($"✓ Log level {logLevel} captured correctly");
    }

    [Fact]
    public void Logger_HandlesExceptions()
    {
        Debug.WriteLine("Testing exception handling in logger");

        // Arrange
        var capturedMessages = new List<string>();
        var logger = TestLoggerFactory.CreateLogger<TestLoggerFactoryTests>(message => capturedMessages.Add(message));
        var testException = new InvalidOperationException("Test exception");

        // Act
        logger.LogError(testException, "Error occurred during test");

        // Assert
        Assert.Single(capturedMessages);
        Assert.Contains("[Error]", capturedMessages[0]);
        Assert.Contains("Error occurred during test", capturedMessages[0]);
        Assert.Contains("Exception: System.InvalidOperationException: Test exception", capturedMessages[0]);
        Debug.WriteLine($"✓ Exception logged correctly: {capturedMessages[0]}");
    }

    [Fact]
    public void Logger_BeginScope_ReturnsNull()
    {
        Debug.WriteLine("Testing BeginScope functionality");

        // Arrange
        var logger = TestLoggerFactory.CreateLogger<TestLoggerFactoryTests>();

        // Act
        var scope = logger.BeginScope("Test scope");

        // Assert
        Assert.Null(scope);
        Debug.WriteLine("✓ BeginScope returns null as expected");
    }

    [Theory]
    [MemberData(nameof(LoggerTestCases))]
    public void Logger_WithVariousInputs_LogsCorrectly(
        string message,
        LogLevel logLevel,
        string expectedContent,
        string description)
    {
        Debug.WriteLine($"Testing logger with various inputs: {description}");

        // Arrange
        var capturedMessages = new List<string>();
        var logger = TestLoggerFactory.CreateLogger<TestLoggerFactoryTests>(msg => capturedMessages.Add(msg));

        // Act
        logger.Log(logLevel, message);

        // Assert
        Assert.Single(capturedMessages);
        Assert.Contains($"[{logLevel}]", capturedMessages[0]);
        Assert.Contains(expectedContent, capturedMessages[0]);
        Debug.WriteLine($"✓ {description} - Message logged correctly");
    }

    public static IEnumerable<object[]> LoggerTestCases => new List<object[]>
    {
        new object[] { "Simple message", LogLevel.Information, "Simple message", "Simple information message" },
        new object[] { "Debug details", LogLevel.Debug, "Debug details", "Debug level message" },
        new object[] { "Warning alert", LogLevel.Warning, "Warning alert", "Warning level message" },
        new object[] { "Critical error", LogLevel.Critical, "Critical error", "Critical level message" },
        new object[] { "", LogLevel.Information, "", "Empty message" },
        new object[] { "Message with special chars: @#$%^&*()", LogLevel.Information, "special chars", "Message with special characters" }
    };
} 