using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmConfig.Models;

/// <summary>
/// Top-level application configuration containing all model configurations and provider registry.
/// </summary>
public record AppConfig
{
    /// <summary>
    /// List of available model configurations.
    /// </summary>
    [JsonPropertyName("models")]
    public required IReadOnlyList<ModelConfig> Models { get; init; }

    /// <summary>
    /// Provider registry containing connection information for all providers.
    /// </summary>
    [JsonPropertyName("provider_registry")]
    public IReadOnlyDictionary<string, ProviderConnectionInfo>? ProviderRegistry { get; init; }

    /// <summary>
    /// Gets a model configuration by its ID.
    /// </summary>
    /// <param name="modelId">The model ID to search for.</param>
    /// <returns>The model configuration if found, null otherwise.</returns>
    public ModelConfig? GetModel(string modelId)
    {
        System.Diagnostics.Debug.WriteLine(
            $"DEBUG: AppConfig.GetModel called with modelId: {modelId ?? "NULL"}"
        );
        System.Diagnostics.Debug.WriteLine($"DEBUG: Models collection is null: {Models == null}");
        if (Models != null && !string.IsNullOrEmpty(modelId))
        {
            System.Diagnostics.Debug.WriteLine($"DEBUG: Models collection count: {Models.Count}");
            var bracketIndex = modelId.IndexOf('[');
            if (bracketIndex >= 0)
            {
                modelId = modelId.Substring(0, bracketIndex);
            }
            return Models.FirstOrDefault(m =>
                m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase)
            );
        }

        return null;
    }

    /// <summary>
    /// Gets all models that have a specific capability.
    /// </summary>
    /// <param name="capability">The capability to search for.</param>
    /// <returns>List of models that have the specified capability.</returns>
    public IReadOnlyList<ModelConfig> GetModelsWithCapability(string capability)
    {
        System.Diagnostics.Debug.WriteLine(
            $"DEBUG: AppConfig.GetModelsWithCapability called with capability: {capability ?? "NULL"}"
        );
        System.Diagnostics.Debug.WriteLine($"DEBUG: Models collection is null: {Models == null}");

        if (Models != null && !string.IsNullOrEmpty(capability))
        {
            System.Diagnostics.Debug.WriteLine($"DEBUG: Models collection count: {Models.Count}");
            var matchingModels = Models.Where(m => m.HasCapability(capability)).ToList();
            System.Diagnostics.Debug.WriteLine(
                $"DEBUG: Found {matchingModels.Count} models with capability {capability}"
            );
            foreach (var model in matchingModels)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"DEBUG: Model {model.Id} has capability {capability}"
                );
            }
            return matchingModels;
        }

        return new List<ModelConfig>();
    }

    /// <summary>
    /// Gets provider connection information by name.
    /// </summary>
    /// <param name="providerName">The provider name to search for.</param>
    /// <returns>The provider connection info if found, null otherwise.</returns>
    public ProviderConnectionInfo? GetProviderConnection(string providerName)
    {
        return ProviderRegistry?.TryGetValue(providerName, out var provider) == true
            ? provider
            : null;
    }

    /// <summary>
    /// Gets all registered provider names.
    /// </summary>
    /// <returns>List of all registered provider names.</returns>
    public IReadOnlyList<string> GetRegisteredProviders()
    {
        return ProviderRegistry?.Keys.ToList() ?? new List<string>();
    }

    /// <summary>
    /// Checks if a provider is registered in the registry.
    /// </summary>
    /// <param name="providerName">The provider name to check.</param>
    /// <returns>True if the provider is registered, false otherwise.</returns>
    public bool IsProviderRegistered(string providerName)
    {
        return ProviderRegistry?.ContainsKey(providerName) == true;
    }
}

/// <summary>
/// Connection information for a provider, configured at infrastructure level.
/// </summary>
public record ProviderConnectionInfo
{
    /// <summary>
    /// The endpoint URL for this provider's API.
    /// </summary>
    [JsonPropertyName("endpoint_url")]
    public required string EndpointUrl { get; init; }

    /// <summary>
    /// Environment variable name containing the API key for this provider.
    /// </summary>
    [JsonPropertyName("api_key_environment_variable")]
    public required string ApiKeyEnvironmentVariable { get; init; }

    /// <summary>
    /// Provider compatibility type (e.g., "OpenAI", "Anthropic").
    /// </summary>
    [JsonPropertyName("compatibility")]
    public string Compatibility { get; init; } = "OpenAI";

    /// <summary>
    /// Custom headers to send with requests to this provider.
    /// </summary>
    [JsonPropertyName("headers")]
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>
    /// Request timeout for this provider.
    /// </summary>
    [JsonPropertyName("timeout")]
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Maximum number of retry attempts for failed requests.
    /// </summary>
    [JsonPropertyName("max_retries")]
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// Human-readable description of this provider.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Gets the API key from the environment variable.
    /// </summary>
    /// <returns>The API key value or null if not found.</returns>
    public string? GetApiKey()
    {
        return Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);
    }

    /// <summary>
    /// Validates that this provider connection info is properly configured.
    /// </summary>
    /// <returns>Validation result indicating if the configuration is valid.</returns>
    public ValidationResult Validate()
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        // Validate required fields
        if (string.IsNullOrWhiteSpace(EndpointUrl))
            errors.Add("EndpointUrl is required");
        else if (!Uri.TryCreate(EndpointUrl, UriKind.Absolute, out _))
            errors.Add("EndpointUrl must be a valid absolute URL");

        if (string.IsNullOrWhiteSpace(ApiKeyEnvironmentVariable))
            errors.Add("ApiKeyEnvironmentVariable is required");

        if (MaxRetries < 0)
            errors.Add("MaxRetries must be non-negative");

        // Check if API key is available
        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            warnings.Add($"Environment variable '{ApiKeyEnvironmentVariable}' is not set or empty");

        return new ValidationResult
        {
            IsValid = !errors.Any(),
            Errors = errors,
            Warnings = warnings,
        };
    }
}

/// <summary>
/// Result of a validation operation.
/// </summary>
public record ValidationResult
{
    /// <summary>
    /// Whether the validation passed (no errors).
    /// </summary>
    public required bool IsValid { get; init; }

    /// <summary>
    /// List of validation errors that prevent operation.
    /// </summary>
    public required IReadOnlyList<string> Errors { get; init; }

    /// <summary>
    /// List of validation warnings that don't prevent operation.
    /// </summary>
    public required IReadOnlyList<string> Warnings { get; init; }
}
