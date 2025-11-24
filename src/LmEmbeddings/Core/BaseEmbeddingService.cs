using AchieveAi.LmDotnetTools.LmCore.Http;
using AchieveAi.LmDotnetTools.LmCore.Validation;
using AchieveAi.LmDotnetTools.LmEmbeddings.Interfaces;
using AchieveAi.LmDotnetTools.LmEmbeddings.Models;
using LmEmbeddings.Models;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmEmbeddings.Core;

/// <summary>
/// Base class for embedding services providing common functionality for text-to-vector conversion.
/// This abstract class implements the core embedding service interface and provides standardized
/// request validation, payload formatting, and error handling patterns.
/// </summary>
/// <remarks>
/// <para>
/// This class serves as the foundation for all embedding service implementations, ensuring
/// consistent behavior across different providers (OpenAI, Jina, etc.). It handles:
/// </para>
/// <list type="bullet">
/// <item><description>Request validation and sanitization</description></item>
/// <item><description>API-specific payload formatting</description></item>
/// <item><description>Standardized error handling and exception patterns</description></item>
/// <item><description>Disposal pattern implementation</description></item>
/// </list>
/// <para>
/// Derived classes must implement the abstract methods to provide provider-specific functionality
/// while inheriting the common validation and formatting logic.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public class MyEmbeddingService : BaseEmbeddingService
/// {
///     public MyEmbeddingService(ILogger&lt;MyEmbeddingService&gt; logger, HttpClient httpClient)
///         : base(logger, httpClient)
///     {
///     }
///
///     public override int EmbeddingSize =&gt; 1536;
///
///     public override async Task&lt;EmbeddingResponse&gt; GenerateEmbeddingsAsync(
///         EmbeddingRequest request,
///         CancellationToken cancellationToken = default)
///     {
///         // Implementation specific to your provider
///         return await CallProviderApiAsync(request, cancellationToken);
///     }
///
///     public override async Task&lt;IReadOnlyList&lt;string&gt;&gt; GetAvailableModelsAsync(
///         CancellationToken cancellationToken = default)
///     {
///         // Return available models for your provider
///         return new[] { "model-1", "model-2" };
///     }
/// }
/// </code>
/// </example>
public abstract class BaseEmbeddingService : BaseHttpService, IEmbeddingService
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BaseEmbeddingService"/> class.
    /// </summary>
    /// <param name="logger">The logger instance for diagnostic and error logging</param>
    /// <param name="httpClient">The HTTP client for making API requests</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="logger"/> or <paramref name="httpClient"/> is null</exception>
    protected BaseEmbeddingService(ILogger logger, HttpClient httpClient)
        : base(logger, httpClient) { }

    /// <inheritdoc />
    /// <remarks>
    /// This property should return the dimensionality of the embedding vectors produced by this service.
    /// Common sizes include 1536 (OpenAI text-embedding-3-small), 3072 (OpenAI text-embedding-3-large),
    /// and 768 (many BERT-based models).
    /// </remarks>
    public abstract int EmbeddingSize { get; }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// This is a simplified API that automatically selects the first available model and returns
    /// only the embedding vector. For more control over the embedding process, use
    /// <see cref="GenerateEmbeddingsAsync(EmbeddingRequest, CancellationToken)"/> instead.
    /// </para>
    /// <para>
    /// The method performs the following steps:
    /// </para>
    /// <list type="number">
    /// <item><description>Validates the input sentence</description></item>
    /// <item><description>Retrieves available models</description></item>
    /// <item><description>Uses the first available model to generate embeddings</description></item>
    /// <item><description>Returns the first embedding vector</description></item>
    /// </list>
    /// </remarks>
    /// <example>
    /// <code>
    /// var embedding = await service.GetEmbeddingAsync("Hello, world!");
    /// Console.WriteLine($"Embedding size: {embedding.Length}");
    /// </code>
    /// </example>
    public virtual async Task<float[]> GetEmbeddingAsync(string sentence, CancellationToken cancellationToken = default)
    {
        ValidationHelper.ValidateNotNullOrWhiteSpace(sentence);
        ThrowIfDisposed();

        // Use the first available model as default for the simple API
        var availableModels = await GetAvailableModelsAsync(cancellationToken);
        if (!availableModels.Any())
        {
            throw new InvalidOperationException("No models available for embedding generation");
        }

        var defaultModel = availableModels[0];
        var response = await GenerateEmbeddingAsync(sentence, defaultModel, cancellationToken);

        return response.Embeddings?.Any() != true
            ? throw new InvalidOperationException("No embeddings returned from service")
            : response.Embeddings[0].Vector;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// This is the primary method that derived classes must implement to provide embedding functionality.
    /// The base class handles request validation through <see cref="ValidateRequest(EmbeddingRequest)"/>
    /// before this method is called.
    /// </para>
    /// <para>
    /// Implementations should:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Format the request using <see cref="FormatRequestPayload(EmbeddingRequest)"/></description></item>
    /// <item><description>Make the HTTP request to the provider's API</description></item>
    /// <item><description>Parse the response and return a standardized <see cref="EmbeddingResponse"/></description></item>
    /// <item><description>Handle provider-specific errors appropriately</description></item>
    /// </list>
    /// </remarks>
    public abstract Task<EmbeddingResponse> GenerateEmbeddingsAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default
    );

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// This is a convenience method that wraps a single text input into an <see cref="EmbeddingRequest"/>
    /// and calls <see cref="GenerateEmbeddingsAsync(EmbeddingRequest, CancellationToken)"/>.
    /// </para>
    /// <para>
    /// For batch processing multiple texts, use <see cref="GenerateEmbeddingsAsync(EmbeddingRequest, CancellationToken)"/>
    /// directly with multiple inputs for better performance.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var response = await service.GenerateEmbeddingAsync("Hello, world!", "text-embedding-3-small");
    /// var embedding = response.Embeddings.First().Vector;
    /// </code>
    /// </example>
    public virtual async Task<EmbeddingResponse> GenerateEmbeddingAsync(
        string text,
        string model,
        CancellationToken cancellationToken = default
    )
    {
        ValidationHelper.ValidateNotNullOrWhiteSpace(text);
        ValidationHelper.ValidateNotNullOrWhiteSpace(model);
        ThrowIfDisposed();

        var request = new EmbeddingRequest { Inputs = new[] { text }, Model = model };

        return await GenerateEmbeddingsAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// This method should return the list of embedding models supported by the provider.
    /// The returned models can be used in <see cref="EmbeddingRequest.Model"/> to specify
    /// which model to use for embedding generation.
    /// </para>
    /// <para>
    /// For services that support only a single model, this method should return a list
    /// containing that single model identifier.
    /// </para>
    /// </remarks>
    public abstract Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Formats the request payload based on the API type specified in the request.
    /// This method provides a unified interface for different API formats while delegating
    /// the actual formatting to API-specific methods.
    /// </summary>
    /// <param name="request">The embedding request containing the API type and parameters</param>
    /// <returns>A dictionary representing the formatted request payload ready for JSON serialization</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null</exception>
    /// <exception cref="ArgumentException">Thrown when the request contains invalid data or unsupported API type</exception>
    /// <remarks>
    /// <para>
    /// This method automatically validates the request using <see cref="ValidateRequest(EmbeddingRequest)"/>
    /// before formatting. The formatting is delegated to API-specific methods:
    /// </para>
    /// <list type="bullet">
    /// <item><description><see cref="EmbeddingApiType.Default"/> → <see cref="FormatOpenAIRequest(EmbeddingRequest)"/></description></item>
    /// <item><description><see cref="EmbeddingApiType.Jina"/> → <see cref="FormatJinaRequest(EmbeddingRequest)"/></description></item>
    /// </list>
    /// </remarks>
    /// <example>
    /// <code>
    /// var request = new EmbeddingRequest
    /// {
    ///     Inputs = new[] { "Hello", "World" },
    ///     Model = "text-embedding-3-small",
    ///     ApiType = EmbeddingApiType.Default
    /// };
    /// var payload = FormatRequestPayload(request);
    /// var json = JsonSerializer.Serialize(payload);
    /// </code>
    /// </example>
    protected virtual Dictionary<string, object> FormatRequestPayload(EmbeddingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        return request.ApiType switch
        {
            EmbeddingApiType.Jina => FormatJinaRequest(request),
            EmbeddingApiType.Default => FormatOpenAIRequest(request),
            _ => throw new ArgumentException($"Unsupported API type: {request.ApiType}", nameof(request)),
        };
    }

    /// <summary>
    /// Formats a request for the Jina AI API format.
    /// This method creates a payload dictionary with Jina-specific parameters and naming conventions.
    /// </summary>
    /// <param name="request">The embedding request to format</param>
    /// <returns>A dictionary containing the Jina API formatted request</returns>
    /// <remarks>
    /// <para>
    /// The Jina API format includes the following specific features:
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>normalized</c> - Controls whether embeddings are L2 normalized</description></item>
    /// <item><description><c>embedding_type</c> - Specifies the encoding format (maps from <see cref="EmbeddingRequest.EncodingFormat"/>)</description></item>
    /// <item><description>Support for binary and base64 encoding formats</description></item>
    /// </list>
    /// <para>
    /// Standard parameters like <c>input</c>, <c>model</c>, and <c>dimensions</c> are included,
    /// along with any additional options specified in <see cref="EmbeddingRequest.AdditionalOptions"/>.
    /// </para>
    /// </remarks>
    protected virtual Dictionary<string, object> FormatJinaRequest(EmbeddingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payload = new Dictionary<string, object>
        {
            ["input"] = request.Inputs.ToArray(),
            ["model"] = request.Model,
        };

        // Add Jina-specific parameters
        if (request.Normalized.HasValue)
        {
            payload["normalized"] = request.Normalized.Value;
        }

        if (!string.IsNullOrEmpty(request.EncodingFormat))
        {
            payload["embedding_type"] = request.EncodingFormat;
        }

        if (request.Dimensions.HasValue)
        {
            payload["dimensions"] = request.Dimensions.Value;
        }

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
    /// Formats a request for the OpenAI API format.
    /// This method creates a payload dictionary with OpenAI-specific parameters and naming conventions.
    /// </summary>
    /// <param name="request">The embedding request to format</param>
    /// <returns>A dictionary containing the OpenAI API formatted request</returns>
    /// <remarks>
    /// <para>
    /// The OpenAI API format includes the following specific features:
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>encoding_format</c> - Specifies how embeddings are encoded (float, base64)</description></item>
    /// <item><description><c>user</c> - Optional user identifier for tracking and abuse monitoring</description></item>
    /// <item><description><c>dimensions</c> - Optional parameter to reduce embedding dimensionality</description></item>
    /// </list>
    /// <para>
    /// Standard parameters like <c>input</c> and <c>model</c> are included,
    /// along with any additional options specified in <see cref="EmbeddingRequest.AdditionalOptions"/>.
    /// </para>
    /// </remarks>
    protected virtual Dictionary<string, object> FormatOpenAIRequest(EmbeddingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payload = new Dictionary<string, object>
        {
            ["input"] = request.Inputs.ToArray(),
            ["model"] = request.Model,
        };

        if (!string.IsNullOrEmpty(request.EncodingFormat))
        {
            payload["encoding_format"] = request.EncodingFormat;
        }

        if (request.Dimensions.HasValue)
        {
            payload["dimensions"] = request.Dimensions.Value;
        }

        if (!string.IsNullOrEmpty(request.User))
        {
            payload["user"] = request.User;
        }

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
    /// Validates an embedding request for correctness and completeness.
    /// This method performs comprehensive validation of all request parameters and throws
    /// standardized exceptions for any validation failures.
    /// </summary>
    /// <param name="request">The request to validate</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null</exception>
    /// <exception cref="ArgumentException">Thrown when request properties are invalid</exception>
    /// <exception cref="ObjectDisposedException">Thrown when the service has been disposed</exception>
    /// <remarks>
    /// <para>
    /// This method validates the following aspects of the request:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Request object is not null</description></item>
    /// <item><description>Inputs collection is not null or empty</description></item>
    /// <item><description>Model name is not null or empty</description></item>
    /// <item><description>All input texts are non-empty</description></item>
    /// <item><description>Service is not disposed</description></item>
    /// <item><description>API-specific parameters are valid (via <see cref="ValidateApiSpecificParameters(EmbeddingRequest)"/>)</description></item>
    /// </list>
    /// <para>
    /// This method is automatically called by <see cref="FormatRequestPayload(EmbeddingRequest)"/>
    /// and should be called by derived classes before processing requests.
    /// </para>
    /// </remarks>
    protected virtual void ValidateRequest(EmbeddingRequest request)
    {
        ValidationHelper.ValidateEmbeddingRequest(request);
        ThrowIfDisposed();

        // Validate API-specific parameters
        ValidateApiSpecificParameters(request);
    }

    /// <summary>
    /// Validates API-specific parameters based on the request's API type.
    /// This method delegates validation to the appropriate API-specific validation method.
    /// </summary>
    /// <param name="request">The request containing API-specific parameters to validate</param>
    /// <exception cref="ArgumentException">Thrown when API-specific parameters are invalid</exception>
    /// <remarks>
    /// <para>
    /// This method routes validation to the appropriate API-specific validator:
    /// </para>
    /// <list type="bullet">
    /// <item><description><see cref="EmbeddingApiType.Jina"/> → <see cref="ValidateJinaParameters(EmbeddingRequest)"/></description></item>
    /// <item><description><see cref="EmbeddingApiType.Default"/> → <see cref="ValidateOpenAIParameters(EmbeddingRequest)"/></description></item>
    /// </list>
    /// </remarks>
    protected virtual void ValidateApiSpecificParameters(EmbeddingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        switch (request.ApiType)
        {
            case EmbeddingApiType.Jina:
                ValidateJinaParameters(request);
                break;
            case EmbeddingApiType.Default:
                ValidateOpenAIParameters(request);
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// Validates Jina AI specific parameters in the embedding request.
    /// This method ensures that Jina-specific parameters conform to the API's requirements.
    /// </summary>
    /// <param name="request">The request containing Jina-specific parameters</param>
    /// <exception cref="ArgumentException">Thrown when Jina-specific parameters are invalid</exception>
    /// <remarks>
    /// <para>
    /// Jina AI supports the following encoding formats:
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>float</c> - Standard floating-point array</description></item>
    /// <item><description><c>binary</c> - Binary encoded embeddings</description></item>
    /// <item><description><c>base64</c> - Base64 encoded embeddings</description></item>
    /// </list>
    /// <para>
    /// The validation is case-insensitive and will throw a descriptive exception
    /// listing the valid formats if an invalid format is provided.
    /// </para>
    /// </remarks>
    protected virtual void ValidateJinaParameters(EmbeddingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.IsNullOrEmpty(request.EncodingFormat))
        {
            var validFormats = new[] { "float", "binary", "base64" };
            ValidationHelper.ValidateAllowedValues(request.EncodingFormat, validFormats);
        }
    }

    /// <summary>
    /// Validates OpenAI specific parameters in the embedding request.
    /// This method ensures that OpenAI-specific parameters conform to the API's requirements.
    /// </summary>
    /// <param name="request">The request containing OpenAI-specific parameters</param>
    /// <exception cref="ArgumentException">Thrown when OpenAI-specific parameters are invalid</exception>
    /// <remarks>
    /// <para>
    /// OpenAI supports the following encoding formats:
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>float</c> - Standard floating-point array</description></item>
    /// <item><description><c>base64</c> - Base64 encoded embeddings (default)</description></item>
    /// </list>
    /// <para>
    /// The validation is case-insensitive and will throw a descriptive exception
    /// listing the valid formats if an invalid format is provided.
    /// </para>
    /// </remarks>
    protected virtual void ValidateOpenAIParameters(EmbeddingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.IsNullOrEmpty(request.EncodingFormat))
        {
            var validFormats = new[] { "float", "base64" };
            ValidationHelper.ValidateAllowedValues(request.EncodingFormat, validFormats);
        }
    }
}
