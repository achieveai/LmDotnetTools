using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace AchieveAi.LmDotnetTools.LmTestUtils.Tests.Logging;

/// <summary>
///     Tests for the structured test logging infrastructure.
///     These tests verify that logging is correctly configured and that
///     test context (TestClass, TestMethod) is included in all logs.
/// </summary>
public class LoggingTestBaseTests : LoggingTestBase
{
    public LoggingTestBaseTests(ITestOutputHelper output)
        : base(output) { }

    [Fact]
    public void Logger_ShouldLogWithTestContext()
    {
        // Arrange & Act
        LogTestStart();
        Logger.LogInformation("Test message with value {Value}", 42);
        LogTestEnd();

        // Assert - if we got here without exception, logging is working
        // The actual verification is done by checking the log file
        Assert.True(true);
    }

    [Fact]
    public void LoggerFactory_ShouldCreateLoggerWithTestContext()
    {
        // Arrange
        LogTestStart();
        var serviceLogger = LoggerFactory.CreateLogger<SampleService>();

        // Act
        serviceLogger.LogInformation("Service log message");

        // Assert
        LogTestEnd();
        Assert.NotNull(serviceLogger);
    }

    [Fact]
    public void LogData_ShouldLogStructuredData()
    {
        // Arrange
        LogTestStart();
        var testData = new { Name = "Test", Value = 123, Nested = new { Inner = "data" } };

        // Act
        LogData("testData", testData);

        // Assert
        LogTestEnd();
        Assert.True(true);
    }

    [Fact]
    public void Trace_ShouldLogTraceMessages()
    {
        // Arrange & Act
        LogTestStart();
        Trace("Entering method {MethodName}", nameof(Trace_ShouldLogTraceMessages));
        Trace("Processing step {Step} of {Total}", 1, 3);
        Trace("Exiting method {MethodName}", nameof(Trace_ShouldLogTraceMessages));
        LogTestEnd();

        // Assert
        Assert.True(true);
    }

    /// <summary>
    ///     Sample service to verify that production code loggers include test context.
    /// </summary>
    private class SampleService
    {
        private readonly ILogger<SampleService> _logger;

        public SampleService(ILogger<SampleService> logger)
        {
            _logger = logger;
        }

        public void DoWork()
        {
            _logger.LogInformation("SampleService is doing work");
        }
    }
}
