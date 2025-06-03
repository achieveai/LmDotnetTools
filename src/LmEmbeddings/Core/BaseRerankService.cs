using AchieveAi.LmDotnetTools.LmEmbeddings.Interfaces;
using AchieveAi.LmDotnetTools.LmEmbeddings.Models;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmEmbeddings.Core;

/// <summary>
/// Base class for reranking services providing common functionality
/// </summary>
public abstract class BaseRerankService : IRerankService
{
    protected readonly ILogger Logger;
    protected readonly HttpClient HttpClient;

    protected BaseRerankService(ILogger logger, HttpClient httpClient)
    {
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <inheritdoc />
    public abstract Task<RerankResponse> RerankAsync(RerankRequest request, CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public virtual async Task<RerankResponse> RerankAsync(string query, IReadOnlyList<string> documents, string model, int? topK = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Query cannot be null or empty", nameof(query));
        if (documents == null || !documents.Any())
            throw new ArgumentException("Documents cannot be null or empty", nameof(documents));
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model cannot be null or empty", nameof(model));

        var request = new RerankRequest
        {
            Query = query,
            Documents = documents,
            Model = model,
            TopK = topK
        };

        return await RerankAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public abstract Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a rerank request
    /// </summary>
    /// <param name="request">The request to validate</param>
    /// <exception cref="ArgumentNullException">Thrown when request is null</exception>
    /// <exception cref="ArgumentException">Thrown when request is invalid</exception>
    protected virtual void ValidateRequest(RerankRequest request)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.Query))
            throw new ArgumentException("Query cannot be null or empty", nameof(request));
        if (request.Documents == null || !request.Documents.Any())
            throw new ArgumentException("Documents cannot be null or empty", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model cannot be null or empty", nameof(request));
        if (request.Documents.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("All documents must be non-empty", nameof(request));
        if (request.TopK.HasValue && request.TopK.Value <= 0)
            throw new ArgumentException("TopK must be positive", nameof(request));
    }

    /// <summary>
    /// Executes an operation with retry logic and exponential backoff
    /// </summary>
    /// <typeparam name="T">The return type</typeparam>
    /// <param name="operation">The operation to execute</param>
    /// <param name="maxRetries">Maximum number of retries</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result of the operation</returns>
    protected async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> operation,
        int maxRetries = 3,
        CancellationToken cancellationToken = default)
    {
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
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                Logger.LogWarning("Request failed (attempt {Attempt}/{MaxRetries}), retrying in {Delay}ms: {Error}",
                    attempt, maxRetries + 1, delay.TotalMilliseconds, ex.Message);
                
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Determines if an HTTP error is retryable
    /// </summary>
    /// <param name="exception">The HTTP exception</param>
    /// <returns>True if the error is retryable</returns>
    protected virtual bool IsRetryableError(HttpRequestException exception)
    {
        // Retry on network errors, timeouts, and server errors (5xx)
        return exception.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
               exception.Message.Contains("network", StringComparison.OrdinalIgnoreCase) ||
               exception.Message.Contains("5", StringComparison.OrdinalIgnoreCase);
    }
} 