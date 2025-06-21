using AchieveAi.LmDotnetTools.LmConfig.Models;

namespace AchieveAi.LmDotnetTools.LmConfig.Agents;

/// <summary>
/// Service responsible for resolving which provider to use for a given model based on configuration,
/// availability, and selection criteria.
/// </summary>
public interface IModelResolver
{
    /// <summary>
    /// Resolves the best provider for a given model ID based on the selection criteria.
    /// </summary>
    /// <param name="modelId">The model ID to resolve a provider for.</param>
    /// <param name="criteria">Optional criteria for provider selection.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The resolved provider configuration, or null if no suitable provider is found.</returns>
    Task<ProviderResolution?> ResolveProviderAsync(
        string modelId, 
        ProviderSelectionCriteria? criteria = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all available providers for a given model ID, ordered by preference.
    /// </summary>
    /// <param name="modelId">The model ID to get providers for.</param>
    /// <param name="criteria">Optional criteria for filtering and ordering providers.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>List of all available provider resolutions for the model, ordered by preference.</returns>
    Task<IReadOnlyList<ProviderResolution>> GetAvailableProvidersAsync(
        string modelId, 
        ProviderSelectionCriteria? criteria = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a specific provider is available and properly configured.
    /// </summary>
    /// <param name="providerName">The name of the provider to check.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True if the provider is available and configured, false otherwise.</returns>
    Task<bool> IsProviderAvailableAsync(
        string providerName, 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all models that have a specific capability.
    /// </summary>
    /// <param name="capability">The capability to search for (e.g., "function-calling", "multimodal").</param>
    /// <param name="criteria">Optional criteria for filtering providers.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>List of model configurations that have the specified capability.</returns>
    Task<IReadOnlyList<ModelConfig>> GetModelsWithCapabilityAsync(
        string capability,
        ProviderSelectionCriteria? criteria = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the best model and provider for a specific capability.
    /// This is useful when you need a model with specific capabilities but don't care about the exact model.
    /// </summary>
    /// <param name="capability">The required capability.</param>
    /// <param name="criteria">Optional criteria for selection.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The best provider resolution for the capability, or null if none found.</returns>
    Task<ProviderResolution?> ResolveByCapabilityAsync(
        string capability,
        ProviderSelectionCriteria? criteria = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates the current configuration and returns any issues found.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>Validation result with any errors or warnings.</returns>
    Task<ValidationResult> ValidateConfigurationAsync(CancellationToken cancellationToken = default);
} 