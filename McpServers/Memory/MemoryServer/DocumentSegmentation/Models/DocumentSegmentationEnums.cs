namespace MemoryServer.DocumentSegmentation.Models;

/// <summary>
/// Enumeration of supported document types for optimal segmentation strategy selection.
/// </summary>
public enum DocumentType
{
    /// <summary>
    /// Generic document without specific structure
    /// </summary>
    Generic,

    /// <summary>
    /// Academic research paper with standard structure
    /// </summary>
    ResearchPaper,

    /// <summary>
    /// News or blog article
    /// </summary>
    Article,

    /// <summary>
    /// Meeting or conversation transcript
    /// </summary>
    Transcript,

    /// <summary>
    /// Business or technical report
    /// </summary>
    Report,

    /// <summary>
    /// Technical documentation or manual
    /// </summary>
    Documentation,

    /// <summary>
    /// Email communication
    /// </summary>
    Email,

    /// <summary>
    /// Chat or messaging conversation
    /// </summary>
    Chat,

    /// <summary>
    /// Legal document or contract
    /// </summary>
    Legal,

    /// <summary>
    /// Technical specification or code documentation
    /// </summary>
    Technical
}

/// <summary>
/// Enumeration of available segmentation strategies.
/// </summary>
public enum SegmentationStrategy
{
    /// <summary>
    /// Segment based on topic boundaries and semantic coherence
    /// </summary>
    TopicBased,

    /// <summary>
    /// Segment based on document structure (headings, sections, etc.)
    /// </summary>
    StructureBased,

    /// <summary>
    /// Segment based on narrative flow and logical progression
    /// </summary>
    NarrativeBased,

    /// <summary>
    /// Hybrid approach combining multiple strategies
    /// </summary>
    Hybrid,

    /// <summary>
    /// Custom strategy for specific use cases
    /// </summary>
    Custom
}

/// <summary>
/// Enumeration of segment relationship types.
/// </summary>
public enum SegmentRelationshipType
{
    /// <summary>
    /// Sequential order relationship (follows in sequence)
    /// </summary>
    Sequential,

    /// <summary>
    /// Hierarchical relationship (parent-child structure)
    /// </summary>
    Hierarchical,

    /// <summary>
    /// Referential relationship (cross-references or citations)
    /// </summary>
    Referential,

    /// <summary>
    /// Topical relationship (shared topics or themes)
    /// </summary>
    Topical
}
