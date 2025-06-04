using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmEmbeddings.Models;

/// <summary>
/// Quality metrics for embedding and reranking operations
/// </summary>
public record QualityMetrics
{
    /// <summary>
    /// Average response quality score (0-1)
    /// </summary>
    [JsonPropertyName("avg_quality_score")]
    public double? AvgQualityScore { get; init; }

    /// <summary>
    /// Percentage of responses that met quality thresholds
    /// </summary>
    [JsonPropertyName("quality_threshold_met_percent")]
    public double? QualityThresholdMetPercent { get; init; }

    /// <summary>
    /// User satisfaction scores (if available)
    /// </summary>
    [JsonPropertyName("user_satisfaction")]
    public double? UserSatisfaction { get; init; }

    /// <summary>
    /// Embedding coherence metrics
    /// </summary>
    [JsonPropertyName("coherence_metrics")]
    public CoherenceMetrics? CoherenceMetrics { get; init; }
}

/// <summary>
/// Coherence metrics for embedding quality assessment
/// </summary>
public record CoherenceMetrics
{
    /// <summary>
    /// Average cosine similarity within document clusters
    /// </summary>
    [JsonPropertyName("avg_intra_cluster_similarity")]
    public double? AvgIntraClusterSimilarity { get; init; }

    /// <summary>
    /// Average distance between different document clusters
    /// </summary>
    [JsonPropertyName("avg_inter_cluster_distance")]
    public double? AvgInterClusterDistance { get; init; }

    /// <summary>
    /// Silhouette score for clustering quality
    /// </summary>
    [JsonPropertyName("silhouette_score")]
    public double? SilhouetteScore { get; init; }
} 