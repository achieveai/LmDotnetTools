using MemoryServer.DocumentSegmentation.Models;
using MemoryServer.DocumentSegmentation.Services;
using MemoryServer.Models;

namespace MemoryServer.DocumentSegmentation.Services;

/// <summary>
/// Interface for narrative-based document segmentation services.
/// Provides specialized methods for detecting narrative flow, logical progression, and causal relationships.
/// </summary>
public interface INarrativeBasedSegmentationService
{
    /// <summary>
    /// Segments document content based on narrative flow and logical progression.
    /// </summary>
    /// <param name="content">The document content to segment</param>
    /// <param name="documentType">Type of document for context</param>
    /// <param name="options">Narrative segmentation options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of narrative-based document segments</returns>
    Task<List<DocumentSegment>> SegmentByNarrativeAsync(
        string content,
        DocumentType documentType = DocumentType.Generic,
        NarrativeSegmentationOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Detects narrative transitions and flow boundaries in the document.
    /// </summary>
    /// <param name="content">The document content to analyze</param>
    /// <param name="documentType">Type of document for context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of detected narrative boundaries</returns>
    Task<List<NarrativeBoundary>> DetectNarrativeTransitionsAsync(
        string content,
        DocumentType documentType = DocumentType.Generic,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyzes the logical flow and narrative structure of the document.
    /// </summary>
    /// <param name="content">The document content to analyze</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Narrative flow analysis results</returns>
    Task<NarrativeFlowAnalysis> AnalyzeLogicalFlowAsync(
        string content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates narrative-based segments for quality and coherence.
    /// </summary>
    /// <param name="segments">List of segments to validate</param>
    /// <param name="originalContent">Original document content</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Narrative validation results</returns>
    Task<NarrativeSegmentationValidation> ValidateNarrativeSegmentsAsync(
        List<DocumentSegment> segments,
        string originalContent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Identifies temporal sequences and chronological patterns in the content.
    /// </summary>
    /// <param name="content">The document content to analyze</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Detected temporal sequences with positions and types</returns>
    Task<List<TemporalSequence>> IdentifyTemporalSequencesAsync(
        string content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Detects causal relationships between different parts of the document.
    /// </summary>
    /// <param name="content">The document content to analyze</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Detected causal relationships</returns>
    Task<List<CausalRelation>> DetectCausalRelationshipsAsync(
        string content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Identifies narrative arc elements (setup, development, climax, resolution) in the content.
    /// </summary>
    /// <param name="content">The document content to analyze</param>
    /// <param name="documentType">Type of document for context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Identified narrative arc elements with their positions and functions</returns>
    Task<Dictionary<NarrativeFunction, List<int>>> IdentifyNarrativeArcElementsAsync(
        string content,
        DocumentType documentType = DocumentType.Generic,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a temporal sequence detected in narrative content.
/// </summary>
public class TemporalSequence
{
    /// <summary>
    /// Starting position of the temporal sequence.
    /// </summary>
    public int StartPosition { get; set; }

    /// <summary>
    /// Ending position of the temporal sequence.
    /// </summary>
    public int EndPosition { get; set; }

    /// <summary>
    /// Type of temporal relationship.
    /// </summary>
    public TemporalRelationship Type { get; set; }

    /// <summary>
    /// Temporal markers that indicate this sequence.
    /// </summary>
    public List<string> TemporalMarkers { get; set; } = new List<string>();

    /// <summary>
    /// Confidence score for this temporal sequence (0.0 to 1.0).
    /// </summary>
    public double Confidence { get; set; }

    /// <summary>
    /// Sequential order within the overall narrative.
    /// </summary>
    public int SequentialOrder { get; set; }
}
