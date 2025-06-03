using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmConfig.Models;

/// <summary>
/// Top-level application configuration containing all model configurations.
/// </summary>
public record AppConfig
{
    /// <summary>
    /// List of available model configurations.
    /// </summary>
    [JsonPropertyName("models")]
    public required IReadOnlyList<ModelConfig> Models { get; init; }

    /// <summary>
    /// Gets a model configuration by its ID.
    /// </summary>
    /// <param name="modelId">The model ID to search for.</param>
    /// <returns>The model configuration if found, null otherwise.</returns>
    public ModelConfig? GetModel(string modelId)
    {
        return Models.FirstOrDefault(m => m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets all models that have a specific capability.
    /// </summary>
    /// <param name="capability">The capability to search for.</param>
    /// <returns>List of models that have the specified capability.</returns>
    public IReadOnlyList<ModelConfig> GetModelsWithCapability(string capability)
    {
        return Models.Where(m => m.HasCapability(capability)).ToList();
    }
} 