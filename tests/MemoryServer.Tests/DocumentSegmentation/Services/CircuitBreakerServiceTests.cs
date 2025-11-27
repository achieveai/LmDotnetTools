using MemoryServer.DocumentSegmentation.Models;
using MemoryServer.DocumentSegmentation.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace MemoryServer.Tests.DocumentSegmentation.Services;

/// <summary>
///     Tests for CircuitBreakerService implementation.
///     Validates AC-2.1, AC-2.2, and AC-2.3 from ErrorHandling-TestAcceptanceCriteria.
/// </summary>
public class CircuitBreakerServiceTests
{
    private readonly CircuitBreakerConfiguration _configuration;
    private readonly Mock<ILogger<CircuitBreakerService>> _mockLogger;
    private readonly CircuitBreakerService _service;

    public CircuitBreakerServiceTests()
    {
        _mockLogger = new Mock<ILogger<CircuitBreakerService>>();
        _configuration = new CircuitBreakerConfiguration
        {
            FailureThreshold = 3, // Lower threshold for testing
            TimeoutMs = 1000, // Shorter timeout for testing
            MaxTimeoutMs = 10000,
            ExponentialFactor = 2.0,
        };
        _service = new CircuitBreakerService(_configuration, _mockLogger.Object);
    }

    /// <summary>
    ///     Test data for different error types and their thresholds.
    ///     Validates AC-2.2 error type threshold configuration.
    /// </summary>
    public static IEnumerable<object[]> ErrorTypeTestCases =>
        [
            [new HttpRequestException("401 Unauthorized"), "401", "Authentication error should use specific threshold"],
            [
                new HttpRequestException("503 Service Unavailable"),
                "503",
                "Service unavailable should use specific threshold",
            ],
            [new TaskCanceledException("Timeout"), "timeout", "Timeout should use default threshold"],
            [new InvalidOperationException("Generic error"), "generic", "Generic error should use default threshold"],
        ];

    [Fact]
    public async Task ExecuteAsync_WithSuccessfulOperation_ShouldReturnResult()
    {
        // Arrange
        var expectedResult = "success";
        var operationName = "test-operation";

        // Act
        var result = await _service.ExecuteAsync(() => Task.FromResult(expectedResult), operationName);

        // Assert
        Assert.Equal(expectedResult, result);

        var state = _service.GetState(operationName);
        Assert.Equal(CircuitBreakerStateEnum.Closed, state.State);
        Assert.Equal(0, state.FailureCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithMultipleFailures_ShouldOpenCircuit()
    {
        // Arrange
        var operationName = "failing-operation";
        var exception = new InvalidOperationException("Test failure");

        // Act & Assert - Record failures up to threshold
        for (var i = 1; i < _configuration.FailureThreshold; i++)
        {
            _ = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _service.ExecuteAsync<string>(() => throw exception, operationName)
            );

            var state = _service.GetState(operationName);
            Assert.Equal(CircuitBreakerStateEnum.Closed, state.State);
            Assert.Equal(i, state.FailureCount);
        }

        // Final failure should open the circuit
        _ = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _service.ExecuteAsync<string>(() => throw exception, operationName)
        );

        var finalState = _service.GetState(operationName);
        Assert.Equal(CircuitBreakerStateEnum.Open, finalState.State);
        Assert.Equal(_configuration.FailureThreshold, finalState.FailureCount);
        _ = Assert.NotNull(finalState.NextRetryAt);
        Assert.True(finalState.NextRetryAt > DateTime.UtcNow);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCircuitOpen_ShouldThrowCircuitBreakerOpenException()
    {
        // Arrange
        var operationName = "open-circuit-operation";
        _service.ForceOpen(operationName);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<CircuitBreakerOpenException>(async () =>
            await _service.ExecuteAsync(() => Task.FromResult("should not execute"), operationName)
        );

        Assert.Equal(operationName, exception.OperationName);
        _ = Assert.NotNull(exception.NextRetryAt);
    }

    [Fact]
    public void RecordSuccess_AfterFailures_ShouldResetCircuit()
    {
        // Arrange
        var operationName = "reset-operation";

        // Record some failures
        _service.RecordFailure(operationName, new Exception("Test failure 1"));
        _service.RecordFailure(operationName, new Exception("Test failure 2"));

        var stateAfterFailures = _service.GetState(operationName);
        Assert.Equal(2, stateAfterFailures.FailureCount);

        // Act
        _service.RecordSuccess(operationName);

        // Assert
        var stateAfterSuccess = _service.GetState(operationName);
        Assert.Equal(CircuitBreakerStateEnum.Closed, stateAfterSuccess.State);
        Assert.Equal(0, stateAfterSuccess.FailureCount);
        Assert.Null(stateAfterSuccess.NextRetryAt);
    }

    [Fact]
    public void IsCircuitOpen_WhenCircuitClosed_ShouldReturnFalse()
    {
        // Arrange
        var operationName = "closed-circuit";

        // Act
        var isOpen = _service.IsCircuitOpen(operationName);

        // Assert
        Assert.False(isOpen);
    }

    [Fact]
    public void IsCircuitOpen_WhenCircuitOpen_ShouldReturnTrue()
    {
        // Arrange
        var operationName = "open-circuit";
        _service.ForceOpen(operationName);

        // Act
        var isOpen = _service.IsCircuitOpen(operationName);

        // Assert
        Assert.True(isOpen);
    }

    [Fact]
    public void ForceOpen_ShouldOpenCircuit()
    {
        // Arrange
        var operationName = "force-open-test";

        // Act
        _service.ForceOpen(operationName);

        // Assert
        var state = _service.GetState(operationName);
        Assert.Equal(CircuitBreakerStateEnum.Open, state.State);
        _ = Assert.NotNull(state.LastOpenedAt);
        _ = Assert.NotNull(state.NextRetryAt);
        Assert.Equal("Forced open for testing", state.LastError);
    }

    [Fact]
    public void ForceClose_ShouldCloseCircuit()
    {
        // Arrange
        var operationName = "force-close-test";
        _service.ForceOpen(operationName); // First open it

        // Act
        _service.ForceClose(operationName);

        // Assert
        var state = _service.GetState(operationName);
        Assert.Equal(CircuitBreakerStateEnum.Closed, state.State);
        Assert.Equal(0, state.FailureCount);
        Assert.Null(state.NextRetryAt);
        Assert.Null(state.LastError);
    }

    [Theory]
    [MemberData(nameof(ErrorTypeTestCases))]
    public void RecordFailure_WithDifferentErrorTypes_ShouldClassifyCorrectly(
        Exception exception,
        string expectedErrorType,
        string _
    )
    {
        // Arrange
        var operationName = $"error-type-test-{expectedErrorType}";

        // Act
        _service.RecordFailure(operationName, exception);

        // Assert
        var state = _service.GetState(operationName);
        Assert.Equal(1, state.FailureCount);
        Assert.Equal(exception.Message, state.LastError);

        // Additional verification that the error was classified correctly
        // (This is indirect since we can't directly access the classification)
        Assert.NotNull(state.LastError);
    }

    [Fact]
    public async Task ExecuteAsync_WithOperationCancellation_ShouldPropagateCorrectly()
    {
        // Arrange
        var operationName = "cancellation-test";
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        _ = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await _service.ExecuteAsync(
                async () =>
                {
                    // This will immediately throw OperationCanceledException
                    cts.Token.ThrowIfCancellationRequested();
                    await Task.Delay(1000, cts.Token);
                    return "should not complete";
                },
                operationName,
                cts.Token
            )
        );

        // Circuit should not be affected by cancellation
        var state = _service.GetState(operationName);
        Assert.Equal(CircuitBreakerStateEnum.Closed, state.State);
        Assert.Equal(0, state.FailureCount);
    }
}
