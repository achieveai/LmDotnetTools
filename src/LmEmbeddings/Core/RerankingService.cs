using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmEmbeddings.Interfaces;
using AchieveAi.LmDotnetTools.LmEmbeddings.Models;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmEmbeddings.Core;

/// <summary>
/// Concrete reranking service that integrates with Cohere's rerank API
/// </summary>
public class RerankingService : IRerankService, IDisposable
{
    private readonly string _endpoint;
    private readonly string _model;
    private readonly string _apiKey;
    private readonly HttpClient _httpClient;
    private readonly ILogger<RerankingService> _logger;
    private readonly bool _disposeHttpClient;

    private readonly int _maxRetries = 2;

    /// <summary>
    /// Initializes a new instance of the RerankingService class
    /// </summary>
    /// <param name="endpoint">The reranking API endpoint URL</param>
    /// <param name="model">The reranking model to use (e.g., "rerank-v3.5")</param>
    /// <param name="apiKey">The API key for authentication</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="httpClient">HTTP client instance (optional, will create one if not provided)</param>
    public RerankingService(
        string endpoint,
        string model,
        string apiKey,
        ILogger<RerankingService>? logger = null,
        HttpClient? httpClient = null
    )
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _apiKey = !string.IsNullOrWhiteSpace(apiKey)
            ? apiKey
            : throw new ArgumentException("API key cannot be null or empty", nameof(apiKey));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RerankingService>.Instance;

        if (httpClient != null)
        {
            _httpClient = httpClient;
            _disposeHttpClient = false;
        }
        else
        {
            _httpClient = new HttpClient();
            _disposeHttpClient = true;
        }

        // Configure HttpClient
        _httpClient.BaseAddress = new Uri(_endpoint);
        _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            _apiKey
        );
    }

    /// <summary>
    /// Initializes a new instance of the RerankingService class using RerankingOptions
    /// </summary>
    /// <param name="options">Reranking configuration options</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="httpClient">HTTP client instance (optional, will create one if not provided)</param>
    public RerankingService(
        RerankingOptions options,
        ILogger<RerankingService>? logger = null,
        HttpClient? httpClient = null
    )
        : this(
            endpoint: options?.BaseUrl ?? throw new ArgumentNullException(nameof(options)),
            model: options.DefaultModel,
            apiKey: string.IsNullOrWhiteSpace(options.ApiKey)
                ? throw new ArgumentException("API key cannot be null or empty", nameof(options.ApiKey))
                : options.ApiKey,
            logger: logger,
            httpClient: httpClient
        )
    {
        // Allow options to control retry count while keeping default behavior
        _maxRetries = options.MaxRetries;
    }

    /// <summary>
    /// Reranks documents based on their relevance to the provided query
    /// </summary>
    /// <param name="query">The query to rank documents against</param>
    /// <param name="documents">The documents to rerank</param>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    /// <returns>List of ranked documents ordered by relevance score (highest first)</returns>
    public async Task<List<RankedDocument>> RerankAsync(
        string query,
        IEnumerable<string> documents,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("Query cannot be null or empty", nameof(query));
        }

        ArgumentNullException.ThrowIfNull(documents);

        var docList = documents.ToList();
        if (docList.Count == 0)
        {
            throw new ArgumentException("Documents cannot be empty", nameof(documents));
        }

        return await ExecuteWithLinearRetryAsync(
            async (attemptNumber) =>
            {
                var requestPayload = new RerankRequest
                {
                    Model = _model,
                    Query = query,
                    Documents = [.. documents],
                    TopN = null, // Return all documents ranked
                };

                var json = JsonSerializer.Serialize(requestPayload);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync("/v1/rerank", content, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                    return ParseResponse(responseJson, docList);
                }

                if (!IsRetryableStatusCode(response.StatusCode))
                {
                    _ = response.EnsureSuccessStatusCode(); // This will throw
                }

                throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.StatusCode}");
            },
            cancellationToken
        );
    }

    /// <summary>
    /// Executes an operation with linear retry logic (500ms × retryCount)
    /// </summary>
    /// <typeparam name="T">The return type</typeparam>
    /// <param name="operation">The operation to execute</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result of the operation</returns>
    private async Task<T> ExecuteWithLinearRetryAsync<T>(
        Func<int, Task<T>> operation,
        CancellationToken cancellationToken = default
    )
    {
        var attempt = 1;
        Exception? lastException = null;

        while (attempt <= _maxRetries + 1) // default 2, total attempts = 3
        {
            try
            {
                return await operation(attempt);
            }
            catch (HttpRequestException ex) when (IsRetryableError(ex) && attempt <= _maxRetries)
            {
                lastException = ex;
                attempt++;

                // Linear backoff: 500ms × retryCount
                var delay = TimeSpan.FromMilliseconds(500 * (attempt - 1));
                _logger.LogWarning(
                    "Rerank request failed (attempt {Attempt}/{MaxAttempts}), retrying in {Delay}ms. Error: {Error}",
                    attempt - 1,
                    _maxRetries + 1,
                    delay.TotalMilliseconds,
                    ex.Message
                );

                await Task.Delay(delay, cancellationToken);
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException && attempt <= _maxRetries)
            {
                lastException = ex;
                attempt++;

                var delay = TimeSpan.FromMilliseconds(500 * (attempt - 1));
                _logger.LogWarning(
                    "Rerank request timed out (attempt {Attempt}/{MaxAttempts}), retrying in {Delay}ms",
                    attempt - 1,
                    _maxRetries + 1,
                    delay.TotalMilliseconds
                );

                await Task.Delay(delay, cancellationToken);
            }
        }

        throw lastException ?? new InvalidOperationException("Operation failed after all retry attempts");
    }

    /// <summary>
    /// Parses the Cohere API response into RankedDocument list
    /// </summary>
    /// <param name="responseJson">The JSON response from Cohere API</param>
    /// <param name="originalDocuments">The original documents for reference</param>
    /// <returns>List of ranked documents</returns>
    private List<RankedDocument> ParseResponse(string responseJson, IList<string> originalDocuments)
    {
        _logger.LogDebug("Parsing JSON response: {ResponseJson}", responseJson);

        try
        {
            var response = JsonSerializer.Deserialize<RerankResponse>(responseJson);

            if (response?.Results == null)
            {
                _logger.LogError("Invalid response structure. Response: {Response}", responseJson);
                throw new InvalidOperationException($"Invalid response from rerank API: missing results");
            }

            var rankedDocs = new List<RankedDocument>();

            foreach (var result in response.Results)
            {
                if (result.Index >= 0 && result.Index < originalDocuments.Count)
                {
                    rankedDocs.Add(
                        new RankedDocument
                        {
                            Index = result.Index,
                            Score = (float)result.RelevanceScore,
                            Document = originalDocuments[result.Index],
                        }
                    );
                }
                else
                {
                    _logger.LogWarning(
                        "Invalid document index {Index} for {DocumentCount} documents",
                        result.Index,
                        originalDocuments.Count
                    );
                }
            }

            // Sort by score descending (highest relevance first)
            rankedDocs.Sort((a, b) => b.Score.CompareTo(a.Score));

            _logger.LogDebug("Parsed {ResultCount} ranked documents from API response", rankedDocs.Count);

            return rankedDocs;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize JSON response: {ResponseJson}", responseJson);
            throw new InvalidOperationException($"Failed to parse rerank API response: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Determines if an HTTP status code is retryable
    /// </summary>
    /// <param name="statusCode">The HTTP status code</param>
    /// <returns>True if the status code indicates a retryable error</returns>
    private static bool IsRetryableStatusCode(HttpStatusCode statusCode)
    {
        return statusCode >= HttpStatusCode.InternalServerError
            || // 5xx errors
            statusCode == HttpStatusCode.TooManyRequests
            || // 429
            statusCode == HttpStatusCode.RequestTimeout; // 408
    }

    /// <summary>
    /// Determines if an HTTP error is retryable
    /// </summary>
    /// <param name="exception">The HTTP exception</param>
    /// <returns>True if the error is retryable</returns>
    private static bool IsRetryableError(HttpRequestException exception)
    {
        var message = exception.Message.ToLowerInvariant();
        return message.Contains("timeout")
            || message.Contains("network")
            || message.Contains('5')
            || // 5xx errors
            message.Contains("429"); // Rate limiting
    }

    /// <summary>
    /// Disposes the RerankingService instance
    /// </summary>
    public void Dispose()
    {
        if (_disposeHttpClient)
        {
            _httpClient?.Dispose();
        }
    }

    public async Task<RerankResponse> RerankAsync(RerankRequest request, CancellationToken cancellationToken = default)
    {
        var rankedDocuments = await RerankAsync(request.Query, request.Documents, cancellationToken);

        return new RerankResponse
        {
            Results = [.. rankedDocuments.Select(doc => new RerankResult { Index = doc.Index, RelevanceScore = doc.Score })],
        };
    }

    public async Task<RerankResponse> RerankAsync(
        string query,
        IReadOnlyList<string> documents,
        string model,
        int? topK = null,
        CancellationToken cancellationToken = default
    )
    {
        var documentList = documents.ToList();
        if (documentList.Count == 0)
        {
            return new RerankResponse { Results = ImmutableList<RerankResult>.Empty };
        }

        var rankedDocuments = await RerankAsync(query, documentList, cancellationToken);

        // Apply topK filtering if specified
        if (topK.HasValue && topK.Value > 0)
        {
            rankedDocuments = [.. rankedDocuments.Take(topK.Value)];
        }

        return new RerankResponse
        {
            Results = [.. rankedDocuments.Select(doc => new RerankResult { Index = doc.Index, RelevanceScore = doc.Score })],
        };
    }

    public Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<string>>(new List<string> { _model });
    }
}
