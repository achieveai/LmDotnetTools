using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Performance;

namespace AchieveAi.LmDotnetTools.LmTestUtils;

/// <summary>
///     Utilities for testing performance tracking functionality
///     Provides helpers for validating performance metrics across all providers
/// </summary>
public static class PerformanceTestHelpers
{
    /// <summary>
    ///     Creates a test performance tracker for testing
    /// </summary>
    /// <returns>IPerformanceTracker instance for testing</returns>
    public static IPerformanceTracker CreateTestPerformanceTracker()
    {
        return new PerformanceTracker();
    }

    /// <summary>
    ///     Creates a test RequestMetrics instance
    /// </summary>
    /// <param name="providerName">Provider name</param>
    /// <param name="model">Model name</param>
    /// <param name="operation">Operation type</param>
    /// <returns>RequestMetrics instance for testing</returns>
    public static RequestMetrics CreateTestRequestMetrics(
        string providerName = "TestProvider",
        string model = "test-model",
        string operation = "TestOperation"
    )
    {
        return RequestMetrics.StartNew(providerName, model, operation);
    }

    /// <summary>
    ///     Creates a completed RequestMetrics instance with test data
    /// </summary>
    /// <param name="providerName">Provider name</param>
    /// <param name="model">Model name</param>
    /// <param name="operation">Operation type</param>
    /// <param name="statusCode">HTTP status code</param>
    /// <param name="promptTokens">Number of prompt tokens</param>
    /// <param name="completionTokens">Number of completion tokens</param>
    /// <returns>Completed RequestMetrics instance</returns>
    public static RequestMetrics CreateCompletedRequestMetrics(
        string providerName = "TestProvider",
        string model = "test-model",
        string operation = "TestOperation",
        int statusCode = 200,
        int promptTokens = 10,
        int completionTokens = 20
    )
    {
        var metrics = CreateTestRequestMetrics(providerName, model, operation);

        var usage = new Usage
        {
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            TotalTokens = promptTokens + completionTokens,
        };

        return metrics.Complete(statusCode, usage);
    }

    /// <summary>
    ///     Creates a failed RequestMetrics instance with test data
    /// </summary>
    /// <param name="providerName">Provider name</param>
    /// <param name="model">Model name</param>
    /// <param name="operation">Operation type</param>
    /// <param name="statusCode">HTTP status code</param>
    /// <param name="errorMessage">Error message</param>
    /// <param name="exceptionType">Exception type</param>
    /// <returns>Failed RequestMetrics instance</returns>
    public static RequestMetrics CreateFailedRequestMetrics(
        string providerName = "TestProvider",
        string model = "test-model",
        string operation = "TestOperation",
        int statusCode = 500,
        string errorMessage = "Test error",
        string exceptionType = "TestException"
    )
    {
        var metrics = CreateTestRequestMetrics(providerName, model, operation);
        return metrics.Complete(statusCode, errorMessage: errorMessage, exceptionType: exceptionType);
    }

    /// <summary>
    ///     Validates that RequestMetrics contains expected values
    /// </summary>
    /// <param name="metrics">RequestMetrics to validate</param>
    /// <param name="expectedProvider">Expected provider name</param>
    /// <param name="expectedModel">Expected model name</param>
    /// <param name="expectedOperation">Expected operation type</param>
    /// <param name="expectedStatusCode">Expected status code</param>
    /// <param name="expectedSuccess">Expected success status</param>
    /// <returns>True if all validations pass</returns>
    public static bool ValidateRequestMetrics(
        RequestMetrics metrics,
        string expectedProvider,
        string expectedModel,
        string expectedOperation,
        int? expectedStatusCode = null,
        bool? expectedSuccess = null
    )
    {
        if (metrics.Provider != expectedProvider)
        {
            return false;
        }

        if (metrics.Model != expectedModel)
        {
            return false;
        }

        if (metrics.Operation != expectedOperation)
        {
            return false;
        }

        if (expectedStatusCode.HasValue && metrics.StatusCode != expectedStatusCode.Value)
        {
            return false;
        }

        if (expectedSuccess.HasValue && metrics.IsSuccess != expectedSuccess.Value)
        {
            return false;
        }

        // Validate timing
        return metrics.StartTime != default && metrics.EndTime != default && metrics.Duration > TimeSpan.Zero;
    }

    /// <summary>
    ///     Validates that Usage contains expected token counts
    /// </summary>
    /// <param name="usage">Usage to validate</param>
    /// <param name="expectedPromptTokens">Expected prompt tokens</param>
    /// <param name="expectedCompletionTokens">Expected completion tokens</param>
    /// <param name="expectedTotalTokens">Expected total tokens</param>
    /// <returns>True if all validations pass</returns>
    public static bool ValidateUsage(
        Usage? usage,
        int expectedPromptTokens,
        int expectedCompletionTokens,
        int expectedTotalTokens
    )
    {
        return usage != null
            && usage.PromptTokens == expectedPromptTokens
            && usage.CompletionTokens == expectedCompletionTokens
            && usage.TotalTokens == expectedTotalTokens;
    }

    /// <summary>
    ///     Creates test data for performance tracking scenarios
    /// </summary>
    /// <returns>Test data for performance tracking</returns>
    public static IEnumerable<object[]> GetPerformanceTestCases()
    {
        return
        [
            ["OpenAI", "gpt-4", "ChatCompletion", 200, 10, 20, true, "Successful OpenAI request"],
            ["Anthropic", "claude-3-sonnet", "ChatCompletion", 200, 15, 25, true, "Successful Anthropic request"],
            ["OpenAI", "gpt-4", "StreamingChatCompletion", 200, 5, 15, true, "Successful streaming request"],
            ["Anthropic", "claude-3-sonnet", "ChatCompletion", 400, 0, 0, false, "Failed request with bad request"],
            ["OpenAI", "gpt-4", "ChatCompletion", 500, 0, 0, false, "Failed request with server error"],
        ];
    }

    /// <summary>
    ///     Creates test data for retry scenarios with performance tracking
    /// </summary>
    /// <returns>Test data for retry performance tracking</returns>
    public static IEnumerable<object[]> GetRetryPerformanceTestCases()
    {
        return
        [
            [0, TimeSpan.FromMilliseconds(100), "No retries - fast response"],
            [1, TimeSpan.FromMilliseconds(300), "1 retry - medium response time"],
            [2, TimeSpan.FromMilliseconds(700), "2 retries - slower response time"],
            [3, TimeSpan.FromSeconds(1.5), "3 retries - slow response time"],
        ];
    }

    /// <summary>
    ///     Simulates a delay for testing timing accuracy
    /// </summary>
    /// <param name="delay">Delay duration</param>
    /// <returns>Task representing the delay</returns>
    public static async Task SimulateDelay(TimeSpan delay)
    {
        await Task.Delay(delay);
    }

    /// <summary>
    ///     Measures the execution time of an operation
    /// </summary>
    /// <param name="operation">Operation to measure</param>
    /// <returns>Tuple containing the result and execution time</returns>
    public static async Task<(T result, TimeSpan duration)> MeasureExecutionTime<T>(Func<Task<T>> operation)
    {
        var startTime = DateTime.UtcNow;
        var result = await operation();
        var endTime = DateTime.UtcNow;
        var duration = endTime - startTime;

        return (result, duration);
    }

    /// <summary>
    ///     Validates that a duration is within expected bounds
    /// </summary>
    /// <param name="actualDuration">Actual measured duration</param>
    /// <param name="expectedDuration">Expected duration</param>
    /// <param name="tolerance">Tolerance for timing variations</param>
    /// <returns>True if duration is within bounds</returns>
    public static bool ValidateDuration(TimeSpan actualDuration, TimeSpan expectedDuration, TimeSpan tolerance)
    {
        var minDuration = expectedDuration - tolerance;
        var maxDuration = expectedDuration + tolerance;

        return actualDuration >= minDuration && actualDuration <= maxDuration;
    }
}
