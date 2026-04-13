using MemoryServer.DocumentSegmentation.Models;

namespace MemoryServer.DocumentSegmentation.Services;

/// <summary>
///     Interface for hybrid document segmentation services.
///     Combines multiple segmentation strategies with intelligent weighting and selection.
/// </summary>
public interface IHybridSegmentationService
{
    /// <summary>
    ///     Segments document content using hybrid approach that combines multiple strategies.
    /// </summary>
    /// <param name="content">Document content to segment</param>
    /// <param name="documentType">Type of document being segmented</param>
    /// <param name="options">Configuration options for hybrid segmentation</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of document segments using optimal strategy combination</returns>
    Task<List<DocumentSegment>> SegmentUsingHybridApproachAsync(
        string content,
        DocumentType documentType = DocumentType.Generic,
        HybridSegmentationOptions? options = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Determines optimal strategy weights for a specific document.
    /// </summary>
    /// <param name="content">Document content to analyze</param>
    /// <param name="documentType">Type of document being analyzed</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Strategy weights indicating the relative importance of each approach</returns>
    Task<StrategyWeights> DetermineStrategyWeightsAsync(
        string content,
        DocumentType documentType = DocumentType.Generic,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Combines segmentation results from multiple strategies using intelligent merging.
    /// </summary>
    /// <param name="structureSegments">Segments from structure-based strategy</param>
    /// <param name="narrativeSegments">Segments from narrative-based strategy</param>
    /// <param name="topicSegments">Segments from topic-based strategy</param>
    /// <param name="weights">Strategy weights for combination</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Combined and optimized segment list</returns>
    Task<List<DocumentSegment>> CombineSegmentationResultsAsync(
        List<DocumentSegment> structureSegments,
        List<DocumentSegment> narrativeSegments,
        List<DocumentSegment> topicSegments,
        StrategyWeights weights,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Validates hybrid segmentation quality and provides improvement suggestions.
    /// </summary>
    /// <param name="segments">Segments to validate</param>
    /// <param name="originalContent">Original document content</param>
    /// <param name="weights">Strategy weights used in segmentation</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Quality assessment with hybrid-specific metrics</returns>
    Task<HybridSegmentationValidation> ValidateHybridSegmentationAsync(
        List<DocumentSegment> segments,
        string originalContent,
        StrategyWeights weights,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Adapts segmentation strategy based on document characteristics and feedback.
    /// </summary>
    /// <param name="content">Document content</param>
    /// <param name="previousResults">Previous segmentation results for learning</param>
    /// <param name="feedback">Quality feedback from previous attempts</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Adapted strategy configuration</returns>
    Task<AdaptiveStrategyConfig> AdaptSegmentationStrategyAsync(
        string content,
        List<DocumentSegmentationResult>? previousResults = null,
        List<string>? feedback = null,
        CancellationToken cancellationToken = default
    );
}
