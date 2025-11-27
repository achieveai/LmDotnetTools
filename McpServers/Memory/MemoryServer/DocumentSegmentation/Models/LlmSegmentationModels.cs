namespace MemoryServer.DocumentSegmentation.Models;

/// <summary>
///     Request structure for LLM-powered document segmentation.
/// </summary>
public class LlmSegmentationRequest
{
    /// <summary>
    ///     Document content to segment
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    ///     Document type for context
    /// </summary>
    public DocumentType DocumentType { get; set; } = DocumentType.Generic;

    /// <summary>
    ///     Preferred segmentation strategy
    /// </summary>
    public SegmentationStrategy Strategy { get; set; } = SegmentationStrategy.Hybrid;

    /// <summary>
    ///     Target segment size in words
    /// </summary>
    public int TargetSegmentSize { get; set; } = 1000;

    /// <summary>
    ///     Maximum segment size in words
    /// </summary>
    public int MaxSegmentSize { get; set; } = 2000;

    /// <summary>
    ///     Minimum segment size in words
    /// </summary>
    public int MinSegmentSize { get; set; } = 200;

    /// <summary>
    ///     Quality threshold for segments (0.0 to 1.0)
    /// </summary>
    public double QualityThreshold { get; set; } = 0.7;

    /// <summary>
    ///     Whether to include detailed reasoning in response
    /// </summary>
    public bool IncludeReasoning { get; set; } = true;

    /// <summary>
    ///     Language code for content (for prompt optimization)
    /// </summary>
    public string Language { get; set; } = "en";

    /// <summary>
    ///     Additional context or instructions for the LLM
    /// </summary>
    public string AdditionalContext { get; set; } = string.Empty;
}

/// <summary>
///     Response from LLM-powered document segmentation.
/// </summary>
public class LlmSegmentationResponse
{
    /// <summary>
    ///     Generated document segments
    /// </summary>
    public List<DocumentSegment> Segments { get; set; } = [];

    /// <summary>
    ///     Strategy that was actually used
    /// </summary>
    public SegmentationStrategy UsedStrategy { get; set; }

    /// <summary>
    ///     Overall quality score for the segmentation
    /// </summary>
    public double OverallQuality { get; set; }

    /// <summary>
    ///     LLM reasoning for segmentation decisions
    /// </summary>
    public string Reasoning { get; set; } = string.Empty;

    /// <summary>
    ///     Processing time in milliseconds
    /// </summary>
    public long ProcessingTimeMs { get; set; }

    /// <summary>
    ///     Token usage statistics
    /// </summary>
    public LlmTokenUsage TokenUsage { get; set; } = new();

    /// <summary>
    ///     Whether fallback segmentation was used
    /// </summary>
    public bool UsedFallback { get; set; }

    /// <summary>
    ///     Any warnings or issues encountered
    /// </summary>
    public List<string> Warnings { get; set; } = [];
}

/// <summary>
///     Token usage statistics for LLM operations.
/// </summary>
public class LlmTokenUsage
{
    /// <summary>
    ///     Input tokens consumed
    /// </summary>
    public int InputTokens { get; set; }

    /// <summary>
    ///     Output tokens generated
    /// </summary>
    public int OutputTokens { get; set; }

    /// <summary>
    ///     Total tokens used
    /// </summary>
    public int TotalTokens => InputTokens + OutputTokens;

    /// <summary>
    ///     Estimated cost in USD
    /// </summary>
    public decimal EstimatedCost { get; set; }

    /// <summary>
    ///     Model used for the operation
    /// </summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>
    ///     Provider used for the operation
    /// </summary>
    public string ProviderName { get; set; } = string.Empty;
}

/// <summary>
///     Enhanced quality assessment for LLM-generated segments.
/// </summary>
public class LlmQualityAssessment : SegmentationQualityAssessment
{
    /// <summary>
    ///     Semantic similarity score between adjacent segments
    /// </summary>
    public double SemanticSimilarityScore { get; set; }

    /// <summary>
    ///     Topic boundary detection accuracy
    /// </summary>
    public double TopicBoundaryAccuracy { get; set; }

    /// <summary>
    ///     Narrative flow preservation score
    /// </summary>
    public double NarrativeFlowScore { get; set; }

    /// <summary>
    ///     LLM confidence in the segmentation
    /// </summary>
    public double LlmConfidence { get; set; }

    /// <summary>
    ///     Detailed feedback from LLM quality analysis
    /// </summary>
    public string DetailedFeedback { get; set; } = string.Empty;
}

/// <summary>
///     Configuration for LLM provider selection and model preferences.
/// </summary>
public class LlmProviderConfiguration
{
    /// <summary>
    ///     Primary provider preference (e.g., "OpenAI", "Anthropic")
    /// </summary>
    public string PrimaryProvider { get; set; } = "OpenAI";

    /// <summary>
    ///     Fallback providers in order of preference
    /// </summary>
    public List<string> FallbackProviders { get; set; } = ["Anthropic"];

    /// <summary>
    ///     Model preferences for different operations
    /// </summary>
    public Dictionary<string, string> ModelPreferences { get; set; } =
        new()
        {
            ["strategy_analysis"] = "gpt-4o-mini",
            ["segmentation"] = "gpt-4o-mini",
            ["quality_validation"] = "gpt-4o-mini",
        };

    /// <summary>
    ///     Maximum retries for failed LLM calls
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    ///     Timeout for LLM operations in seconds
    /// </summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    ///     Temperature setting for LLM calls
    /// </summary>
    public double Temperature { get; set; } = 0.1;

    /// <summary>
    ///     Whether to enable structured output mode
    /// </summary>
    public bool UseStructuredOutput { get; set; } = true;
}
