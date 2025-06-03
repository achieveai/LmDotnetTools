using AchieveAi.LmDotnetTools.LmEmbeddings.Interfaces;
using AchieveAi.LmDotnetTools.LmEmbeddings.Models;
using LmEmbeddings.Models;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

namespace AchieveAi.LmDotnetTools.LmEmbeddings.Core;

/// <summary>
/// Base class for embedding services providing common functionality
/// </summary>
public abstract class BaseEmbeddingService : IEmbeddingService
{
    protected readonly ILogger Logger;
    protected readonly HttpClient HttpClient;
    private bool _disposed = false;

    protected BaseEmbeddingService(ILogger logger, HttpClient httpClient)
    {
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <inheritdoc />
    public abstract int EmbeddingSize { get; }

    /// <inheritdoc />
    public virtual async Task<float[]> GetEmbeddingAsync(string sentence, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sentence))
            throw new ArgumentException("Sentence cannot be null or empty", nameof(sentence));

        if (_disposed)
            throw new ObjectDisposedException(GetType().Name);

        // Use the first available model as default for the simple API
        var availableModels = await GetAvailableModelsAsync(cancellationToken);
        if (!availableModels.Any())
            throw new InvalidOperationException("No models available for embedding generation");

        var defaultModel = availableModels.First();
        var response = await GenerateEmbeddingAsync(sentence, defaultModel, cancellationToken);
        
        if (response.Embeddings?.Any() != true)
            throw new InvalidOperationException("No embeddings returned from service");

        return response.Embeddings.First().Vector;
    }

    /// <inheritdoc />
    public abstract Task<EmbeddingResponse> GenerateEmbeddingsAsync(EmbeddingRequest request, CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public virtual async Task<EmbeddingResponse> GenerateEmbeddingAsync(string text, string model, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text cannot be null or empty", nameof(text));
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model cannot be null or empty", nameof(model));

        if (_disposed)
            throw new ObjectDisposedException(GetType().Name);

        var request = new EmbeddingRequest
        {
            Inputs = new[] { text },
            Model = model
        };

        return await GenerateEmbeddingsAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public abstract Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Formats the request payload based on the API type
    /// </summary>
    /// <param name="request">The embedding request</param>
    /// <returns>The formatted request payload as a dictionary</returns>
    protected virtual Dictionary<string, object> FormatRequestPayload(EmbeddingRequest request)
    {
        ValidateRequest(request);

        return request.ApiType switch
        {
            EmbeddingApiType.Jina => FormatJinaRequest(request),
            EmbeddingApiType.Default => FormatOpenAIRequest(request),
            _ => throw new ArgumentException($"Unsupported API type: {request.ApiType}")
        };
    }

    /// <summary>
    /// Formats a request for the Jina AI API
    /// </summary>
    /// <param name="request">The embedding request</param>
    /// <returns>The formatted request payload</returns>
    protected virtual Dictionary<string, object> FormatJinaRequest(EmbeddingRequest request)
    {
        var payload = new Dictionary<string, object>
        {
            ["input"] = request.Inputs.ToArray(),
            ["model"] = request.Model
        };

        // Add Jina-specific parameters
        if (request.Normalized.HasValue)
            payload["normalized"] = request.Normalized.Value;

        if (!string.IsNullOrEmpty(request.EncodingFormat))
            payload["embedding_type"] = request.EncodingFormat;

        if (request.Dimensions.HasValue)
            payload["dimensions"] = request.Dimensions.Value;

        // Add any additional options
        if (request.AdditionalOptions != null)
        {
            foreach (var option in request.AdditionalOptions)
            {
                payload[option.Key] = option.Value;
            }
        }

        return payload;
    }

    /// <summary>
    /// Formats a request for the OpenAI API
    /// </summary>
    /// <param name="request">The embedding request</param>
    /// <returns>The formatted request payload</returns>
    protected virtual Dictionary<string, object> FormatOpenAIRequest(EmbeddingRequest request)
    {
        var payload = new Dictionary<string, object>
        {
            ["input"] = request.Inputs.ToArray(),
            ["model"] = request.Model
        };

        if (!string.IsNullOrEmpty(request.EncodingFormat))
            payload["encoding_format"] = request.EncodingFormat;

        if (request.Dimensions.HasValue)
            payload["dimensions"] = request.Dimensions.Value;

        if (!string.IsNullOrEmpty(request.User))
            payload["user"] = request.User;

        // Add any additional options
        if (request.AdditionalOptions != null)
        {
            foreach (var option in request.AdditionalOptions)
            {
                payload[option.Key] = option.Value;
            }
        }

        return payload;
    }

    /// <summary>
    /// Validates an embedding request
    /// </summary>
    /// <param name="request">The request to validate</param>
    /// <exception cref="ArgumentNullException">Thrown when request is null</exception>
    /// <exception cref="ArgumentException">Thrown when request is invalid</exception>
    protected virtual void ValidateRequest(EmbeddingRequest request)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));
        if (request.Inputs == null || !request.Inputs.Any())
            throw new ArgumentException("Inputs cannot be null or empty", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model cannot be null or empty", nameof(request));
        if (request.Inputs.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("All input texts must be non-empty", nameof(request));

        if (_disposed)
            throw new ObjectDisposedException(GetType().Name);

        // Validate API-specific parameters
        ValidateApiSpecificParameters(request);
    }

    /// <summary>
    /// Validates API-specific parameters
    /// </summary>
    /// <param name="request">The request to validate</param>
    protected virtual void ValidateApiSpecificParameters(EmbeddingRequest request)
    {
        switch (request.ApiType)
        {
            case EmbeddingApiType.Jina:
                ValidateJinaParameters(request);
                break;
            case EmbeddingApiType.Default:
                ValidateOpenAIParameters(request);
                break;
        }
    }

    /// <summary>
    /// Validates Jina AI specific parameters
    /// </summary>
    /// <param name="request">The request to validate</param>
    protected virtual void ValidateJinaParameters(EmbeddingRequest request)
    {
        if (!string.IsNullOrEmpty(request.EncodingFormat))
        {
            var validFormats = new[] { "float", "binary", "base64" };
            if (!validFormats.Contains(request.EncodingFormat, StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Invalid encoding format for Jina API: {request.EncodingFormat}. Valid formats: {string.Join(", ", validFormats)}");
            }
        }
    }

    /// <summary>
    /// Validates OpenAI specific parameters
    /// </summary>
    /// <param name="request">The request to validate</param>
    protected virtual void ValidateOpenAIParameters(EmbeddingRequest request)
    {
        if (!string.IsNullOrEmpty(request.EncodingFormat))
        {
            var validFormats = new[] { "float", "base64" };
            if (!validFormats.Contains(request.EncodingFormat, StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Invalid encoding format for OpenAI API: {request.EncodingFormat}. Valid formats: {string.Join(", ", validFormats)}");
            }
        }

        if (request.Normalized.HasValue)
        {
            Logger.LogWarning("Normalized parameter is not supported by OpenAI API and will be ignored");
        }
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
        if (_disposed)
            throw new ObjectDisposedException(GetType().Name);

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
    /// Executes an HTTP operation with retry logic and exponential backoff
    /// This version handles HttpResponseMessage status codes directly
    /// </summary>
    /// <typeparam name="T">The return type</typeparam>
    /// <param name="httpOperation">The HTTP operation that returns HttpResponseMessage</param>
    /// <param name="responseProcessor">Function to process successful responses</param>
    /// <param name="maxRetries">Maximum number of retries</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result of the operation</returns>
    protected async Task<T> ExecuteHttpWithRetryAsync<T>(
        Func<Task<HttpResponseMessage>> httpOperation,
        Func<HttpResponseMessage, Task<T>> responseProcessor,
        int maxRetries = 3,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(GetType().Name);

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
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    Logger.LogWarning("HTTP request failed with status {StatusCode} (attempt {Attempt}/{MaxRetries}), retrying in {Delay}ms",
                        response.StatusCode, attempt, maxRetries + 1, delay.TotalMilliseconds);
                    
                    response.Dispose(); // Clean up the failed response
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }
                
                // Not retryable or max retries exceeded, throw
                response.EnsureSuccessStatusCode();
                return default(T)!; // This line should never be reached
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
    /// Determines if an HTTP status code is retryable
    /// </summary>
    /// <param name="statusCode">The HTTP status code</param>
    /// <returns>True if the status code indicates a retryable error</returns>
    protected virtual bool IsRetryableStatusCode(HttpStatusCode statusCode)
    {
        // Retry on server errors (5xx)
        return (int)statusCode >= 500 && (int)statusCode < 600;
    }

    /// <summary>
    /// Determines if an HTTP error is retryable
    /// </summary>
    /// <param name="exception">The HTTP exception</param>
    /// <returns>True if the error is retryable</returns>
    protected virtual bool IsRetryableError(HttpRequestException exception)
    {
        // Retry on network errors, timeouts, and server errors (5xx)
        var message = exception.Message;
        
        // Check for network/timeout errors
        if (message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("network", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        
        // Check for HTTP 5xx status codes in the exception message
        // EnsureSuccessStatusCode() creates messages like "Response status code does not indicate success: 500 (Internal Server Error)"
        if (message.Contains("500", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("501", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("502", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("503", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("504", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Internal Server Error", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Bad Gateway", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Service Unavailable", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Gateway Timeout", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        
        return false;
    }

    /// <summary>
    /// Releases the unmanaged resources used by the service and optionally releases the managed resources
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
                Logger.LogDebug("Disposing embedding service");
            }

            _disposed = true;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
} 