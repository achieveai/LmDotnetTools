using System.Collections.Concurrent;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Http;
using AchieveAi.LmDotnetTools.LmCore.Validation;
using AchieveAi.LmDotnetTools.LmEmbeddings.Models;
using LmEmbeddings.Models;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmEmbeddings.Core;

/// <summary>
/// Generic server-based embedding service that supports batch processing and multiple API types.
/// This implementation provides efficient batch processing, automatic text chunking, and support
/// for both OpenAI and Jina AI API formats.
/// </summary>
/// <remarks>
/// <para>
/// This service is designed for high-throughput embedding generation with the following features:
/// </para>
/// <list type="bullet">
/// <item><description>Automatic batch processing with configurable batch sizes</description></item>
/// <item><description>Text chunking for inputs exceeding maximum length limits</description></item>
/// <item><description>Support for multiple API formats (OpenAI, Jina AI)</description></item>
/// <item><description>Configurable timeout and retry mechanisms</description></item>
/// <item><description>Efficient queue-based processing for individual requests</description></item>
/// </list>
/// <para>
/// The service automatically batches individual embedding requests for improved efficiency
/// and provides both individual and batch API methods for different use cases.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Basic configuration for OpenAI
/// var service = new ServerEmbeddings(
///     endpoint: "https://api.openai.com",
///     model: Environment.GetEnvironmentVariable("EMBEDDING_MODEL") ?? "text-embedding-3-small",
///     embeddingSize: 1536,
///     apiKey: Environment.GetEnvironmentVariable("EMBEDDING_API_KEY"));
///
/// // Advanced configuration with custom settings
/// var service = new ServerEmbeddings(
///     endpoint: "https://api.jina.ai",
///     model: Environment.GetEnvironmentVariable("EMBEDDING_MODEL") ?? "jina-embeddings-v2-base-en",
///     embeddingSize: 768,
///     apiKey: Environment.GetEnvironmentVariable("JINA_API_KEY"),
///     maxBatchSize: 50,
///     apiType: EmbeddingApiType.Jina,
///     logger: loggerFactory.CreateLogger&lt;ServerEmbeddings&gt;(),
///     httpClient: customHttpClient);
/// </code>
/// </example>
public class ServerEmbeddings : BaseEmbeddingService
{
    private readonly string _endpoint;
    private readonly string _model;
    private readonly int _embeddingSize;
    private readonly string _apiKey;
    private readonly int _maxBatchSize;
    private readonly EmbeddingApiType _apiType;
    private readonly ConcurrentQueue<BatchRequest> _batchQueue;
    private readonly Timer _batchTimer;
    private readonly object _batchLock = new object();
    private const int BatchTimeoutMs = 100; // 100ms batch timeout
    private const int MaxTextLength = 8192; // Maximum text length before chunking

    /// <summary>
    /// Initializes a new instance of the <see cref="ServerEmbeddings"/> class with comprehensive configuration options.
    /// </summary>
    /// <param name="endpoint">The API endpoint URL for the embedding service</param>
    /// <param name="model">The embedding model identifier to use for all requests</param>
    /// <param name="embeddingSize">The dimensionality of the embedding vectors produced by the model</param>
    /// <param name="apiKey">The API key for authentication with the embedding service</param>
    /// <param name="maxBatchSize">Maximum number of texts to process in a single batch request (default: 100)</param>
    /// <param name="apiType">The API format type to use (OpenAI or Jina AI, default: OpenAI)</param>
    /// <param name="logger">Logger instance for diagnostic and error logging (optional)</param>
    /// <param name="httpClient">HTTP client instance for making API requests (optional, will create new if not provided)</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="endpoint"/>, <paramref name="model"/>, or <paramref name="apiKey"/> is null</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="embeddingSize"/>, <paramref name="maxBatchSize"/> is not positive, or <paramref name="apiKey"/> is empty</exception>
    /// <remarks>
    /// <para>
    /// This constructor sets up the service with the following default behaviors:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Batch timeout of 100ms for efficient request aggregation</description></item>
    /// <item><description>Maximum text length of 8192 characters before chunking</description></item>
    /// <item><description>Automatic HTTP client configuration with Bearer token authentication</description></item>
    /// <item><description>Background timer for processing batched requests</description></item>
    /// </list>
    /// <para>
    /// The service will automatically configure the HTTP client with the provided endpoint
    /// and API key. If no HTTP client is provided, a new one will be created.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Basic configuration for OpenAI
    /// var service = new ServerEmbeddings(
    ///     endpoint: "https://api.openai.com",
    ///     model: Environment.GetEnvironmentVariable("EMBEDDING_MODEL") ?? "text-embedding-3-small",
    ///     embeddingSize: 1536,
    ///     apiKey: Environment.GetEnvironmentVariable("EMBEDDING_API_KEY"));
    ///
    /// // Advanced configuration with custom settings
    /// var service = new ServerEmbeddings(
    ///     endpoint: "https://api.jina.ai",
    ///     model: Environment.GetEnvironmentVariable("EMBEDDING_MODEL") ?? "jina-embeddings-v2-base-en",
    ///     embeddingSize: 768,
    ///     apiKey: Environment.GetEnvironmentVariable("JINA_API_KEY"),
    ///     maxBatchSize: 50,
    ///     apiType: EmbeddingApiType.Jina,
    ///     logger: loggerFactory.CreateLogger&lt;ServerEmbeddings&gt;(),
    ///     httpClient: customHttpClient);
    /// </code>
    /// </example>
    public ServerEmbeddings(
        string endpoint,
        string model,
        int embeddingSize,
        string apiKey,
        int maxBatchSize = 100,
        EmbeddingApiType apiType = EmbeddingApiType.Default,
        ILogger<ServerEmbeddings>? logger = null,
        HttpClient? httpClient = null
    )
        : base(
            logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ServerEmbeddings>.Instance,
            httpClient ?? new HttpClient()
        )
    {
        ValidationHelper.ValidateNotNull(endpoint);
        ValidationHelper.ValidateNotNull(model);
        ValidationHelper.ValidatePositive(embeddingSize);
        ValidationHelper.ValidateNotNullOrWhiteSpace(apiKey);
        ValidationHelper.ValidatePositive(maxBatchSize);
        ValidationHelper.ValidateEnumDefined(apiType);

        _endpoint = endpoint;
        _model = model;
        _embeddingSize = embeddingSize;
        _apiKey = apiKey;
        _maxBatchSize = maxBatchSize;
        _apiType = apiType;

        _batchQueue = new ConcurrentQueue<BatchRequest>();
        _batchTimer = new Timer(ProcessBatch, null, BatchTimeoutMs, BatchTimeoutMs);

        // Configure HttpClient
        HttpClient.BaseAddress = new Uri(_endpoint);
        HttpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            _apiKey
        );
    }

    /// <inheritdoc />
    /// <remarks>
    /// This property returns the embedding size specified during service initialization.
    /// The value represents the dimensionality of all embedding vectors produced by this service instance.
    /// </remarks>
    public override int EmbeddingSize => _embeddingSize;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// This implementation uses an efficient batch processing system for individual embedding requests.
    /// The method enqueues the request and processes it as part of a batch for improved performance.
    /// </para>
    /// <para>
    /// The batch processing behavior:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Requests are automatically batched with a 100ms timeout</description></item>
    /// <item><description>Batches are immediately processed when reaching the maximum batch size</description></item>
    /// <item><description>Individual requests receive their specific embedding result</description></item>
    /// </list>
    /// <para>
    /// For processing multiple texts simultaneously, consider using
    /// <see cref="GenerateEmbeddingsAsync(EmbeddingRequest, CancellationToken)"/> directly for better performance.
    /// </para>
    /// </remarks>
    public override async Task<float[]> GetEmbeddingAsync(
        string sentence,
        CancellationToken cancellationToken = default
    )
    {
        ValidationHelper.ValidateNotNullOrWhiteSpace(sentence);

        // Use batch processing for efficiency
        var tcs = new TaskCompletionSource<float[]>();
        var batchRequest = new BatchRequest
        {
            Text = sentence,
            TaskCompletionSource = tcs,
            CancellationToken = cancellationToken,
        };

        _batchQueue.Enqueue(batchRequest);

        // Trigger immediate processing if queue is getting full
        if (_batchQueue.Count >= _maxBatchSize)
        {
            _ = Task.Run(() => ProcessBatch(null), cancellationToken);
        }

        return await tcs.Task;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// This implementation provides comprehensive request processing with the following features:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Automatic text chunking for inputs exceeding maximum length</description></item>
    /// <item><description>Request validation using the base class validation methods</description></item>
    /// <item><description>API-specific payload formatting based on the request's API type</description></item>
    /// <item><description>HTTP retry logic with exponential backoff</description></item>
    /// <item><description>Standardized response parsing and error handling</description></item>
    /// </list>
    /// <para>
    /// The method automatically applies text chunking if any input exceeds the maximum text length
    /// and formats the request payload according to the specified API type (OpenAI or Jina).
    /// </para>
    /// </remarks>
    public override async Task<EmbeddingResponse> GenerateEmbeddingsAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ValidateRequest(request);

        // Apply text chunking if needed
        var processedInputs = ApplyTextChunking(request.Inputs);

        var chunkedRequest = new EmbeddingRequest
        {
            Inputs = processedInputs,
            Model = request.Model ?? _model,
            ApiType = request.ApiType == EmbeddingApiType.Default ? _apiType : request.ApiType,
            EncodingFormat = request.EncodingFormat,
            Dimensions = request.Dimensions,
            User = request.User,
            Normalized = request.Normalized,
            AdditionalOptions = request.AdditionalOptions,
        };

        return await ExecuteHttpWithRetryAsyncLinear(
            async () =>
            {
                var requestPayload = FormatRequestPayload(chunkedRequest);
                var json = JsonSerializer.Serialize(requestPayload);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

                return await HttpClient.PostAsync("/v1/embeddings", content, cancellationToken);
            },
            async (response) =>
            {
                var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                var embeddingResponse = JsonSerializer.Deserialize<EmbeddingResponse>(responseJson);

                return embeddingResponse?.Embeddings == null ? throw new InvalidOperationException("Invalid response from API") : embeddingResponse;
            },
            cancellationToken: cancellationToken
        );
    }

    /// <inheritdoc />
    /// <remarks>
    /// For ServerEmbeddings, this method returns the single model configured during initialization.
    /// The service is designed to work with a specific model, so only that model is reported as available.
    /// </remarks>
    public override Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        // For ServerEmbeddings, we return the configured model
        return Task.FromResult<IReadOnlyList<string>>(new[] { _model });
    }

    /// <summary>
    /// Executes HTTP operation with linear backoff retry logic (1s × retryCount)
    /// </summary>
    /// <typeparam name="T">The return type</typeparam>
    /// <param name="httpOperation">The HTTP operation to execute</param>
    /// <param name="responseProcessor">Function to process the HTTP response</param>
    /// <param name="maxRetries">Maximum number of retries</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result of the operation</returns>
    protected async Task<T> ExecuteHttpWithRetryAsyncLinear<T>(
        Func<Task<HttpResponseMessage>> httpOperation,
        Func<HttpResponseMessage, Task<T>> responseProcessor,
        int maxRetries = 3,
        CancellationToken cancellationToken = default
    )
    {
        var attempt = 0;
        Exception? lastException = null;

        while (attempt <= maxRetries)
        {
            try
            {
                var response = await httpOperation();

                if (response.IsSuccessStatusCode)
                {
                    return await responseProcessor(response);
                }

                if (!HttpRetryHelper.IsRetryableStatusCode(response.StatusCode))
                {
                    _ = response.EnsureSuccessStatusCode(); // This will throw
                }

                lastException = new HttpRequestException($"HTTP {(int)response.StatusCode} {response.StatusCode}");
            }
            catch (HttpRequestException ex) when (HttpRetryHelper.IsRetryableError(ex))
            {
                lastException = ex;
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                lastException = ex;
            }

            attempt++;
            if (attempt <= maxRetries)
            {
                // Linear backoff: 1s × retryCount
                var delay = TimeSpan.FromSeconds(attempt);
                Logger.LogWarning(
                    "Request failed (attempt {Attempt}/{MaxRetries}), retrying in {Delay}ms. Error: {Error}",
                    attempt,
                    maxRetries + 1,
                    delay.TotalMilliseconds,
                    lastException?.Message
                );

                await Task.Delay(delay, cancellationToken);
            }
        }

        throw lastException ?? new InvalidOperationException("Operation failed after all retry attempts");
    }

    /// <summary>
    /// Applies text chunking to inputs that exceed the maximum length
    /// </summary>
    /// <param name="inputs">The input texts</param>
    /// <returns>Chunked inputs</returns>
    private static string[] ApplyTextChunking(IEnumerable<string> inputs)
    {
        var chunkedInputs = new List<string>();

        foreach (var input in inputs)
        {
            if (input.Length <= MaxTextLength)
            {
                chunkedInputs.Add(input);
            }
            else
            {
                // Split long text into chunks
                var chunks = ChunkText(input, MaxTextLength);
                chunkedInputs.AddRange(chunks);
            }
        }

        return [.. chunkedInputs];
    }

    /// <summary>
    /// Chunks text into smaller pieces
    /// </summary>
    /// <param name="text">The text to chunk</param>
    /// <param name="maxLength">Maximum length per chunk</param>
    /// <returns>Text chunks</returns>
    private static IEnumerable<string> ChunkText(string text, int maxLength)
    {
        var chunks = new List<string>();
        var currentIndex = 0;

        while (currentIndex < text.Length)
        {
            var remainingLength = text.Length - currentIndex;
            var chunkLength = Math.Min(maxLength, remainingLength);

            // Try to break at word boundaries if possible
            if (chunkLength < remainingLength)
            {
                var lastSpaceIndex = text.LastIndexOf(' ', currentIndex + chunkLength - 1, chunkLength);
                if (lastSpaceIndex > currentIndex)
                {
                    chunkLength = lastSpaceIndex - currentIndex + 1;
                }
            }

            chunks.Add(text.Substring(currentIndex, chunkLength).Trim());
            currentIndex += chunkLength;
        }

        return chunks;
    }

    /// <summary>
    /// Processes the batch queue
    /// </summary>
    /// <param name="state">Timer state (unused)</param>
    private void ProcessBatch(object? state)
    {
        if (_batchQueue.IsEmpty)
        {
            return;
        }

        lock (_batchLock)
        {
            var batch = new List<BatchRequest>();

            // Dequeue up to maxBatchSize items
            while (batch.Count < _maxBatchSize && _batchQueue.TryDequeue(out var request))
            {
                batch.Add(request);
            }

            if (batch.Count == 0)
            {
                return;
            }

            // Process batch asynchronously
            _ = Task.Run(async () =>
            {
                try
                {
                    var inputs = batch.Select(b => b.Text).ToArray();
                    var request = new EmbeddingRequest
                    {
                        Inputs = inputs,
                        Model = _model,
                        ApiType = _apiType,
                    };

                    var response = await GenerateEmbeddingsAsync(request);

                    // Complete each task with its corresponding embedding
                    for (var i = 0; i < batch.Count && i < response.Embeddings.Count; i++)
                    {
                        batch[i].TaskCompletionSource.SetResult(response.Embeddings.ElementAt(i).Vector);
                    }
                }
                catch (Exception ex)
                {
                    // Complete all tasks with the exception
                    foreach (var batchRequest in batch)
                    {
                        batchRequest.TaskCompletionSource.SetException(ex);
                    }
                }
            });
        }
    }

    /// <summary>
    /// Represents a batch request for embedding generation
    /// </summary>
    private class BatchRequest
    {
        public required string Text { get; set; }
        public required TaskCompletionSource<float[]> TaskCompletionSource { get; set; }
        public CancellationToken CancellationToken { get; set; }
    }

    /// <summary>
    /// Disposes the ServerEmbeddings instance
    /// </summary>
    /// <param name="disposing">Whether to dispose managed resources</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _batchTimer?.Dispose();
        }
        base.Dispose(disposing);
    }
}
