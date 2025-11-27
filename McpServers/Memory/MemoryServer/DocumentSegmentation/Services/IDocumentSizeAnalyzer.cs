using MemoryServer.DocumentSegmentation.Models;

namespace MemoryServer.DocumentSegmentation.Services;

/// <summary>
///     Service interface for analyzing document size and determining segmentation requirements.
/// </summary>
public interface IDocumentSizeAnalyzer
{
    /// <summary>
    ///     Analyzes document content to determine size metrics.
    /// </summary>
    /// <param name="content">Document content to analyze</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Document statistics including character, word, and estimated token counts</returns>
    Task<DocumentStatistics> AnalyzeDocumentAsync(string content, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Determines if a document should be segmented based on configured thresholds.
    /// </summary>
    /// <param name="statistics">Document statistics from analysis</param>
    /// <param name="documentType">Type of document for specific thresholds</param>
    /// <returns>True if document should be segmented</returns>
    bool ShouldSegmentDocument(DocumentStatistics statistics, DocumentType documentType = DocumentType.Generic);

    /// <summary>
    ///     Calculates optimal segment count for a document.
    /// </summary>
    /// <param name="statistics">Document statistics from analysis</param>
    /// <param name="targetSegmentSize">Target size per segment in words</param>
    /// <param name="maxSegmentSize">Maximum allowed segment size in words</param>
    /// <returns>Recommended number of segments</returns>
    int CalculateOptimalSegmentCount(
        DocumentStatistics statistics,
        int targetSegmentSize = 1000,
        int maxSegmentSize = 2000
    );

    /// <summary>
    ///     Estimates processing time for document segmentation.
    /// </summary>
    /// <param name="statistics">Document statistics from analysis</param>
    /// <param name="strategy">Segmentation strategy to use</param>
    /// <returns>Estimated processing time in milliseconds</returns>
    long EstimateProcessingTime(DocumentStatistics statistics, SegmentationStrategy strategy);
}
