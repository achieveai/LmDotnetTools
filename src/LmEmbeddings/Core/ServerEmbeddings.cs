using AchieveAi.LmDotnetTools.LmEmbeddings.Interfaces;
using AchieveAi.LmDotnetTools.LmEmbeddings.Models;
using LmEmbeddings.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace AchieveAi.LmDotnetTools.LmEmbeddings.Core;

/// <summary>
/// Generic server-based embedding service that supports batch processing and multiple API types
/// </summary>
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
    /// Initializes a new instance of the ServerEmbeddings class
    /// </summary>
    /// <param name="endpoint">The API endpoint URL</param>
    /// <param name="model">The embedding model to use</param>
    /// <param name="embeddingSize">The size of the embedding vectors</param>
    /// <param name="apiKey">The API key for authentication</param>
    /// <param name="maxBatchSize">Maximum number of texts to process in a single batch</param>
    /// <param name="apiType">The API type (OpenAI or Jina)</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="httpClient">HTTP client instance</param>
    public ServerEmbeddings(
        string endpoint,
        string model,
        int embeddingSize,
        string apiKey,
        int maxBatchSize = 100,
        EmbeddingApiType apiType = EmbeddingApiType.Default,
        ILogger<ServerEmbeddings>? logger = null,
        HttpClient? httpClient = null)
        : base(logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ServerEmbeddings>.Instance,
               httpClient ?? new HttpClient())
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _embeddingSize = embeddingSize > 0 ? embeddingSize : throw new ArgumentException("Embedding size must be positive", nameof(embeddingSize));
        _apiKey = !string.IsNullOrWhiteSpace(apiKey) ? apiKey : throw new ArgumentException("API key cannot be null or empty", nameof(apiKey));
        _maxBatchSize = maxBatchSize > 0 ? maxBatchSize : throw new ArgumentException("Max batch size must be positive", nameof(maxBatchSize));
        _apiType = apiType;

        _batchQueue = new ConcurrentQueue<BatchRequest>();
        _batchTimer = new Timer(ProcessBatch, null, BatchTimeoutMs, BatchTimeoutMs);

        // Configure HttpClient
        HttpClient.BaseAddress = new Uri(_endpoint);
        HttpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
    }

    /// <inheritdoc />
    public override int EmbeddingSize => _embeddingSize;

    /// <inheritdoc />
    public override async Task<float[]> GetEmbeddingAsync(string sentence, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sentence))
            throw new ArgumentException("Sentence cannot be null or empty", nameof(sentence));

        // Use batch processing for efficiency
        var tcs = new TaskCompletionSource<float[]>();
        var batchRequest = new BatchRequest
        {
            Text = sentence,
            TaskCompletionSource = tcs,
            CancellationToken = cancellationToken
        };

        _batchQueue.Enqueue(batchRequest);

        // Trigger immediate processing if queue is getting full
        if (_batchQueue.Count >= _maxBatchSize)
        {
            _ = Task.Run(() => ProcessBatch(null));
        }

        return await tcs.Task;
    }

    /// <inheritdoc />
    public override async Task<EmbeddingResponse> GenerateEmbeddingsAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
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
            AdditionalOptions = request.AdditionalOptions
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

                if (embeddingResponse?.Embeddings == null)
                    throw new InvalidOperationException("Invalid response from API");

                return embeddingResponse;
            },
            cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
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
        CancellationToken cancellationToken = default)
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

                if (!IsRetryableStatusCode(response.StatusCode))
                {
                    response.EnsureSuccessStatusCode(); // This will throw
                }

                lastException = new HttpRequestException($"HTTP {(int)response.StatusCode} {response.StatusCode}");
            }
            catch (HttpRequestException ex) when (IsRetryableError(ex))
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
                Logger.LogWarning("Request failed (attempt {Attempt}/{MaxRetries}), retrying in {Delay}ms. Error: {Error}",
                    attempt, maxRetries + 1, delay.TotalMilliseconds, lastException?.Message);
                
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
    private string[] ApplyTextChunking(IEnumerable<string> inputs)
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

        return chunkedInputs.ToArray();
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
            return;

        lock (_batchLock)
        {
            var batch = new List<BatchRequest>();
            
            // Dequeue up to maxBatchSize items
            while (batch.Count < _maxBatchSize && _batchQueue.TryDequeue(out var request))
            {
                batch.Add(request);
            }

            if (batch.Count == 0)
                return;

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
                        ApiType = _apiType
                    };

                    var response = await GenerateEmbeddingsAsync(request);
                    
                    // Complete each task with its corresponding embedding
                    for (int i = 0; i < batch.Count && i < response.Embeddings.Count; i++)
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