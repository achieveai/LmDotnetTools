using MemoryServer.DocumentSegmentation.Models;

namespace MemoryServer.DocumentSegmentation.Services;

/// <summary>
///     Interface for structure-based document segmentation services.
///     Provides specialized methods for detecting structural boundaries and analyzing hierarchical organization.
/// </summary>
public interface IStructureBasedSegmentationService
{
    /// <summary>
    ///     Segments document content based on structural elements like headings, sections, and hierarchical organization.
    /// </summary>
    /// <param name="content">Document content to segment</param>
    /// <param name="documentType">Type of document being segmented</param>
    /// <param name="options">Configuration options for structure-based segmentation</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of document segments based on structural boundaries</returns>
    Task<List<DocumentSegment>> SegmentByStructureAsync(
        string content,
        DocumentType documentType = DocumentType.Generic,
        StructureSegmentationOptions? options = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Detects structural boundaries within the document content.
    /// </summary>
    /// <param name="content">Document content to analyze</param>
    /// <param name="documentType">Type of document being analyzed</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of detected structural boundaries</returns>
    Task<List<StructureBoundary>> DetectStructuralBoundariesAsync(
        string content,
        DocumentType documentType = DocumentType.Generic,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Analyzes the hierarchical structure of a document.
    /// </summary>
    /// <param name="content">Document content to analyze</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Hierarchical structure analysis results</returns>
    Task<HierarchicalStructureAnalysis> AnalyzeHierarchicalStructureAsync(
        string content,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Validates structure-based segments for quality and organization.
    /// </summary>
    /// <param name="segments">Segments to validate</param>
    /// <param name="originalContent">Original document content</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Validation results with structural quality metrics</returns>
    Task<StructureSegmentationValidation> ValidateStructureSegmentsAsync(
        List<DocumentSegment> segments,
        string originalContent,
        CancellationToken cancellationToken = default
    );
}
