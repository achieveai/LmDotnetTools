using MemoryServer.DocumentSegmentation.Models;

namespace MemoryServer.DocumentSegmentation.Services;

/// <summary>
///     Interface for topic-based document segmentation services.
///     Provides specialized methods for detecting topic boundaries and analyzing thematic coherence.
/// </summary>
public interface ITopicBasedSegmentationService
{
    /// <summary>
    ///     Segments document content based on topic boundaries and thematic coherence.
    /// </summary>
    /// <param name="content">The document content to segment</param>
    /// <param name="documentType">Type of document for optimization</param>
    /// <param name="options">Configuration options for topic-based segmentation</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of topic-based segments</returns>
    Task<List<DocumentSegment>> SegmentByTopicsAsync(
        string content,
        DocumentType documentType = DocumentType.Generic,
        TopicSegmentationOptions? options = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Detects topic boundaries within the document content.
    /// </summary>
    /// <param name="content">The document content to analyze</param>
    /// <param name="documentType">Type of document for context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of topic boundaries with confidence scores</returns>
    Task<List<TopicBoundary>> DetectTopicBoundariesAsync(
        string content,
        DocumentType documentType = DocumentType.Generic,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Analyzes thematic coherence of a text segment.
    /// </summary>
    /// <param name="content">The content to analyze</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Coherence analysis with score and key topics</returns>
    Task<ThematicCoherenceAnalysis> AnalyzeThematicCoherenceAsync(
        string content,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Validates topic-based segments for quality and coherence.
    /// </summary>
    /// <param name="segments">Segments to validate</param>
    /// <param name="originalContent">Original document content</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Validation results with quality metrics</returns>
    Task<TopicSegmentationValidation> ValidateTopicSegmentsAsync(
        List<DocumentSegment> segments,
        string originalContent,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Performs comprehensive topic analysis on a document segment.
    /// </summary>
    /// <param name="content">The content to analyze</param>
    /// <param name="analysisMethod">Method to use for topic analysis</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Detailed topic analysis results</returns>
    Task<TopicAnalysis> AnalyzeTopicsAsync(
        string content,
        TopicAnalysisMethod analysisMethod = TopicAnalysisMethod.Hybrid,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Identifies topic transitions and their quality between adjacent segments.
    /// </summary>
    /// <param name="previousContent">Content of the previous segment</param>
    /// <param name="currentContent">Content of the current segment</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Topic transition quality analysis</returns>
    Task<TopicTransitionQuality> AnalyzeTopicTransitionAsync(
        string previousContent,
        string currentContent,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Calculates semantic similarity between two text segments for topic analysis.
    /// </summary>
    /// <param name="content1">First segment content</param>
    /// <param name="content2">Second segment content</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Semantic similarity score (0.0 to 1.0)</returns>
    Task<double> CalculateSemanticSimilarityAsync(
        string content1,
        string content2,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Extracts key terms and concepts from content for topic analysis.
    /// </summary>
    /// <param name="content">Content to analyze</param>
    /// <param name="maxKeywords">Maximum number of keywords to extract</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Dictionary of keywords with their importance scores</returns>
    Task<Dictionary<string, double>> ExtractKeywordsAsync(
        string content,
        int maxKeywords = 10,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Assesses topic coherence within a segment and identifies issues.
    /// </summary>
    /// <param name="content">Content to assess</param>
    /// <param name="primaryTopic">Primary topic of the segment</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Topic coherence assessment</returns>
    Task<TopicCoherence> AssessTopicCoherenceAsync(
        string content,
        string primaryTopic,
        CancellationToken cancellationToken = default
    );
}
