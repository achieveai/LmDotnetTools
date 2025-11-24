using MemoryServer.DocumentSegmentation.Models;

namespace MemoryServer.DocumentSegmentation.Services;

/// <summary>
/// Interface for resilience service that combines circuit breaker and retry policies.
/// Implements comprehensive error handling and resilience patterns.
/// </summary>
public interface IResilienceService
{
    /// <summary>
    /// Executes an operation with full resilience protection (circuit breaker + retry + fallback).
    /// </summary>
    Task<ResilienceOperationResult<T>> ExecuteWithResilienceAsync<T>(
        Func<Task<T>> operation,
        Func<Task<T>>? fallbackOperation,
        string operationName,
        CancellationToken cancellationToken = default
    )
        where T : class;

    /// <summary>
    /// Gets current error metrics for monitoring and alerting.
    /// </summary>
    ErrorMetrics GetErrorMetrics();

    /// <summary>
    /// Resets error metrics (for testing or maintenance).
    /// </summary>
    void ResetMetrics();

    /// <summary>
    /// Gets health status of the resilience service.
    /// </summary>
    ResilienceHealthStatus GetHealthStatus();
}

/// <summary>
/// Health status of the resilience service.
/// </summary>
public record ResilienceHealthStatus
{
    /// <summary>
    /// Overall health status.
    /// </summary>
    public bool IsHealthy { get; init; }

    /// <summary>
    /// Number of operations currently in circuit open state.
    /// </summary>
    public int OpenCircuitCount { get; init; }

    /// <summary>
    /// Current fallback usage rate (percentage).
    /// </summary>
    public double FallbackUsageRate { get; init; }

    /// <summary>
    /// Average response time in milliseconds.
    /// </summary>
    public double AverageResponseTimeMs { get; init; }

    /// <summary>
    /// Overall error rate (percentage).
    /// </summary>
    public double ErrorRate { get; init; }

    /// <summary>
    /// Last health check timestamp.
    /// </summary>
    public DateTime LastCheckAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Implementation of resilience service that orchestrates circuit breaker, retry, and fallback mechanisms.
/// Provides comprehensive error handling and metrics collection for Document Segmentation operations.
/// </summary>
public class ResilienceService : IResilienceService
{
    private readonly ICircuitBreakerService _circuitBreaker;
    private readonly IRetryPolicyService _retryPolicy;
    private readonly GracefulDegradationConfiguration _degradationConfig;
    private readonly ILogger<ResilienceService> _logger;

    // Metrics tracking
    private readonly Lock _metricsLock = new();
    private ErrorMetrics _currentMetrics = new();
    private readonly List<(
        DateTime Timestamp,
        double ResponseTimeMs,
        bool Success,
        bool UsedFallback
    )> _recentOperations = [];
    private const int MaxRecentOperations = 1000;

    public ResilienceService(
        ICircuitBreakerService circuitBreaker,
        IRetryPolicyService retryPolicy,
        GracefulDegradationConfiguration degradationConfig,
        ILogger<ResilienceService> logger
    )
    {
        _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));
        _retryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
        _degradationConfig = degradationConfig ?? throw new ArgumentNullException(nameof(degradationConfig));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Executes an operation with full resilience protection.
    /// Implements AC-4.1, AC-4.2, AC-4.3, and AC-4.4 from ErrorHandling-TestAcceptanceCriteria.
    /// </summary>
    public async Task<ResilienceOperationResult<T>> ExecuteWithResilienceAsync<T>(
        Func<Task<T>> operation,
        Func<Task<T>>? fallbackOperation,
        string operationName,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        var correlationId = Guid.NewGuid().ToString();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var degradationReason = string.Empty;

        _logger.LogDebug(
            "Starting resilient operation {OperationName}. CorrelationId: {CorrelationId}",
            operationName,
            correlationId
        );

        try
        {
            // First, try the main operation with circuit breaker and retry protection
            var result = await ExecuteMainOperationAsync(operation, operationName, correlationId, cancellationToken);

            var responseTimeMs = stopwatch.Elapsed.TotalMilliseconds;
            RecordOperationMetrics(responseTimeMs, success: true, usedFallback: false);

            _logger.LogInformation(
                "Operation {OperationName} completed successfully in {ResponseTime}ms. CorrelationId: {CorrelationId}",
                operationName,
                responseTimeMs,
                correlationId
            );

            return new ResilienceOperationResult<T>
            {
                Data = result,
                DegradedMode = false,
                QualityScore = 1.0,
                StrategyUsed = "LLM-Enhanced",
                ProcessingTimeMs = responseTimeMs,
                Success = true,
                CorrelationId = correlationId,
            };
        }
        catch (CircuitBreakerOpenException circuitEx)
        {
            // Circuit is open - skip main operation entirely and go directly to fallback
            _logger.LogWarning(
                "Circuit breaker is open for operation {OperationName}, using immediate fallback. CorrelationId: {CorrelationId}",
                operationName,
                correlationId
            );

            // Record circuit breaker event
            RecordErrorMetrics(circuitEx);

            // Go directly to fallback - no retries, no main operation
            if (fallbackOperation == null)
            {
                var responseTimeMs = stopwatch.Elapsed.TotalMilliseconds;
                RecordOperationMetrics(responseTimeMs, success: false, usedFallback: false);

                return new ResilienceOperationResult<T>
                {
                    Data = null,
                    DegradedMode = false,
                    QualityScore = 0.0,
                    StrategyUsed = "Failed",
                    DegradationReason =
                        $"Circuit breaker is open: {circuitEx.Message}. No fallback operation available.",
                    ProcessingTimeMs = responseTimeMs,
                    Success = false,
                    ErrorMessage = $"Circuit breaker is open: {circuitEx.Message}",
                    CorrelationId = correlationId,
                };
            }

            // Execute fallback immediately when circuit is open
            using var fallbackCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
            fallbackCts.CancelAfter(_degradationConfig.FallbackTimeoutMs);

            try
            {
                var fallbackResult = await fallbackOperation.Invoke().WaitAsync(fallbackCts.Token);
                var circuitDegradationReason =
                    $"Circuit breaker is open: {circuitEx.Message}. Using immediate fallback to rule-based segmentation.";

                var responseTimeMs = stopwatch.Elapsed.TotalMilliseconds;
                RecordOperationMetrics(responseTimeMs, success: true, usedFallback: true);

                _logger.LogInformation(
                    "Immediate fallback operation succeeded for {OperationName} in {ResponseTime}ms (circuit open). CorrelationId: {CorrelationId}",
                    operationName,
                    responseTimeMs,
                    correlationId
                );

                return new ResilienceOperationResult<T>
                {
                    Data = fallbackResult,
                    DegradedMode = true,
                    QualityScore = _degradationConfig.RuleBasedQualityScore,
                    StrategyUsed = "Rule-Based (Circuit Open)",
                    DegradationReason = circuitDegradationReason,
                    ProcessingTimeMs = responseTimeMs,
                    Success = true,
                    CorrelationId = correlationId,
                };
            }
            catch (OperationCanceledException cancelEx) when (cancelEx.CancellationToken == fallbackCts.Token)
            {
                // Fallback timed out
                _logger.LogError(
                    "Immediate fallback operation timed out for {OperationName} after {TimeoutMs}ms (circuit open). CorrelationId: {CorrelationId}",
                    operationName,
                    _degradationConfig.FallbackTimeoutMs,
                    correlationId
                );

                var responseTimeMs = stopwatch.Elapsed.TotalMilliseconds;
                RecordOperationMetrics(responseTimeMs, success: false, usedFallback: true);

                return new ResilienceOperationResult<T>
                {
                    Data = null,
                    DegradedMode = true,
                    QualityScore = 0.0,
                    StrategyUsed = "Failed",
                    DegradationReason =
                        $"Circuit breaker is open: {circuitEx.Message}. Immediate fallback operation timed out after {_degradationConfig.FallbackTimeoutMs}ms.",
                    ProcessingTimeMs = responseTimeMs,
                    Success = false,
                    ErrorMessage = $"Circuit breaker is open: Fallback operation timeout",
                    CorrelationId = correlationId,
                };
            }
            catch (Exception fallbackEx)
            {
                _logger.LogError(
                    fallbackEx,
                    "Immediate fallback operation failed for {OperationName} (circuit open). CorrelationId: {CorrelationId}",
                    operationName,
                    correlationId
                );

                var responseTimeMs = stopwatch.Elapsed.TotalMilliseconds;
                RecordOperationMetrics(responseTimeMs, success: false, usedFallback: true);

                return new ResilienceOperationResult<T>
                {
                    Data = null,
                    DegradedMode = true,
                    QualityScore = 0.0,
                    StrategyUsed = "Failed",
                    DegradationReason =
                        $"Circuit breaker is open and immediate fallback failed. Circuit: {circuitEx.Message}, Fallback: {fallbackEx.Message}",
                    ProcessingTimeMs = responseTimeMs,
                    Success = false,
                    ErrorMessage = $"Circuit breaker is open: {fallbackEx.Message}",
                    CorrelationId = correlationId,
                };
            }
        }
        catch (TaskCanceledException ex) when (ex.Message.Contains("timeout") || ex.Message.Contains("Network timeout"))
        {
            // Log the timeout warning first
            _logger.LogWarning(
                "Network timeout occurred for operation {OperationName}: {ExceptionMessage}. CorrelationId: {CorrelationId}",
                operationName,
                ex.Message,
                correlationId
            );

            // Handle as regular exception - try fallback logic
            if (fallbackOperation == null)
            {
                var responseTimeMs = stopwatch.Elapsed.TotalMilliseconds;
                RecordOperationMetrics(responseTimeMs, success: false, usedFallback: false);

                return new ResilienceOperationResult<T>
                {
                    Data = null,
                    DegradedMode = false,
                    QualityScore = 0.0,
                    StrategyUsed = "Failed",
                    DegradationReason = $"Operation failed: {ex.Message}. No fallback operation available.",
                    ProcessingTimeMs = responseTimeMs,
                    Success = false,
                    ErrorMessage = $"Operation failed: {ex.Message}",
                    CorrelationId = correlationId,
                };
            }

            // Try fallback for timeout - use same logic as regular exceptions
            // Ensure fallback completes within time limit (AC-4.1: 5 seconds)
            using var fallbackCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
            fallbackCts.CancelAfter(_degradationConfig.FallbackTimeoutMs);

            try
            {
                var fallbackResult = await fallbackOperation.Invoke().WaitAsync(fallbackCts.Token);
                var timeoutDegradationReason =
                    $"Network timeout occurred: {ex.Message}. Fallback to rule-based segmentation.";

                var responseTimeMs = stopwatch.Elapsed.TotalMilliseconds;
                RecordOperationMetrics(responseTimeMs, success: true, usedFallback: true);

                _logger.LogInformation(
                    "Fallback operation succeeded for {OperationName} in {ResponseTime}ms. CorrelationId: {CorrelationId}",
                    operationName,
                    responseTimeMs,
                    correlationId
                );

                return new ResilienceOperationResult<T>
                {
                    Data = fallbackResult,
                    DegradedMode = true,
                    QualityScore = _degradationConfig.RuleBasedQualityScore,
                    StrategyUsed = "Rule-Based (Fallback)",
                    DegradationReason = timeoutDegradationReason,
                    ProcessingTimeMs = responseTimeMs,
                    Success = true,
                    CorrelationId = correlationId,
                };
            }
            catch (OperationCanceledException cancelEx) when (cancelEx.CancellationToken == fallbackCts.Token)
            {
                // Fallback timed out
                _logger.LogError(
                    "Fallback operation timed out for {OperationName} after {TimeoutMs}ms. CorrelationId: {CorrelationId}",
                    operationName,
                    _degradationConfig.FallbackTimeoutMs,
                    correlationId
                );

                var responseTimeMs = stopwatch.Elapsed.TotalMilliseconds;
                RecordOperationMetrics(responseTimeMs, success: false, usedFallback: true);

                return new ResilienceOperationResult<T>
                {
                    Data = null,
                    DegradedMode = true,
                    QualityScore = 0.0,
                    StrategyUsed = "Failed",
                    DegradationReason =
                        $"Network timeout occurred: {ex.Message}. Fallback operation timed out after {_degradationConfig.FallbackTimeoutMs}ms.",
                    ProcessingTimeMs = responseTimeMs,
                    Success = false,
                    ErrorMessage = $"Operation failed: Fallback operation timeout",
                    CorrelationId = correlationId,
                };
            }
            catch (Exception fallbackEx)
            {
                _logger.LogError(
                    fallbackEx,
                    "Fallback operation also failed for {OperationName}. CorrelationId: {CorrelationId}",
                    operationName,
                    correlationId
                );

                var responseTimeMs = stopwatch.Elapsed.TotalMilliseconds;
                RecordOperationMetrics(responseTimeMs, success: false, usedFallback: true);

                return new ResilienceOperationResult<T>
                {
                    Data = null,
                    DegradedMode = true,
                    QualityScore = 0.0,
                    StrategyUsed = "Failed",
                    DegradationReason =
                        $"Network timeout and fallback both failed. Timeout: {ex.Message}, Fallback: {fallbackEx.Message}",
                    ProcessingTimeMs = responseTimeMs,
                    Success = false,
                    ErrorMessage = $"Operation failed: {fallbackEx.Message}",
                    CorrelationId = correlationId,
                };
            }
        }
        catch (OperationCanceledException)
        {
            // Don't attempt fallback for cancellation - just re-throw
            throw;
        }
        catch (Exception ex)
        {
            // Log at appropriate level based on error type
            var errorType = ClassifyErrorType(ex);
            if (errorType == "authentication")
            {
                _logger.LogError(
                    ex,
                    "Authentication error occurred in operation {OperationName}. CorrelationId: {CorrelationId}",
                    operationName,
                    correlationId
                );
            }
            else
            {
                _logger.LogWarning(
                    ex,
                    "Main operation {OperationName} failed, attempting fallback. CorrelationId: {CorrelationId}",
                    operationName,
                    correlationId
                );
            }

            // Record the failure
            RecordErrorMetrics(ex);

            // If we have a fallback operation, try it
            if (fallbackOperation != null)
            {
                // Ensure fallback completes within time limit (AC-4.1: 5 seconds)
                using var fallbackCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                fallbackCts.CancelAfter(_degradationConfig.FallbackTimeoutMs);

                try
                {
                    var fallbackResult = await fallbackOperation.Invoke().WaitAsync(fallbackCts.Token);
                    degradationReason = $"LLM operation failed: {ex.Message}. Fallback to rule-based segmentation.";

                    var responseTimeMs = stopwatch.Elapsed.TotalMilliseconds;

                    // Check performance degradation (AC-4.3: <20% degradation)
                    var performanceDegradation = CalculatePerformanceDegradation(responseTimeMs);

                    RecordOperationMetrics(responseTimeMs, success: true, usedFallback: true);

                    _logger.LogInformation(
                        "Fallback operation succeeded for {OperationName} in {ResponseTime}ms. CorrelationId: {CorrelationId}",
                        operationName,
                        responseTimeMs,
                        correlationId
                    );

                    return new ResilienceOperationResult<T>
                    {
                        Data = fallbackResult,
                        DegradedMode = true, // AC-4.2: degradedMode flag
                        QualityScore = _degradationConfig.RuleBasedQualityScore, // AC-4.2: quality score
                        StrategyUsed = "Rule-Based (Fallback)", // AC-4.2: strategy indication
                        DegradationReason = degradationReason, // AC-4.2: reasoning explanation
                        ProcessingTimeMs = responseTimeMs,
                        Success = true,
                        CorrelationId = correlationId,
                    };
                }
                catch (OperationCanceledException cancelEx) when (cancelEx.CancellationToken == fallbackCts.Token)
                {
                    // Fallback timed out - return failure result
                    _logger.LogError(
                        "Fallback operation timed out for {OperationName} after {TimeoutMs}ms. CorrelationId: {CorrelationId}",
                        operationName,
                        _degradationConfig.FallbackTimeoutMs,
                        correlationId
                    );

                    var responseTimeMs = stopwatch.Elapsed.TotalMilliseconds;
                    RecordOperationMetrics(responseTimeMs, success: false, usedFallback: true);

                    return new ResilienceOperationResult<T>
                    {
                        Data = null,
                        DegradedMode = true,
                        QualityScore = 0.0,
                        StrategyUsed = "Failed",
                        DegradationReason =
                            $"Main operation failed: {ex.Message}. Fallback operation timed out after {_degradationConfig.FallbackTimeoutMs}ms.",
                        ProcessingTimeMs = responseTimeMs,
                        Success = false,
                        ErrorMessage = $"Operation failed: Fallback operation timeout",
                        CorrelationId = correlationId,
                    };
                }
                catch (OperationCanceledException)
                {
                    // Don't convert cancellation to error - re-throw
                    throw;
                }
                catch (Exception fallbackEx)
                {
                    _logger.LogError(
                        fallbackEx,
                        "Fallback operation also failed for {OperationName}. CorrelationId: {CorrelationId}",
                        operationName,
                        correlationId
                    );

                    var responseTimeMs = stopwatch.Elapsed.TotalMilliseconds;
                    RecordOperationMetrics(responseTimeMs, success: false, usedFallback: true);
                    RecordErrorMetrics(fallbackEx);

                    return new ResilienceOperationResult<T>
                    {
                        Data = null,
                        DegradedMode = true,
                        QualityScore = 0.0,
                        StrategyUsed = "Failed",
                        DegradationReason =
                            $"Both main and fallback operations failed. Main: {ex.Message}, Fallback: {fallbackEx.Message}",
                        ProcessingTimeMs = responseTimeMs,
                        Success = false,
                        ErrorMessage = $"Operation failed: {fallbackEx.Message}",
                        CorrelationId = correlationId,
                    };
                }
            }
            else
            {
                // No fallback available
                var responseTimeMs = stopwatch.Elapsed.TotalMilliseconds;
                RecordOperationMetrics(responseTimeMs, success: false, usedFallback: false);

                return new ResilienceOperationResult<T>
                {
                    Data = null,
                    DegradedMode = false,
                    QualityScore = 0.0,
                    StrategyUsed = "Failed",
                    ProcessingTimeMs = responseTimeMs,
                    Success = false,
                    ErrorMessage = $"Operation failed: {ex.Message}",
                    CorrelationId = correlationId,
                };
            }
        }
    }

    /// <summary>
    /// Gets current error metrics for monitoring and alerting.
    /// Implements AC-5.3 from ErrorHandling-TestAcceptanceCriteria.
    /// </summary>
    public ErrorMetrics GetErrorMetrics()
    {
        lock (_metricsLock)
        {
            return _currentMetrics with
            {
                ResponseTimes = CalculateResponseTimePercentiles(),
                LastUpdated = DateTime.UtcNow,
            };
        }
    }

    /// <summary>
    /// Resets error metrics (for testing or maintenance).
    /// Implements AC-5.2 state cleanup.
    /// </summary>
    public void ResetMetrics()
    {
        lock (_metricsLock)
        {
            _currentMetrics = new ErrorMetrics();
            _recentOperations.Clear();

            _logger.LogInformation("Error metrics have been reset");
        }
    }

    /// <summary>
    /// Gets health status of the resilience service.
    /// Implements AC-5.1 service recovery detection.
    /// </summary>
    public ResilienceHealthStatus GetHealthStatus()
    {
        lock (_metricsLock)
        {
            var recentOps = _recentOperations.Where(op => op.Timestamp > DateTime.UtcNow.AddMinutes(-5)).ToList();
            var totalOps = recentOps.Count;

            if (totalOps == 0)
            {
                return new ResilienceHealthStatus
                {
                    IsHealthy = true,
                    OpenCircuitCount = 0,
                    FallbackUsageRate = 0,
                    AverageResponseTimeMs = 0,
                    ErrorRate = 0,
                };
            }

            var successfulOps = recentOps.Count(op => op.Success);
            var fallbackOps = recentOps.Count(op => op.UsedFallback);
            var errorRate = (double)(totalOps - successfulOps) / totalOps * 100;
            var fallbackRate = (double)fallbackOps / totalOps * 100;
            var avgResponseTime = recentOps.Average(op => op.ResponseTimeMs);

            // Health criteria: error rate < 10%, fallback rate < 30%, avg response time < 15s
            var isHealthy = errorRate < 10 && fallbackRate < 30 && avgResponseTime < 15000;

            return new ResilienceHealthStatus
            {
                IsHealthy = isHealthy,
                OpenCircuitCount = 0, // TODO: Get from circuit breaker service
                FallbackUsageRate = fallbackRate,
                AverageResponseTimeMs = avgResponseTime,
                ErrorRate = errorRate,
            };
        }
    }

    #region Private Helper Methods

    private async Task<T> ExecuteMainOperationAsync<T>(
        Func<Task<T>> operation,
        string operationName,
        string correlationId,
        CancellationToken cancellationToken
    )
        where T : class
    {
        // Combine circuit breaker and retry policy
        return await _circuitBreaker.ExecuteAsync(
            async () => await _retryPolicy.ExecuteAsync(operation, operationName, cancellationToken),
            operationName,
            cancellationToken
        );
    }

    private void RecordOperationMetrics(double responseTimeMs, bool success, bool usedFallback)
    {
        lock (_metricsLock)
        {
            _recentOperations.Add((DateTime.UtcNow, responseTimeMs, success, usedFallback));

            // Keep only recent operations to prevent memory growth (AC-5.2)
            if (_recentOperations.Count > MaxRecentOperations)
            {
                _recentOperations.RemoveRange(0, _recentOperations.Count - MaxRecentOperations);
            }

            if (usedFallback)
            {
                _currentMetrics = _currentMetrics with { FallbackUsageCount = _currentMetrics.FallbackUsageCount + 1 };
            }
        }
    }

    private void RecordErrorMetrics(Exception exception)
    {
        lock (_metricsLock)
        {
            var errorType = ClassifyErrorType(exception);
            var currentCount = _currentMetrics.ErrorCounts.GetValueOrDefault(errorType, 0);

            _currentMetrics = _currentMetrics with
            {
                ErrorCounts = _currentMetrics.ErrorCounts.SetItem(errorType, currentCount + 1),
            };
        }
    }

    private static string ClassifyErrorType(Exception exception)
    {
        return exception switch
        {
            HttpRequestException httpEx when httpEx.Message.Contains("timeout") => "timeout",
            HttpRequestException httpEx when httpEx.Message.Contains("429") => "rate_limit",
            HttpRequestException httpEx when httpEx.Message.Contains("503") => "service_unavailable",
            HttpRequestException httpEx when httpEx.Message.Contains("401") => "authentication",
            TaskCanceledException => "timeout",
            CircuitBreakerOpenException => "circuit_breaker_open",
            _ => "unknown",
        };
    }

    private ResponseTimePercentiles CalculateResponseTimePercentiles()
    {
        var recentTimes = _recentOperations
            .Where(op => op.Timestamp > DateTime.UtcNow.AddMinutes(-5))
            .Select(op => op.ResponseTimeMs)
            .OrderBy(t => t)
            .ToList();

        return recentTimes.Count == 0
            ? new ResponseTimePercentiles
            {
                P50 = 0,
                P95 = 0,
                P99 = 0,
                Average = 0,
                Min = 0,
                Max = 0,
            }
            : new ResponseTimePercentiles
            {
                P50 = CalculatePercentile(recentTimes, 0.5),
                P95 = CalculatePercentile(recentTimes, 0.95),
                P99 = CalculatePercentile(recentTimes, 0.99),
                Average = recentTimes.Average(),
                Min = recentTimes.Min(),
                Max = recentTimes.Max(),
            };
    }

    private static double CalculatePercentile(List<double> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0)
        {
            return 0;
        }

        var index = (int)Math.Ceiling(sortedValues.Count * percentile) - 1;
        index = Math.Max(0, Math.Min(index, sortedValues.Count - 1));

        return sortedValues[index];
    }

    private double CalculatePerformanceDegradation(double currentResponseTime)
    {
        lock (_metricsLock)
        {
            var baselineOps = _recentOperations
                .Where(op => !op.UsedFallback && op.Success && op.Timestamp > DateTime.UtcNow.AddMinutes(-10))
                .ToList();

            if (baselineOps.Count == 0)
            {
                return 0;
            }

            var baselineAverage = baselineOps.Average(op => op.ResponseTimeMs);
            return (currentResponseTime - baselineAverage) / baselineAverage;
        }
    }

    #endregion
}
