using MemoryServer.DocumentSegmentation.Models;
using MemoryServer.Models;

namespace MemoryServer.DocumentSegmentation.Services;

/// <summary>
/// Interface for comprehensive quality assessment of document segmentation results.
/// Provides detailed analysis of semantic coherence, independence, topic consistency, and completeness.
/// </summary>
public interface ISegmentationQualityAssessmentService
{
    /// <summary>
    /// Performs comprehensive quality assessment of segmentation results.
    /// </summary>
    /// <param name="segments">List of document segments to assess</param>
    /// <param name="originalContent">Original document content for reference</param>
    /// <param name="documentType">Type of document being assessed</param>
    /// <param name="options">Assessment configuration options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Detailed quality assessment with metrics and recommendations</returns>
    Task<ComprehensiveQualityAssessment> AssessSegmentationQualityAsync(
        List<DocumentSegment> segments,
        string originalContent,
        DocumentType documentType = DocumentType.Generic,
        QualityAssessmentOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates semantic coherence within individual segments.
    /// </summary>
    /// <param name="segment">Segment to validate</param>
    /// <param name="options">Assessment options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Semantic coherence validation results</returns>
    Task<SemanticCoherenceValidation> ValidateSemanticCoherenceAsync(
        DocumentSegment segment,
        QualityAssessmentOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Calculates independence score for a segment.
    /// </summary>
    /// <param name="segment">Segment to analyze</param>
    /// <param name="allSegments">All segments for context analysis</param>
    /// <param name="originalContent">Original document content</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Independence score analysis</returns>
    Task<IndependenceScoreAnalysis> CalculateIndependenceScoreAsync(
        DocumentSegment segment,
        List<DocumentSegment> allSegments,
        string originalContent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates topic consistency within and across segments.
    /// </summary>
    /// <param name="segments">List of segments to analyze</param>
    /// <param name="originalContent">Original document content</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Topic consistency validation results</returns>
    Task<TopicConsistencyValidation> ValidateTopicConsistencyAsync(
        List<DocumentSegment> segments,
        string originalContent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies completeness of segmentation coverage.
    /// </summary>
    /// <param name="segments">List of segments</param>
    /// <param name="originalContent">Original document content</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Completeness verification results</returns>
    Task<CompletenessVerification> VerifyCompletenessAsync(
        List<DocumentSegment> segments,
        string originalContent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Identifies and analyzes quality issues across segments.
    /// </summary>
    /// <param name="segments">List of segments to analyze</param>
    /// <param name="originalContent">Original document content</param>
    /// <param name="assessmentResults">Previous assessment results for context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Quality issue analysis</returns>
    Task<QualityIssueAnalysis> AnalyzeQualityIssuesAsync(
        List<DocumentSegment> segments,
        string originalContent,
        ComprehensiveQualityAssessment? assessmentResults = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates improvement recommendations based on quality assessment.
    /// </summary>
    /// <param name="assessment">Quality assessment results</param>
    /// <param name="documentType">Type of document</param>
    /// <param name="strategy">Segmentation strategy used</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Improvement recommendations</returns>
    Task<ImprovementRecommendations> GenerateImprovementRecommendationsAsync(
        ComprehensiveQualityAssessment assessment,
        DocumentType documentType,
        SegmentationStrategy strategy,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Compares quality across different segmentation approaches.
    /// </summary>
    /// <param name="segmentationResults">Multiple segmentation results to compare</param>
    /// <param name="originalContent">Original document content</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Comparative quality analysis</returns>
    Task<ComparativeQualityAnalysis> CompareSegmentationQualityAsync(
        Dictionary<SegmentationStrategy, List<DocumentSegment>> segmentationResults,
        string originalContent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates quality against custom criteria.
    /// </summary>
    /// <param name="segments">Segments to validate</param>
    /// <param name="customCriteria">Custom validation criteria</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Custom validation results</returns>
    Task<CustomValidationResults> ValidateCustomCriteriaAsync(
        List<DocumentSegment> segments,
        List<CustomQualityCriterion> customCriteria,
        CancellationToken cancellationToken = default);
}
