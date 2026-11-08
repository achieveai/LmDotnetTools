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
    /// <param name="retryOptions">Retry configuration options (defaults to RetryOptions.Default)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <param name="checkDisposed">Optional function to check if object is disposed</param>
    /// <returns>The result of the operation</returns>
    public static async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> operation,
        ILogger logger,
        RetryOptions? retryOptions = null,
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

        var options = retryOptions ?? RetryOptions.Default;
        var attempt = 0;
        while (true)
        {
            try
            {
                return await operation();
            }
            catch (HttpRequestException ex) when (attempt < options.MaxRetries && IsRetryableError(ex, options))
            {
                attempt++;
                await WaitBeforeRetryAsync(ex, attempt, options, logger, cancellationToken);
            }
        }
    }

    /// <summary>
    ///     Executes an operation with retry logic and exponential backoff (legacy overload)
    /// </summary>
    public static Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> operation,
        ILogger logger,
        int maxRetries,
        CancellationToken cancellationToken = default,
        Func<bool>? checkDisposed = null
    )
    {
        var options = new RetryOptions { MaxRetries = maxRetries };
        return ExecuteWithRetryAsync(operation, logger, options, cancellationToken, checkDisposed);
    }

    /// <summary>
    ///     Executes an HTTP operation with retry logic and exponential backoff
    ///     This version handles HttpResponseMessage status codes directly
    /// </summary>
    /// <typeparam name="T">The return type</typeparam>
    /// <param name="httpOperation">The HTTP operation that returns HttpResponseMessage</param>
    /// <param name="responseProcessor">Function to process successful responses</param>
    /// <param name="logger">Logger for retry attempts</param>
    /// <param name="retryOptions">Retry configuration options (defaults to RetryOptions.Default)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <param name="checkDisposed">Optional function to check if object is disposed</param>
    /// <returns>The result of the operation</returns>
    public static async Task<T> ExecuteHttpWithRetryAsync<T>(
        Func<Task<HttpResponseMessage>> httpOperation,
        Func<HttpResponseMessage, Task<T>> responseProcessor,
        ILogger logger,
        RetryOptions? retryOptions = null,
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

        var options = retryOptions ?? RetryOptions.Default;
        var attempt = 0;
        while (true)
        {
            // Set when the server asked for a longer wait than this client honors. The failure thrown
            // for it must then not be retried by the transport catch below either.
            var retryRefused = false;
            try
            {
                var response = await httpOperation();

                if (response.IsSuccessStatusCode)
                {
                    return await responseProcessor(response);
                }

                // Retryable under THESE options: the global 429/5xx set, widened by any opt-in
                // AdditionalRetryableStatusCodes this caller configured.
                if (attempt >= options.MaxRetries || !options.IsRetryableStatusCode(response.StatusCode))
                {
                    throw await FailedResponseExceptionAsync(response, cancellationToken).ConfigureAwait(false);
                }

                // The server's own Retry-After wins over backoff, up to the honored cap.
                var retryAfter = RetryAfterHeader.Read(response, options.TimeProvider);
                var cap = HonoredRetryAfterCap(options);
                if (retryAfter > cap)
                {
                    retryRefused = true;
                    throw RefusedRetryException(response, retryAfter.Value, cap, logger);
                }

                attempt++;
                var delay = retryAfter ?? options.CalculateDelay(attempt);
                logger.LogWarning(
                    "HTTP request failed with status {StatusCode} (attempt {Attempt}/{MaxRetries}), retrying in {Delay}ms (Retry-After honored: {FromRetryAfter})",
                    response.StatusCode,
                    attempt,
                    options.MaxAttempts,
                    delay.TotalMilliseconds,
                    retryAfter is not null
                );
                HttpRetryScope.NotifyRetry(options, attempt, response.StatusCode, delay, retryAfter is not null);

                response.Dispose(); // Clean up the failed response
                await Task.Delay(delay, options.TimeProvider, cancellationToken);
            }
            catch (HttpRequestException ex)
                when (!retryRefused && attempt < options.MaxRetries && IsRetryableError(ex, options))
            {
                attempt++;
                await WaitBeforeRetryAsync(ex, attempt, options, logger, cancellationToken);
            }
        }
    }

    /// <summary>
    ///     The longest server-requested wait this client will sit out: <see cref="RetryOptions.MaxRetryAfter" />,
    ///     but never longer than a timer can wait (<see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)" />
    ///     rejects anything past about 49.7 days), so an accepted wait can always be waited.
    /// </summary>
    private static TimeSpan HonoredRetryAfterCap(RetryOptions options)
    {
        var timerLimit = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
        return options.MaxRetryAfter < timerLimit ? options.MaxRetryAfter : timerLimit;
    }

    /// <summary>
    ///     The failure for a response whose requested wait exceeds <paramref name="cap" />. It fails at once:
    ///     the error body is NOT read, because a server that stalls the body would otherwise hold up the
    ///     fast failure this refusal exists to give.
    /// </summary>
    private static HttpRequestException RefusedRetryException(
        HttpResponseMessage response,
        TimeSpan retryAfter,
        TimeSpan cap,
        ILogger logger
    )
    {
        var statusCode = response.StatusCode;
        var reasonPhrase = response.ReasonPhrase;
        response.Dispose();

        logger.LogWarning(
            "HTTP request failed with status {StatusCode}; Retry-After {RetryAfter}ms exceeds the {MaxRetryAfter}ms cap, failing without retry",
            statusCode,
            retryAfter.TotalMilliseconds,
            cap.TotalMilliseconds
        );

        return new HttpRequestException(
            $"HTTP request failed with status {statusCode} ({reasonPhrase}). Retry-After of {retryAfter.TotalSeconds:0.###}s exceeds the {cap.TotalSeconds:0.###}s cap",
            null,
            statusCode
        );
    }

    /// <summary>
    ///     The failure for a response that is not retried: a non-retryable status, or the retry budget is
    ///     spent. Captures the diagnostics, reads the body for the message, and disposes the response so the
    ///     connection is not leaked. (Only the success path keeps a response alive, for the caller.)
    /// </summary>
    private static async Task<HttpRequestException> FailedResponseExceptionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken
    )
    {
        var statusCode = response.StatusCode;
        var reasonPhrase = response.ReasonPhrase;
        string responseBody;
        try
        {
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed body read is diagnostic-only: fall back to status-only diagnostics and throw
            // with the ALREADY-CAPTURED original status below. This deliberately also swallows an
            // HttpRequestException raised by the body read itself — otherwise it would escape to the
            // outer retry catch and could retry a non-retryable status (or mask the real status).
            // Deliberate cancellation is NOT swallowed — it must surface as cancellation.
            responseBody = string.Empty;
        }
        finally
        {
            response.Dispose();
        }

        var errorMessage = $"HTTP request failed with status {statusCode} ({reasonPhrase})";
        if (!string.IsNullOrWhiteSpace(responseBody))
        {
            errorMessage += $". Response body: {responseBody}";
        }

        return new HttpRequestException(errorMessage, null, statusCode);
    }

    /// <summary>
    ///     Backoff wait for a failure with no response to read a Retry-After from: a transport failure,
    ///     or an exception the operation threw itself.
    /// </summary>
    private static async Task WaitBeforeRetryAsync(
        HttpRequestException ex,
        int attempt,
        RetryOptions options,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        var delay = options.CalculateDelay(attempt);
        logger.LogWarning(
            "Request failed (attempt {Attempt}/{MaxRetries}), retrying in {Delay}ms: {Error}",
            attempt,
            options.MaxAttempts,
            delay.TotalMilliseconds,
            ex.Message
        );
        HttpRetryScope.NotifyRetry(options, attempt, ex.StatusCode, delay, fromRetryAfter: false);

        await Task.Delay(delay, options.TimeProvider, cancellationToken);
    }

    /// <summary>
    ///     Executes an HTTP operation with retry logic (legacy overload)
    /// </summary>
    public static Task<T> ExecuteHttpWithRetryAsync<T>(
        Func<Task<HttpResponseMessage>> httpOperation,
        Func<HttpResponseMessage, Task<T>> responseProcessor,
        ILogger logger,
        int maxRetries,
        CancellationToken cancellationToken = default,
        Func<bool>? checkDisposed = null
    )
    {
        var options = new RetryOptions { MaxRetries = maxRetries };
        return ExecuteHttpWithRetryAsync(
            httpOperation,
            responseProcessor,
            logger,
            options,
            cancellationToken,
            checkDisposed
        );
    }

    /// <summary>
    ///     Determines if an HTTP status code is retryable under the GLOBAL classification (429 + 5xx).
    ///     A single transport can widen this for itself via
    ///     <see cref="RetryOptions.AdditionalRetryableStatusCodes" />; use
    ///     <see cref="RetryOptions.IsRetryableStatusCode(HttpStatusCode)" /> when options are in hand.
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
    public static bool IsRetryableError(HttpRequestException exception) => IsRetryableError(exception, null);

    /// <summary>
    ///     Determines if an HTTP error is retryable under <paramref name="retryOptions" />, whose
    ///     <see cref="RetryOptions.AdditionalRetryableStatusCodes" /> may widen the global 429/5xx set.
    ///     Passing <see langword="null" /> uses <see cref="RetryOptions.Default" /> (global set only).
    /// </summary>
    /// <param name="exception">The HTTP exception</param>
    /// <param name="retryOptions">Retry configuration whose retryable-status set applies</param>
    /// <returns>True if the error is retryable</returns>
    public static bool IsRetryableError(HttpRequestException exception, RetryOptions? retryOptions)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // When the exception carries an HTTP status code, classify strictly by that status. This avoids
        // wrongly retrying a non-retryable status (e.g. 400/401) just because its response body, which the
        // helper embeds in the exception message, happens to mention "500"/"timeout"/etc.
        if (exception.StatusCode is { } statusCode)
        {
            return (retryOptions ?? RetryOptions.Default).IsRetryableStatusCode(statusCode);
        }

        // No status code is present (genuine network/transport failures from SendAsync). Fall back to
        // substring detection over the message: network errors, timeouts, connection errors, and 5xx/429.
        var message = exception.Message;

        // Check for network/timeout errors
        if (
            message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || message.Contains("network", StringComparison.OrdinalIgnoreCase)
        )
        {
            return true;
        }

        // Check for connection errors (often wrapped in HttpRequestException)
        // These include "response ended prematurely", "connection was closed", etc.
        if (
            message.Contains("response ended prematurely", StringComparison.OrdinalIgnoreCase)
            || message.Contains("ResponseEnded", StringComparison.OrdinalIgnoreCase)
            || message.Contains("connection was closed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("An error occurred while sending the request", StringComparison.OrdinalIgnoreCase)
        )
        {
            return true;
        }

        // Check inner exception for HttpIOException or similar connection errors
        if (exception.InnerException != null)
        {
            var innerMessage = exception.InnerException.Message;
            if (
                innerMessage.Contains("response ended prematurely", StringComparison.OrdinalIgnoreCase)
                || innerMessage.Contains("ResponseEnded", StringComparison.OrdinalIgnoreCase)
                || innerMessage.Contains("connection was closed", StringComparison.OrdinalIgnoreCase)
            )
            {
                return true;
            }
        }

        // Check for rate limiting (429)
        if (
            message.Contains("429", StringComparison.OrdinalIgnoreCase)
            || message.Contains("TooManyRequests", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase)
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
}
