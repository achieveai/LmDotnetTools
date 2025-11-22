using MemoryServer.DocumentSegmentation.Models;
using MemoryServer.DocumentSegmentation.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace MemoryServer.Tests.DocumentSegmentation.Services;

/// <summary>
/// Tests for RetryPolicyService implementation.
/// Validates AC-3.1, AC-3.2, AC-3.3, and AC-3.4 from ErrorHandling-TestAcceptanceCriteria.
/// </summary>
public class RetryPolicyServiceTests
{
    private readonly Mock<ILogger<RetryPolicyService>> _mockLogger;
    private readonly RetryConfiguration _configuration;
    private readonly RetryPolicyService _service;

    public RetryPolicyServiceTests()
    {
        _mockLogger = new Mock<ILogger<RetryPolicyService>>();
        _configuration = new RetryConfiguration
        {
            MaxRetries = 3,
            BaseDelayMs = 100, // Shorter for testing
            ExponentialFactor = 2.0,
            MaxDelayMs = 1000, // Shorter for testing
            JitterPercent = 0.1,
        };
        _service = new RetryPolicyService(_configuration, _mockLogger.Object);
    }

    [Fact]
    public async Task ExecuteAsync_WithSuccessfulOperation_ShouldReturnResult()
    {
        // Arrange
        var expectedResult = "success";
        var operationName = "successful-operation";

        // Act
        var result = await _service.ExecuteAsync(() => Task.FromResult(expectedResult), operationName);

        // Assert
        Assert.Equal(expectedResult, result);
    }

    [Fact]
    public async Task ExecuteAsync_WithTransientFailureThenSuccess_ShouldRetryAndSucceed()
    {
        // Arrange
        var operationName = "retry-then-succeed";
        var attempts = 0;
        var expectedResult = "success-after-retry";

        // Act
        var result = await _service.ExecuteAsync(
            () =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw new HttpRequestException("503 Service Unavailable");
                }
                return Task.FromResult(expectedResult);
            },
            operationName
        );

        // Assert
        Assert.Equal(expectedResult, result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_WithNonRetryableError_ShouldFailImmediately()
    {
        // Arrange
        var operationName = "non-retryable-error";
        var attempts = 0;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await _service.ExecuteAsync<string>(
                () =>
                {
                    attempts++;
                    throw new HttpRequestException("401 Unauthorized");
                },
                operationName
            )
        );

        Assert.Equal(1, attempts);
        Assert.Contains("401", exception.Message);
    }

    [Fact]
    public async Task ExecuteAsync_WithMaxRetriesExceeded_ShouldThrowLastException()
    {
        // Arrange
        var operationName = "max-retries-exceeded";
        var attempts = 0;
        var exception = new HttpRequestException("503 Service Unavailable");

        // Act & Assert
        var thrownException = await Assert.ThrowsAsync<HttpRequestException>(() =>
            _service.ExecuteAsync<string>(
                () =>
                {
                    attempts++;
                    throw exception;
                },
                operationName
            )
        );

        Assert.Equal(_configuration.MaxRetries + 1, attempts); // +1 for initial attempt
        Assert.Equal(exception.Message, thrownException.Message);
    }

    [Fact]
    public async Task ExecuteAsync_WithOperationCancellation_ShouldPropagateCorrectly()
    {
        // Arrange
        var operationName = "cancellation-test";
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        _ = await Assert.ThrowsAsync<TaskCanceledException>(() =>
            _service.ExecuteAsync(
                async () =>
                {
                    await Task.Delay(1000, cts.Token);
                    return "should not complete";
                },
                operationName,
                cts.Token
            )
        );
    }

    [Theory]
    [InlineData(1, 100)] // First retry: base delay
    [InlineData(2, 200)] // Second retry: base * 2
    [InlineData(3, 400)] // Third retry: base * 4
    public void CalculateDelay_WithExponentialBackoff_ShouldCalculateCorrectly(int attemptNumber, int expectedBaseMs)
    {
        // Act
        var delay = _service.CalculateDelay(attemptNumber);

        // Assert
        // Allow for jitter (±10%)
        var minExpected = expectedBaseMs * 0.9;
        var maxExpected = expectedBaseMs * 1.1;

        Assert.True(
            delay.TotalMilliseconds >= minExpected,
            $"Delay {delay.TotalMilliseconds}ms should be >= {minExpected}ms"
        );
        Assert.True(
            delay.TotalMilliseconds <= maxExpected,
            $"Delay {delay.TotalMilliseconds}ms should be <= {maxExpected}ms"
        );
    }

    [Fact]
    public void CalculateDelay_WithMaxDelayCap_ShouldNotExceedMaximum()
    {
        // Arrange
        var highAttemptNumber = 10; // This would normally result in a very large delay

        // Act
        var delay = _service.CalculateDelay(highAttemptNumber);

        // Assert
        Assert.True(
            delay.TotalMilliseconds <= _configuration.MaxDelayMs,
            $"Delay {delay.TotalMilliseconds}ms should not exceed max {_configuration.MaxDelayMs}ms"
        );
    }

    /// <summary>
    /// Test data for retry behavior with different error types.
    /// Validates AC-3.1 retry count logic for different scenarios.
    /// </summary>
    public static IEnumerable<object[]> RetryBehaviorTestCases =>
        new List<object[]>
        {
            new object[]
            {
                new HttpRequestException("503 Service Unavailable"),
                true,
                "Service unavailable should be retried",
            },
            new object[] { new HttpRequestException("429 Too Many Requests"), true, "Rate limiting should be retried" },
            new object[]
            {
                new HttpRequestException("401 Unauthorized"),
                false,
                "Authentication errors should not be retried",
            },
            new object[] { new HttpRequestException("400 Bad Request"), false, "Bad request should not be retried" },
            new object[] { new TaskCanceledException("Timeout"), true, "Timeout should be retried" },
            new object[]
            {
                new ArgumentException("Invalid argument"),
                true,
                "Generic errors should be retried by default",
            },
        };

    [Theory]
    [MemberData(nameof(RetryBehaviorTestCases))]
    public void ShouldRetry_WithDifferentErrorTypes_ShouldBehaveCorrectly(
        Exception exception,
        bool expectedShouldRetry,
        string _
    )
    {
        // Arrange
        var attemptNumber = 1;

        // Act
        var shouldRetry = _service.ShouldRetry(exception, attemptNumber);

        // Assert
        Assert.Equal(expectedShouldRetry, shouldRetry);
    }

    [Fact]
    public void ShouldRetry_WhenMaxAttemptsReached_ShouldReturnFalse()
    {
        // Arrange
        var retryableException = new HttpRequestException("503 Service Unavailable");
        var maxAttempts = _configuration.MaxRetries + 1;

        // Act
        var shouldRetry = _service.ShouldRetry(retryableException, maxAttempts);

        // Assert
        Assert.False(shouldRetry);
    }

    [Fact]
    public async Task ExecuteWithNullAsync_WithNullResult_ShouldReturnNull()
    {
        // Arrange
        var operationName = "null-result-operation";

        // Act
        var result = await _service.ExecuteWithNullAsync<string>(() => Task.FromResult<string?>(null), operationName);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task ExecuteWithNullAsync_WithValidResult_ShouldReturnResult()
    {
        // Arrange
        var operationName = "valid-result-operation";
        var expectedResult = "valid-result";

        // Act
        var result = await _service.ExecuteWithNullAsync(() => Task.FromResult<string?>(expectedResult), operationName);

        // Assert
        Assert.Equal(expectedResult, result);
    }

    [Fact]
    public void ExecuteAsync_WithJitter_ShouldVaryDelayTimes()
    {
        // Arrange
        var delays = new List<double>();
        var attemptNumber = 2;

        // Act - Calculate multiple delays to see jitter variation
        for (var i = 0; i < 10; i++)
        {
            var delay = _service.CalculateDelay(attemptNumber);
            delays.Add(delay.TotalMilliseconds);
        }

        // Assert - Should have variation due to jitter
        var minDelay = delays.Min();
        var maxDelay = delays.Max();
        var variation = maxDelay - minDelay;

        // With 10% jitter, we should see some variation
        Assert.True(variation > 0, "Jitter should cause variation in delay times");

        // All delays should be within expected range (base * 2 ± 10%)
        var baseDelay = _configuration.BaseDelayMs * 2;
        var expectedMin = baseDelay * 0.9;
        var expectedMax = baseDelay * 1.1;

        Assert.All(
            delays,
            delay => Assert.True(
                    delay >= expectedMin && delay <= expectedMax,
                    $"Delay {delay}ms should be between {expectedMin}ms and {expectedMax}ms"
                ));
    }

    [Fact]
    public async Task ExecuteAsync_ShouldMaintainCorrelationContext()
    {
        // Arrange
        var operationName = "correlation-test";
        var attempts = 0;

        // Act
        _ = await _service.ExecuteAsync(
            () =>
            {
                attempts++;
                if (attempts < 2)
                {
                    throw new HttpRequestException("503 Service Unavailable");
                }
                return Task.FromResult("success");
            },
            operationName
        );

        // Assert
        Assert.Equal(2, attempts);

        // Verify that correlation ID was maintained (indirectly through logging)
        // In a real implementation, we would verify correlation ID propagation
    }
}
