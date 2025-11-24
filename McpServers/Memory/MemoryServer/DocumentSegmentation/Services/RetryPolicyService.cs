using System.Collections.Immutable;
using System.Net.Sockets;
using System.Text.Json;
using MemoryServer.DocumentSegmentation.Models;

namespace MemoryServer.DocumentSegmentation.Services;

/// <summary>
/// Interface for retry policy functionality.
/// Implements AC-3.1, AC-3.2, AC-3.3, and AC-3.4 from ErrorHandling-TestAcceptanceCriteria.
/// </summary>
public interface IRetryPolicyService
{
    /// <summary>
    /// Executes an operation with retry policy.
    /// </summary>
    Task<T> ExecuteAsync<T>(
        Func<Task<T>> operation,
        string operationName,
        CancellationToken cancellationToken = default
    )
        where T : class;

    /// <summary>
    /// Executes an operation with retry policy, allowing null results.
    /// </summary>
    Task<T?> ExecuteWithNullAsync<T>(
        Func<Task<T?>> operation,
        string operationName,
        CancellationToken cancellationToken = default
    )
        where T : class;

    /// <summary>
    /// Determines if an error should be retried.
    /// </summary>
    bool ShouldRetry(Exception exception, int attemptNumber);

    /// <summary>
    /// Calculates the delay for the next retry attempt.
    /// </summary>
    TimeSpan CalculateDelay(int attemptNumber, Exception? lastException = null);
}

/// <summary>
/// Implementation of retry policy service with exponential backoff and jitter.
/// Provides centralized retry logic for all operations that may fail transiently.
/// </summary>
public class RetryPolicyService : IRetryPolicyService
{
    private readonly RetryConfiguration _configuration;
    private readonly ILogger<RetryPolicyService> _logger;

    public RetryPolicyService(RetryConfiguration configuration, ILogger<RetryPolicyService> logger)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Executes an operation with retry policy.
    /// Implements AC-3.1, AC-3.2, AC-3.3, and AC-3.4.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(
        Func<Task<T>> operation,
        string operationName,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        ArgumentNullException.ThrowIfNull(operation);

        var context = new RetryContext
        {
            AttemptNumber = 1,
            MaxAttempts = _configuration.MaxRetries + 1, // +1 for initial attempt
            CorrelationId = Guid.NewGuid().ToString(),
            RequestParameters = ExtractRequestParameters(),
            TotalElapsed = TimeSpan.Zero,
        };

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        Exception? lastException = null;

        while (context.AttemptNumber <= context.MaxAttempts)
        {
            try
            {
                _logger.LogDebug(
                    "Executing {OperationName}, attempt {Attempt}/{MaxAttempts}. CorrelationId: {CorrelationId}",
                    operationName,
                    context.AttemptNumber,
                    context.MaxAttempts,
                    context.CorrelationId
                );

                var result = await operation();

                if (context.AttemptNumber > 1)
                {
                    _logger.LogInformation(
                        "Operation {OperationName} succeeded on attempt {Attempt} after {ElapsedMs}ms. CorrelationId: {CorrelationId}",
                        operationName,
                        context.AttemptNumber,
                        stopwatch.ElapsedMilliseconds,
                        context.CorrelationId
                    );
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug(
                    "Operation {OperationName} was cancelled on attempt {Attempt}. CorrelationId: {CorrelationId}",
                    operationName,
                    context.AttemptNumber,
                    context.CorrelationId
                );
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                var errorType = ClassifyError(ex);

                context = context with { LastError = ex, ErrorType = errorType, TotalElapsed = stopwatch.Elapsed };

                if (!ShouldRetry(ex, context.AttemptNumber))
                {
                    _logger.LogError(
                        ex,
                        "Non-retryable error in {OperationName} on attempt {Attempt}. CorrelationId: {CorrelationId}",
                        operationName,
                        context.AttemptNumber,
                        context.CorrelationId
                    );
                    throw;
                }

                if (context.AttemptNumber >= context.MaxAttempts)
                {
                    _logger.LogError(
                        ex,
                        "All {MaxAttempts} retry attempts failed for {OperationName}. Total elapsed: {ElapsedMs}ms. CorrelationId: {CorrelationId}",
                        context.MaxAttempts,
                        operationName,
                        stopwatch.ElapsedMilliseconds,
                        context.CorrelationId
                    );
                    throw;
                }

                var delay = CalculateDelay(context.AttemptNumber, ex);

                _logger.LogWarning(
                    ex,
                    "Attempt {Attempt} failed for {OperationName}. Retrying in {DelayMs}ms. CorrelationId: {CorrelationId}",
                    context.AttemptNumber,
                    operationName,
                    delay.TotalMilliseconds,
                    context.CorrelationId
                );

                await Task.Delay(delay, cancellationToken);

                context = context with { AttemptNumber = context.AttemptNumber + 1 };
            }
        }

        // This should never be reached due to the logic above, but compiler requires it
        throw lastException ?? new InvalidOperationException("Retry logic error");
    }

    /// <summary>
    /// Executes an operation with retry policy, allowing null results.
    /// </summary>
    public async Task<T?> ExecuteWithNullAsync<T>(
        Func<Task<T?>> operation,
        string operationName,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        try
        {
            return await ExecuteAsync(
                async () =>
                {
                    var result = await operation();
                    return result ?? throw new InvalidOperationException("Operation returned null");
                },
                operationName,
                cancellationToken
            );
        }
        catch (InvalidOperationException ex) when (ex.Message == "Operation returned null")
        {
            _logger.LogWarning("Operation {OperationName} returned null after all retry attempts", operationName);
            return null;
        }
    }

    /// <summary>
    /// Determines if an error should be retried.
    /// Implements AC-3.1 retry count and non-retryable error logic.
    /// </summary>
    public bool ShouldRetry(Exception exception, int attemptNumber)
    {
        var errorType = ClassifyError(exception);
        var errorCode = GetErrorCode(exception);

        // Check if this error type is non-retryable
        if (_configuration.NonRetryableErrors.Contains(errorCode))
        {
            _logger.LogDebug("Error {ErrorCode} is non-retryable", errorCode);
            return false;
        }

        // Special handling for rate limiting - should be retried
        if (errorCode == "429")
        {
            var maxRetries = _configuration.ErrorTypeRetries.GetValueOrDefault("429", _configuration.MaxRetries);
            return attemptNumber < maxRetries + 1;
        }

        // Check error-specific retry limits
        if (
            _configuration.ErrorTypeRetries.TryGetValue(
                errorType.ToString().ToLowerInvariant(),
                out var typeSpecificLimit
            )
        )
        {
            return attemptNumber < typeSpecificLimit + 1;
        }

        // Default retry logic
        return attemptNumber < _configuration.MaxRetries + 1;
    }

    /// <summary>
    /// Calculates the delay for the next retry attempt.
    /// Implements AC-3.2 exponential backoff and AC-3.3 jitter.
    /// </summary>
    public TimeSpan CalculateDelay(int attemptNumber, Exception? lastException = null)
    {
        // Handle special case for rate limiting with Retry-After header
        if (lastException is HttpRequestException httpEx && httpEx.Message.Contains("429"))
        {
            var retryAfter = ExtractRetryAfterHeader(httpEx);
            if (retryAfter.HasValue)
            {
                _logger.LogDebug(
                    "Using Retry-After header value: {RetryAfterMs}ms",
                    retryAfter.Value.TotalMilliseconds
                );
                return retryAfter.Value;
            }
        }

        // Calculate exponential backoff delay
        var delay = TimeSpan.FromMilliseconds(
            _configuration.BaseDelayMs * Math.Pow(_configuration.ExponentialFactor, attemptNumber - 1)
        );

        // Apply jitter to prevent thundering herd (±10% as per AC-3.3)
        var jitterRange = delay.TotalMilliseconds * _configuration.JitterPercent;
        var jitter = (Random.Shared.NextDouble() - 0.5) * 2 * jitterRange; // Random between -jitterRange and +jitterRange

        delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds + jitter);

        // Cap at maximum delay
        if (delay.TotalMilliseconds > _configuration.MaxDelayMs)
        {
            delay = TimeSpan.FromMilliseconds(_configuration.MaxDelayMs);
        }

        // Ensure minimum delay
        if (delay.TotalMilliseconds < 100)
        {
            delay = TimeSpan.FromMilliseconds(100);
        }

        _logger.LogDebug(
            "Calculated retry delay for attempt {Attempt}: {DelayMs}ms",
            attemptNumber,
            delay.TotalMilliseconds
        );

        return delay;
    }

    #region Private Helper Methods

    private static ErrorType ClassifyError(Exception exception)
    {
        return exception switch
        {
            HttpRequestException httpEx when httpEx.Message.Contains("timeout") => ErrorType.NetworkTimeout,
            HttpRequestException httpEx when httpEx.Message.Contains("429") => ErrorType.RateLimit,
            HttpRequestException httpEx when httpEx.Message.Contains("401") => ErrorType.Authentication,
            HttpRequestException httpEx when httpEx.Message.Contains("503") => ErrorType.ServiceUnavailable,
            TaskCanceledException => ErrorType.NetworkTimeout,
            ArgumentException => ErrorType.MalformedResponse,
            JsonException => ErrorType.MalformedResponse,
            SocketException => ErrorType.ConnectionFailure,
            _ => ErrorType.Unknown,
        };
    }

    private static string GetErrorCode(Exception exception)
    {
        return exception switch
        {
            HttpRequestException httpEx when httpEx.Message.Contains("401") => "401",
            HttpRequestException httpEx when httpEx.Message.Contains("400") => "400",
            HttpRequestException httpEx when httpEx.Message.Contains("403") => "403",
            HttpRequestException httpEx when httpEx.Message.Contains("429") => "429",
            HttpRequestException httpEx when httpEx.Message.Contains("503") => "503",
            TaskCanceledException => "timeout",
            _ => "generic",
        };
    }

    private static TimeSpan? ExtractRetryAfterHeader(HttpRequestException httpException)
    {
        // In a real implementation, this would parse the Retry-After header from the HTTP response
        // For now, return null to use exponential backoff
        // TODO: Implement actual header parsing when integrated with HTTP client
        return null;
    }

    private static ImmutableDictionary<string, object> ExtractRequestParameters()
    {
        // In a real implementation, this would capture the original request parameters
        // For now, return empty dictionary
        // TODO: Implement parameter extraction when integrated with actual operations
        return ImmutableDictionary<string, object>.Empty;
    }

    #endregion
}

/// <summary>
/// Extension methods for easier retry policy usage.
/// </summary>
public static class RetryPolicyExtensions
{
    /// <summary>
    /// Executes an operation with the default retry policy.
    /// </summary>
    public static async Task<T> WithRetryAsync<T>(
        this IRetryPolicyService retryService,
        Func<Task<T>> operation,
        string operationName,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        ArgumentNullException.ThrowIfNull(retryService);
        return await retryService.ExecuteAsync(operation, operationName, cancellationToken);
    }

    /// <summary>
    /// Executes an operation with retry policy, allowing null results.
    /// </summary>
    public static async Task<T?> WithRetryOrNullAsync<T>(
        this IRetryPolicyService retryService,
        Func<Task<T?>> operation,
        string operationName,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        ArgumentNullException.ThrowIfNull(retryService);
        return await retryService.ExecuteWithNullAsync(operation, operationName, cancellationToken);
    }
}
