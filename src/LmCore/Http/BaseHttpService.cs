using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmCore.Http;

/// <summary>
///     Base class for HTTP-based services providing common infrastructure
/// </summary>
public abstract class BaseHttpService : IDisposable
{
    /// <summary>
    ///     HTTP client for making API requests
    /// </summary>
    protected readonly HttpClient HttpClient;

    /// <summary>
    ///     Logger instance for the service
    /// </summary>
    protected readonly ILogger Logger;

    /// <summary>
    ///     Tracks whether the service has been disposed
    /// </summary>
    private bool _disposed;

    /// <summary>
    ///     Initializes a new instance of the BaseHttpService class
    /// </summary>
    /// <param name="logger">Logger instance for the service</param>
    /// <param name="httpClient">HTTP client for making API requests</param>
    /// <exception cref="ArgumentNullException">Thrown when logger or httpClient is null</exception>
    protected BaseHttpService(ILogger logger, HttpClient httpClient)
    {
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <summary>
    ///     Releases all resources used by the service
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Executes an operation with retry logic and exponential backoff
    /// </summary>
    /// <typeparam name="T">The return type</typeparam>
    /// <param name="operation">The operation to execute</param>
    /// <param name="retryOptions">Retry configuration options (defaults to RetryOptions.Default)</param>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    /// <returns>The result of the operation</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the service has been disposed</exception>
    protected async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> operation,
        RetryOptions? retryOptions = null,
        CancellationToken cancellationToken = default
    )
    {
        ThrowIfDisposed();

        return await HttpRetryHelper.ExecuteWithRetryAsync(operation, Logger, retryOptions, cancellationToken);
    }

    /// <summary>
    ///     Executes an operation with retry logic and exponential backoff (legacy overload)
    /// </summary>
    protected async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> operation,
        int maxRetries,
        CancellationToken cancellationToken = default
    )
    {
        ThrowIfDisposed();

        return await HttpRetryHelper.ExecuteWithRetryAsync(operation, Logger, maxRetries, cancellationToken);
    }

    /// <summary>
    ///     Executes an HTTP operation with retry logic for HTTP-specific scenarios
    /// </summary>
    /// <typeparam name="T">The return type</typeparam>
    /// <param name="httpOperation">The HTTP operation that returns an HttpResponseMessage</param>
    /// <param name="responseProcessor">Function to process the successful HTTP response</param>
    /// <param name="retryOptions">Retry configuration options (defaults to RetryOptions.Default)</param>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    /// <returns>The processed result from the HTTP response</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the service has been disposed</exception>
    protected async Task<T> ExecuteHttpWithRetryAsync<T>(
        Func<Task<HttpResponseMessage>> httpOperation,
        Func<HttpResponseMessage, Task<T>> responseProcessor,
        RetryOptions? retryOptions = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        return await HttpRetryHelper.ExecuteHttpWithRetryAsync(
            httpOperation,
            responseProcessor,
            Logger,
            retryOptions,
            cancellationToken
        );
    }

    /// <summary>
    ///     Executes an HTTP operation with retry logic (legacy overload)
    /// </summary>
    protected async Task<T> ExecuteHttpWithRetryAsync<T>(
        Func<Task<HttpResponseMessage>> httpOperation,
        Func<HttpResponseMessage, Task<T>> responseProcessor,
        int maxRetries,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        return await HttpRetryHelper.ExecuteHttpWithRetryAsync(
            httpOperation,
            responseProcessor,
            Logger,
            maxRetries,
            cancellationToken
        );
    }

    /// <summary>
    ///     Checks if the service has been disposed and throws an exception if it has
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when the service has been disposed</exception>
    protected void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(GetType().Name);
        }
    }

    /// <summary>
    ///     Releases the unmanaged resources used by the service and optionally releases the managed resources
    /// </summary>
    /// <param name="disposing">true to release both managed and unmanaged resources; false to release only unmanaged resources</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                // Dispose managed resources
                // Note: HttpClient is typically managed by DI container, so we don't dispose it here
                Logger.LogDebug("Disposing HTTP service: {ServiceType}", GetType().Name);
            }

            _disposed = true;
        }
    }
}
