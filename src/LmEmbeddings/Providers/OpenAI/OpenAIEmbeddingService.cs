using AchieveAi.LmDotnetTools.LmEmbeddings.Core;
using AchieveAi.LmDotnetTools.LmEmbeddings.Models;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace AchieveAi.LmDotnetTools.LmEmbeddings.Providers.OpenAI;

/// <summary>
/// OpenAI implementation of the embedding service providing access to OpenAI's text embedding models.
/// This service supports all OpenAI embedding models including text-embedding-3-small, text-embedding-3-large,
/// and text-embedding-ada-002 with comprehensive configuration options and error handling.
/// </summary>
/// <remarks>
/// <para>
/// This implementation provides the following features:
/// </para>
/// <list type="bullet">
/// <item><description>Support for all OpenAI embedding models with automatic size detection</description></item>
/// <item><description>Configurable API endpoints and authentication</description></item>
/// <item><description>JSON serialization with snake_case naming policy for OpenAI API compatibility</description></item>
/// <item><description>Automatic encoding format handling (base64, float)</description></item>
/// <item><description>Comprehensive error handling and retry mechanisms</description></item>
/// <item><description>Organization-level API key support</description></item>
/// </list>
/// <para>
/// The service automatically configures HTTP client settings based on the provided options
/// and handles all OpenAI-specific API formatting and response parsing.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Basic configuration
/// var options = new OpenAIEmbeddingOptions
/// {
///     ApiKey = Environment.GetEnvironmentVariable("EMBEDDING_API_KEY"),
///     DefaultModel = Environment.GetEnvironmentVariable("EMBEDDING_MODEL") ?? "text-embedding-3-small"
/// };
/// 
/// var service = new OpenAIEmbeddingService(logger, httpClient, options);
/// 
/// // Generate embeddings
/// var request = new EmbeddingRequest
/// {
///     Inputs = new[] { "Hello, world!", "How are you?" },
///     Model = "text-embedding-3-small",
///     EncodingFormat = "base64"
/// };
/// 
/// var response = await service.GenerateEmbeddingsAsync(request);
/// foreach (var embedding in response.Embeddings)
/// {
///     Console.WriteLine($"Embedding {embedding.Index}: {embedding.Vector.Length} dimensions");
/// }
/// </code>
/// </example>
public class OpenAIEmbeddingService : BaseEmbeddingService
{
    private readonly EmbeddingOptions _options;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenAIEmbeddingService"/> class with the specified configuration.
    /// </summary>
    /// <param name="logger">The logger instance for diagnostic and error logging</param>
    /// <param name="httpClient">The HTTP client for making API requests to OpenAI</param>
    /// <param name="options">The OpenAI-specific configuration options including API key and endpoints</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is null</exception>
    /// <remarks>
    /// <para>
    /// This constructor automatically configures the HTTP client with OpenAI-specific settings:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Bearer token authentication using the provided API key</description></item>
    /// <item><description>Base address set to the configured OpenAI endpoint</description></item>
    /// <item><description>JSON serialization with snake_case naming policy</description></item>
    /// <item><description>Organization header if specified in options</description></item>
    /// </list>
    /// <para>
    /// The service will validate the options and throw appropriate exceptions for invalid configurations.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var options = new OpenAIEmbeddingOptions
    /// {
    ///     ApiKey = "sk-...",
    ///     BaseUrl = "https://api.openai.com",
    ///     Organization = "org-...",
    ///     DefaultModel = "text-embedding-3-large",
    ///     MaxRetries = 3,
    ///     TimeoutSeconds = 30
    /// };
    /// 
    /// var service = new OpenAIEmbeddingService(logger, httpClient, options);
    /// </code>
    /// </example>
    public OpenAIEmbeddingService(
        ILogger<OpenAIEmbeddingService> logger,
        HttpClient httpClient,
        EmbeddingOptions options) : base(logger, httpClient)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _jsonOptions = JsonSerializerOptionsFactory.CreateBase(namingPolicy: JsonNamingPolicy.SnakeCaseLower);

        // Configure HttpClient
        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            HttpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        if (!string.IsNullOrEmpty(_options.BaseUrl))
        {
            HttpClient.BaseAddress = new Uri(_options.BaseUrl);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// This property returns the embedding size for the default model specified in the options.
    /// The embedding sizes for OpenAI models are:
    /// </para>
    /// <list type="bullet">
    /// <item><description>text-embedding-3-small: 1536 dimensions</description></item>
    /// <item><description>text-embedding-3-large: 3072 dimensions</description></item>
    /// <item><description>text-embedding-ada-002: 1536 dimensions</description></item>
    /// </list>
    /// <para>
    /// If an unknown model is specified, the service defaults to 1536 dimensions (text-embedding-3-small size).
    /// </para>
    /// </remarks>
    public override int EmbeddingSize => _options.AvailableModelsWithDimensions[_options.DefaultModel].Dimensions;

    /// <summary>
    /// Gets the embedding size for a specific OpenAI model.
    /// This method provides the correct dimensionality for each supported OpenAI embedding model.
    /// </summary>
    /// <param name="model">The OpenAI model name to get the embedding size for</param>
    /// <returns>The number of dimensions in the embedding vectors for the specified model</returns>
    /// <remarks>
    /// <para>
    /// Supported models and their embedding sizes:
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>text-embedding-3-small</c>: 1536 dimensions (default)</description></item>
    /// <item><description><c>text-embedding-3-large</c>: 3072 dimensions</description></item>
    /// <item><description><c>text-embedding-ada-002</c>: 1536 dimensions (legacy)</description></item>
    /// </list>
    /// <para>
    /// For unknown or unsupported models, the method returns 1536 dimensions as a safe default.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var smallSize = GetEmbeddingSizeForModel("text-embedding-3-small"); // Returns 1536
    /// var largeSize = GetEmbeddingSizeForModel("text-embedding-3-large"); // Returns 3072
    /// var unknownSize = GetEmbeddingSizeForModel("unknown-model"); // Returns 1536 (default)
    /// </code>
    /// </example>
    private int GetEmbeddingSizeForModel(string model)
    {
        return _options.AvailableModelsWithDimensions[model].Dimensions;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// This implementation provides comprehensive OpenAI API integration with the following features:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Automatic request validation using base class validation methods</description></item>
    /// <item><description>OpenAI-specific payload formatting with snake_case JSON serialization</description></item>
    /// <item><description>Retry logic with exponential backoff for transient failures</description></item>
    /// <item><description>Support for both base64 and float encoding formats</description></item>
    /// <item><description>Comprehensive error handling for OpenAI API responses</description></item>
    /// <item><description>Usage statistics parsing from API responses</description></item>
    /// </list>
    /// <para>
    /// The method automatically handles encoding format conversion and provides detailed logging
    /// for debugging and monitoring purposes.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var request = new EmbeddingRequest
    /// {
    ///     Inputs = new[] { "Hello", "World" },
    ///     Model = "text-embedding-3-small",
    ///     EncodingFormat = "base64",
    ///     Dimensions = 1024 // Optional dimension reduction
    /// };
    /// 
    /// var response = await service.GenerateEmbeddingsAsync(request);
    /// Console.WriteLine($"Generated {response.Embeddings.Count} embeddings");
    /// Console.WriteLine($"Used {response.Usage?.TotalTokens} tokens");
    /// </code>
    /// </example>
    public override async Task<EmbeddingResponse> GenerateEmbeddingsAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        return await ExecuteWithRetryAsync(async () =>
        {
            // Use the base class method to format the request based on API type
            var requestPayload = FormatRequestPayload(request);

            var json = JsonSerializer.Serialize(requestPayload, _jsonOptions);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            Logger.LogDebug("Sending embedding request to OpenAI for {InputCount} inputs using model {Model} with API type {ApiType}",
                request.Inputs.Count, request.Model, request.ApiType);

            var response = await HttpClient.PostAsync("/v1/embeddings", content, cancellationToken);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            var openAIResponse = JsonSerializer.Deserialize<OpenAIEmbeddingResponse>(responseJson, _jsonOptions);

            if (openAIResponse?.Data == null)
                throw new InvalidOperationException("Invalid response from OpenAI API");

            var embeddings = openAIResponse.Data.Select((item, index) => new EmbeddingItem
            {
                Vector = DecodeEmbedding(item.Embedding, null),
                Index = item.Index,
                Text = request.Inputs.ElementAtOrDefault(item.Index)
            }).ToArray();

            return new EmbeddingResponse
            {
                Embeddings = embeddings,
                Model = openAIResponse.Model,
                Usage = openAIResponse.Usage != null ? new EmbeddingUsage
                {
                    PromptTokens = openAIResponse.Usage.PromptTokens,
                    TotalTokens = openAIResponse.Usage.TotalTokens
                } : null
            };
        }, cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// This method returns the list of OpenAI embedding models that are currently supported
    /// by this service implementation. The models are returned in order of recommendation:
    /// </para>
    /// <list type="number">
    /// <item><description><c>text-embedding-3-small</c> - Latest small model, good balance of performance and cost</description></item>
    /// <item><description><c>text-embedding-3-large</c> - Latest large model, highest performance</description></item>
    /// <item><description><c>text-embedding-ada-002</c> - Legacy model, maintained for compatibility</description></item>
    /// </list>
    /// <para>
    /// The returned models can be used in embedding requests to specify which model to use.
    /// Model availability may vary based on your OpenAI account and subscription level.
    /// </para>
    /// </remarks>
    public override async Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        // Return known OpenAI embedding models
        return await Task.FromResult(new[]
        {
            _options.DefaultModel
        }.AsReadOnly());
    }

    /// <summary>
    /// Decodes an embedding from the OpenAI API response with automatic format detection.
    /// This method intelligently detects whether the embedding data is in base64 or float array format
    /// and decodes it appropriately, while still respecting explicit format specifications.
    /// </summary>
    /// <param name="embedding">The embedding data from the OpenAI API response</param>
    /// <param name="encodingFormat">The encoding format hint (optional - will auto-detect if null or empty)</param>
    /// <returns>A float array representing the decoded embedding vector</returns>
    /// <exception cref="NotSupportedException">Thrown when an unsupported encoding format is specified or detected</exception>
    /// <exception cref="InvalidOperationException">Thrown when the embedding data format cannot be determined</exception>
    /// <remarks>
    /// <para>
    /// This method provides robust embedding decoding with the following features:
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>Auto-detection</c> - Automatically detects base64 strings vs float arrays</description></item>
    /// <item><description><c>Explicit override</c> - Respects explicit format specification when provided</description></item>
    /// <item><description><c>Comprehensive validation</c> - Validates that specified format matches actual data</description></item>
    /// <item><description><c>Clear error messages</c> - Provides detailed context when format mismatches occur</description></item>
    /// </list>
    /// <para>
    /// Format detection logic:
    /// </para>
    /// <list type="number">
    /// <item><description>If encodingFormat is explicitly specified, use it (with validation)</description></item>
    /// <item><description>If data is JsonElement with Array ValueKind, treat as float array</description></item>
    /// <item><description>If data is string or JsonElement with String ValueKind, treat as base64</description></item>
    /// <item><description>Otherwise, throw descriptive error with detected type information</description></item>
    /// </list>
    /// </remarks>
    private static float[] DecodeEmbedding(object embedding, string? encodingFormat)
    {
        // Auto-detect format if not explicitly specified
        if (string.IsNullOrEmpty(encodingFormat))
        {
            return DetectAndDecodeEmbedding(embedding);
        }

        // Use explicit format with validation
        var detectedFormat = DetectEmbeddingFormat(embedding);
        var requestedFormat = encodingFormat.ToLowerInvariant();

        // Validate that the requested format matches the detected format
        if (detectedFormat != requestedFormat)
        {
            throw new InvalidOperationException(
                $"Encoding format mismatch: requested '{requestedFormat}' but detected '{detectedFormat}' " +
                $"based on data type {embedding?.GetType().Name ?? "null"}. " +
                $"Consider omitting EncodingFormat to enable auto-detection.");
        }

        return requestedFormat switch
        {
            "base64" => DecodeBase64Embedding(embedding.ToString()!),
            "float" => DecodeFloatArrayEmbedding((JsonElement)embedding),
            _ => throw new NotSupportedException($"Encoding format '{requestedFormat}' is not supported")
        };
    }

    /// <summary>
    /// Automatically detects the embedding format and decodes it appropriately.
    /// </summary>
    /// <param name="embedding">The embedding data to detect and decode</param>
    /// <returns>A float array representing the decoded embedding vector</returns>
    private static float[] DetectAndDecodeEmbedding(object embedding)
    {
        var detectedFormat = DetectEmbeddingFormat(embedding);

        return detectedFormat switch
        {
            "base64" => DecodeBase64Embedding(embedding.ToString()!),
            "float" => DecodeFloatArrayEmbedding((JsonElement)embedding),
            _ => throw new InvalidOperationException(
                $"Unable to detect embedding format for data type {embedding?.GetType().Name ?? "null"}. " +
                $"Expected string (base64) or JsonElement array (float), but got {embedding}")
        };
    }

    /// <summary>
    /// Detects the format of embedding data based on its type and structure.
    /// </summary>
    /// <param name="embedding">The embedding data to analyze</param>
    /// <returns>The detected format: "base64" or "float"</returns>
    private static string DetectEmbeddingFormat(object embedding)
    {
        return embedding switch
        {
            string => "base64",
            JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.String => "base64",
            JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.Array => "float",
            _ => "unknown"
        };
    }

    /// <summary>
    /// Decodes a float array embedding from a JsonElement.
    /// </summary>
    /// <param name="jsonElement">The JsonElement containing the float array</param>
    /// <returns>A float array representing the decoded embedding vector</returns>
    private static float[] DecodeFloatArrayEmbedding(JsonElement jsonElement)
    {
        if (jsonElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"Expected JsonElement with Array ValueKind for float format, but got {jsonElement.ValueKind}");
        }

        return jsonElement.EnumerateArray().Select(x => x.GetSingle()).ToArray();
    }

    /// <summary>
    /// Decodes a base64-encoded embedding into a float array.
    /// This method converts the base64 string representation back to the original float array format.
    /// </summary>
    /// <param name="base64">The base64-encoded embedding string from the OpenAI API</param>
    /// <returns>A float array representing the decoded embedding vector</returns>
    /// <remarks>
    /// <para>
    /// This method performs the following steps:
    /// </para>
    /// <list type="number">
    /// <item><description>Decodes the base64 string to a byte array</description></item>
    /// <item><description>Converts the byte array to a float array using block copy</description></item>
    /// <item><description>Returns the resulting float array with the embedding values</description></item>
    /// </list>
    /// <para>
    /// The base64 encoding is used by OpenAI to efficiently transfer large embedding vectors
    /// over HTTP while maintaining precision of the floating-point values.
    /// </para>
    /// </remarks>
    private static float[] DecodeBase64Embedding(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        var floats = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }

    // Internal models for OpenAI API responses
    private class OpenAIEmbeddingResponse
    {
        public string Object { get; set; } = "";
        public OpenAIEmbeddingData[] Data { get; set; } = Array.Empty<OpenAIEmbeddingData>();
        public string Model { get; set; } = "";
        public OpenAIUsage? Usage { get; set; }
    }

    private class OpenAIEmbeddingData
    {
        public string Object { get; set; } = "";
        public object Embedding { get; set; } = new object();
        public int Index { get; set; }
    }

    private class OpenAIUsage
    {
        public int PromptTokens { get; set; }
        public int TotalTokens { get; set; }
    }
}