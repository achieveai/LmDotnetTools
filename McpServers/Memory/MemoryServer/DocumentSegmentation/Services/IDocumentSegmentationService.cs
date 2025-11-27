using MemoryServer.DocumentSegmentation.Models;
using MemoryServer.Models;

namespace MemoryServer.DocumentSegmentation.Services;

/// <summary>
///     Service interface for document segmentation operations with LLM-powered intelligent segmentation.
/// </summary>
public interface IDocumentSegmentationService
{
    /// <summary>
    ///     Segments a document into logical chunks using the optimal strategy.
    /// </summary>
    /// <param name="content">The document content to segment</param>
    /// <param name="request">Segmentation request parameters</param>
    /// <param name="sessionContext">Session context for isolation</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Segmentation result with segments and metadata</returns>
    Task<DocumentSegmentationResult> SegmentDocumentAsync(
        string content,
        DocumentSegmentationRequest request,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Determines if a document should be segmented based on size and complexity.
    /// </summary>
    /// <param name="content">The document content to analyze</param>
    /// <param name="documentType">Type of document for optimization</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if segmentation is recommended</returns>
    Task<bool> ShouldSegmentAsync(
        string content,
        DocumentType documentType = DocumentType.Generic,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Determines the optimal segmentation strategy for a document.
    /// </summary>
    /// <param name="content">The document content to analyze</param>
    /// <param name="documentType">Type of document for optimization</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Recommended segmentation strategy</returns>
    Task<SegmentationStrategy> DetermineOptimalStrategyAsync(
        string content,
        DocumentType documentType = DocumentType.Generic,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Validates the quality of segmentation results.
    /// </summary>
    /// <param name="result">Segmentation result to validate</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Quality assessment with scores and feedback</returns>
    Task<SegmentationQualityAssessment> ValidateSegmentationQualityAsync(
        DocumentSegmentationResult result,
        CancellationToken cancellationToken = default
    );
}
