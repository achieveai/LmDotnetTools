using System.Collections.Concurrent;
using MemoryServer.DocumentSegmentation.Models;

namespace MemoryServer.DocumentSegmentation.Services;

/// <summary>
///     Interface for circuit breaker functionality.
///     Implements AC-2.1, AC-2.2, and AC-2.3 from ErrorHandling-TestAcceptanceCriteria.
/// </summary>
public interface ICircuitBreakerService
{
    /// <summary>
    ///     Executes an operation with circuit breaker protection.
    /// </summary>
    Task<T> ExecuteAsync<T>(
        Func<Task<T>> operation,
        string operationName,
        CancellationToken cancellationToken = default
    )
        where T : class;

    /// <summary>
    ///     Records a successful operation, potentially closing the circuit.
    /// </summary>
    void RecordSuccess(string operationName);

    /// <summary>
    ///     Records a failure, potentially opening the circuit.
    /// </summary>
    void RecordFailure(string operationName, Exception exception);

    /// <summary>
    ///     Gets the current state of the circuit breaker for an operation.
    /// </summary>
    CircuitBreakerState GetState(string operationName);

    /// <summary>
    ///     Checks if the circuit is currently open for an operation.
    /// </summary>
    bool IsCircuitOpen(string operationName);

    /// <summary>
    ///     Forces the circuit to open (for testing purposes).
    /// </summary>
    void ForceOpen(string operationName);

    /// <summary>
    ///     Forces the circuit to close (for testing purposes).
    /// </summary>
    void ForceClose(string operationName);
}

/// <summary>
///     Implementation of circuit breaker service with configurable thresholds and timing.
///     Provides centralized circuit breaker functionality for all LLM operations.
/// </summary>
public class CircuitBreakerService : ICircuitBreakerService
{
    private readonly ConcurrentDictionary<string, CircuitBreakerState> _circuitStates;
    private readonly CircuitBreakerConfiguration _configuration;
    private readonly ConcurrentDictionary<string, object> _locks;
    private readonly ILogger<CircuitBreakerService> _logger;

    public CircuitBreakerService(CircuitBreakerConfiguration configuration, ILogger<CircuitBreakerService> logger)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _circuitStates = new ConcurrentDictionary<string, CircuitBreakerState>();
        _locks = new ConcurrentDictionary<string, object>();
    }

    /// <summary>
    ///     Executes an operation with circuit breaker protection.
    ///     Implements AC-2.1 state transitions and AC-2.3 recovery timing.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(
        Func<Task<T>> operation,
        string operationName,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        ArgumentNullException.ThrowIfNull(operation, nameof(operation));

        // Check cancellation before doing any work
        cancellationToken.ThrowIfCancellationRequested();

        var state = GetOrCreateState(operationName);
        var lockObject = _locks.GetOrAdd(operationName, _ => new object());

        lock (lockObject)
        {
            // Check if circuit is open
            if (state.State == CircuitBreakerStateEnum.Open)
            {
                if (DateTime.UtcNow < state.NextRetryAt)
                {
                    _logger.LogWarning(
                        "Circuit breaker is open for {OperationName}. Next retry at {NextRetry}",
                        operationName,
                        state.NextRetryAt
                    );
                    throw new CircuitBreakerOpenException(operationName, state.NextRetryAt);
                }

                // Transition to half-open
                _logger.LogInformation("Circuit breaker transitioning to Half-Open for {OperationName}", operationName);

                var halfOpenState = state with { State = CircuitBreakerStateEnum.HalfOpen };
                _ = _circuitStates.TryUpdate(operationName, halfOpenState, state);
                state = halfOpenState;
            }
        }

        try
        {
            _logger.LogDebug(
                "Executing {OperationName} with circuit breaker in {State} state",
                operationName,
                state.State
            );

            // Check cancellation again before executing the operation
            cancellationToken.ThrowIfCancellationRequested();

            var result = await operation();

            // Record success
            RecordSuccess(operationName);
            return result;
        }
        catch (OperationCanceledException)
        {
            // Don't record cancellation as a failure - it's not a service error
            _logger.LogDebug("Operation {OperationName} was cancelled", operationName);
            throw;
        }
        catch (Exception ex)
        {
            RecordFailure(operationName, ex);
            throw;
        }
    }

    /// <summary>
    ///     Records a successful operation, potentially closing the circuit.
    ///     Implements AC-2.1 success transitions.
    /// </summary>
    public void RecordSuccess(string operationName)
    {
        var lockObject = _locks.GetOrAdd(operationName, _ => new object());

        lock (lockObject)
        {
            var currentState = GetOrCreateState(operationName);

            if (currentState.State != CircuitBreakerStateEnum.Closed)
            {
                _logger.LogInformation(
                    "Circuit breaker closing for {OperationName} after successful operation",
                    operationName
                );
            }

            // Reset to closed state on success
            var newState = new CircuitBreakerState
            {
                State = CircuitBreakerStateEnum.Closed,
                FailureCount = 0,
                LastOpenedAt = currentState.LastOpenedAt,
                NextRetryAt = null,
                TotalOpenings = currentState.TotalOpenings,
                LastError = null,
            };

            _ = _circuitStates.TryUpdate(operationName, newState, currentState);
        }
    }

    /// <summary>
    ///     Records a failure, potentially opening the circuit.
    ///     Implements AC-2.1 failure transitions and AC-2.2 threshold configuration.
    /// </summary>
    public void RecordFailure(string operationName, Exception exception)
    {
        ArgumentException.ThrowIfNullOrEmpty(operationName, nameof(operationName));
        ArgumentNullException.ThrowIfNull(exception, nameof(exception));

        var lockObject = _locks.GetOrAdd(operationName, _ => new object());
        var errorType = ClassifyError(exception);
        var threshold = GetFailureThreshold(errorType);

        lock (lockObject)
        {
            var currentState = GetOrCreateState(operationName);
            var newFailureCount = currentState.FailureCount + 1;

            _logger.LogWarning(
                "Recording failure for {OperationName}. Count: {FailureCount}/{Threshold}. Error: {Error}",
                operationName,
                newFailureCount,
                threshold,
                exception.Message
            );

            if (newFailureCount >= threshold)
            {
                // Open the circuit
                var nextRetryAt = CalculateNextRetryTime(currentState.TotalOpenings);

                _logger.LogWarning(
                    "Circuit breaker opening for {OperationName} after {FailureCount} failures. Next retry: {NextRetry}",
                    operationName,
                    newFailureCount,
                    nextRetryAt
                );

                var openState = new CircuitBreakerState
                {
                    State = CircuitBreakerStateEnum.Open,
                    FailureCount = newFailureCount,
                    LastOpenedAt = DateTime.UtcNow,
                    NextRetryAt = nextRetryAt,
                    TotalOpenings = currentState.TotalOpenings + 1,
                    LastError = exception.Message,
                };

                _ = _circuitStates.TryUpdate(operationName, openState, currentState);
            }
            else
            {
                // Increment failure count but keep circuit closed
                var failureState = currentState with
                {
                    FailureCount = newFailureCount,
                    LastError = exception.Message,
                };

                _ = _circuitStates.TryUpdate(operationName, failureState, currentState);
            }
        }
    }

    /// <summary>
    ///     Gets the current state of the circuit breaker for an operation.
    /// </summary>
    public CircuitBreakerState GetState(string operationName)
    {
        return GetOrCreateState(operationName);
    }

    /// <summary>
    ///     Checks if the circuit is currently open for an operation.
    /// </summary>
    public bool IsCircuitOpen(string operationName)
    {
        var state = GetOrCreateState(operationName);

        return state.State == CircuitBreakerStateEnum.Open && DateTime.UtcNow < state.NextRetryAt;
    }

    /// <summary>
    ///     Forces the circuit to open (for testing purposes).
    /// </summary>
    public void ForceOpen(string operationName)
    {
        var lockObject = _locks.GetOrAdd(operationName, _ => new object());

        lock (lockObject)
        {
            var currentState = GetOrCreateState(operationName);
            var nextRetryAt = DateTime.UtcNow.AddMilliseconds(_configuration.TimeoutMs);

            var openState = currentState with
            {
                State = CircuitBreakerStateEnum.Open,
                LastOpenedAt = DateTime.UtcNow,
                NextRetryAt = nextRetryAt,
                TotalOpenings = currentState.TotalOpenings + 1,
                LastError = "Forced open for testing",
            };

            _ = _circuitStates.TryUpdate(operationName, openState, currentState);

            _logger.LogWarning("Circuit breaker forced open for {OperationName}", operationName);
        }
    }

    /// <summary>
    ///     Forces the circuit to close (for testing purposes).
    /// </summary>
    public void ForceClose(string operationName)
    {
        var lockObject = _locks.GetOrAdd(operationName, _ => new object());

        lock (lockObject)
        {
            var currentState = GetOrCreateState(operationName);

            var closedState = new CircuitBreakerState
            {
                State = CircuitBreakerStateEnum.Closed,
                FailureCount = 0,
                LastOpenedAt = currentState.LastOpenedAt,
                NextRetryAt = null,
                TotalOpenings = currentState.TotalOpenings,
                LastError = null,
            };

            _ = _circuitStates.TryUpdate(operationName, closedState, currentState);

            _logger.LogInformation("Circuit breaker forced closed for {OperationName}", operationName);
        }
    }

    #region Private Helper Methods

    private CircuitBreakerState GetOrCreateState(string operationName)
    {
        return _circuitStates.GetOrAdd(
            operationName,
            _ => new CircuitBreakerState
            {
                State = CircuitBreakerStateEnum.Closed,
                FailureCount = 0,
                LastOpenedAt = null,
                NextRetryAt = null,
                TotalOpenings = 0,
                LastError = null,
            }
        );
    }

    private static string ClassifyError(Exception exception)
    {
        return exception switch
        {
            HttpRequestException httpEx when httpEx.Message.Contains("401") => "401",
            HttpRequestException httpEx when httpEx.Message.Contains("429") => "429",
            HttpRequestException httpEx when httpEx.Message.Contains("503") => "503",
            TaskCanceledException => "timeout",
            _ => "generic",
        };
    }

    private int GetFailureThreshold(string errorType)
    {
        return _configuration.ErrorTypeThresholds.TryGetValue(errorType, out var threshold)
            ? threshold
            : _configuration.FailureThreshold;
    }

    private DateTime CalculateNextRetryTime(int openingCount)
    {
        // Implement exponential backoff with cap as per AC-2.3
        var baseTimeout = _configuration.TimeoutMs;
        var exponentialTimeout = baseTimeout * Math.Pow(_configuration.ExponentialFactor, openingCount);
        var cappedTimeout = Math.Min(exponentialTimeout, _configuration.MaxTimeoutMs);

        return DateTime.UtcNow.AddMilliseconds(cappedTimeout);
    }

    #endregion
}

/// <summary>
///     Exception thrown when a circuit breaker is open.
/// </summary>
public class CircuitBreakerOpenException : Exception
{
    public CircuitBreakerOpenException(string operationName, DateTime? nextRetryAt)
        : base($"Circuit breaker is open for operation '{operationName}'. Next retry at: {nextRetryAt}")
    {
        OperationName = operationName;
        NextRetryAt = nextRetryAt;
    }

    public string OperationName { get; }
    public DateTime? NextRetryAt { get; }
}
