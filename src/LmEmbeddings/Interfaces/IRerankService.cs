using AchieveAi.LmDotnetTools.LmEmbeddings.Models;

namespace AchieveAi.LmDotnetTools.LmEmbeddings.Interfaces;

/// <summary>
///     Interface for reranking services that can reorder documents based on relevance to a query
/// </summary>
public interface IRerankService
{
    /// <summary>
    ///     Reranks documents based on their relevance to the provided query
    /// </summary>
    /// <param name="request">The rerank request containing query, documents, and configuration</param>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    /// <returns>The rerank response with scored and ordered documents</returns>
    Task<RerankResponse> RerankAsync(RerankRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Reranks documents based on their relevance to the provided query (simplified version)
    /// </summary>
    /// <param name="query">The query to rank documents against</param>
    /// <param name="documents">The documents to rerank</param>
    /// <param name="model">The model to use for reranking</param>
    /// <param name="topK">Maximum number of documents to return</param>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    /// <returns>The rerank response with scored and ordered documents</returns>
    Task<RerankResponse> RerankAsync(
        string query,
        IReadOnlyList<string> documents,
        string model,
        int? topK = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Gets the list of available reranking models for this provider
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    /// <returns>List of available model names</returns>
    Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default);
}
