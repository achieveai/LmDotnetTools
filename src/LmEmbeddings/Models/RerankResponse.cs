using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmEmbeddings.Models;

/// <summary>
///     Represents a response from the Cohere rerank API
/// </summary>
public record RerankResponse
{
    /// <summary>
    ///     An ordered list of ranked documents
    /// </summary>
    [JsonPropertyName("results")]
    public required ImmutableList<RerankResult> Results { get; init; }

    /// <summary>
    ///     Unique identifier for the rerank request
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>
    ///     Metadata about the rerank response
    /// </summary>
    [JsonPropertyName("meta")]
    public RerankMeta? Meta { get; init; }
}

/// <summary>
///     Represents a single ranked document result
/// </summary>
public record RerankResult
{
    /// <summary>
    ///     The original index of this document in the input list
    /// </summary>
    [JsonPropertyName("index")]
    public required int Index { get; init; }

    /// <summary>
    ///     The relevance score (0.0 to 1.0, higher values indicate higher relevance)
    /// </summary>
    [JsonPropertyName("relevance_score")]
    public required double RelevanceScore { get; init; }
}

/// <summary>
///     Metadata information from the rerank API response
/// </summary>
public record RerankMeta
{
    /// <summary>
    ///     API version information
    /// </summary>
    [JsonPropertyName("api_version")]
    public RerankApiVersion? ApiVersion { get; init; }

    /// <summary>
    ///     Billing usage information
    /// </summary>
    [JsonPropertyName("billed_units")]
    public RerankUsage? BilledUnits { get; init; }
}

/// <summary>
///     API version information
/// </summary>
public record RerankApiVersion
{
    /// <summary>
    ///     The API version string
    /// </summary>
    [JsonPropertyName("version")]
    public required string Version { get; init; }

    /// <summary>
    ///     Whether this API version is experimental
    /// </summary>
    [JsonPropertyName("is_experimental")]
    public bool? IsExperimental { get; init; }
}

/// <summary>
///     Usage and billing information
/// </summary>
public record RerankUsage
{
    /// <summary>
    ///     Number of search units consumed
    /// </summary>
    [JsonPropertyName("search_units")]
    public required int SearchUnits { get; init; }
}
