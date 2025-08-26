using MemoryServer.DocumentSegmentation.Models;
using Microsoft.Extensions.Logging;

namespace MemoryServer.DocumentSegmentation.Services;

/// <summary>
/// Service for analyzing documents and determining optimal segmentation strategies.
/// Uses document characteristics and heuristics to recommend the best approach.
/// </summary>
public interface IDocumentAnalysisService
{
    /// <summary>
    /// Analyzes document characteristics to determine document type.
    /// </summary>
    /// <param name="content">Document content to analyze</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Detected document type with confidence</returns>
    Task<DocumentTypeDetection> DetectDocumentTypeAsync(
        string content,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Analyzes document structure and content to recommend optimal segmentation strategy.
    /// </summary>
    /// <param name="content">Document content to analyze</param>
    /// <param name="documentType">Document type if known</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Strategy recommendation with confidence and reasoning</returns>
    Task<StrategyRecommendation> AnalyzeOptimalStrategyAsync(
        string content,
        DocumentType? documentType = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Analyzes document complexity to help determine processing approach.
    /// </summary>
    /// <param name="content">Document content to analyze</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Document complexity analysis</returns>
    Task<DocumentComplexityAnalysis> AnalyzeComplexityAsync(
        string content,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// Document type detection result with confidence.
/// </summary>
public class DocumentTypeDetection
{
    /// <summary>
    /// Detected document type
    /// </summary>
    public DocumentType DocumentType { get; set; } = DocumentType.Generic;

    /// <summary>
    /// Confidence score (0.0 to 1.0)
    /// </summary>
    public double Confidence { get; set; }

    /// <summary>
    /// Characteristics that led to this detection
    /// </summary>
    public List<string> DetectedCharacteristics { get; set; } = new();

    /// <summary>
    /// Alternative document types considered
    /// </summary>
    public List<(DocumentType Type, double Confidence)> Alternatives { get; set; } = new();
}

/// <summary>
/// Document complexity analysis result.
/// </summary>
public class DocumentComplexityAnalysis
{
    /// <summary>
    /// Overall complexity score (0.0 to 1.0)
    /// </summary>
    public double ComplexityScore { get; set; }

    /// <summary>
    /// Structural complexity (headings, lists, tables)
    /// </summary>
    public double StructuralComplexity { get; set; }

    /// <summary>
    /// Semantic complexity (topic diversity, vocabulary)
    /// </summary>
    public double SemanticComplexity { get; set; }

    /// <summary>
    /// Length complexity (word count, paragraph count)
    /// </summary>
    public double LengthComplexity { get; set; }

    /// <summary>
    /// Detected document features
    /// </summary>
    public DocumentFeatures Features { get; set; } = new();

    /// <summary>
    /// Processing recommendations based on complexity
    /// </summary>
    public List<string> Recommendations { get; set; } = new();
}

/// <summary>
/// Document features detected during analysis.
/// </summary>
public class DocumentFeatures
{
    /// <summary>
    /// Number of headings detected
    /// </summary>
    public int HeadingCount { get; set; }

    /// <summary>
    /// Maximum heading depth
    /// </summary>
    public int MaxHeadingDepth { get; set; }

    /// <summary>
    /// Number of list items
    /// </summary>
    public int ListItemCount { get; set; }

    /// <summary>
    /// Number of code blocks
    /// </summary>
    public int CodeBlockCount { get; set; }

    /// <summary>
    /// Number of tables
    /// </summary>
    public int TableCount { get; set; }

    /// <summary>
    /// Number of links
    /// </summary>
    public int LinkCount { get; set; }

    /// <summary>
    /// Average sentence length
    /// </summary>
    public double AverageSentenceLength { get; set; }

    /// <summary>
    /// Vocabulary diversity (unique words / total words)
    /// </summary>
    public double VocabularyDiversity { get; set; }

    /// <summary>
    /// Paragraph count
    /// </summary>
    public int ParagraphCount { get; set; }

    /// <summary>
    /// Total word count
    /// </summary>
    public int WordCount { get; set; }

    /// <summary>
    /// Whether document has conversational patterns
    /// </summary>
    public bool HasConversationalPatterns { get; set; }

    /// <summary>
    /// Whether document has formal structure
    /// </summary>
    public bool HasFormalStructure { get; set; }

    /// <summary>
    /// Whether document has narrative flow
    /// </summary>
    public bool HasNarrativeFlow { get; set; }
}
