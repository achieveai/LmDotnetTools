using MemoryServer.DocumentSegmentation.Models;

namespace MemoryServer.DocumentSegmentation.Services;

/// <summary>
/// Configuration options for narrative-based document segmentation.
/// Focuses on logical flow, temporal sequences, and causal relationships.
/// </summary>
public class NarrativeSegmentationOptions
{
    /// <summary>
    /// Minimum size for a narrative segment in characters.
    /// </summary>
    public int MinSegmentSize { get; set; } = 100;

    /// <summary>
    /// Maximum size for a narrative segment in characters.
    /// </summary>
    public int MaxSegmentSize { get; set; } = 5000;

    /// <summary>
    /// Maximum number of segments to create.
    /// </summary>
    public int MaxSegments { get; set; } = 50;

    /// <summary>
    /// Depth of narrative flow analysis. Higher values perform deeper logical analysis.
    /// </summary>
    public int NarrativeFlowAnalysisDepth { get; set; } = 3;

    /// <summary>
    /// Whether to detect and analyze temporal sequences in the narrative.
    /// </summary>
    public bool DetectTemporalSequences { get; set; } = true;

    /// <summary>
    /// Whether to analyze causal relationships between narrative elements.
    /// </summary>
    public bool AnalyzeCausalRelationships { get; set; } = true;

    /// <summary>
    /// Whether to detect narrative arc elements (setup, development, climax, resolution).
    /// </summary>
    public bool DetectNarrativeArcs { get; set; } = true;

    /// <summary>
    /// Whether to use LLM enhancement for complex narrative understanding.
    /// </summary>
    public bool UseLlmEnhancement { get; set; } = true;

    /// <summary>
    /// Minimum confidence threshold for narrative boundary detection (0.0 to 1.0).
    /// </summary>
    public double MinNarrativeConfidence { get; set; } = 0.6;

    /// <summary>
    /// Whether to merge segments with weak narrative transitions.
    /// </summary>
    public bool MergeWeakTransitions { get; set; } = true;

    /// <summary>
    /// Minimum narrative flow coherence score to accept a segmentation (0.0 to 1.0).
    /// </summary>
    public double MinFlowCoherence { get; set; } = 0.5;
}

/// <summary>
/// Represents a detected boundary in narrative flow.
/// </summary>
public class NarrativeBoundary
{
    /// <summary>
    /// Position in the document where the narrative boundary occurs.
    /// </summary>
    public int Position { get; set; }

    /// <summary>
    /// Confidence score for this boundary (0.0 to 1.0).
    /// </summary>
    public double Confidence { get; set; }

    /// <summary>
    /// Type of narrative transition at this boundary.
    /// </summary>
    public NarrativeTransitionType TransitionType { get; set; }

    /// <summary>
    /// Narrative function of the segment starting at this boundary.
    /// </summary>
    public NarrativeFunction Function { get; set; }

    /// <summary>
    /// Phrases or words that triggered the detection of this boundary.
    /// </summary>
    public List<string> TriggerPhrases { get; set; } = new List<string>();

    /// <summary>
    /// Logical relationship between the segment before and after this boundary.
    /// </summary>
    public LogicalRelationship LogicalRelationship { get; set; }

    /// <summary>
    /// Temporal relationship at this boundary (if applicable).
    /// </summary>
    public TemporalRelationship? TemporalRelationship { get; set; }

    /// <summary>
    /// Additional metadata about this narrative boundary.
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
}

/// <summary>
/// Types of narrative transitions that can occur between segments.
/// </summary>
public enum NarrativeTransitionType
{
    /// <summary>
    /// Temporal transition - time-based progression.
    /// </summary>
    Temporal,

    /// <summary>
    /// Causal transition - cause and effect relationship.
    /// </summary>
    Causal,

    /// <summary>
    /// Logical transition - logical progression or reasoning.
    /// </summary>
    Logical,

    /// <summary>
    /// Thematic transition - change in theme or focus.
    /// </summary>
    Thematic,

    /// <summary>
    /// Structural transition - change in narrative structure.
    /// </summary>
    Structural,

    /// <summary>
    /// Conditional transition - hypothetical or conditional relationship.
    /// </summary>
    Conditional,

    /// <summary>
    /// Comparative transition - comparison or contrast.
    /// </summary>
    Comparative,

    /// <summary>
    /// Sequential transition - step-by-step progression.
    /// </summary>
    Sequential
}

/// <summary>
/// Narrative functions that segments can serve in the overall narrative.
/// </summary>
public enum NarrativeFunction
{
    /// <summary>
    /// Setup or introduction of the narrative.
    /// </summary>
    Setup,

    /// <summary>
    /// Background information or context.
    /// </summary>
    Background,

    /// <summary>
    /// Development of the main narrative.
    /// </summary>
    Development,

    /// <summary>
    /// Complication or conflict introduction.
    /// </summary>
    Complication,

    /// <summary>
    /// Climax or turning point.
    /// </summary>
    Climax,

    /// <summary>
    /// Resolution or conclusion.
    /// </summary>
    Resolution,

    /// <summary>
    /// Supporting detail or elaboration.
    /// </summary>
    Supporting,

    /// <summary>
    /// Transition or bridging content.
    /// </summary>
    Transition,

    /// <summary>
    /// Analysis or interpretation.
    /// </summary>
    Analysis,

    /// <summary>
    /// Summary or synthesis.
    /// </summary>
    Summary
}

/// <summary>
/// Types of logical relationships between narrative segments.
/// </summary>
public enum LogicalRelationship
{
    /// <summary>
    /// Sequential relationship - one follows another.
    /// </summary>
    Sequential,

    /// <summary>
    /// Causal relationship - one causes another.
    /// </summary>
    Causal,

    /// <summary>
    /// Parallel relationship - happening simultaneously.
    /// </summary>
    Parallel,

    /// <summary>
    /// Contrasting relationship - opposite or different.
    /// </summary>
    Contrasting,

    /// <summary>
    /// Supporting relationship - one supports or elaborates another.
    /// </summary>
    Supporting,

    /// <summary>
    /// Conditional relationship - dependent on conditions.
    /// </summary>
    Conditional,

    /// <summary>
    /// Comparative relationship - comparison between elements.
    /// </summary>
    Comparative,

    /// <summary>
    /// Explanatory relationship - one explains another.
    /// </summary>
    Explanatory,

    /// <summary>
    /// No clear relationship.
    /// </summary>
    Independent
}

/// <summary>
/// Types of temporal relationships in narrative flow.
/// </summary>
public enum TemporalRelationship
{
    /// <summary>
    /// Chronological sequence - events in time order.
    /// </summary>
    Chronological,

    /// <summary>
    /// Simultaneous - happening at the same time.
    /// </summary>
    Simultaneous,

    /// <summary>
    /// Flashback - reference to earlier events.
    /// </summary>
    Flashback,

    /// <summary>
    /// Flash-forward - reference to future events.
    /// </summary>
    FlashForward,

    /// <summary>
    /// Cyclical - recurring patterns.
    /// </summary>
    Cyclical,

    /// <summary>
    /// Overlapping - time periods that overlap.
    /// </summary>
    Overlapping
}

/// <summary>
/// Analysis of narrative flow and structure in a document.
/// </summary>
public class NarrativeFlowAnalysis
{
    /// <summary>
    /// Overall type of narrative structure detected.
    /// </summary>
    public NarrativeType OverallNarrativeType { get; set; }

    /// <summary>
    /// Type of temporal progression in the narrative.
    /// </summary>
    public TemporalProgression TemporalProgression { get; set; }

    /// <summary>
    /// Causal chain relationships detected in the narrative.
    /// </summary>
    public List<CausalRelation> CausalChain { get; set; } = new List<CausalRelation>();

    /// <summary>
    /// Narrative elements identified in the document.
    /// </summary>
    public Dictionary<NarrativeFunction, List<int>> NarrativeElements { get; set; } = new Dictionary<NarrativeFunction, List<int>>();

    /// <summary>
    /// Overall flow coherence score (0.0 to 1.0).
    /// </summary>
    public double FlowCoherence { get; set; }

    /// <summary>
    /// Logical consistency score (0.0 to 1.0).
    /// </summary>
    public double LogicalConsistency { get; set; }

    /// <summary>
    /// Temporal consistency score (0.0 to 1.0).
    /// </summary>
    public double TemporalConsistency { get; set; }

    /// <summary>
    /// Narrative completeness score (0.0 to 1.0).
    /// </summary>
    public double NarrativeCompleteness { get; set; }

    /// <summary>
    /// Key narrative markers found in the text.
    /// </summary>
    public List<string> NarrativeMarkers { get; set; } = new List<string>();

    /// <summary>
    /// Transition quality scores for each detected boundary.
    /// </summary>
    public Dictionary<int, double> TransitionQualities { get; set; } = new Dictionary<int, double>();
}

/// <summary>
/// Types of overall narrative structures.
/// </summary>
public enum NarrativeType
{
    /// <summary>
    /// Sequential narrative - events in order.
    /// </summary>
    Sequential,

    /// <summary>
    /// Causal narrative - cause and effect driven.
    /// </summary>
    Causal,

    /// <summary>
    /// Comparative narrative - comparing different elements.
    /// </summary>
    Comparative,

    /// <summary>
    /// Descriptive narrative - describing something.
    /// </summary>
    Descriptive,

    /// <summary>
    /// Problem-solution narrative - identifying and solving problems.
    /// </summary>
    ProblemSolution,

    /// <summary>
    /// Process narrative - step-by-step procedures.
    /// </summary>
    Process,

    /// <summary>
    /// Argumentative narrative - building an argument.
    /// </summary>
    Argumentative,

    /// <summary>
    /// Story narrative - traditional story structure.
    /// </summary>
    Story,

    /// <summary>
    /// Mixed or complex narrative structure.
    /// </summary>
    Mixed
}

/// <summary>
/// Types of temporal progression patterns.
/// </summary>
public enum TemporalProgression
{
    /// <summary>
    /// Linear progression forward in time.
    /// </summary>
    Linear,

    /// <summary>
    /// Non-linear with flashbacks and flash-forwards.
    /// </summary>
    NonLinear,

    /// <summary>
    /// Cyclical patterns repeating.
    /// </summary>
    Cyclical,

    /// <summary>
    /// Multiple parallel timelines.
    /// </summary>
    Parallel,

    /// <summary>
    /// Static - no clear temporal progression.
    /// </summary>
    Static,

    /// <summary>
    /// Mixed temporal patterns.
    /// </summary>
    Mixed
}

/// <summary>
/// Represents a causal relationship between narrative elements.
/// </summary>
public class CausalRelation
{
    /// <summary>
    /// Position of the cause element.
    /// </summary>
    public int CausePosition { get; set; }

    /// <summary>
    /// Position of the effect element.
    /// </summary>
    public int EffectPosition { get; set; }

    /// <summary>
    /// Strength of the causal relationship (0.0 to 1.0).
    /// </summary>
    public double Strength { get; set; }

    /// <summary>
    /// Text that indicates the causal relationship.
    /// </summary>
    public string CausalIndicator { get; set; } = string.Empty;

    /// <summary>
    /// Type of causal relationship.
    /// </summary>
    public CausalType Type { get; set; }
}

/// <summary>
/// Types of causal relationships.
/// </summary>
public enum CausalType
{
    /// <summary>
    /// Direct cause and effect.
    /// </summary>
    Direct,

    /// <summary>
    /// Indirect or mediated causation.
    /// </summary>
    Indirect,

    /// <summary>
    /// Conditional causation.
    /// </summary>
    Conditional,

    /// <summary>
    /// Necessary condition.
    /// </summary>
    Necessary,

    /// <summary>
    /// Sufficient condition.
    /// </summary>
    Sufficient,

    /// <summary>
    /// Contributing factor.
    /// </summary>
    Contributing
}

/// <summary>
/// Validation results for narrative-based segmentation.
/// </summary>
public class NarrativeSegmentationValidation
{
    /// <summary>
    /// Overall narrative flow coherence score (0.0 to 1.0).
    /// </summary>
    public double FlowCoherence { get; set; }

    /// <summary>
    /// Logical consistency across segments (0.0 to 1.0).
    /// </summary>
    public double LogicalConsistency { get; set; }

    /// <summary>
    /// Temporal consistency score (0.0 to 1.0).
    /// </summary>
    public double TemporalConsistency { get; set; }

    /// <summary>
    /// Narrative completeness score (0.0 to 1.0).
    /// </summary>
    public double NarrativeCompleteness { get; set; }

    /// <summary>
    /// Quality of transitions between segments (0.0 to 1.0).
    /// </summary>
    public double TransitionQuality { get; set; }

    /// <summary>
    /// Overall narrative quality score (0.0 to 1.0).
    /// </summary>
    public double OverallQuality { get; set; }

    /// <summary>
    /// Individual segment validation results.
    /// </summary>
    public List<NarrativeSegmentValidationResult> SegmentResults { get; set; } = new List<NarrativeSegmentValidationResult>();

    /// <summary>
    /// Issues found during narrative validation.
    /// </summary>
    public List<ValidationIssue> Issues { get; set; } = new List<ValidationIssue>();

    /// <summary>
    /// Recommendations for improving narrative segmentation.
    /// </summary>
    public List<string> Recommendations { get; set; } = new List<string>();
}

/// <summary>
/// Validation result for an individual narrative segment.
/// </summary>
public class NarrativeSegmentValidationResult
{
    /// <summary>
    /// ID of the segment being validated.
    /// </summary>
    public string SegmentId { get; set; } = string.Empty;

    /// <summary>
    /// Flow coherence of this segment (0.0 to 1.0).
    /// </summary>
    public double FlowCoherence { get; set; }

    /// <summary>
    /// Logical consistency within the segment (0.0 to 1.0).
    /// </summary>
    public double LogicalConsistency { get; set; }

    /// <summary>
    /// Temporal consistency of the segment (0.0 to 1.0).
    /// </summary>
    public double TemporalConsistency { get; set; }

    /// <summary>
    /// Narrative function clarity (0.0 to 1.0).
    /// </summary>
    public double NarrativeFunctionClarity { get; set; }

    /// <summary>
    /// Quality of transitions into and out of this segment (0.0 to 1.0).
    /// </summary>
    public double TransitionQuality { get; set; }

    /// <summary>
    /// Issues specific to this segment.
    /// </summary>
    public List<ValidationIssue> Issues { get; set; } = new List<ValidationIssue>();
}
