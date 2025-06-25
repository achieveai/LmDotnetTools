using AchieveAi.LmDotnetTools.LmConfig.Models;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmEmbeddings.Interfaces;

namespace MemoryServer.Services;

/// <summary>
/// Service for integrating with LmConfig to provide centralized model configuration and provider management.
/// </summary>
public interface ILmConfigService
{
    /// <summary>
    /// Gets the optimal model configuration for a specific capability.
    /// </summary>
    /// <param name="capability">The required capability (e.g., "chat", "embedding", "reranking")</param>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    /// <returns>The optimal model configuration, or null if none found</returns>
    Task<ModelConfig?> GetOptimalModelAsync(string capability, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an agent for a specific capability using the optimal model.
    /// </summary>
    /// <param name="capability">The required capability</param>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    /// <returns>Configured agent instance</returns>
    Task<IAgent> CreateAgentAsync(string capability, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an agent for a specific model ID and capability, bypassing the automatic model selection.
    /// </summary>
    /// <param name="modelId">The specific model ID to use (from models.json)</param>
    /// <param name="capability">The required capability for fallback JSON schema generation</param>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    /// <returns>Configured agent instance for the specified model</returns>
    Task<IAgent> CreateAgentWithModelAsync(string modelId, string capability, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an embedding service using the optimal embedding model.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    /// <returns>Configured embedding service instance</returns>
    Task<IEmbeddingService> CreateEmbeddingServiceAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a reranking service using the optimal reranking model.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    /// <returns>Configured reranking service instance</returns>
    Task<IRerankService> CreateRerankServiceAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the complete application configuration.
    /// </summary>
    /// <returns>The app configuration</returns>
    AppConfig GetConfiguration();

    /// <summary>
    /// Gets all models that support a specific capability.
    /// </summary>
    /// <param name="capability">The required capability</param>
    /// <returns>List of models supporting the capability</returns>
    IReadOnlyList<ModelConfig> GetModelsWithCapability(string capability);

    /// <summary>
    /// Validates that required models are configured for memory operations.
    /// </summary>
    /// <returns>True if all required models are available</returns>
    bool ValidateRequiredModels();
} 