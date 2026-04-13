namespace MemoryServer.DocumentSegmentation.Models;

/// <summary>
///     Request parameters for document segmentation.
/// </summary>
public class DocumentSegmentationRequest
{
    /// <summary>
    ///     Type of document for strategy optimization
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
    public int MinSegmentSize { get; set; } = 100;

    /// <summary>
    ///     Word overlap between adjacent segments for context preservation
    /// </summary>
    public int ContextOverlap { get; set; } = 75;

    /// <summary>
    ///     Additional metadata for the segmentation request
    /// </summary>
    public Dictionary<string, object>? Metadata { get; set; }
}

/// <summary>
///     Result of document segmentation operation.
/// </summary>
public class DocumentSegmentationResult
{
    /// <summary>
    ///     Generated document segments
    /// </summary>
    public List<DocumentSegment> Segments { get; set; } = [];

    /// <summary>
    ///     Segmentation metadata and processing information
    /// </summary>
    public SegmentationMetadata Metadata { get; set; } = new();

    /// <summary>
    ///     Relationships between segments
    /// </summary>
    public List<SegmentRelationship> Relationships { get; set; } = [];

    /// <summary>
    ///     Whether segmentation completed successfully
    /// </summary>
    public bool IsComplete { get; set; }

    /// <summary>
    ///     Any warnings or issues encountered during segmentation
    /// </summary>
    public List<string> Warnings { get; set; } = [];
}

/// <summary>
///     Individual document segment with content and metadata.
/// </summary>
public class DocumentSegment
{
    /// <summary>
    ///     Unique segment identifier
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     Segment content text
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    ///     Position in the sequence of segments
    /// </summary>
    public int SequenceNumber { get; set; }

    /// <summary>
    ///     LLM-generated segment title
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    ///     LLM-generated segment summary
    /// </summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    ///     Quality assessment for this segment
    /// </summary>
    public SegmentQuality Quality { get; set; } = new();

    /// <summary>
    ///     Additional segment metadata
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = [];
}

/// <summary>
///     Quality assessment scores for a segment.
/// </summary>
public class SegmentQuality
{
    /// <summary>
    ///     Coherence score (0.0-1.0) - how well the content holds together
    /// </summary>
    public double CoherenceScore { get; set; }

    /// <summary>
    ///     Independence score (0.0-1.0) - how self-contained the segment is
    /// </summary>
    public double IndependenceScore { get; set; }

    /// <summary>
    ///     Topic consistency score (0.0-1.0) - how consistently the segment focuses on topics
    /// </summary>
    public double TopicConsistencyScore { get; set; }

    /// <summary>
    ///     Whether the segment passes quality thresholds
    /// </summary>
    public bool PassesQualityThreshold { get; set; }

    /// <summary>
    ///     List of identified quality issues
    /// </summary>
    public List<string> QualityIssues { get; set; } = [];
}

/// <summary>
///     Relationship between two document segments.
/// </summary>
public class SegmentRelationship
{
    /// <summary>
    ///     Unique relationship identifier
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     Source segment identifier
    /// </summary>
    public string SourceSegmentId { get; set; } = string.Empty;

    /// <summary>
    ///     Target segment identifier
    /// </summary>
    public string TargetSegmentId { get; set; } = string.Empty;

    /// <summary>
    ///     Type of relationship
    /// </summary>
    public SegmentRelationshipType RelationshipType { get; set; }

    /// <summary>
    ///     Strength of the relationship (0.0-1.0)
    /// </summary>
    public double Strength { get; set; } = 1.0;

    /// <summary>
    ///     Additional relationship metadata
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = [];
}

/// <summary>
///     Metadata about the segmentation process and results.
/// </summary>
public class SegmentationMetadata
{
    /// <summary>
    ///     Strategy used for segmentation
    /// </summary>
    public SegmentationStrategy StrategyUsed { get; set; }

    /// <summary>
    ///     Total processing time in milliseconds
    /// </summary>
    public long ProcessingTimeMs { get; set; }

    /// <summary>
    ///     Model or method used for segmentation
    /// </summary>
    public string ProcessingMethod { get; set; } = string.Empty;

    /// <summary>
    ///     Original document statistics
    /// </summary>
    public DocumentStatistics OriginalDocument { get; set; } = new();

    /// <summary>
    ///     Statistics for generated segments
    /// </summary>
    public SegmentStatistics SegmentStats { get; set; } = new();

    /// <summary>
    ///     Overall quality assessment
    /// </summary>
    public SegmentationQualityAssessment QualityAssessment { get; set; } = new();
}

/// <summary>
///     Statistics about the original document.
/// </summary>
public class DocumentStatistics
{
    /// <summary>
    ///     Total character count
    /// </summary>
    public int CharacterCount { get; set; }

    /// <summary>
    ///     Total word count
    /// </summary>
    public int WordCount { get; set; }

    /// <summary>
    ///     Estimated token count
    /// </summary>
    public int TokenCount { get; set; }

    /// <summary>
    ///     Number of paragraphs
    /// </summary>
    public int ParagraphCount { get; set; }

    /// <summary>
    ///     Number of sentences
    /// </summary>
    public int SentenceCount { get; set; }
}

/// <summary>
///     Statistics about generated segments.
/// </summary>
public class SegmentStatistics
{
    /// <summary>
    ///     Total number of segments generated
    /// </summary>
    public int TotalSegments { get; set; }

    /// <summary>
    ///     Average segment size in words
    /// </summary>
    public double AverageSegmentSize { get; set; }

    /// <summary>
    ///     Minimum segment size in words
    /// </summary>
    public int MinSegmentSize { get; set; }

    /// <summary>
    ///     Maximum segment size in words
    /// </summary>
    public int MaxSegmentSize { get; set; }

    /// <summary>
    ///     Total number of relationships created
    /// </summary>
    public int TotalRelationships { get; set; }
}

/// <summary>
///     Overall quality assessment for segmentation results.
/// </summary>
public class SegmentationQualityAssessment
{
    /// <summary>
    ///     Overall quality score (0.0-1.0)
    /// </summary>
    public double OverallScore { get; set; }

    /// <summary>
    ///     Average coherence score across all segments
    /// </summary>
    public double AverageCoherenceScore { get; set; }

    /// <summary>
    ///     Average independence score across all segments
    /// </summary>
    public double AverageIndependenceScore { get; set; }

    /// <summary>
    ///     Average topic consistency score across all segments
    /// </summary>
    public double AverageTopicConsistencyScore { get; set; }

    /// <summary>
    ///     Percentage of segments that pass quality thresholds
    /// </summary>
    public double QualityPassRate { get; set; }

    /// <summary>
    ///     Whether the overall segmentation meets quality standards
    /// </summary>
    public bool MeetsQualityStandards { get; set; }

    /// <summary>
    ///     Quality feedback and recommendations
    /// </summary>
    public List<string> QualityFeedback { get; set; } = [];
}
