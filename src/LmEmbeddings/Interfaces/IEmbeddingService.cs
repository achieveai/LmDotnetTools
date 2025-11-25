using AchieveAi.LmDotnetTools.LmEmbeddings.Models;

namespace AchieveAi.LmDotnetTools.LmEmbeddings.Interfaces;

/// <summary>
///     Interface for embedding services that can generate vector embeddings from text
/// </summary>
public interface IEmbeddingService : IDisposable
{
    /// <summary>
    ///     Gets the size of embeddings produced by this service
    /// </summary>
    int EmbeddingSize { get; }

    /// <summary>
    ///     Generates a single embedding for the provided text (simple API)
    /// </summary>
    /// <param name="sentence">The text to embed</param>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    /// <returns>The embedding vector as float array</returns>
    Task<float[]> GetEmbeddingAsync(string sentence, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Generates embeddings for the provided text inputs (comprehensive API)
    /// </summary>
    /// <param name="request">The embedding request containing texts and configuration</param>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    /// <returns>The embedding response with generated vectors</returns>
    Task<EmbeddingResponse> GenerateEmbeddingsAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Generates embeddings for a single text input (backward compatibility)
    /// </summary>
    /// <param name="text">The text to embed</param>
    /// <param name="model">The model to use for embedding</param>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    /// <returns>The embedding response with generated vector</returns>
    Task<EmbeddingResponse> GenerateEmbeddingAsync(
        string text,
        string model,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Gets the list of available embedding models for this provider
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    /// <returns>List of available model names</returns>
    Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default);
}
