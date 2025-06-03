using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmEmbeddings.Models;

/// <summary>
/// Represents a request to the Cohere rerank API
/// </summary>
public record RerankRequest
{
    /// <summary>
    /// The identifier of the model to use (e.g., "rerank-v3.5")
    /// </summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    /// <summary>
    /// The search query to rank documents against
    /// </summary>
    [JsonPropertyName("query")]
    public required string Query { get; init; }

    /// <summary>
    /// A list of documents to be ranked. For optimal performance, 
    /// avoid sending more than 1,000 documents in a single request.
    /// </summary>
    [JsonPropertyName("documents")]
    public required ImmutableList<string> Documents { get; init; }

    /// <summary>
    /// Limits the number of returned rerank results to the specified value. 
    /// If not specified, all rerank results will be returned.
    /// </summary>
    [JsonPropertyName("top_n")]
    public int? TopN { get; init; }

    /// <summary>
    /// Maximum tokens per document. Defaults to 4096 in the API.
    /// Long documents will be automatically truncated to this number of tokens.
    /// </summary>
    [JsonPropertyName("max_tokens_per_doc")]
    public int? MaxTokensPerDoc { get; init; }
} 