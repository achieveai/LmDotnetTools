using MemoryServer.DocumentSegmentation.Models;

namespace MemoryServer.DocumentSegmentation.Services;

/// <summary>
/// Configuration options for structure-based segmentation.
/// </summary>
public class StructureSegmentationOptions
{
    /// <summary>
    /// Minimum segment size in characters.
    /// </summary>
    public int MinSegmentSize { get; set; } = 200;

    /// <summary>
    /// Maximum segment size in characters.
    /// </summary>
    public int MaxSegmentSize { get; set; } = 8000;

    /// <summary>
    /// Minimum confidence score for structural boundaries (0.0-1.0).
    /// </summary>
    public double MinStructuralBoundaryConfidence { get; set; } = 0.7;

    /// <summary>
    /// Whether to respect document hierarchy when segmenting.
    /// </summary>
    public bool RespectHierarchy { get; set; } = true;

    /// <summary>
    /// Maximum heading depth to consider for segmentation (1-6).
    /// </summary>
    public int MaxHeadingDepth { get; set; } = 4;

    /// <summary>
    /// Whether to merge small sections into larger segments.
    /// </summary>
    public bool MergeSmallSections { get; set; } = true;

    /// <summary>
    /// Minimum section size before merging (in characters).
    /// </summary>
    public int MinSectionSizeForMerging { get; set; } = 150;

    /// <summary>
    /// Whether to use LLM enhancement for structural analysis.
    /// </summary>
    public bool UseLlmEnhancement { get; set; } = true;

    /// <summary>
    /// Whether to preserve list structures intact.
    /// </summary>
    public bool PreserveListStructures { get; set; } = true;

    /// <summary>
    /// Whether to treat tables as separate segments.
    /// </summary>
    public bool TablesAsSeparateSegments { get; set; } = false;
}

/// <summary>
/// Represents a structural boundary detected in the document.
/// </summary>
public class StructureBoundary
{
    /// <summary>
    /// Position in the document where the structural boundary occurs.
    /// </summary>
    public int Position { get; set; }

    /// <summary>
    /// Confidence score for this boundary (0.0-1.0).
    /// </summary>
    public double Confidence { get; set; }

    /// <summary>
    /// Type of structural element.
    /// </summary>
    public StructuralElementType ElementType { get; set; }

    /// <summary>
    /// Heading level (1-6) if this is a heading boundary.
    /// </summary>
    public int? HeadingLevel { get; set; }

    /// <summary>
    /// Text content of the structural element (e.g., heading text).
    /// </summary>
    public string ElementText { get; set; } = string.Empty;

    /// <summary>
    /// Hierarchical level in the document structure.
    /// </summary>
    public int HierarchicalLevel { get; set; }

    /// <summary>
    /// Whether this boundary starts a new major section.
    /// </summary>
    public bool IsMajorSection { get; set; }

    /// <summary>
    /// Parent heading or section identifier.
    /// </summary>
    public string? ParentSectionId { get; set; }

    /// <summary>
    /// Formatting indicators that suggest structure.
    /// </summary>
    public List<string> FormattingIndicators { get; set; } = new();
}

/// <summary>
/// Types of structural elements in a document.
/// </summary>
public enum StructuralElementType
{
    /// <summary>
    /// Markdown or formatted heading.
    /// </summary>
    Heading,

    /// <summary>
    /// Section break or divider.
    /// </summary>
    SectionBreak,

    /// <summary>
    /// List item or numbered list.
    /// </summary>
    List,

    /// <summary>
    /// Table or structured data.
    /// </summary>
    Table,

    /// <summary>
    /// Code block or formatted code.
    /// </summary>
    CodeBlock,

    /// <summary>
    /// Blockquote or indented content.
    /// </summary>
    BlockQuote,

    /// <summary>
    /// Page break or document break.
    /// </summary>
    PageBreak,

    /// <summary>
    /// Paragraph with strong formatting cues.
    /// </summary>
    FormattedParagraph,

    /// <summary>
    /// Other structural element.
    /// </summary>
    Other
}

/// <summary>
/// Analysis of hierarchical structure within a document.
/// </summary>
public class HierarchicalStructureAnalysis
{
    /// <summary>
    /// Overall structural complexity score (0.0-1.0).
    /// </summary>
    public double StructuralComplexity { get; set; }

    /// <summary>
    /// Maximum heading depth found in the document.
    /// </summary>
    public int MaxHeadingDepth { get; set; }

    /// <summary>
    /// Total number of headings detected.
    /// </summary>
    public int TotalHeadings { get; set; }

    /// <summary>
    /// Number of major sections in the document.
    /// </summary>
    public int MajorSections { get; set; }

    /// <summary>
    /// Whether the document has a clear hierarchical structure.
    /// </summary>
    public bool HasClearHierarchy { get; set; }

    /// <summary>
    /// Structure consistency score (0.0-1.0).
    /// </summary>
    public double HierarchyConsistency { get; set; }

    /// <summary>
    /// Detected document outline or table of contents.
    /// </summary>
    public List<StructuralOutlineItem> DocumentOutline { get; set; } = new();

    /// <summary>
    /// Structural patterns detected in the document.
    /// </summary>
    public List<string> StructuralPatterns { get; set; } = new();

    /// <summary>
    /// Quality of structural organization.
    /// </summary>
    public double OrganizationQuality { get; set; }
}

/// <summary>
/// Represents an item in the document's structural outline.
/// </summary>
public class StructuralOutlineItem
{
    /// <summary>
    /// Heading or section title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Hierarchical level (1 = top level).
    /// </summary>
    public int Level { get; set; }

    /// <summary>
    /// Position in the document.
    /// </summary>
    public int Position { get; set; }

    /// <summary>
    /// Length of this section in characters.
    /// </summary>
    public int SectionLength { get; set; }

    /// <summary>
    /// Child sections under this item.
    /// </summary>
    public List<StructuralOutlineItem> Children { get; set; } = new();

    /// <summary>
    /// Type of structural element.
    /// </summary>
    public StructuralElementType ElementType { get; set; }
}

/// <summary>
/// Validation results for structure-based segmentation.
/// </summary>
public class StructureSegmentationValidation
{
    /// <summary>
    /// Overall quality score for the segmentation (0.0-1.0).
    /// </summary>
    public double OverallQuality { get; set; }

    /// <summary>
    /// Structural clarity score across all segments.
    /// </summary>
    public double StructuralClarity { get; set; }

    /// <summary>
    /// Hierarchy preservation score.
    /// </summary>
    public double HierarchyPreservation { get; set; }

    /// <summary>
    /// Section completeness score.
    /// </summary>
    public double SectionCompleteness { get; set; }

    /// <summary>
    /// Boundary accuracy score.
    /// </summary>
    public double BoundaryAccuracy { get; set; }

    /// <summary>
    /// Individual segment validation results.
    /// </summary>
    public List<StructureSegmentValidationResult> SegmentResults { get; set; } = new();

    /// <summary>
    /// Issues identified during validation.
    /// </summary>
    public List<StructureValidationIssue> Issues { get; set; } = new();

    /// <summary>
    /// Recommendations for improvement.
    /// </summary>
    public List<string> Recommendations { get; set; } = new();
}

/// <summary>
/// Validation result for an individual structure-based segment.
/// </summary>
public class StructureSegmentValidationResult
{
    /// <summary>
    /// Segment identifier.
    /// </summary>
    public string SegmentId { get; set; } = string.Empty;

    /// <summary>
    /// Structural clarity score for this segment.
    /// </summary>
    public double StructuralClarity { get; set; }

    /// <summary>
    /// Section completeness (how complete the structural unit is).
    /// </summary>
    public double SectionCompleteness { get; set; }

    /// <summary>
    /// Hierarchy consistency within this segment.
    /// </summary>
    public double HierarchyConsistency { get; set; }

    /// <summary>
    /// Content organization quality.
    /// </summary>
    public double OrganizationQuality { get; set; }

    /// <summary>
    /// Quality issues found in this segment.
    /// </summary>
    public List<StructureValidationIssue> Issues { get; set; } = new();
}

/// <summary>
/// Represents a validation issue found during structural quality assessment.
/// </summary>
public class StructureValidationIssue
{
    /// <summary>
    /// Type of validation issue.
    /// </summary>
    public StructureValidationIssueType Type { get; set; }

    /// <summary>
    /// Severity level of the issue.
    /// </summary>
    public ValidationSeverity Severity { get; set; }

    /// <summary>
    /// Human-readable description of the issue.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Position in the document where the issue occurs.
    /// </summary>
    public int Position { get; set; }

    /// <summary>
    /// Suggested fix or improvement.
    /// </summary>
    public string SuggestedFix { get; set; } = string.Empty;
}

/// <summary>
/// Types of structural validation issues.
/// </summary>
public enum StructureValidationIssueType
{
    /// <summary>
    /// Incomplete section or broken hierarchy.
    /// </summary>
    IncompleteSection,

    /// <summary>
    /// Poor structural organization.
    /// </summary>
    PoorOrganization,

    /// <summary>
    /// Mixed heading levels inappropriately.
    /// </summary>
    InconsistentHeadingLevels,

    /// <summary>
    /// Segment cuts across logical structural boundaries.
    /// </summary>
    CrossBoundarySegment,

    /// <summary>
    /// Missing structural context.
    /// </summary>
    MissingStructuralContext,

    /// <summary>
    /// Orphaned content without clear structural placement.
    /// </summary>
    OrphanedContent,

    /// <summary>
    /// Overly fragmented structure.
    /// </summary>
    Fragmentation,

    /// <summary>
    /// Structural elements spanning multiple segments inappropriately.
    /// </summary>
    BrokenStructuralElements
}
