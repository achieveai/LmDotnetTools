using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using MemoryServer.DocumentSegmentation.Models;

namespace MemoryServer.DocumentSegmentation.Models;

/// <summary>
/// Configuration options for topic-based segmentation.
/// </summary>
public class TopicSegmentationOptions
{
    /// <summary>
    /// Minimum segment size in characters.
    /// </summary>
    public int MinSegmentSize { get; set; } = 100;

    /// <summary>
    /// Maximum segment size in characters.
    /// </summary>
    public int MaxSegmentSize { get; set; } = 5000;

    /// <summary>
    /// Minimum confidence score for topic boundaries (0.0-1.0).
    /// </summary>
    public double MinTopicBoundaryConfidence { get; set; } = 0.6;

    /// <summary>
    /// Minimum thematic coherence score required (0.0-1.0).
    /// </summary>
    public double MinThematicCoherence { get; set; } = 0.7;

    /// <summary>
    /// Maximum number of segments to create.
    /// </summary>
    public int MaxSegments { get; set; } = 50;

    /// <summary>
    /// Whether to use LLM enhancement for low-confidence boundaries.
    /// </summary>
    public bool UseLlmEnhancement { get; set; } = true;

    /// <summary>
    /// Whether to merge adjacent segments with similar topics.
    /// </summary>
    public bool MergeSimilarTopics { get; set; } = true;

    /// <summary>
    /// Topic similarity threshold for merging (0.0-1.0).
    /// </summary>
    public double TopicSimilarityThreshold { get; set; } = 0.8;
}

/// <summary>
/// Represents a topic boundary detected in the document.
/// </summary>
public class TopicBoundary
{
    /// <summary>
    /// Position in the document where the topic boundary occurs.
    /// </summary>
    public int Position { get; set; }

    /// <summary>
    /// Confidence score for this boundary (0.0-1.0).
    /// </summary>
    public double Confidence { get; set; }

    /// <summary>
    /// Type of topic transition.
    /// </summary>
    public TopicTransitionType TransitionType { get; set; }

    /// <summary>
    /// Topic before the boundary.
    /// </summary>
    public string? PreviousTopic { get; set; }

    /// <summary>
    /// Topic after the boundary.
    /// </summary>
    public string? NextTopic { get; set; }

    /// <summary>
    /// Keywords that indicate the topic change.
    /// </summary>
    public List<string> TransitionKeywords { get; set; } = new();

    /// <summary>
    /// Strength of the topic transition (0.0-1.0).
    /// </summary>
    public double TransitionStrength { get; set; }
}

/// <summary>
/// Types of topic transitions.
/// </summary>
public enum TopicTransitionType
{
    /// <summary>
    /// Gradual transition between related topics.
    /// </summary>
    Gradual,

    /// <summary>
    /// Sharp transition between unrelated topics.
    /// </summary>
    Sharp,

    /// <summary>
    /// Return to a previously mentioned topic.
    /// </summary>
    Return,

    /// <summary>
    /// Introduction of a completely new topic.
    /// </summary>
    NewTopic,

    /// <summary>
    /// Conclusion or summary of the current topic.
    /// </summary>
    Conclusion,
}

/// <summary>
/// Analysis of thematic coherence within a text segment.
/// </summary>
public class ThematicCoherenceAnalysis
{
    /// <summary>
    /// Overall coherence score (0.0-1.0).
    /// </summary>
    public double CoherenceScore { get; set; }

    /// <summary>
    /// Primary topic of the segment.
    /// </summary>
    public string PrimaryTopic { get; set; } = string.Empty;

    /// <summary>
    /// Secondary topics mentioned in the segment.
    /// </summary>
    public List<string> SecondaryTopics { get; set; } = new();

    /// <summary>
    /// Key concepts and themes identified.
    /// </summary>
    public List<string> KeyConcepts { get; set; } = new();

    /// <summary>
    /// Topic keywords extracted from the content.
    /// </summary>
    public List<string> TopicKeywords { get; set; } = new();

    /// <summary>
    /// Semantic unity score (0.0-1.0).
    /// </summary>
    public double SemanticUnity { get; set; }

    /// <summary>
    /// Topic consistency score (0.0-1.0).
    /// </summary>
    public double TopicConsistency { get; set; }

    /// <summary>
    /// Reasons for coherence score.
    /// </summary>
    public List<string> CoherenceReasons { get; set; } = new();
}

/// <summary>
/// Validation results for topic-based segmentation.
/// </summary>
public class TopicSegmentationValidation
{
    /// <summary>
    /// Overall quality score for the segmentation (0.0-1.0).
    /// </summary>
    public double OverallQuality { get; set; }

    /// <summary>
    /// Average topic coherence across all segments.
    /// </summary>
    public double AverageTopicCoherence { get; set; }

    /// <summary>
    /// Topic boundary accuracy score.
    /// </summary>
    public double BoundaryAccuracy { get; set; }

    /// <summary>
    /// Segment independence score.
    /// </summary>
    public double SegmentIndependence { get; set; }

    /// <summary>
    /// Topic coverage completeness.
    /// </summary>
    public double TopicCoverage { get; set; }

    /// <summary>
    /// Individual segment validation results.
    /// </summary>
    public List<SegmentValidationResult> SegmentResults { get; set; } = new();

    /// <summary>
    /// Issues identified during validation.
    /// </summary>
    public List<ValidationIssue> Issues { get; set; } = new();

    /// <summary>
    /// Recommendations for improvement.
    /// </summary>
    public List<string> Recommendations { get; set; } = new();
}

/// <summary>
/// Validation result for an individual segment.
/// </summary>
public class SegmentValidationResult
{
    /// <summary>
    /// Segment identifier.
    /// </summary>
    public string SegmentId { get; set; } = string.Empty;

    /// <summary>
    /// Topic coherence score for this segment.
    /// </summary>
    public double TopicCoherence { get; set; }

    /// <summary>
    /// Independence score (how well it stands alone).
    /// </summary>
    public double Independence { get; set; }

    /// <summary>
    /// Topic clarity score.
    /// </summary>
    public double TopicClarity { get; set; }

    /// <summary>
    /// Quality issues found in this segment.
    /// </summary>
    public List<ValidationIssue> Issues { get; set; } = new();
}

/// <summary>
/// Represents a validation issue found during quality assessment.
/// </summary>
public class ValidationIssue
{
    /// <summary>
    /// Type of issue.
    /// </summary>
    public ValidationIssueType Type { get; set; }

    /// <summary>
    /// Severity level of the issue.
    /// </summary>
    public ValidationSeverity Severity { get; set; }

    /// <summary>
    /// Description of the issue.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Suggested resolution.
    /// </summary>
    public string? Resolution { get; set; }

    /// <summary>
    /// Position in the document where the issue occurs.
    /// </summary>
    public int? Position { get; set; }
}

/// <summary>
/// Types of validation issues.
/// </summary>
public enum ValidationIssueType
{
    /// <summary>
    /// Topic mixing within a segment.
    /// </summary>
    TopicMixing,

    /// <summary>
    /// Poor topic coherence.
    /// </summary>
    PoorCoherence,

    /// <summary>
    /// Segment too small or fragmented.
    /// </summary>
    Fragmentation,

    /// <summary>
    /// Segment too large with multiple topics.
    /// </summary>
    Oversized,

    /// <summary>
    /// Missing context for understanding.
    /// </summary>
    MissingContext,

    /// <summary>
    /// Unclear topic boundaries.
    /// </summary>
    UnclearBoundaries,

    /// <summary>
    /// Topic duplication across segments.
    /// </summary>
    TopicDuplication,
}

/// <summary>
/// Severity levels for validation issues.
/// </summary>
public enum ValidationSeverity
{
    /// <summary>
    /// Informational - minor quality issue.
    /// </summary>
    Info,

    /// <summary>
    /// Warning - quality issue that should be addressed.
    /// </summary>
    Warning,

    /// <summary>
    /// Error - significant quality issue requiring attention.
    /// </summary>
    Error,

    /// <summary>
    /// Critical - severe issue affecting usability.
    /// </summary>
    Critical,
}

/// <summary>
/// Comprehensive analysis of topics within a document segment.
/// </summary>
public class TopicAnalysis
{
    /// <summary>
    /// Primary topic of the segment.
    /// </summary>
    public string PrimaryTopic { get; set; } = string.Empty;

    /// <summary>
    /// Secondary topics present in the segment.
    /// </summary>
    public List<string> SecondaryTopics { get; set; } = new();

    /// <summary>
    /// Key terms and concepts associated with the primary topic.
    /// </summary>
    public List<string> KeyTerms { get; set; } = new();

    /// <summary>
    /// Topic coherence score for the segment (0.0 to 1.0).
    /// </summary>
    public double CoherenceScore { get; set; }

    /// <summary>
    /// Topic density score indicating focus on the primary topic.
    /// </summary>
    public double TopicDensity { get; set; }

    /// <summary>
    /// Coverage percentage of the primary topic within the segment.
    /// </summary>
    public double TopicCoverage { get; set; }

    /// <summary>
    /// Semantic keywords extracted from the segment.
    /// </summary>
    public Dictionary<string, double> SemanticKeywords { get; set; } = new();

    /// <summary>
    /// Analysis method used to identify topics.
    /// </summary>
    public TopicAnalysisMethod AnalysisMethod { get; set; }

    /// <summary>
    /// Confidence in the topic analysis results.
    /// </summary>
    public double AnalysisConfidence { get; set; }
}

/// <summary>
/// Methods available for topic analysis.
/// </summary>
public enum TopicAnalysisMethod
{
    /// <summary>
    /// Keyword frequency and term analysis.
    /// </summary>
    KeywordAnalysis,

    /// <summary>
    /// Semantic similarity and embedding analysis.
    /// </summary>
    SemanticAnalysis,

    /// <summary>
    /// LLM-powered topic identification.
    /// </summary>
    LlmAnalysis,

    /// <summary>
    /// Combination of multiple methods.
    /// </summary>
    Hybrid,

    /// <summary>
    /// Rule-based topic detection.
    /// </summary>
    RuleBased,
}

/// <summary>
/// Represents the quality of a topic transition between segments.
/// </summary>
public class TopicTransitionQuality
{
    /// <summary>
    /// Overall transition quality score (0.0 to 1.0).
    /// </summary>
    public double Score { get; set; }

    /// <summary>
    /// Smoothness of the topic transition.
    /// </summary>
    public double Smoothness { get; set; }

    /// <summary>
    /// Logical connection between topics.
    /// </summary>
    public double LogicalConnection { get; set; }

    /// <summary>
    /// Contextual continuity across the transition.
    /// </summary>
    public double ContextualContinuity { get; set; }

    /// <summary>
    /// Presence of transitional elements.
    /// </summary>
    public bool HasTransitionalElements { get; set; }

    /// <summary>
    /// Transitional phrases or elements identified.
    /// </summary>
    public List<string> TransitionalElements { get; set; } = new();
}

/// <summary>
/// Represents the coherence assessment of a topic within a segment.
/// </summary>
public class TopicCoherence
{
    /// <summary>
    /// Topic being assessed for coherence.
    /// </summary>
    public string Topic { get; set; } = string.Empty;

    /// <summary>
    /// Overall coherence score (0.0 to 1.0).
    /// </summary>
    public double Score { get; set; }

    /// <summary>
    /// Consistency of terminology usage.
    /// </summary>
    public double TerminologyConsistency { get; set; }

    /// <summary>
    /// Semantic coherence between sentences.
    /// </summary>
    public double SemanticCoherence { get; set; }

    /// <summary>
    /// Thematic focus strength.
    /// </summary>
    public double ThematicFocus { get; set; }

    /// <summary>
    /// Conceptual unity across the segment.
    /// </summary>
    public double ConceptualUnity { get; set; }

    /// <summary>
    /// Issues detected that affect coherence.
    /// </summary>
    public List<TopicCoherenceIssue> Issues { get; set; } = new();

    /// <summary>
    /// Suggestions for improving topic coherence.
    /// </summary>
    public List<string> ImprovementSuggestions { get; set; } = new();
}

/// <summary>
/// Represents a topic coherence issue within a segment.
/// </summary>
public class TopicCoherenceIssue
{
    /// <summary>
    /// Type of coherence issue.
    /// </summary>
    public TopicCoherenceIssueType Type { get; set; }

    /// <summary>
    /// Description of the issue.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Severity of the issue.
    /// </summary>
    public ValidationSeverity Severity { get; set; }

    /// <summary>
    /// Position where the issue occurs.
    /// </summary>
    public int Position { get; set; }

    /// <summary>
    /// Impact on overall coherence score.
    /// </summary>
    public double Impact { get; set; }
}

/// <summary>
/// Types of topic coherence issues.
/// </summary>
public enum TopicCoherenceIssueType
{
    /// <summary>
    /// Multiple unrelated topics in single segment.
    /// </summary>
    TopicDrift,

    /// <summary>
    /// Inconsistent terminology usage.
    /// </summary>
    TerminologyInconsistency,

    /// <summary>
    /// Weak thematic connection.
    /// </summary>
    WeakThematicConnection,

    /// <summary>
    /// Abrupt topic changes within segment.
    /// </summary>
    AbruptTopicChange,

    /// <summary>
    /// Insufficient topic development.
    /// </summary>
    InsufficientDevelopment,

    /// <summary>
    /// Unclear topic focus.
    /// </summary>
    UnclearFocus,
}

/// <summary>
/// Enhanced topic segmentation options with additional configuration.
/// </summary>
public class TopicSegmentationOptionsExtended : TopicSegmentationOptions
{
    /// <summary>
    /// Minimum topic coherence score required for valid segments.
    /// Default: 0.7
    /// </summary>
    [Range(0.0, 1.0)]
    public double MinTopicCoherence { get; set; } = 0.7;

    /// <summary>
    /// Maximum number of secondary topics allowed per segment.
    /// Default: 3
    /// </summary>
    [Range(1, 10)]
    public int MaxSecondaryTopics { get; set; } = 3;

    /// <summary>
    /// Preferred topic analysis method.
    /// Default: Hybrid
    /// </summary>
    public TopicAnalysisMethod PreferredAnalysisMethod { get; set; } = TopicAnalysisMethod.Hybrid;

    /// <summary>
    /// Enable semantic similarity analysis.
    /// Default: true
    /// </summary>
    public bool EnableSemanticAnalysis { get; set; } = true;

    /// <summary>
    /// Minimum semantic similarity threshold for topic boundaries.
    /// Default: 0.3
    /// </summary>
    [Range(0.0, 1.0)]
    public double MinSemanticSimilarity { get; set; } = 0.3;

    /// <summary>
    /// Enable automatic keyword extraction.
    /// Default: true
    /// </summary>
    public bool EnableKeywordExtraction { get; set; } = true;

    /// <summary>
    /// Maximum number of keywords to extract per segment.
    /// Default: 10
    /// </summary>
    [Range(3, 50)]
    public int MaxKeywordsPerSegment { get; set; } = 10;
}
