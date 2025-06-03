using AchieveAi.LmDotnetTools.LmEmbeddings.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmEmbeddings.Core;

/// <summary>
/// Concrete reranking service that integrates with Cohere's rerank API
/// </summary>
public class RerankingService : IDisposable
{
    private readonly string _endpoint;
    private readonly string _model;
    private readonly string _apiKey;
    private readonly HttpClient _httpClient;
    private readonly ILogger<RerankingService> _logger;
    private readonly bool _disposeHttpClient;

    private const int MaxRetries = 2;
    private const int DocumentTruncationTokens = 1024;

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
        HttpClient? httpClient = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _apiKey = !string.IsNullOrWhiteSpace(apiKey) ? apiKey : throw new ArgumentException("API key cannot be null or empty", nameof(apiKey));
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
        _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
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
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Query cannot be null or empty", nameof(query));
        if (documents == null)
            throw new ArgumentNullException(nameof(documents));

        var docList = documents.ToList();
        if (docList.Count == 0)
            throw new ArgumentException("Documents cannot be empty", nameof(documents));

        return await ExecuteWithLinearRetryAsync(async (attemptNumber) =>
        {
            // Apply document truncation on retry attempts
            var processedDocs = attemptNumber > 1 ? TruncateDocuments(docList) : docList;

            var requestPayload = new RerankRequest
            {
                Model = _model,
                Query = query,
                Documents = processedDocs.ToImmutableList(),
                TopN = null, // Return all documents ranked
                MaxTokensPerDoc = attemptNumber > 1 ? DocumentTruncationTokens : null
            };

            var json = JsonSerializer.Serialize(requestPayload);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            _logger.LogDebug("Sending rerank request (attempt {Attempt}) with {DocumentCount} documents",
                attemptNumber, processedDocs.Count);

            var response = await _httpClient.PostAsync("/v2/rerank", content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                return ParseResponse(responseJson, docList);
            }

            if (!IsRetryableStatusCode(response.StatusCode))
            {
                response.EnsureSuccessStatusCode(); // This will throw
            }

            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.StatusCode}");
        }, cancellationToken);
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
        CancellationToken cancellationToken = default)
    {
        var attempt = 1;
        Exception? lastException = null;

        while (attempt <= MaxRetries + 1) // MaxRetries = 2, so total attempts = 3
        {
            try
            {
                return await operation(attempt);
            }
            catch (HttpRequestException ex) when (IsRetryableError(ex) && attempt <= MaxRetries)
            {
                lastException = ex;
                attempt++;

                // Linear backoff: 500ms × retryCount
                var delay = TimeSpan.FromMilliseconds(500 * (attempt - 1));
                _logger.LogWarning("Rerank request failed (attempt {Attempt}/{MaxAttempts}), retrying in {Delay}ms. Error: {Error}",
                    attempt - 1, MaxRetries + 1, delay.TotalMilliseconds, ex.Message);

                await Task.Delay(delay, cancellationToken);
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException && attempt <= MaxRetries)
            {
                lastException = ex;
                attempt++;

                var delay = TimeSpan.FromMilliseconds(500 * (attempt - 1));
                _logger.LogWarning("Rerank request timed out (attempt {Attempt}/{MaxAttempts}), retrying in {Delay}ms",
                    attempt - 1, MaxRetries + 1, delay.TotalMilliseconds);

                await Task.Delay(delay, cancellationToken);
            }
        }

        throw lastException ?? new InvalidOperationException("Operation failed after all retry attempts");
    }

    /// <summary>
    /// Truncates documents to approximately 1024 tokens for retry attempts
    /// </summary>
    /// <param name="documents">The original documents</param>
    /// <returns>Truncated documents</returns>
    private List<string> TruncateDocuments(IList<string> documents)
    {
        var truncated = new List<string>();
        
        foreach (var doc in documents)
        {
            if (doc.Length <= DocumentTruncationTokens * 4) // Rough approximation: 1 token ≈ 4 characters
            {
                truncated.Add(doc);
            }
            else
            {
                // Truncate at word boundary if possible
                var maxLength = DocumentTruncationTokens * 4;
                var truncatedText = doc.Substring(0, maxLength);
                var lastSpaceIndex = truncatedText.LastIndexOf(' ');
                
                if (lastSpaceIndex > maxLength * 0.8) // Only use word boundary if it's not too early
                {
                    truncatedText = truncatedText.Substring(0, lastSpaceIndex);
                }
                
                truncated.Add(truncatedText.Trim());
                _logger.LogDebug("Truncated document from {OriginalLength} to {TruncatedLength} characters",
                    doc.Length, truncatedText.Length);
            }
        }
        
        return truncated;
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
                    rankedDocs.Add(new RankedDocument
                    {
                        Index = result.Index,
                        Score = (float)result.RelevanceScore,
                        Document = originalDocuments[result.Index]
                    });
                }
                else
                {
                    _logger.LogWarning("Invalid document index {Index} for {DocumentCount} documents", result.Index, originalDocuments.Count);
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
        return statusCode >= HttpStatusCode.InternalServerError || // 5xx errors
               statusCode == HttpStatusCode.TooManyRequests ||      // 429
               statusCode == HttpStatusCode.RequestTimeout;         // 408
    }

    /// <summary>
    /// Determines if an HTTP error is retryable
    /// </summary>
    /// <param name="exception">The HTTP exception</param>
    /// <returns>True if the error is retryable</returns>
    private static bool IsRetryableError(HttpRequestException exception)
    {
        var message = exception.Message.ToLowerInvariant();
        return message.Contains("timeout") ||
               message.Contains("network") ||
               message.Contains("5") || // 5xx errors
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
} 