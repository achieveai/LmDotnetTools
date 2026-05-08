using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmConfig.Models;

/// <summary>
///     Cache structure for OpenRouter model data.
/// </summary>
public record OpenRouterCache
{
    /// <summary>
    ///     When the cache was created.
    /// </summary>
    [JsonPropertyName("cached_at")]
    public DateTime CachedAt { get; init; }

    /// <summary>
    ///     Raw models data from OpenRouter API.
    /// </summary>
    [JsonPropertyName("models_data")]
    public JsonNode? ModelsData { get; init; }

    /// <summary>
    ///     Model details data keyed by model slug.
    /// </summary>
    [JsonPropertyName("model_details")]
    public Dictionary<string, JsonNode> ModelDetails { get; init; } = [];

    /// <summary>
    ///     Checks if the cache is still valid (less than 24 hours old).
    /// </summary>
    public bool IsValid => DateTime.UtcNow - CachedAt < TimeSpan.FromHours(24);
}

/// <summary>
///     OpenRouter model information from the API.
/// </summary>
public record OpenRouterModel
{
    [JsonPropertyName("slug")]
    public string Slug { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("context_length")]
    public int ContextLength { get; init; }

    [JsonPropertyName("input_modalities")]
    public string[] InputModalities { get; init; } = [];

    [JsonPropertyName("output_modalities")]
    public string[] OutputModalities { get; init; } = [];

    [JsonPropertyName("has_text_output")]
    public bool HasTextOutput { get; init; }

    [JsonPropertyName("group")]
    public string? Group { get; init; }

    [JsonPropertyName("author")]
    public string? Author { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

/// <summary>
///     OpenRouter endpoint/provider information.
/// </summary>
public record OpenRouterEndpoint
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("provider_name")]
    public string ProviderName { get; init; } = string.Empty;

    [JsonPropertyName("adapter_name")]
    public string AdapterName { get; init; } = string.Empty;

    [JsonPropertyName("model_variant_slug")]
    public string ModelVariantSlug { get; init; } = string.Empty;

    [JsonPropertyName("provider_info")]
    public OpenRouterProviderInfo? ProviderInfo { get; init; }
}

/// <summary>
///     OpenRouter provider information.
/// </summary>
public record OpenRouterProviderInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("slug")]
    public string Slug { get; init; } = string.Empty;

    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; init; } = string.Empty;
}
