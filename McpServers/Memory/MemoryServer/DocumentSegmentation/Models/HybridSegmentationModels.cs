using MemoryServer.DocumentSegmentation.Models;
using MemoryServer.Models;

namespace MemoryServer.DocumentSegmentation.Services;

/// <summary>
/// Configuration options for hybrid segmentation that combines multiple strategies.
/// </summary>
public class HybridSegmentationOptions
{
    /// <summary>
    /// Minimum segment size in characters.
    /// </summary>
    public int MinSegmentSize { get; set; } = 150;

    /// <summary>
    /// Maximum segment size in characters.
    /// </summary>
    public int MaxSegmentSize { get; set; } = 10000;

    /// <summary>
    /// Maximum number of segments to generate.
    /// </summary>
    public int MaxSegments { get; set; } = 50;

    /// <summary>
    /// Whether to use adaptive strategy weighting based on document characteristics.
    /// </summary>
    public bool UseAdaptiveWeighting { get; set; } = true;

    /// <summary>
    /// Whether to apply post-processing optimization to merged results.
    /// </summary>
    public bool ApplyPostProcessingOptimization { get; set; } = true;

    /// <summary>
    /// Minimum consensus score required for segment boundaries (0.0-1.0).
    /// </summary>
    public double MinConsensusScore { get; set; } = 0.6;

    /// <summary>
    /// Strategy weighting preferences. If null, will be determined automatically.
    /// </summary>
    public StrategyWeights? PreferredWeights { get; set; }

    /// <summary>
    /// Whether to use LLM enhancement for strategy combination.
    /// </summary>
    public bool UseLlmEnhancement { get; set; } = true;

    /// <summary>
    /// Quality threshold for accepting hybrid segmentation results (0.0-1.0).
    /// </summary>
    public double QualityThreshold { get; set; } = 0.75;

    /// <summary>
    /// Whether to fallback to single-strategy if hybrid approach fails.
    /// </summary>
    public bool EnableFallback { get; set; } = true;

    /// <summary>
    /// Primary fallback strategy to use if hybrid approach fails.
    /// </summary>
    public SegmentationStrategy FallbackStrategy { get; set; } = SegmentationStrategy.TopicBased;
}

/// <summary>
/// Represents the relative weights for different segmentation strategies in hybrid approach.
/// </summary>
public class StrategyWeights
{
    /// <summary>
    /// Weight for structure-based segmentation (0.0-1.0).
    /// </summary>
    public double StructureWeight { get; set; } = 0.33;

    /// <summary>
    /// Weight for narrative-based segmentation (0.0-1.0).
    /// </summary>
    public double NarrativeWeight { get; set; } = 0.33;

    /// <summary>
    /// Weight for topic-based segmentation (0.0-1.0).
    /// </summary>
    public double TopicWeight { get; set; } = 0.34;

    /// <summary>
    /// Confidence in the determined weights (0.0-1.0).
    /// </summary>
    public double Confidence { get; set; } = 0.8;

    /// <summary>
    /// Method used to determine the weights.
    /// </summary>
    public WeightDeterminationMethod Method { get; set; } = WeightDeterminationMethod.Automatic;

    /// <summary>
    /// Explanation of why these weights were chosen.
    /// </summary>
    public string Rationale { get; set; } = string.Empty;

    /// <summary>
    /// Validates that weights sum to approximately 1.0.
    /// </summary>
    public bool AreValid => Math.Abs((StructureWeight + NarrativeWeight + TopicWeight) - 1.0) < 0.01;

    /// <summary>
    /// Normalizes weights to sum to 1.0.
    /// </summary>
    public void Normalize()
    {
        var total = StructureWeight + NarrativeWeight + TopicWeight;
        if (total > 0)
        {
            StructureWeight /= total;
            NarrativeWeight /= total;
            TopicWeight /= total;
        }
        else
        {
            // Equal weights if all are zero
            StructureWeight = NarrativeWeight = TopicWeight = 1.0 / 3.0;
        }
    }
}

/// <summary>
/// Method used to determine strategy weights.
/// </summary>
public enum WeightDeterminationMethod
{
    /// <summary>
    /// Weights determined automatically based on document analysis.
    /// </summary>
    Automatic,

    /// <summary>
    /// Weights specified manually by user or configuration.
    /// </summary>
    Manual,

    /// <summary>
    /// Weights learned from previous segmentation results.
    /// </summary>
    Learned,

    /// <summary>
    /// Weights determined using LLM analysis.
    /// </summary>
    LlmAnalysis,

    /// <summary>
    /// Equal weights for all strategies.
    /// </summary>
    Equal,
}

/// <summary>
/// Validation results for hybrid segmentation approach.
/// </summary>
public class HybridSegmentationValidation
{
    /// <summary>
    /// Overall quality score for the hybrid segmentation (0.0-1.0).
    /// </summary>
    public double OverallQuality { get; set; }

    /// <summary>
    /// How well the strategies were combined (0.0-1.0).
    /// </summary>
    public double StrategyCombinationScore { get; set; }

    /// <summary>
    /// Consistency of segments across different strategies (0.0-1.0).
    /// </summary>
    public double CrossStrategyConsistency { get; set; }

    /// <summary>
    /// Quality of boundary consensus between strategies (0.0-1.0).
    /// </summary>
    public double BoundaryConsensusQuality { get; set; }

    /// <summary>
    /// Effectiveness of the chosen strategy weights (0.0-1.0).
    /// </summary>
    public double WeightEffectiveness { get; set; }

    /// <summary>
    /// Individual validation results for each contributing strategy.
    /// </summary>
    public Dictionary<SegmentationStrategy, double> StrategyValidationScores { get; set; } = new();

    /// <summary>
    /// Issues identified during hybrid segmentation validation.
    /// </summary>
    public List<ValidationIssue> Issues { get; set; } = new();

    /// <summary>
    /// Recommendations for improving hybrid segmentation quality.
    /// </summary>
    public List<string> Recommendations { get; set; } = new();

    /// <summary>
    /// Whether the hybrid segmentation meets quality standards.
    /// </summary>
    public bool MeetsQualityStandards =>
        OverallQuality >= 0.7
        && !Issues.Any(i => i.Severity == MemoryServer.DocumentSegmentation.Models.ValidationSeverity.Error);
}

/// <summary>
/// Adaptive configuration for dynamic strategy adjustment.
/// </summary>
public class AdaptiveStrategyConfig
{
    /// <summary>
    /// Recommended strategy weights based on adaptation.
    /// </summary>
    public StrategyWeights RecommendedWeights { get; set; } = new();

    /// <summary>
    /// Confidence in the adaptive recommendations (0.0-1.0).
    /// </summary>
    public double AdaptationConfidence { get; set; }

    /// <summary>
    /// Primary strategy recommended for this document type.
    /// </summary>
    public SegmentationStrategy PrimaryStrategy { get; set; }

    /// <summary>
    /// Secondary strategy for supplemental segmentation.
    /// </summary>
    public SegmentationStrategy? SecondaryStrategy { get; set; }

    /// <summary>
    /// Reasons for the adaptive strategy changes.
    /// </summary>
    public List<string> AdaptationReasons { get; set; } = new();

    /// <summary>
    /// Performance metrics that influenced the adaptation.
    /// </summary>
    public Dictionary<string, double> PerformanceMetrics { get; set; } = new();

    /// <summary>
    /// Whether this configuration should be applied.
    /// </summary>
    public bool ShouldApply => AdaptationConfidence >= 0.7;
}

/// <summary>
/// Results from boundary consensus analysis across multiple strategies.
/// </summary>
public class BoundaryConsensus
{
    /// <summary>
    /// Position in the document where consensus boundary occurs.
    /// </summary>
    public int Position { get; set; }

    /// <summary>
    /// Number of strategies that agree on this boundary.
    /// </summary>
    public int AgreementCount { get; set; }

    /// <summary>
    /// Total number of strategies considered.
    /// </summary>
    public int TotalStrategies { get; set; }

    /// <summary>
    /// Consensus strength (0.0-1.0).
    /// </summary>
    public double ConsensusStrength => TotalStrategies > 0 ? (double)AgreementCount / TotalStrategies : 0.0;

    /// <summary>
    /// Strategies that agree on this boundary.
    /// </summary>
    public List<SegmentationStrategy> AgreingStrategies { get; set; } = new();

    /// <summary>
    /// Average confidence of agreeing strategies.
    /// </summary>
    public double AverageConfidence { get; set; }

    /// <summary>
    /// Whether this boundary should be included in final segmentation.
    /// </summary>
    public bool ShouldInclude { get; set; }
}

/// <summary>
/// Segment merge operation for combining results from multiple strategies.
/// </summary>
public class SegmentMergeOperation
{
    /// <summary>
    /// Source segments to be merged.
    /// </summary>
    public List<DocumentSegment> SourceSegments { get; set; } = new();

    /// <summary>
    /// Strategy that produced each source segment.
    /// </summary>
    public Dictionary<string, SegmentationStrategy> SegmentSources { get; set; } = new();

    /// <summary>
    /// Merge strategy to use for combining segments.
    /// </summary>
    public SegmentMergeStrategy MergeStrategy { get; set; } = SegmentMergeStrategy.WeightedCombination;

    /// <summary>
    /// Quality threshold for including segments in merge.
    /// </summary>
    public double QualityThreshold { get; set; } = 0.5;

    /// <summary>
    /// Result of the merge operation.
    /// </summary>
    public DocumentSegment? MergedSegment { get; set; }

    /// <summary>
    /// Success indicator for the merge operation.
    /// </summary>
    public bool MergeSuccessful { get; set; }
}

/// <summary>
/// Strategy for merging segments from multiple approaches.
/// </summary>
public enum SegmentMergeStrategy
{
    /// <summary>
    /// Take the highest quality segment.
    /// </summary>
    BestQuality,

    /// <summary>
    /// Combine segments using weighted averaging.
    /// </summary>
    WeightedCombination,

    /// <summary>
    /// Take the longest segment.
    /// </summary>
    Longest,

    /// <summary>
    /// Take segment from highest-weighted strategy.
    /// </summary>
    HighestWeight,

    /// <summary>
    /// Combine content and merge metadata.
    /// </summary>
    ContentMerge,
}
