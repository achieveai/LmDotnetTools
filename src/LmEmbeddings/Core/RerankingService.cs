using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Http;
using AchieveAi.LmDotnetTools.LmEmbeddings.Interfaces;
using AchieveAi.LmDotnetTools.LmEmbeddings.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmEmbeddings.Core;

/// <summary>
///     Concrete reranking service that integrates with Cohere's rerank API
/// </summary>
public class RerankingService : IRerankService, IDisposable
{
    private readonly string _apiKey;
    private readonly bool _disposeHttpClient;
    private readonly string _endpoint;
    private readonly HttpClient _httpClient;
    private readonly ILogger<RerankingService> _logger;

    private readonly int _maxRetries = 2;
    private readonly string _model;

    /// <summary>
    ///     Initializes a new instance of the RerankingService class
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
        _logger = logger ?? NullLogger<RerankingService>.Instance;

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
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
    }

    /// <summary>
    ///     Initializes a new instance of the RerankingService class using RerankingOptions
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
            options?.BaseUrl ?? throw new ArgumentNullException(nameof(options)),
            options.DefaultModel,
            string.IsNullOrWhiteSpace(options.ApiKey)
                ? throw new ArgumentException("API key cannot be null or empty", nameof(options.ApiKey))
                : options.ApiKey,
            logger,
            httpClient
        )
    {
        // Allow options to control retry count while keeping default behavior
        _maxRetries = options.MaxRetries;
    }

    /// <summary>
    ///     Disposes the RerankingService instance
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
        ArgumentNullException.ThrowIfNull(request);

        var rankedDocuments = await RerankAsync(request.Query, request.Documents, cancellationToken);

        return new RerankResponse
        {
            Results =
            [
                .. rankedDocuments.Select(doc => new RerankResult { Index = doc.Index, RelevanceScore = doc.Score }),
            ],
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
            return new RerankResponse { Results = [] };
        }

        var rankedDocuments = await RerankAsync(query, documentList, cancellationToken);

        // Apply topK filtering if specified
        if (topK.HasValue && topK.Value > 0)
        {
            rankedDocuments = [.. rankedDocuments.Take(topK.Value)];
        }

        return new RerankResponse
        {
            Results =
            [
                .. rankedDocuments.Select(doc => new RerankResult { Index = doc.Index, RelevanceScore = doc.Score }),
            ],
        };
    }

    public Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<string>>([_model]);
    }

    /// <summary>
    ///     Approximate characters-per-token used to budget documents into batches that fit
    ///     within the reranker model's combined (query + all documents) context window.
    ///     The server enforces this on the whole request, not per document.
    /// </summary>
    private const double CharsPerTokenEstimate = 4.0;

    /// <summary>
    ///     Conservative token budget per rerank request, leaving headroom below typical
    ///     8192-token reranker context limits for tokenizer estimation error and query tokens.
    /// </summary>
    private const int MaxTokensPerBatch = 6000;

    /// <summary>
    ///     Reranks documents based on their relevance to the provided query
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

        var queryTokens = EstimateTokens(query);
        var batches = BuildTokenBudgetBatches(docList, queryTokens);

        if (batches.Count == 1)
        {
            return await RerankBatchAsync(query, docList, cancellationToken);
        }

        _logger.LogDebug(
            "Splitting {DocumentCount} documents into {BatchCount} rerank batches to stay within the model's context window",
            docList.Count,
            batches.Count
        );

        var allResults = new List<RankedDocument>();
        foreach (var batch in batches)
        {
            var batchDocs = batch.Select(originalIndex => docList[originalIndex]).ToList();
            var batchResults = await RerankBatchAsync(query, batchDocs, cancellationToken);

            allResults.AddRange(
                batchResults.Select(r => new RankedDocument
                {
                    Index = batch[r.Index],
                    Score = r.Score,
                    Document = r.Document,
                })
            );
        }

        allResults.Sort((a, b) => b.Score.CompareTo(a.Score));
        return allResults;
    }

    /// <summary>
    ///     Sends a single rerank request for a batch of documents that already fits within the
    ///     model's context window.
    /// </summary>
    private async Task<List<RankedDocument>> RerankBatchAsync(
        string query,
        IList<string> docList,
        CancellationToken cancellationToken
    )
    {
        return await ExecuteWithLinearRetryAsync(
            async attemptNumber =>
            {
                var requestPayload = new RerankRequest
                {
                    Model = _model,
                    Query = query,
                    Documents = [.. docList],
                    TopN = docList.Count, // Return all documents ranked
                };

                var json = JsonSerializer.Serialize(requestPayload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

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
    ///     Groups document indices into batches whose estimated combined token count (plus the
    ///     query) stays within <see cref="MaxTokensPerBatch" />. A single document that alone
    ///     exceeds the budget is still sent by itself, since it cannot be split further.
    /// </summary>
    private static List<List<int>> BuildTokenBudgetBatches(IReadOnlyList<string> docList, int queryTokens)
    {
        // Floor at 1 rather than at the first document's size: keying the floor off docList[0]
        // raises the ceiling for *every* batch whenever that one document happens to be large.
        // An oversized document is already sent by itself, because the split below only fires
        // once the current batch is non-empty.
        var budget = Math.Max(MaxTokensPerBatch - queryTokens, 1);
        var batches = new List<List<int>>();
        var currentBatch = new List<int>();
        var currentTokens = 0;

        for (var i = 0; i < docList.Count; i++)
        {
            var docTokens = EstimateTokens(docList[i]);

            if (currentBatch.Count > 0 && currentTokens + docTokens > budget)
            {
                batches.Add(currentBatch);
                currentBatch = [];
                currentTokens = 0;
            }

            currentBatch.Add(i);
            currentTokens += docTokens;
        }

        if (currentBatch.Count > 0)
        {
            batches.Add(currentBatch);
        }

        return batches;
    }

    private static int EstimateTokens(string text) => (int)Math.Ceiling(text.Length / CharsPerTokenEstimate);

    /// <summary>
    ///     Executes an operation with linear retry logic (500ms × retryCount)
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
            catch (HttpRequestException ex) when (HttpRetryHelper.IsRetryableError(ex) && attempt <= _maxRetries)
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
    ///     Parses the Cohere API response into RankedDocument list
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
                throw new InvalidOperationException("Invalid response from rerank API: missing results");
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
    ///     Determines if an HTTP status code is retryable
    /// </summary>
    /// <param name="statusCode">The HTTP status code</param>
    /// <returns>True if the status code indicates a retryable error</returns>
    private static bool IsRetryableStatusCode(HttpStatusCode statusCode)
    {
        return statusCode
            is >= HttpStatusCode.InternalServerError
                or // 5xx errors
                HttpStatusCode.TooManyRequests
                or // 429
                HttpStatusCode.RequestTimeout; // 408
    }
}
