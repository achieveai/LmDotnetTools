using System.Text.RegularExpressions;
using MemoryServer.DocumentSegmentation.Models;
using Microsoft.Extensions.Options;

namespace MemoryServer.DocumentSegmentation.Services;

/// <summary>
///     Service for analyzing document size and determining segmentation requirements.
/// </summary>
public partial class DocumentSizeAnalyzer : IDocumentSizeAnalyzer
{
    // Regex patterns for text analysis
    private static readonly Regex WordPattern = MyRegex();
    private static readonly Regex SentencePattern = MyRegex1();
    private static readonly Regex ParagraphPattern = MyRegex2();
    private readonly ILogger<DocumentSizeAnalyzer> _logger;
    private readonly DocumentSegmentationOptions _options;

    public DocumentSizeAnalyzer(ILogger<DocumentSizeAnalyzer> logger, IOptions<DocumentSegmentationOptions> options)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    ///     Analyzes document content to determine size metrics.
    /// </summary>
    public async Task<DocumentStatistics> AnalyzeDocumentAsync(
        string content,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogDebug("Analyzing document size, content length: {Length} characters", content?.Length ?? 0);

            // Handle null content
            if (content == null)
            {
                return new DocumentStatistics
                {
                    CharacterCount = 0,
                    WordCount = 0,
                    SentenceCount = 0,
                    ParagraphCount = 1, // Default to 1 paragraph even for empty content
                    TokenCount = 0,
                };
            }

            // Handle empty or whitespace-only content
            if (string.IsNullOrWhiteSpace(content))
            {
                return new DocumentStatistics
                {
                    CharacterCount = content.Length, // Preserve actual character count (including whitespace)
                    WordCount = 0,
                    SentenceCount = 0,
                    ParagraphCount = content.Length == 0 ? 1 : CountParagraphs(content), // 1 for empty, counted for whitespace
                    TokenCount = 0,
                };
            }

            var statistics = new DocumentStatistics
            {
                CharacterCount = content.Length,
                WordCount = CountWords(content),
                SentenceCount = CountSentences(content),
                ParagraphCount = CountParagraphs(content),
                // Estimate token count (rough approximation: 1 token ≈ 4 characters for English)
                TokenCount = EstimateTokenCount(content),
            };

            _logger.LogDebug(
                "Document analysis complete: {WordCount} words, {TokenCount} tokens, {SentenceCount} sentences, {ParagraphCount} paragraphs",
                statistics.WordCount,
                statistics.TokenCount,
                statistics.SentenceCount,
                statistics.ParagraphCount
            );

            return await Task.FromResult(statistics);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error analyzing document content");
            throw;
        }
    }

    /// <summary>
    ///     Determines if a document should be segmented based on configured thresholds.
    /// </summary>
    public bool ShouldSegmentDocument(DocumentStatistics statistics, DocumentType documentType = DocumentType.Generic)
    {
        if (statistics == null)
        {
            return false;
        }

        var thresholds = _options.Thresholds;
        var minWords = GetMinWordsForDocumentType(documentType, thresholds.MinDocumentSizeWords);
        var maxWords = thresholds.MaxDocumentSizeWords;

        var shouldSegment = statistics.WordCount >= minWords && statistics.WordCount <= maxWords;

        _logger.LogDebug(
            "Document segmentation decision for {DocumentType}: {WordCount} words, threshold: {MinWords}-{MaxWords}, should segment: {ShouldSegment}",
            documentType,
            statistics.WordCount,
            minWords,
            maxWords,
            shouldSegment
        );

        return shouldSegment;
    }

    /// <summary>
    ///     Calculates optimal segment count for a document.
    /// </summary>
    public int CalculateOptimalSegmentCount(
        DocumentStatistics statistics,
        int targetSegmentSize = 1000,
        int maxSegmentSize = 2000
    )
    {
        if (statistics == null || statistics.WordCount <= targetSegmentSize)
        {
            return 1;
        }

        // Calculate initial segment count based on target size
        var initialCount = (int)Math.Ceiling((double)statistics.WordCount / targetSegmentSize);

        // Ensure no segment exceeds maximum size
        var maxPossibleSegments = (int)Math.Ceiling((double)statistics.WordCount / maxSegmentSize);

        var optimalCount = Math.Max(initialCount, maxPossibleSegments);

        _logger.LogDebug(
            "Calculated optimal segment count: {Count} for {WordCount} words (target: {TargetSize}, max: {MaxSize})",
            optimalCount,
            statistics.WordCount,
            targetSegmentSize,
            maxSegmentSize
        );

        return optimalCount;
    }

    /// <summary>
    ///     Estimates processing time for document segmentation.
    /// </summary>
    public long EstimateProcessingTime(DocumentStatistics statistics, SegmentationStrategy strategy)
    {
        if (statistics == null)
        {
            return 0;
        }

        // Base processing time (ms per word)
        var baseTimePerWord = strategy switch
        {
            SegmentationStrategy.TopicBased => 2.0, // More complex analysis
            SegmentationStrategy.StructureBased => 1.0, // Pattern-based, faster
            SegmentationStrategy.NarrativeBased => 2.5, // Most complex
            SegmentationStrategy.Hybrid => 3.0, // Combines multiple approaches
            SegmentationStrategy.Custom => 2.0, // Variable complexity
            _ => 1.5, // Default
        };

        // LLM overhead for intelligent segmentation
        var llmOverhead = _options.LlmOptions.EnableLlmSegmentation ? 5000 : 0; // 5 seconds base overhead

        var estimatedTime = (long)((statistics.WordCount * baseTimePerWord) + llmOverhead);

        _logger.LogDebug(
            "Estimated processing time for {Strategy}: {Time}ms for {WordCount} words",
            strategy,
            estimatedTime,
            statistics.WordCount
        );

        return estimatedTime;
    }

    #region Private Methods

    private static int CountWords(string content)
    {
        return WordPattern.Matches(content).Count;
    }

    private static int CountSentences(string content)
    {
        return SentencePattern.Matches(content).Count;
    }

    private static int CountParagraphs(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return 1; // Always at least 1 paragraph for empty content
        }

        // Count paragraph breaks and add 1 for the content itself
        var breaks = ParagraphPattern.Matches(content).Count;
        return Math.Max(1, breaks + 1); // Ensure at least 1 paragraph
    }

    private static int EstimateTokenCount(string content)
    {
        // Simple approximation: 1 token ≈ 4 characters for English text
        // This is a rough estimate; actual tokenization would require specific model tokenizers
        return (int)Math.Ceiling(content.Length / 4.0);
    }

    private static int GetMinWordsForDocumentType(DocumentType documentType, int defaultMinWords)
    {
        // Adjust minimum word thresholds based on document type
        return documentType switch
        {
            DocumentType.Email => 250, // Lower threshold for emails (300 words should segment)
            DocumentType.Chat => 150, // Even lower for chat messages (200 words should segment)
            DocumentType.ResearchPaper => 2000, // Higher threshold for academic papers
            DocumentType.Legal => 1000, // Higher threshold for legal documents
            DocumentType.Technical => 1500, // Higher threshold for technical docs
            _ => defaultMinWords, // Use default for other types
        };
    }

    [GeneratedRegex(@"\b\w+\b", RegexOptions.Compiled)]
    private static partial Regex MyRegex();

    [GeneratedRegex(@"[.!?]+\s*", RegexOptions.Compiled)]
    private static partial Regex MyRegex1();

    [GeneratedRegex(@"\n\s*\n", RegexOptions.Compiled)]
    private static partial Regex MyRegex2();

    #endregion
}
