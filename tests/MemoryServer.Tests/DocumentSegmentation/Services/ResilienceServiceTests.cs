using MemoryServer.DocumentSegmentation.Models;
using MemoryServer.DocumentSegmentation.Services;
using Microsoft.Extensions.Logging;
using Moq;
using System.Collections.Immutable;
using Xunit;

namespace MemoryServer.Tests.DocumentSegmentation.Services;

/// <summary>
/// Tests for ResilienceService implementation.
/// Validates comprehensive error handling, circuit breaker integration, and graceful degradation.
/// Covers AC-4.1, AC-4.2, AC-4.3, AC-4.4, and AC-5.1 to AC-5.4 from ErrorHandling-TestAcceptanceCriteria.
/// </summary>
public class ResilienceServiceTests
{
  private readonly Mock<ICircuitBreakerService> _mockCircuitBreaker;
  private readonly Mock<IRetryPolicyService> _mockRetryPolicy;
  private readonly Mock<ILogger<ResilienceService>> _mockLogger;
  private readonly GracefulDegradationConfiguration _degradationConfig;
  private readonly ResilienceService _service;

  public ResilienceServiceTests()
  {
    _mockCircuitBreaker = new Mock<ICircuitBreakerService>();
    _mockRetryPolicy = new Mock<IRetryPolicyService>();
    _mockLogger = new Mock<ILogger<ResilienceService>>();
    
    _degradationConfig = new GracefulDegradationConfiguration
    {
      FallbackTimeoutMs = 5000,
      RuleBasedQualityScore = 0.7,
      RuleBasedMaxProcessingMs = 10000,
      MaxPerformanceDegradationPercent = 0.2
    };

    _service = new ResilienceService(
      _mockCircuitBreaker.Object,
      _mockRetryPolicy.Object,
      _degradationConfig,
      _mockLogger.Object);
  }

  [Fact]
  public async Task ExecuteWithResilienceAsync_WithSuccessfulOperation_ShouldReturnSuccessResult()
  {
    // Arrange
    var expectedData = "success-data";
    var operationName = "successful-operation";

    _mockCircuitBreaker
      .Setup(cb => cb.ExecuteAsync(It.IsAny<Func<Task<string>>>(), operationName, It.IsAny<CancellationToken>()))
      .ReturnsAsync(expectedData);

    _mockRetryPolicy
      .Setup(rp => rp.ExecuteAsync(It.IsAny<Func<Task<string>>>(), operationName, It.IsAny<CancellationToken>()))
      .ReturnsAsync(expectedData);

    // Act
    var result = await _service.ExecuteWithResilienceAsync<string>(
      () => Task.FromResult(expectedData),
      null,
      operationName);

    // Assert
    Assert.True(result.Success);
    Assert.Equal(expectedData, result.Data);
    Assert.False(result.DegradedMode);
    Assert.Equal(1.0, result.QualityScore);
    Assert.Equal("LLM-Enhanced", result.StrategyUsed);
    Assert.NotNull(result.CorrelationId);
    Assert.True(result.ProcessingTimeMs >= 0);
  }

  [Fact]
  public async Task ExecuteWithResilienceAsync_WithFailureAndSuccessfulFallback_ShouldReturnDegradedResult()
  {
    // Arrange
    var fallbackData = "fallback-data";
    var operationName = "failing-operation";
    var exception = new HttpRequestException("503 Service Unavailable");

    _mockCircuitBreaker
      .Setup(cb => cb.ExecuteAsync(It.IsAny<Func<Task<string>>>(), operationName, It.IsAny<CancellationToken>()))
      .ThrowsAsync(exception);

    _mockRetryPolicy
      .Setup(rp => rp.ExecuteAsync(It.IsAny<Func<Task<string>>>(), operationName, It.IsAny<CancellationToken>()))
      .ThrowsAsync(exception);

    // Act
    var result = await _service.ExecuteWithResilienceAsync<string>(
      () => Task.FromResult("main-data"),
      () => Task.FromResult(fallbackData),
      operationName);

    // Assert - Implements AC-4.1 and AC-4.2
    Assert.True(result.Success);
    Assert.Equal(fallbackData, result.Data);
    Assert.True(result.DegradedMode); // AC-4.2: degradedMode flag
    Assert.Equal(_degradationConfig.RuleBasedQualityScore, result.QualityScore); // AC-4.2: quality score
    Assert.Equal("Rule-Based (Fallback)", result.StrategyUsed); // AC-4.2: strategy indication
    Assert.Contains("LLM operation failed", result.DegradationReason); // AC-4.2: reasoning explanation
    Assert.NotNull(result.CorrelationId);
    Assert.True(result.ProcessingTimeMs < _degradationConfig.FallbackTimeoutMs); // AC-4.1: within 5 seconds
  }

  [Fact]
  public async Task ExecuteWithResilienceAsync_WithFailureAndNoFallback_ShouldReturnFailureResult()
  {
    // Arrange
    var operationName = "failing-operation-no-fallback";
    var exception = new HttpRequestException("503 Service Unavailable");

    _mockCircuitBreaker
      .Setup(cb => cb.ExecuteAsync(It.IsAny<Func<Task<string>>>(), operationName, It.IsAny<CancellationToken>()))
      .ThrowsAsync(exception);

    _mockRetryPolicy
      .Setup(rp => rp.ExecuteAsync(It.IsAny<Func<Task<string>>>(), operationName, It.IsAny<CancellationToken>()))
      .ThrowsAsync(exception);

    // Act
    var result = await _service.ExecuteWithResilienceAsync<string>(
      () => Task.FromResult("main-data"),
      null, // No fallback
      operationName);

    // Assert
    Assert.False(result.Success);
    Assert.Null(result.Data);
    Assert.False(result.DegradedMode);
    Assert.Equal(0.0, result.QualityScore);
    Assert.Equal("Failed", result.StrategyUsed);
    Assert.Contains("Operation failed", result.ErrorMessage);
    Assert.NotNull(result.CorrelationId);
  }

  [Fact]
  public async Task ExecuteWithResilienceAsync_WithBothOperationsFailingc_ShouldReturnFailureResult()
  {
    // Arrange
    var operationName = "both-failing-operation";
    var mainException = new HttpRequestException("503 Service Unavailable");
    var fallbackException = new InvalidOperationException("Fallback also failed");

    _mockCircuitBreaker
      .Setup(cb => cb.ExecuteAsync(It.IsAny<Func<Task<string>>>(), operationName, It.IsAny<CancellationToken>()))
      .ThrowsAsync(mainException);

    _mockRetryPolicy
      .Setup(rp => rp.ExecuteAsync(It.IsAny<Func<Task<string>>>(), operationName, It.IsAny<CancellationToken>()))
      .ThrowsAsync(mainException);

    // Act
    var result = await _service.ExecuteWithResilienceAsync<string>(
      () => Task.FromResult("main-data"),
      () => throw fallbackException,
      operationName);

    // Assert
    Assert.False(result.Success);
    Assert.Null(result.Data);
    Assert.True(result.DegradedMode);
    Assert.Equal(0.0, result.QualityScore);
    Assert.Equal("Failed", result.StrategyUsed);
    Assert.Contains("Both main and fallback operations failed", result.DegradationReason);
    Assert.Contains(fallbackException.Message, result.ErrorMessage);
    Assert.NotNull(result.CorrelationId);
  }

  [Fact]
  public async Task ExecuteWithResilienceAsync_WithFallbackTimeout_ShouldRespectTimeLimit()
  {
    // Arrange - Set very short timeout for testing
    var shortTimeoutConfig = new GracefulDegradationConfiguration
    {
      FallbackTimeoutMs = 100, // Very short timeout
      RuleBasedQualityScore = 0.7
    };

    var serviceWithShortTimeout = new ResilienceService(
      _mockCircuitBreaker.Object,
      _mockRetryPolicy.Object,
      shortTimeoutConfig,
      _mockLogger.Object);

    var operationName = "timeout-test";
    var exception = new HttpRequestException("503 Service Unavailable");

    _mockCircuitBreaker
      .Setup(cb => cb.ExecuteAsync(It.IsAny<Func<Task<string>>>(), operationName, It.IsAny<CancellationToken>()))
      .ThrowsAsync(exception);

    _mockRetryPolicy
      .Setup(rp => rp.ExecuteAsync(It.IsAny<Func<Task<string>>>(), operationName, It.IsAny<CancellationToken>()))
      .ThrowsAsync(exception);

    // Act
    var result = await serviceWithShortTimeout.ExecuteWithResilienceAsync<string>(
      () => Task.FromResult("main-data"),
      async () =>
      {
        await Task.Delay(500); // Longer than timeout
        return "should-not-complete";
      },
      operationName);

    // Assert - Should fail due to timeout
    Assert.False(result.Success);
    Assert.True(result.ErrorMessage?.ToLowerInvariant()?.Contains("canceled") == true ||
                result.ErrorMessage?.ToLowerInvariant()?.Contains("timeout") == true,
                $"Expected error message to contain 'canceled' or 'timeout', but got: {result.ErrorMessage}");
  }

  [Fact]
  public void GetErrorMetrics_ShouldReturnCurrentMetrics()
  {
    // Act
    var metrics = _service.GetErrorMetrics();

    // Assert - Implements AC-5.3
    Assert.NotNull(metrics);
    Assert.NotNull(metrics.ErrorCounts);
    Assert.NotNull(metrics.StateDurations);
    Assert.Equal(0, metrics.FallbackUsageCount);
    Assert.NotNull(metrics.RecoveryTimes);
    Assert.True(metrics.LastUpdated <= DateTime.UtcNow);
    Assert.True(metrics.LastUpdated > DateTime.UtcNow.AddMinutes(-1));
  }

  [Fact]
  public async Task ResetMetrics_ShouldClearAllMetrics()
  {
    // Arrange - First, execute an operation to generate some metrics
    await _service.ExecuteWithResilienceAsync<string>(
      () => Task.FromResult("test"),
      null,
      "test-operation");

    // Act
    _service.ResetMetrics();

    // Assert - Implements AC-5.2 state cleanup
    var metrics = _service.GetErrorMetrics();
    Assert.Empty(metrics.ErrorCounts);
    Assert.Equal(0, metrics.FallbackUsageCount);
    Assert.Empty(metrics.RecoveryTimes);
  }

  [Fact]
  public void GetHealthStatus_WithNoOperations_ShouldReturnHealthy()
  {
    // Act
    var health = _service.GetHealthStatus();

    // Assert - Implements AC-5.1 service recovery detection
    Assert.True(health.IsHealthy);
    Assert.Equal(0, health.OpenCircuitCount);
    Assert.Equal(0, health.FallbackUsageRate);
    Assert.Equal(0, health.AverageResponseTimeMs);
    Assert.Equal(0, health.ErrorRate);
    Assert.True(health.LastCheckAt <= DateTime.UtcNow);
  }

  /// <summary>
  /// Test data for various error scenarios to validate resilience behavior.
  /// Tests different combinations of errors and recovery patterns.
  /// </summary>
  public static IEnumerable<object[]> ErrorScenarioTestCases => new List<object[]>
  {
    new object[] 
    { 
      new HttpRequestException("timeout"), 
      true, 
      "Network timeouts should use fallback" 
    },
    new object[] 
    { 
      new HttpRequestException("429 Too Many Requests"), 
      true, 
      "Rate limiting should use fallback" 
    },
    new object[] 
    { 
      new HttpRequestException("401 Unauthorized"), 
      true, 
      "Auth errors should use fallback" 
    },
    new object[] 
    { 
      new ArgumentException("Invalid response"), 
      true, 
      "Malformed responses should use fallback" 
    },
    new object[] 
    { 
      new InvalidOperationException("Generic error"), 
      true, 
      "Generic errors should use fallback" 
    }
  };

  [Theory]
  [MemberData(nameof(ErrorScenarioTestCases))]
  public async Task ExecuteWithResilienceAsync_WithVariousErrors_ShouldHandleGracefully(
    Exception exception,
    bool shouldUseFallback,
    string scenario)
  {
    // Arrange
    var operationName = $"error-scenario-{scenario}";
    var fallbackData = "fallback-result";

    _mockCircuitBreaker
      .Setup(cb => cb.ExecuteAsync(It.IsAny<Func<Task<string>>>(), operationName, It.IsAny<CancellationToken>()))
      .ThrowsAsync(exception);

    _mockRetryPolicy
      .Setup(rp => rp.ExecuteAsync(It.IsAny<Func<Task<string>>>(), operationName, It.IsAny<CancellationToken>()))
      .ThrowsAsync(exception);

    // Act
    var result = await _service.ExecuteWithResilienceAsync<string>(
      () => Task.FromResult("main-data"),
      shouldUseFallback ? () => Task.FromResult(fallbackData) : null,
      operationName);

    // Assert
    if (shouldUseFallback)
    {
      Assert.True(result.Success);
      Assert.Equal(fallbackData, result.Data);
      Assert.True(result.DegradedMode);
      Assert.Equal(_degradationConfig.RuleBasedQualityScore, result.QualityScore);
    }
    else
    {
      Assert.False(result.Success);
      Assert.Null(result.Data);
    }

    Assert.NotNull(result.CorrelationId);
    Assert.True(result.ProcessingTimeMs >= 0);
  }

  [Fact]
  public async Task ExecuteWithResilienceAsync_ShouldTrackMetricsCorrectly()
  {
    // Arrange
    var operationName = "metrics-tracking-test";
    var fallbackData = "fallback-data";
    var exception = new HttpRequestException("503 Service Unavailable");

    _mockCircuitBreaker
      .Setup(cb => cb.ExecuteAsync(It.IsAny<Func<Task<string>>>(), operationName, It.IsAny<CancellationToken>()))
      .ThrowsAsync(exception);

    _mockRetryPolicy
      .Setup(rp => rp.ExecuteAsync(It.IsAny<Func<Task<string>>>(), operationName, It.IsAny<CancellationToken>()))
      .ThrowsAsync(exception);

    // Act
    var result = await _service.ExecuteWithResilienceAsync<string>(
      () => Task.FromResult("main-data"),
      () => Task.FromResult(fallbackData),
      operationName);

    // Assert metrics tracking
    var metrics = _service.GetErrorMetrics();
    Assert.True(metrics.FallbackUsageCount > 0);
    Assert.True(metrics.ErrorCounts.ContainsKey("service_unavailable"));
    
    var health = _service.GetHealthStatus();
    Assert.True(health.FallbackUsageRate > 0);
  }

  [Fact]
  public async Task ExecuteWithResilienceAsync_ShouldRespectCancellation()
  {
    // Arrange
    var operationName = "cancellation-test";
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    _mockCircuitBreaker
      .Setup(cb => cb.ExecuteAsync(It.IsAny<Func<Task<string>>>(), operationName, cts.Token))
      .ThrowsAsync(new OperationCanceledException(cts.Token));

    _mockRetryPolicy
      .Setup(rp => rp.ExecuteAsync(It.IsAny<Func<Task<string>>>(), operationName, cts.Token))
      .ThrowsAsync(new OperationCanceledException(cts.Token));

    // Act & Assert
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
      _service.ExecuteWithResilienceAsync<string>(
        () => Task.FromResult("main-data"),
        () => Task.FromResult("fallback-data"),
        operationName,
        cts.Token));
  }

  [Fact]
  public async Task ExecuteWithResilienceAsync_WithMultipleOperations_ShouldCalculateHealthCorrectly()
  {
    // Arrange - Execute multiple operations to build up health metrics
    var operations = new[]
    {
      ("success-1", false, false), // Success
      ("success-2", false, false), // Success  
      ("fail-with-fallback", true, false), // Fail main, success fallback
      ("fail-both", true, true), // Fail both main and fallback
      ("success-3", false, false) // Success
    };

    var successData = "success-data";
    var fallbackData = "fallback-data";

    foreach (var (opName, shouldFail, shouldFailFallback) in operations)
    {
      if (shouldFail)
      {
        _mockCircuitBreaker
          .Setup(cb => cb.ExecuteAsync(It.IsAny<Func<Task<string>>>(), opName, It.IsAny<CancellationToken>()))
          .ThrowsAsync(new HttpRequestException("503 Service Unavailable"));

        _mockRetryPolicy
          .Setup(rp => rp.ExecuteAsync(It.IsAny<Func<Task<string>>>(), opName, It.IsAny<CancellationToken>()))
          .ThrowsAsync(new HttpRequestException("503 Service Unavailable"));
      }
      else
      {
        _mockCircuitBreaker
          .Setup(cb => cb.ExecuteAsync(It.IsAny<Func<Task<string>>>(), opName, It.IsAny<CancellationToken>()))
          .ReturnsAsync(successData);

        _mockRetryPolicy
          .Setup(rp => rp.ExecuteAsync(It.IsAny<Func<Task<string>>>(), opName, It.IsAny<CancellationToken>()))
          .ReturnsAsync(successData);
      }

      // Act
      var result = await _service.ExecuteWithResilienceAsync<string>(
        () => Task.FromResult(successData),
        shouldFailFallback ? () => throw new InvalidOperationException("Fallback failed") 
                           : () => Task.FromResult(fallbackData),
        opName);

      // Brief delay to ensure different timestamps
      await Task.Delay(10);
    }

    // Assert health calculations
    var health = _service.GetHealthStatus();
    var metrics = _service.GetErrorMetrics();

    // We should have some fallback usage
    Assert.True(metrics.FallbackUsageCount > 0);
    Assert.True(health.FallbackUsageRate > 0);
    
    // Error rate should be reasonable (1 complete failure out of 5 operations = 20%)
    Assert.True(health.ErrorRate <= 25); // Allow some margin
    
    // Response times should be tracked
    Assert.True(health.AverageResponseTimeMs >= 0);
  }
}
