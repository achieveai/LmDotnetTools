using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Http;
using AchieveAi.LmDotnetTools.LmCore.Validation;
using AchieveAi.LmDotnetTools.LmEmbeddings.Interfaces;
using AchieveAi.LmDotnetTools.LmEmbeddings.Models;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmEmbeddings.Core;

/// <summary>
/// Base class for reranking services providing common functionality for document relevance scoring.
/// This abstract class implements the core reranking service interface and provides standardized
/// request validation, error handling, and common patterns for reranking implementations.
/// </summary>
/// <remarks>
/// <para>
/// This class serves as the foundation for all reranking service implementations, ensuring
/// consistent behavior across different providers. It handles:
/// </para>
/// <list type="bullet">
/// <item><description>Request validation and sanitization</description></item>
/// <item><description>Standardized error handling and exception patterns</description></item>
/// <item><description>Common method signatures and parameter validation</description></item>
/// <item><description>Disposal pattern implementation</description></item>
/// </list>
/// <para>
/// Derived classes must implement the abstract methods to provide provider-specific functionality
/// while inheriting the common validation and error handling logic.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public class MyRerankService : BaseRerankService
/// {
///     public MyRerankService(ILogger&lt;MyRerankService&gt; logger, HttpClient httpClient)
///         : base(logger, httpClient)
///     {
///     }
///
///     public override async Task&lt;RerankResponse&gt; RerankAsync(
///         RerankRequest request,
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
///         return new[] { "rerank-model-1", "rerank-model-2" };
///     }
/// }
/// </code>
/// </example>
public abstract class BaseRerankService : BaseHttpService, IRerankService
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BaseRerankService"/> class.
    /// </summary>
    /// <param name="logger">The logger instance for diagnostic and error logging</param>
    /// <param name="httpClient">The HTTP client for making API requests</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="logger"/> or <paramref name="httpClient"/> is null</exception>
    protected BaseRerankService(ILogger logger, HttpClient httpClient)
        : base(logger, httpClient) { }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// This is the primary method that derived classes must implement to provide reranking functionality.
    /// The base class handles request validation through parameter validation before this method is called.
    /// </para>
    /// <para>
    /// Implementations should:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Validate the request using <see cref="ValidationHelper.ValidateRerankRequest(object?)"/></description></item>
    /// <item><description>Make the HTTP request to the provider's API</description></item>
    /// <item><description>Parse the response and return a standardized <see cref="RerankResponse"/></description></item>
    /// <item><description>Handle provider-specific errors appropriately</description></item>
    /// </list>
    /// </remarks>
    public abstract Task<RerankResponse> RerankAsync(
        RerankRequest request,
        CancellationToken cancellationToken = default
    );

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// This is a convenience method that wraps the parameters into a <see cref="RerankRequest"/>
    /// and calls <see cref="RerankAsync(RerankRequest, CancellationToken)"/>.
    /// </para>
    /// <para>
    /// This method performs comprehensive validation of all parameters before creating the request object.
    /// For more control over the reranking process, use <see cref="RerankAsync(RerankRequest, CancellationToken)"/>
    /// directly with a custom request object.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var documents = new[] { "Document 1", "Document 2", "Document 3" };
    /// var response = await service.RerankAsync("search query", documents, "rerank-model", topK: 2);
    /// var topDocuments = response.Results.Take(2).ToList();
    /// </code>
    /// </example>
    public virtual async Task<RerankResponse> RerankAsync(
        string query,
        IReadOnlyList<string> documents,
        string model,
        int? topK = null,
        CancellationToken cancellationToken = default
    )
    {
        ValidationHelper.ValidateNotNullOrWhiteSpace(query);
        ValidationHelper.ValidateStringCollectionElements(documents);
        ValidationHelper.ValidateNotNullOrWhiteSpace(model);

        if (topK.HasValue)
        {
            ValidationHelper.ValidatePositive(topK.Value);
        }

        var request = new RerankRequest
        {
            Query = query,
            Documents = documents.ToImmutableList(),
            Model = model,
            TopN = topK,
        };

        return await RerankAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// This method should return the list of reranking models supported by the provider.
    /// The returned models can be used in <see cref="RerankRequest.Model"/> to specify
    /// which model to use for document reranking.
    /// </para>
    /// <para>
    /// For services that support only a single model, this method should return a list
    /// containing that single model identifier.
    /// </para>
    /// </remarks>
    public abstract Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a rerank request
    /// </summary>
    /// <param name="request">The request to validate</param>
    /// <exception cref="ArgumentNullException">Thrown when request is null</exception>
    /// <exception cref="ArgumentException">Thrown when request is invalid</exception>
    protected virtual void ValidateRequest(RerankRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Query))
            throw new ArgumentException("Query cannot be null or empty", nameof(request));
        if (request.Documents == null || request.Documents.IsEmpty)
            throw new ArgumentException("Documents cannot be null or empty", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model cannot be null or empty", nameof(request));
        if (request.Documents.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("All documents must be non-empty", nameof(request));
        if (request.TopN.HasValue && request.TopN.Value <= 0)
            throw new ArgumentException("TopN must be positive", nameof(request));

        ThrowIfDisposed();
    }
}
