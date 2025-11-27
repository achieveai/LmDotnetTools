using System.Net;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmCore.Http;

/// <summary>
///     Utility class for HTTP retry logic with exponential backoff
///     Provides common retry functionality for HTTP operations
/// </summary>
public static class HttpRetryHelper
{
    /// <summary>
    ///     Executes an operation with retry logic and exponential backoff
    /// </summary>
    /// <typeparam name="T">The return type</typeparam>
    /// <param name="operation">The operation to execute</param>
    /// <param name="logger">Logger for retry attempts</param>
    /// <param name="maxRetries">Maximum number of retries</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <param name="checkDisposed">Optional function to check if object is disposed</param>
    /// <returns>The result of the operation</returns>
    public static async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> operation,
        ILogger logger,
        int maxRetries = 3,
        CancellationToken cancellationToken = default,
        Func<bool>? checkDisposed = null
    )
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(logger);
        if (checkDisposed?.Invoke() == true)
        {
            throw new ObjectDisposedException("Service has been disposed");
        }

        var attempt = 0;
        while (true)
        {
            try
            {
                return await operation();
            }
            catch (HttpRequestException ex) when (attempt < maxRetries && IsRetryableError(ex))
            {
                attempt++;
                var delay = CalculateDelay(attempt);
                logger.LogWarning(
                    "Request failed (attempt {Attempt}/{MaxRetries}), retrying in {Delay}ms: {Error}",
                    attempt,
                    maxRetries + 1,
                    delay.TotalMilliseconds,
                    ex.Message
                );

                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    /// <summary>
    ///     Executes an HTTP operation with retry logic and exponential backoff
    ///     This version handles HttpResponseMessage status codes directly
    /// </summary>
    /// <typeparam name="T">The return type</typeparam>
    /// <param name="httpOperation">The HTTP operation that returns HttpResponseMessage</param>
    /// <param name="responseProcessor">Function to process successful responses</param>
    /// <param name="logger">Logger for retry attempts</param>
    /// <param name="maxRetries">Maximum number of retries</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <param name="checkDisposed">Optional function to check if object is disposed</param>
    /// <returns>The result of the operation</returns>
    public static async Task<T> ExecuteHttpWithRetryAsync<T>(
        Func<Task<HttpResponseMessage>> httpOperation,
        Func<HttpResponseMessage, Task<T>> responseProcessor,
        ILogger logger,
        int maxRetries = 3,
        CancellationToken cancellationToken = default,
        Func<bool>? checkDisposed = null
    )
    {
        ArgumentNullException.ThrowIfNull(httpOperation);
        ArgumentNullException.ThrowIfNull(responseProcessor);
        ArgumentNullException.ThrowIfNull(logger);
        if (checkDisposed?.Invoke() == true)
        {
            throw new ObjectDisposedException("Service has been disposed");
        }

        var attempt = 0;
        while (true)
        {
            try
            {
                var response = await httpOperation();

                if (response.IsSuccessStatusCode)
                {
                    return await responseProcessor(response);
                }

                // Check if this is a retryable status code
                if (attempt < maxRetries && IsRetryableStatusCode(response.StatusCode))
                {
                    attempt++;
                    var delay = CalculateDelay(attempt);
                    logger.LogWarning(
                        "HTTP request failed with status {StatusCode} (attempt {Attempt}/{MaxRetries}), retrying in {Delay}ms",
                        response.StatusCode,
                        attempt,
                        maxRetries + 1,
                        delay.TotalMilliseconds
                    );

                    response.Dispose(); // Clean up the failed response
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                // Not retryable or max retries exceeded, throw
                try
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        // Try to read the response body for better error information
                        var responseBody = await response.Content.ReadAsStringAsync();
                        var errorMessage =
                            $"HTTP request failed with status {response.StatusCode} ({response.ReasonPhrase})";
                        if (!string.IsNullOrWhiteSpace(responseBody))
                        {
                            errorMessage += $". Response body: {responseBody}";
                        }

                        throw new HttpRequestException(errorMessage, null, response.StatusCode);
                    }
                }
                catch (Exception ex) when (ex is not HttpRequestException)
                {
                    // If reading response body fails, fall back to standard behavior
                    _ = response.EnsureSuccessStatusCode();
                }

                return default!; // This line should never be reached
            }
            catch (HttpRequestException ex) when (attempt < maxRetries && IsRetryableError(ex))
            {
                attempt++;
                var delay = CalculateDelay(attempt);
                logger.LogWarning(
                    "Request failed (attempt {Attempt}/{MaxRetries}), retrying in {Delay}ms: {Error}",
                    attempt,
                    maxRetries + 1,
                    delay.TotalMilliseconds,
                    ex.Message
                );

                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    /// <summary>
    ///     Determines if an HTTP status code is retryable
    /// </summary>
    /// <param name="statusCode">The HTTP status code</param>
    /// <returns>True if the status code indicates a retryable error</returns>
    public static bool IsRetryableStatusCode(HttpStatusCode statusCode)
    {
        // Retry on server errors (5xx) and rate limiting (429)
        return statusCode == HttpStatusCode.TooManyRequests || ((int)statusCode >= 500 && (int)statusCode < 600);
    }

    /// <summary>
    ///     Determines if an HTTP error is retryable
    ///     Uses comprehensive error detection logic
    /// </summary>
    /// <param name="exception">The HTTP exception</param>
    /// <returns>True if the error is retryable</returns>
    public static bool IsRetryableError(HttpRequestException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        // Retry on network errors, timeouts, and server errors (5xx)
        var message = exception.Message;

        // Check for network/timeout errors
        if (
            message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || message.Contains("network", StringComparison.OrdinalIgnoreCase)
        )
        {
            return true;
        }

        // Check for HTTP 5xx status codes in the exception message
        // EnsureSuccessStatusCode() creates messages like "Response status code does not indicate success: 500 (Internal Server Error)"
        return message.Contains("500", StringComparison.OrdinalIgnoreCase)
            || message.Contains("501", StringComparison.OrdinalIgnoreCase)
            || message.Contains("502", StringComparison.OrdinalIgnoreCase)
            || message.Contains("503", StringComparison.OrdinalIgnoreCase)
            || message.Contains("504", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Internal Server Error", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Bad Gateway", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Service Unavailable", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Gateway Timeout", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Calculates the delay for exponential backoff
    /// </summary>
    /// <param name="attempt">The current attempt number (1-based)</param>
    /// <returns>The delay for this attempt</returns>
    private static TimeSpan CalculateDelay(int attempt)
    {
        return TimeSpan.FromSeconds(Math.Pow(2, attempt));
    }
}
