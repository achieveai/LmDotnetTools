using MemoryServer.DocumentSegmentation.Models;
using MemoryServer.Models;

namespace MemoryServer.DocumentSegmentation.Services;

/// <summary>
/// Configuration options for quality assessment.
/// </summary>
public class QualityAssessmentOptions
{
    /// <summary>
    /// Minimum coherence score threshold (0.0-1.0).
    /// </summary>
    public double MinCoherenceThreshold { get; set; } = 0.7;

    /// <summary>
    /// Minimum independence score threshold (0.0-1.0).
    /// </summary>
    public double MinIndependenceThreshold { get; set; } = 0.6;

    /// <summary>
    /// Minimum topic consistency threshold (0.0-1.0).
    /// </summary>
    public double MinTopicConsistencyThreshold { get; set; } = 0.7;

    /// <summary>
    /// Minimum overall quality score threshold (0.0-1.0).
    /// </summary>
    public double MinOverallQualityThreshold { get; set; } = 0.75;

    /// <summary>
    /// Maximum acceptable content overlap between segments (0.0-1.0).
    /// </summary>
    public double MaxContentOverlapThreshold { get; set; } = 0.1;

    /// <summary>
    /// Minimum segment completeness score (0.0-1.0).
    /// </summary>
    public double MinCompletenessThreshold { get; set; } = 0.8;

    /// <summary>
    /// Whether to use LLM enhancement for quality assessment.
    /// </summary>
    public bool UseLlmEnhancement { get; set; } = true;

    /// <summary>
    /// Whether to perform deep semantic analysis.
    /// </summary>
    public bool PerformDeepSemanticAnalysis { get; set; } = true;

    /// <summary>
    /// Whether to validate cross-segment relationships.
    /// </summary>
    public bool ValidateCrossSegmentRelationships { get; set; } = true;

    /// <summary>
    /// Custom weights for different quality metrics.
    /// </summary>
    public QualityMetricWeights MetricWeights { get; set; } = new();

    /// <summary>
    /// Specific validation rules to apply.
    /// </summary>
    public List<ValidationRule> ValidationRules { get; set; } = new();
}

/// <summary>
/// Weights for different quality metrics in overall score calculation.
/// </summary>
public class QualityMetricWeights
{
    /// <summary>
    /// Weight for coherence score (0.0-1.0).
    /// </summary>
    public double CoherenceWeight { get; set; } = 0.3;

    /// <summary>
    /// Weight for independence score (0.0-1.0).
    /// </summary>
    public double IndependenceWeight { get; set; } = 0.25;

    /// <summary>
    /// Weight for topic consistency score (0.0-1.0).
    /// </summary>
    public double TopicConsistencyWeight { get; set; } = 0.3;

    /// <summary>
    /// Weight for completeness score (0.0-1.0).
    /// </summary>
    public double CompletenessWeight { get; set; } = 0.15;

    /// <summary>
    /// Validates that weights sum to approximately 1.0.
    /// </summary>
    public bool AreValid => Math.Abs((CoherenceWeight + IndependenceWeight + TopicConsistencyWeight + CompletenessWeight) - 1.0) < 0.01;

    /// <summary>
    /// Normalizes weights to sum to 1.0.
    /// </summary>
    public void Normalize()
    {
        var total = CoherenceWeight + IndependenceWeight + TopicConsistencyWeight + CompletenessWeight;
        if (total > 0)
        {
            CoherenceWeight /= total;
            IndependenceWeight /= total;
            TopicConsistencyWeight /= total;
            CompletenessWeight /= total;
        }
        else
        {
            CoherenceWeight = IndependenceWeight = TopicConsistencyWeight = CompletenessWeight = 0.25;
        }
    }
}

/// <summary>
/// Comprehensive quality assessment results with detailed metrics and analysis.
/// </summary>
public class ComprehensiveQualityAssessment
{
    /// <summary>
    /// Overall quality score (0.0-1.0).
    /// </summary>
    public double OverallQualityScore { get; set; }

    /// <summary>
    /// Individual quality metric scores.
    /// </summary>
    public QualityMetricScores MetricScores { get; set; } = new();

    /// <summary>
    /// Detailed semantic coherence validation results.
    /// </summary>
    public List<SemanticCoherenceValidation> CoherenceValidations { get; set; } = new();

    /// <summary>
    /// Independence score analysis for each segment.
    /// </summary>
    public List<IndependenceScoreAnalysis> IndependenceAnalyses { get; set; } = new();

    /// <summary>
    /// Topic consistency validation results.
    /// </summary>
    public TopicConsistencyValidation TopicConsistency { get; set; } = new();

    /// <summary>
    /// Completeness verification results.
    /// </summary>
    public CompletenessVerification Completeness { get; set; } = new();

    /// <summary>
    /// Quality issue analysis.
    /// </summary>
    public QualityIssueAnalysis QualityIssues { get; set; } = new();

    /// <summary>
    /// Improvement recommendations.
    /// </summary>
    public ImprovementRecommendations Recommendations { get; set; } = new();

    /// <summary>
    /// Whether the segmentation meets quality standards.
    /// </summary>
    public bool MeetsQualityStandards { get; set; }

    /// <summary>
    /// Confidence in the quality assessment (0.0-1.0).
    /// </summary>
    public double AssessmentConfidence { get; set; }

    /// <summary>
    /// Processing time for the assessment in milliseconds.
    /// </summary>
    public long ProcessingTimeMs { get; set; }

    /// <summary>
    /// Metadata about the assessment process.
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
/// Individual quality metric scores.
/// </summary>
public class QualityMetricScores
{
    /// <summary>
    /// Average coherence score across all segments (0.0-1.0).
    /// </summary>
    public double AverageCoherenceScore { get; set; }

    /// <summary>
    /// Average independence score across all segments (0.0-1.0).
    /// </summary>
    public double AverageIndependenceScore { get; set; }

    /// <summary>
    /// Average topic consistency score across all segments (0.0-1.0).
    /// </summary>
    public double AverageTopicConsistencyScore { get; set; }

    /// <summary>
    /// Overall completeness score (0.0-1.0).
    /// </summary>
    public double CompletenessScore { get; set; }

    /// <summary>
    /// Cross-segment relationship quality score (0.0-1.0).
    /// </summary>
    public double RelationshipQualityScore { get; set; }

    /// <summary>
    /// Content coverage score (0.0-1.0).
    /// </summary>
    public double ContentCoverageScore { get; set; }

    /// <summary>
    /// Boundary quality score (0.0-1.0).
    /// </summary>
    public double BoundaryQualityScore { get; set; }
}

/// <summary>
/// Semantic coherence validation results for a segment.
/// </summary>
public class SemanticCoherenceValidation
{
    /// <summary>
    /// Segment ID being validated.
    /// </summary>
    public string SegmentId { get; set; } = string.Empty;

    /// <summary>
    /// Coherence score (0.0-1.0).
    /// </summary>
    public double CoherenceScore { get; set; }

    /// <summary>
    /// Lexical coherence score (0.0-1.0).
    /// </summary>
    public double LexicalCoherenceScore { get; set; }

    /// <summary>
    /// Semantic coherence score (0.0-1.0).
    /// </summary>
    public double SemanticCoherenceScore { get; set; }

    /// <summary>
    /// Structural coherence score (0.0-1.0).
    /// </summary>
    public double StructuralCoherenceScore { get; set; }

    /// <summary>
    /// Identified coherence issues.
    /// </summary>
    public List<CoherenceIssue> CoherenceIssues { get; set; } = new();

    /// <summary>
    /// Whether the segment passes coherence validation.
    /// </summary>
    public bool PassesValidation { get; set; }

    /// <summary>
    /// Detailed analysis notes.
    /// </summary>
    public string AnalysisNotes { get; set; } = string.Empty;
}

/// <summary>
/// Independence score analysis for a segment.
/// </summary>
public class IndependenceScoreAnalysis
{
    /// <summary>
    /// Segment ID being analyzed.
    /// </summary>
    public string SegmentId { get; set; } = string.Empty;

    /// <summary>
    /// Overall independence score (0.0-1.0).
    /// </summary>
    public double IndependenceScore { get; set; }

    /// <summary>
    /// Self-containment score (0.0-1.0).
    /// </summary>
    public double SelfContainmentScore { get; set; }

    /// <summary>
    /// Context dependency score (0.0-1.0, lower is better).
    /// </summary>
    public double ContextDependencyScore { get; set; }

    /// <summary>
    /// Cross-reference dependency score (0.0-1.0, lower is better).
    /// </summary>
    public double CrossReferenceDependencyScore { get; set; }

    /// <summary>
    /// Identified dependencies on other segments.
    /// </summary>
    public List<SegmentDependency> Dependencies { get; set; } = new();

    /// <summary>
    /// Whether the segment is sufficiently independent.
    /// </summary>
    public bool IsIndependent { get; set; }

    /// <summary>
    /// Recommendations for improving independence.
    /// </summary>
    public List<string> IndependenceRecommendations { get; set; } = new();
}

/// <summary>
/// Topic consistency validation results.
/// </summary>
public class TopicConsistencyValidation
{
    /// <summary>
    /// Overall topic consistency score (0.0-1.0).
    /// </summary>
    public double OverallConsistencyScore { get; set; }

    /// <summary>
    /// Within-segment topic consistency scores.
    /// </summary>
    public Dictionary<string, double> WithinSegmentConsistency { get; set; } = new();

    /// <summary>
    /// Cross-segment topic overlap analysis.
    /// </summary>
    public List<TopicOverlapAnalysis> TopicOverlaps { get; set; } = new();

    /// <summary>
    /// Identified topic violations.
    /// </summary>
    public List<TopicViolation> TopicViolations { get; set; } = new();

    /// <summary>
    /// Topic distribution across segments.
    /// </summary>
    public Dictionary<string, List<string>> TopicDistribution { get; set; } = new();

    /// <summary>
    /// Whether topic consistency meets standards.
    /// </summary>
    public bool MeetsConsistencyStandards { get; set; }
}

/// <summary>
/// Completeness verification results.
/// </summary>
public class CompletenessVerification
{
    /// <summary>
    /// Overall completeness score (0.0-1.0).
    /// </summary>
    public double CompletenessScore { get; set; }

    /// <summary>
    /// Content coverage percentage (0.0-1.0).
    /// </summary>
    public double ContentCoveragePercentage { get; set; }

    /// <summary>
    /// Information preservation score (0.0-1.0).
    /// </summary>
    public double InformationPreservationScore { get; set; }

    /// <summary>
    /// Identified content gaps.
    /// </summary>
    public List<ContentGap> ContentGaps { get; set; } = new();

    /// <summary>
    /// Identified content overlaps.
    /// </summary>
    public List<ContentOverlap> ContentOverlaps { get; set; } = new();

    /// <summary>
    /// Missing content analysis.
    /// </summary>
    public List<string> MissingContentAreas { get; set; } = new();

    /// <summary>
    /// Whether completeness meets standards.
    /// </summary>
    public bool MeetsCompletenessStandards { get; set; }
}

/// <summary>
/// Quality issue analysis results.
/// </summary>
public class QualityIssueAnalysis
{
    /// <summary>
    /// Total number of quality issues identified.
    /// </summary>
    public int TotalIssueCount { get; set; }

    /// <summary>
    /// Issues categorized by severity.
    /// </summary>
    public Dictionary<QualityIssueSeverity, List<QualityIssue>> IssuesBySeverity { get; set; } = new();

    /// <summary>
    /// Issues categorized by type.
    /// </summary>
    public Dictionary<QualityIssueType, List<QualityIssue>> IssuesByType { get; set; } = new();

    /// <summary>
    /// Issues categorized by affected segment.
    /// </summary>
    public Dictionary<string, List<QualityIssue>> IssuesBySegment { get; set; } = new();

    /// <summary>
    /// Overall issue severity score (0.0-1.0, lower is better).
    /// </summary>
    public double OverallIssueSeverityScore { get; set; }

    /// <summary>
    /// Whether the issue level is acceptable.
    /// </summary>
    public bool IsAcceptableIssueLevel { get; set; }
}

/// <summary>
/// Improvement recommendations based on quality assessment.
/// </summary>
public class ImprovementRecommendations
{
    /// <summary>
    /// Priority recommendations for immediate action.
    /// </summary>
    public List<QualityRecommendation> HighPriorityRecommendations { get; set; } = new();

    /// <summary>
    /// Medium priority recommendations for enhancement.
    /// </summary>
    public List<QualityRecommendation> MediumPriorityRecommendations { get; set; } = new();

    /// <summary>
    /// Low priority recommendations for optimization.
    /// </summary>
    public List<QualityRecommendation> LowPriorityRecommendations { get; set; } = new();

    /// <summary>
    /// Strategy-specific recommendations.
    /// </summary>
    public Dictionary<SegmentationStrategy, List<QualityRecommendation>> StrategyRecommendations { get; set; } = new();

    /// <summary>
    /// Implementation guidance for recommendations.
    /// </summary>
    public List<ImplementationGuidance> ImplementationGuidance { get; set; } = new();

    /// <summary>
    /// Expected impact of implementing recommendations.
    /// </summary>
    public ExpectedImpactAnalysis ExpectedImpact { get; set; } = new();
}

/// <summary>
/// Comparative quality analysis across different segmentation approaches.
/// </summary>
public class ComparativeQualityAnalysis
{
    /// <summary>
    /// Quality scores for each strategy.
    /// </summary>
    public Dictionary<SegmentationStrategy, double> StrategyQualityScores { get; set; } = new();

    /// <summary>
    /// Detailed metric comparison across strategies.
    /// </summary>
    public Dictionary<SegmentationStrategy, QualityMetricScores> MetricComparison { get; set; } = new();

    /// <summary>
    /// Best performing strategy overall.
    /// </summary>
    public SegmentationStrategy BestOverallStrategy { get; set; }

    /// <summary>
    /// Best strategies for specific metrics.
    /// </summary>
    public Dictionary<string, SegmentationStrategy> BestStrategyByMetric { get; set; } = new();

    /// <summary>
    /// Strategy ranking by overall quality.
    /// </summary>
    public List<StrategyRanking> StrategyRankings { get; set; } = new();

    /// <summary>
    /// Comparative analysis insights.
    /// </summary>
    public List<string> ComparativeInsights { get; set; } = new();
}

/// <summary>
/// Custom validation results for user-defined criteria.
/// </summary>
public class CustomValidationResults
{
    /// <summary>
    /// Results for each custom criterion.
    /// </summary>
    public Dictionary<string, CustomCriterionResult> CriterionResults { get; set; } = new();

    /// <summary>
    /// Overall custom validation score (0.0-1.0).
    /// </summary>
    public double OverallCustomScore { get; set; }

    /// <summary>
    /// Whether custom validation passes.
    /// </summary>
    public bool PassesCustomValidation { get; set; }

    /// <summary>
    /// Custom validation feedback.
    /// </summary>
    public List<string> CustomFeedback { get; set; } = new();
}

// Supporting model classes...

/// <summary>
/// Represents a coherence issue within a segment.
/// </summary>
public class CoherenceIssue
{
    public CoherenceIssueType Type { get; set; }
    public string Description { get; set; } = string.Empty;
    public double Severity { get; set; }
    public int Position { get; set; }
    public string Context { get; set; } = string.Empty;
}

/// <summary>
/// Types of coherence issues.
/// </summary>
public enum CoherenceIssueType
{
    LexicalInconsistency,
    SemanticDisconnection,
    StructuralBreak,
    TopicShift,
    LogicalGap,
    ReferentialAmbiguity
}

/// <summary>
/// Represents a dependency between segments.
/// </summary>
public class SegmentDependency
{
    public string DependentSegmentId { get; set; } = string.Empty;
    public string DependsOnSegmentId { get; set; } = string.Empty;
    public DependencyType Type { get; set; }
    public double Strength { get; set; }
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Types of segment dependencies.
/// </summary>
public enum DependencyType
{
    Referential,
    Contextual,
    Sequential,
    Causal,
    Conceptual,
    Definitional
}

/// <summary>
/// Topic overlap analysis between segments.
/// </summary>
public class TopicOverlapAnalysis
{
    public string Segment1Id { get; set; } = string.Empty;
    public string Segment2Id { get; set; } = string.Empty;
    public double OverlapPercentage { get; set; }
    public List<string> SharedTopics { get; set; } = new();
    public bool IsProblematic { get; set; }
    public string OverlapReason { get; set; } = string.Empty;
}

/// <summary>
/// Topic violation within segmentation.
/// </summary>
public class TopicViolation
{
    public string SegmentId { get; set; } = string.Empty;
    public TopicViolationType Type { get; set; }
    public string Description { get; set; } = string.Empty;
    public double Severity { get; set; }
    public List<string> ConflictingTopics { get; set; } = new();
}

/// <summary>
/// Types of topic violations.
/// </summary>
public enum TopicViolationType
{
    MultipleTopicsInSegment,
    TopicSplitAcrossSegments,
    InconsistentTopicBoundaries,
    OrphanedTopicContent,
    TopicContinuityBreak,
    UnrelatedContentMixing
}

/// <summary>
/// Content gap in segmentation.
/// </summary>
public class ContentGap
{
    public int StartPosition { get; set; }
    public int EndPosition { get; set; }
    public string MissingContent { get; set; } = string.Empty;
    public GapType Type { get; set; }
    public double Significance { get; set; }
    public List<string> PotentialCauses { get; set; } = new();
}

/// <summary>
/// Content overlap between segments.
/// </summary>
public class ContentOverlap
{
    public string Segment1Id { get; set; } = string.Empty;
    public string Segment2Id { get; set; } = string.Empty;
    public string OverlappingContent { get; set; } = string.Empty;
    public double OverlapPercentage { get; set; }
    public OverlapType Type { get; set; }
    public bool IsProblematic { get; set; }
}

/// <summary>
/// Quality issue with details.
/// </summary>
public class QualityIssue
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public QualityIssueType Type { get; set; }
    public QualityIssueSeverity Severity { get; set; }
    public string Description { get; set; } = string.Empty;
    public List<string> AffectedSegmentIds { get; set; } = new();
    public string Context { get; set; } = string.Empty;
    public List<string> RecommendedActions { get; set; } = new();
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
/// Quality recommendation for improvement.
/// </summary>
public class QualityRecommendation
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public RecommendationType Type { get; set; }
    public RecommendationPriority Priority { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> AffectedSegmentIds { get; set; } = new();
    public List<string> ActionSteps { get; set; } = new();
    public double ExpectedImpact { get; set; }
    public string Implementation { get; set; } = string.Empty;
}

/// <summary>
/// Enums for categorization
/// </summary>
public enum QualityIssueType
{
    PoorCoherence,
    LowIndependence,
    TopicInconsistency,
    CompletenessGap,
    BoundaryIssue,
    ContentOverlap,
    StructuralProblem,
    SemanticIssue
}

public enum QualityIssueSeverity
{
    Low,
    Medium,
    High,
    Critical
}

public enum GapType
{
    ContentMissing,
    TransitionMissing,
    ContextMissing,
    ConclusionMissing
}

public enum OverlapType
{
    ExactDuplication,
    ParaphrasingOverlap,
    ConceptualOverlap,
    ReferentialOverlap
}

public enum RecommendationType
{
    SegmentMerge,
    SegmentSplit,
    BoundaryAdjustment,
    ContentReorganization,
    StrategyChange,
    QualityImprovement
}

public enum RecommendationPriority
{
    Low,
    Medium,
    High,
    Critical
}

/// <summary>
/// Additional supporting classes for completeness
/// </summary>
public class ValidationRule
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ValidationRuleType Type { get; set; }
    public Dictionary<string, object> Parameters { get; set; } = new();
    public bool IsEnabled { get; set; } = true;
}

public enum ValidationRuleType
{
    MinimumLength,
    MaximumLength,
    CoherenceThreshold,
    TopicConsistency,
    CustomRule
}

public class CustomQualityCriterion
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Func<DocumentSegment, bool> ValidationFunction { get; set; } = _ => true;
    public double Weight { get; set; } = 1.0;
}

public class CustomCriterionResult
{
    public bool Passed { get; set; }
    public double Score { get; set; }
    public string Feedback { get; set; } = string.Empty;
}

public class StrategyRanking
{
    public SegmentationStrategy Strategy { get; set; }
    public int Rank { get; set; }
    public double Score { get; set; }
    public string Strengths { get; set; } = string.Empty;
    public string Weaknesses { get; set; } = string.Empty;
}

public class ImplementationGuidance
{
    public string RecommendationId { get; set; } = string.Empty;
    public List<string> Steps { get; set; } = new();
    public TimeSpan EstimatedEffort { get; set; }
    public List<string> Prerequisites { get; set; } = new();
    public List<string> Resources { get; set; } = new();
}

public class ExpectedImpactAnalysis
{
    public double QualityImprovement { get; set; }
    public double PerformanceImpact { get; set; }
    public string ImpactDescription { get; set; } = string.Empty;
    public List<string> BenefitAreas { get; set; } = new();
    public List<string> PotentialRisks { get; set; } = new();
}
