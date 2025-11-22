using System.Text.RegularExpressions;
using MemoryServer.DocumentSegmentation.Models;
using Microsoft.Extensions.Options;

namespace MemoryServer.DocumentSegmentation.Services;

/// <summary>
/// Implementation of document analysis service that uses heuristics and pattern recognition
/// to determine document types and recommend segmentation strategies.
/// </summary>
public partial class DocumentAnalysisService : IDocumentAnalysisService
{
    private readonly ILogger<DocumentAnalysisService> _logger;
    private readonly DocumentSegmentationOptions _options;

    // Pattern regex for different document characteristics
    private static readonly Regex EmailHeaderPattern = MyRegex();
    private static readonly Regex ChatTimestampPattern = MyRegex1();
    private static readonly Regex LegalCitationPattern = MyRegex2();
    private static readonly Regex HeadingPattern = MyRegex3();
    private static readonly Regex CodeBlockPattern = MyRegex4();
    private static readonly Regex ListPattern = MyRegex5();
    private static readonly Regex TablePattern = MyRegex6();
    private static readonly Regex LinkPattern = MyRegex7();
    private static readonly Regex MethodologyPattern = MyRegex8();
    private static readonly Regex ConversationPattern = MyRegex9();
    private static readonly char[] separator = [' ', '\t', '\n', '\r'];
    private static readonly string[] separatorArray = ["\n\n", "\r\n\r\n"];
    private static readonly char[] separatorArray0 = ['.', '!', '?'];
    private static readonly char[] separatorArray1 = [' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?'];

    public DocumentAnalysisService(
        ILogger<DocumentAnalysisService> logger,
        IOptions<DocumentSegmentationOptions> options
    )
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Analyzes document characteristics to determine document type.
    /// </summary>
    public async Task<DocumentTypeDetection> DetectDocumentTypeAsync(
        string content,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new DocumentTypeDetection
            {
                DocumentType = DocumentType.Generic,
                Confidence = 1.0,
                DetectedCharacteristics = { "Empty or whitespace-only content" },
            };
        }

        _logger.LogDebug(
            "Analyzing document characteristics for type detection, content length: {Length}",
            content.Length
        );

        var characteristics = new List<string>();
        var scores = new Dictionary<DocumentType, double>();

        // Initialize all scores
        foreach (var type in Enum.GetValues<DocumentType>())
        {
            scores[type] = 0.0;
        }

        // Analyze email characteristics
        AnalyzeEmailCharacteristics(content, characteristics, scores);

        // Analyze chat characteristics
        AnalyzeChatCharacteristics(content, characteristics, scores);

        // Analyze legal characteristics
        AnalyzeLegalCharacteristics(content, characteristics, scores);

        // Analyze research paper characteristics
        AnalyzeResearchCharacteristics(content, characteristics, scores);

        // Analyze technical documentation characteristics
        AnalyzeTechnicalCharacteristics(content, characteristics, scores);

        // Find the highest scoring type
        var bestMatch = scores.OrderByDescending(kvp => kvp.Value).First();
        var alternatives = scores
            .Where(kvp => kvp.Key != bestMatch.Key && kvp.Value > 0.1)
            .OrderByDescending(kvp => kvp.Value)
            .Take(3)
            .Select(kvp => (kvp.Key, kvp.Value))
            .ToList();

        var result = new DocumentTypeDetection
        {
            DocumentType = bestMatch.Key,
            Confidence = Math.Min(bestMatch.Value, 1.0),
            DetectedCharacteristics = characteristics,
            Alternatives = alternatives,
        };

        _logger.LogDebug(
            "Document type detection completed: {DocumentType} with confidence {Confidence:F2}",
            result.DocumentType,
            result.Confidence
        );

        return await Task.FromResult(result);
    }

    /// <summary>
    /// Analyzes document structure and content to recommend optimal segmentation strategy.
    /// </summary>
    public async Task<StrategyRecommendation> AnalyzeOptimalStrategyAsync(
        string content,
        DocumentType? documentType = null,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new StrategyRecommendation
            {
                Strategy = SegmentationStrategy.TopicBased,
                Confidence = 0.5,
                Reasoning = "Default strategy for empty content",
            };
        }

        _logger.LogDebug("Analyzing optimal segmentation strategy for content length: {Length}", content.Length);

        // Detect document type if not provided
        var typeDetection = documentType.HasValue
            ? new DocumentTypeDetection { DocumentType = documentType.Value, Confidence = 1.0 }
            : await DetectDocumentTypeAsync(content, cancellationToken);

        // Analyze document complexity
        var complexity = await AnalyzeComplexityAsync(content, cancellationToken);

        // Calculate strategy scores based on document characteristics
        var strategyScores = CalculateStrategyScores(typeDetection, complexity, content);

        // Find the best strategy
        var bestStrategy = strategyScores.OrderByDescending(kvp => kvp.Value).First();
        var alternatives = strategyScores
            .Where(kvp => kvp.Key != bestStrategy.Key && kvp.Value > 0.2)
            .OrderByDescending(kvp => kvp.Value)
            .Take(2)
            .Select(kvp => kvp.Key)
            .ToList();

        var reasoning = GenerateStrategyReasoning(bestStrategy.Key, typeDetection, complexity);

        var result = new StrategyRecommendation
        {
            Strategy = bestStrategy.Key,
            Confidence = Math.Min(bestStrategy.Value, 1.0),
            Reasoning = reasoning,
            Alternatives = alternatives,
        };

        _logger.LogInformation(
            "Strategy recommendation: {Strategy} with confidence {Confidence:F2} for {DocumentType}",
            result.Strategy,
            result.Confidence,
            typeDetection.DocumentType
        );

        return result;
    }

    /// <summary>
    /// Analyzes document complexity to help determine processing approach.
    /// </summary>
    public async Task<DocumentComplexityAnalysis> AnalyzeComplexityAsync(
        string content,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new DocumentComplexityAnalysis
            {
                ComplexityScore = 0.0,
                StructuralComplexity = 0.0,
                SemanticComplexity = 0.0,
                LengthComplexity = 0.0,
            };
        }

        _logger.LogDebug("Analyzing document complexity for content length: {Length}", content.Length);

        var features = AnalyzeDocumentFeatures(content);

        // Calculate complexity scores
        var structuralComplexity = CalculateStructuralComplexity(features);
        var semanticComplexity = CalculateSemanticComplexity(features, content);
        var lengthComplexity = CalculateLengthComplexity(features);

        var overallComplexity = (structuralComplexity + semanticComplexity + lengthComplexity) / 3.0;

        var recommendations = GenerateComplexityRecommendations(overallComplexity, features);

        var result = new DocumentComplexityAnalysis
        {
            ComplexityScore = overallComplexity,
            StructuralComplexity = structuralComplexity,
            SemanticComplexity = semanticComplexity,
            LengthComplexity = lengthComplexity,
            Features = features,
            Recommendations = recommendations,
        };

        _logger.LogDebug(
            "Complexity analysis completed: Overall={ComplexityScore:F2}, Structural={StructuralComplexity:F2}, Semantic={SemanticComplexity:F2}, Length={LengthComplexity:F2}",
            result.ComplexityScore,
            result.StructuralComplexity,
            result.SemanticComplexity,
            result.LengthComplexity
        );

        return await Task.FromResult(result);
    }

    #region Private Helper Methods

    private static void AnalyzeEmailCharacteristics(
        string content,
        List<string> characteristics,
        Dictionary<DocumentType, double> scores
    )
    {
        var emailHeaders = EmailHeaderPattern.Matches(content).Count;
        if (emailHeaders >= 3)
        {
            characteristics.Add($"Email headers detected ({emailHeaders})");
            scores[DocumentType.Email] += 0.8;
        }

        // Look for email signature patterns
        if (content.Contains("--") && (content.Contains('@') || content.Contains("mailto:")))
        {
            characteristics.Add("Email signature patterns");
            scores[DocumentType.Email] += 0.3;
        }

        // Reply/forward patterns
        if (content.Contains("Re:") || content.Contains("Fwd:") || content.Contains("Original Message"))
        {
            characteristics.Add("Email thread patterns");
            scores[DocumentType.Email] += 0.4;
        }
    }

    private static void AnalyzeChatCharacteristics(
        string content,
        List<string> characteristics,
        Dictionary<DocumentType, double> scores
    )
    {
        var timestampMatches = ChatTimestampPattern.Matches(content).Count;
        var conversationMatches = ConversationPattern.Matches(content).Count;

        if (timestampMatches >= 3)
        {
            characteristics.Add($"Timestamp patterns detected ({timestampMatches})");
            scores[DocumentType.Chat] += 0.6;
        }

        if (conversationMatches >= 5)
        {
            characteristics.Add($"Conversational patterns detected ({conversationMatches})");
            scores[DocumentType.Chat] += 0.7;
        }

        // Look for short, informal messages
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var shortLines = lines.Count(line => line.Trim().Length is < 100 and > 5);
        if (shortLines > lines.Length * 0.6 && lines.Length > 10)
        {
            characteristics.Add("Short, conversational messages");
            scores[DocumentType.Chat] += 0.5;
        }
    }

    private static void AnalyzeLegalCharacteristics(
        string content,
        List<string> characteristics,
        Dictionary<DocumentType, double> scores
    )
    {
        var legalCitations = LegalCitationPattern.Matches(content).Count;
        if (legalCitations >= 2)
        {
            characteristics.Add($"Legal citations detected ({legalCitations})");
            scores[DocumentType.Legal] += 0.7;
        }

        // Look for legal terminology
        var legalTerms = new[]
        {
            "plaintiff",
            "defendant",
            "court",
            "statute",
            "regulation",
            "hereby",
            "whereas",
            "jurisdiction",
        };
        var legalTermCount = legalTerms.Count(term => content.Contains(term, StringComparison.OrdinalIgnoreCase));

        if (legalTermCount >= 3)
        {
            characteristics.Add($"Legal terminology ({legalTermCount} terms)");
            scores[DocumentType.Legal] += 0.6;
        }

        // Formal structure indicators
        if (content.Contains("WHEREAS") || content.Contains("THEREFORE") || content.Contains("IN WITNESS WHEREOF"))
        {
            characteristics.Add("Formal legal structure");
            scores[DocumentType.Legal] += 0.8;
        }
    }

    private static void AnalyzeResearchCharacteristics(
        string content,
        List<string> characteristics,
        Dictionary<DocumentType, double> scores
    )
    {
        var methodologyMatches = MethodologyPattern.Matches(content).Count;
        if (methodologyMatches >= 3)
        {
            characteristics.Add($"Research methodology sections ({methodologyMatches})");
            scores[DocumentType.ResearchPaper] += 0.7;
        }

        // Look for academic formatting
        if (content.Contains("et al.") || content.Contains("ibid.") || content.Contains("op. cit."))
        {
            characteristics.Add("Academic citation style");
            scores[DocumentType.ResearchPaper] += 0.6;
        }

        // Abstract pattern
        if (
            content.Contains("ABSTRACT", StringComparison.OrdinalIgnoreCase)
            || content.Contains("Abstract:", StringComparison.OrdinalIgnoreCase)
        )
        {
            characteristics.Add("Academic abstract");
            scores[DocumentType.ResearchPaper] += 0.8;
        }

        // Bibliography
        if (
            content.Contains("References", StringComparison.OrdinalIgnoreCase)
            || content.Contains("Bibliography", StringComparison.OrdinalIgnoreCase)
        )
        {
            characteristics.Add("Academic references section");
            scores[DocumentType.ResearchPaper] += 0.5;
        }
    }

    private static void AnalyzeTechnicalCharacteristics(
        string content,
        List<string> characteristics,
        Dictionary<DocumentType, double> scores
    )
    {
        var codeBlocks = CodeBlockPattern.Matches(content).Count;
        if (codeBlocks >= 2)
        {
            characteristics.Add($"Code blocks detected ({codeBlocks})");
            scores[DocumentType.Technical] += 0.8;
        }

        // Technical terms
        var techTerms = new[]
        {
            "API",
            "function",
            "method",
            "class",
            "variable",
            "parameter",
            "algorithm",
            "implementation",
        };
        var techTermCount = techTerms.Count(term => content.Contains(term, StringComparison.OrdinalIgnoreCase));

        if (techTermCount >= 4)
        {
            characteristics.Add($"Technical terminology ({techTermCount} terms)");
            scores[DocumentType.Technical] += 0.6;
        }

        // Documentation patterns
        if (
            content.Contains("Usage:", StringComparison.OrdinalIgnoreCase)
            || content.Contains("Example:", StringComparison.OrdinalIgnoreCase)
            || content.Contains("Installation:", StringComparison.OrdinalIgnoreCase)
        )
        {
            characteristics.Add("Technical documentation patterns");
            scores[DocumentType.Technical] += 0.7;
        }
    }

    private static Dictionary<SegmentationStrategy, double> CalculateStrategyScores(
        DocumentTypeDetection typeDetection,
        DocumentComplexityAnalysis complexity,
        string content
    )
    {
        var scores = new Dictionary<SegmentationStrategy, double>();

        // Base scores by document type
        switch (typeDetection.DocumentType)
        {
            case DocumentType.Email:
            case DocumentType.Chat:
                scores[SegmentationStrategy.TopicBased] = 0.8;
                scores[SegmentationStrategy.StructureBased] = 0.2;
                scores[SegmentationStrategy.NarrativeBased] = 0.6;
                scores[SegmentationStrategy.Hybrid] = 0.7;
                break;

            case DocumentType.ResearchPaper:
            case DocumentType.Legal:
                scores[SegmentationStrategy.TopicBased] = 0.5;
                scores[SegmentationStrategy.StructureBased] = 0.9;
                scores[SegmentationStrategy.NarrativeBased] = 0.4;
                scores[SegmentationStrategy.Hybrid] = 0.8;
                break;

            case DocumentType.Technical:
                scores[SegmentationStrategy.TopicBased] = 0.6;
                scores[SegmentationStrategy.StructureBased] = 0.8;
                scores[SegmentationStrategy.NarrativeBased] = 0.3;
                scores[SegmentationStrategy.Hybrid] = 0.9;
                break;
            case DocumentType.Generic:
                break;
            case DocumentType.Article:
                break;
            case DocumentType.Transcript:
                break;
            case DocumentType.Report:
                break;
            case DocumentType.Documentation:
                break;
            default:
                scores[SegmentationStrategy.TopicBased] = 0.7;
                scores[SegmentationStrategy.StructureBased] = 0.6;
                scores[SegmentationStrategy.NarrativeBased] = 0.6;
                scores[SegmentationStrategy.Hybrid] = 0.8;
                break;
        }

        // Adjust scores based on complexity
        if (complexity.StructuralComplexity > 0.7)
        {
            scores[SegmentationStrategy.StructureBased] += 0.2;
            scores[SegmentationStrategy.Hybrid] += 0.1;
        }

        if (complexity.SemanticComplexity > 0.7)
        {
            scores[SegmentationStrategy.TopicBased] += 0.2;
            scores[SegmentationStrategy.Hybrid] += 0.15;
        }

        if (complexity.Features.HasNarrativeFlow)
        {
            scores[SegmentationStrategy.NarrativeBased] += 0.3;
            scores[SegmentationStrategy.Hybrid] += 0.1;
        }

        // Length-based adjustments
        if (content.Length > 10000)
        {
            scores[SegmentationStrategy.Hybrid] += 0.2;
        }

        // Normalize scores
        foreach (var key in scores.Keys.ToList())
        {
            scores[key] = Math.Min(scores[key], 1.0);
        }

        return scores;
    }

    private static DocumentFeatures AnalyzeDocumentFeatures(string content)
    {
        var features = new DocumentFeatures
        {
            // Basic counts
            WordCount = content.Split(separator, StringSplitOptions.RemoveEmptyEntries).Length,
            ParagraphCount = content.Split(separatorArray, StringSplitOptions.RemoveEmptyEntries).Length,

            // Structural features
            HeadingCount = HeadingPattern.Matches(content).Count,
            ListItemCount = ListPattern.Matches(content).Count,
            CodeBlockCount = CodeBlockPattern.Matches(content).Count,
            TableCount = TablePattern.Matches(content).Count,
            LinkCount = LinkPattern.Matches(content).Count
        };

        // Calculate heading depth
        var headingMatches = HeadingPattern.Matches(content);
        features.MaxHeadingDepth = 0;
        foreach (Match match in headingMatches)
        {
            if (match.Value.StartsWith('#'))
            {
                var depth = match.Value.TakeWhile(c => c == '#').Count();
                features.MaxHeadingDepth = Math.Max(features.MaxHeadingDepth, depth);
            }
        }

        // Linguistic features
        var sentences = content.Split(separatorArray0, StringSplitOptions.RemoveEmptyEntries);
        features.AverageSentenceLength =
            sentences.Length > 0
                ? sentences.Average(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length)
                : 0;

        // Vocabulary diversity
        var words = content.ToLower().Split(separatorArray1, StringSplitOptions.RemoveEmptyEntries);
        var uniqueWords = words.Distinct().Count();
        features.VocabularyDiversity = words.Length > 0 ? (double)uniqueWords / words.Length : 0;

        // Pattern recognition
        features.HasConversationalPatterns = ConversationPattern.Matches(content).Count >= 3;
        features.HasFormalStructure = features.HeadingCount >= 3 || features.ListItemCount >= 5;
        features.HasNarrativeFlow = DetectNarrativeFlow(content);

        return features;
    }

    private static bool DetectNarrativeFlow(string content)
    {
        // Look for narrative indicators
        var narrativeIndicators = new[]
        {
            "first",
            "then",
            "next",
            "finally",
            "however",
            "meanwhile",
            "subsequently",
            "earlier",
            "later",
            "before",
            "after",
            "during",
            "while",
        };

        var indicatorCount = narrativeIndicators.Count(indicator =>
            content.Contains(indicator, StringComparison.OrdinalIgnoreCase)
        );

        // Look for temporal references
        var temporalPattern = MyRegex10();
        var temporalMatches = temporalPattern.Matches(content).Count;

        return indicatorCount >= 3 || temporalMatches >= 2;
    }

    private static double CalculateStructuralComplexity(DocumentFeatures features)
    {
        var score = 0.0;

        // Heading complexity
        if (features.HeadingCount > 0)
        {
            score += Math.Min(features.HeadingCount / 10.0, 0.3);
            score += Math.Min(features.MaxHeadingDepth / 6.0, 0.2);
        }

        // List complexity
        if (features.ListItemCount > 0)
        {
            score += Math.Min(features.ListItemCount / 20.0, 0.2);
        }

        // Other structural elements
        score += Math.Min(features.CodeBlockCount / 5.0, 0.1);
        score += Math.Min(features.TableCount / 3.0, 0.1);
        score += Math.Min(features.LinkCount / 10.0, 0.1);

        return Math.Min(score, 1.0);
    }

    private static double CalculateSemanticComplexity(DocumentFeatures features, string content)
    {
        var score = 0.0;

        // Vocabulary diversity
        score += features.VocabularyDiversity * 0.4;

        // Sentence length variety
        score += Math.Min(features.AverageSentenceLength / 25.0, 0.3);

        // Content variety indicators
        if (features.HasConversationalPatterns)
        {
            score += 0.1;
        }

        if (features.HasNarrativeFlow)
        {
            score += 0.2;
        }

        return Math.Min(score, 1.0);
    }

    private static double CalculateLengthComplexity(DocumentFeatures features)
    {
        var score = 0.0;

        // Word count complexity
        score += Math.Min(features.WordCount / 5000.0, 0.5);

        // Paragraph count complexity
        score += Math.Min(features.ParagraphCount / 50.0, 0.3);

        // Structure vs length ratio
        if (features.WordCount > 0)
        {
            var structureRatio = (features.HeadingCount + features.ListItemCount) / (double)features.WordCount * 1000;
            score += Math.Min(structureRatio, 0.2);
        }

        return Math.Min(score, 1.0);
    }

    private static string GenerateStrategyReasoning(
        SegmentationStrategy strategy,
        DocumentTypeDetection typeDetection,
        DocumentComplexityAnalysis complexity
    )
    {
        var reasons = new List<string>
        {
            // Document type reasoning
            $"Document identified as {typeDetection.DocumentType} with {typeDetection.Confidence:P0} confidence"
        };

        // Strategy-specific reasoning
        switch (strategy)
        {
            case SegmentationStrategy.TopicBased:
                reasons.Add("Topic-based segmentation recommended for content with diverse themes");
                if (complexity.SemanticComplexity > 0.6)
                {
                    reasons.Add("High semantic complexity indicates topic diversity");
                }

                break;

            case SegmentationStrategy.StructureBased:
                reasons.Add("Structure-based segmentation recommended due to formal document organization");
                if (complexity.StructuralComplexity > 0.6)
                {
                    reasons.Add($"High structural complexity detected (headings: {complexity.Features.HeadingCount})");
                }

                break;

            case SegmentationStrategy.NarrativeBased:
                reasons.Add("Narrative-based segmentation recommended for sequential content");
                if (complexity.Features.HasNarrativeFlow)
                {
                    reasons.Add("Narrative flow patterns detected");
                }

                break;

            case SegmentationStrategy.Hybrid:
                reasons.Add("Hybrid approach recommended for complex content requiring multiple strategies");
                if (complexity.ComplexityScore > 0.7)
                {
                    reasons.Add("High overall complexity justifies multi-strategy approach");
                }

                break;
            case SegmentationStrategy.Custom:
                break;
            default:
                break;
        }

        // Complexity reasoning
        if (complexity.ComplexityScore > 0.8)
        {
            reasons.Add("Very high document complexity requires sophisticated segmentation");
        }
        else if (complexity.ComplexityScore < 0.3)
        {
            reasons.Add("Low document complexity allows for simpler segmentation approach");
        }

        return string.Join(". ", reasons) + ".";
    }

    private static List<string> GenerateComplexityRecommendations(double complexity, DocumentFeatures features)
    {
        var recommendations = new List<string>();

        if (complexity > 0.8)
        {
            recommendations.Add("Use hybrid segmentation strategy");
            recommendations.Add("Consider breaking into smaller chunks");
            recommendations.Add("Apply multiple quality validation passes");
        }
        else if (complexity > 0.6)
        {
            recommendations.Add("Use appropriate specialized strategy");
            recommendations.Add("Apply standard quality validation");
        }
        else if (complexity > 0.3)
        {
            recommendations.Add("Simple segmentation approach sufficient");
            recommendations.Add("Basic quality validation adequate");
        }
        else
        {
            recommendations.Add("Minimal segmentation needed");
            recommendations.Add("Consider processing as single segment");
        }

        // Feature-specific recommendations
        if (features.HasFormalStructure)
        {
            recommendations.Add("Leverage structural elements for segmentation");
        }

        if (features.HasNarrativeFlow)
        {
            recommendations.Add("Preserve narrative sequence in segments");
        }

        if (features.CodeBlockCount > 0)
        {
            recommendations.Add("Ensure code blocks remain intact within segments");
        }

        return recommendations;
    }

    [GeneratedRegex(@"(From:|To:|Subject:|Date:|Reply-To:)", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex MyRegex();

    [GeneratedRegex(
        @"\d{1,2}:\d{2}(\s?[AP]M)?|\d{4}-\d{2}-\d{2}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        "en-US"
    )]
    private static partial Regex MyRegex1();

    [GeneratedRegex(@"\d+\s+[A-Z][a-z]+\.?\s+\d+|\§\s*\d+|Art\.?\s*\d+", RegexOptions.Compiled)]
    private static partial Regex MyRegex2();

    [GeneratedRegex(@"^#{1,6}\s+.+$|^[A-Z][A-Z\s\d\.\)]{2,50}$", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex MyRegex3();

    [GeneratedRegex(@"```[\s\S]*?```|`[^`]+`", RegexOptions.Compiled)]
    private static partial Regex MyRegex4();

    [GeneratedRegex(@"^\s*[-*+•]\s+.+$|^\s*\d+[\.\)]\s+.+$", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex MyRegex5();

    [GeneratedRegex(@"\|.*?\|", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex MyRegex6();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^\)]+)\)|https?://[^\s]+", RegexOptions.Compiled)]
    private static partial Regex MyRegex7();

    [GeneratedRegex(
        @"\b(methodology|methods?|experiment|analysis|results?|conclusion|abstract|introduction)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        "en-US"
    )]
    private static partial Regex MyRegex8();

    [GeneratedRegex(@"^\s*[A-Za-z][A-Za-z\s]*:\s|^\s*\[[^\]]+\]\s*:", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex MyRegex9();

    [GeneratedRegex(
        @"\b(yesterday|today|tomorrow|last\s+\w+|next\s+\w+|\d+\s+(days?|weeks?|months?|years?)\s+(ago|later))\b",
        RegexOptions.IgnoreCase,
        "en-US"
    )]
    private static partial Regex MyRegex10();

    #endregion
}
