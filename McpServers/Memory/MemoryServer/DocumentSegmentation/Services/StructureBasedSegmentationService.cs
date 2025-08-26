using System.Text.RegularExpressions;
using MemoryServer.DocumentSegmentation.Integration;
using MemoryServer.DocumentSegmentation.Models;
using MemoryServer.DocumentSegmentation.Services;
using MemoryServer.Models;
using Microsoft.Extensions.Logging;

namespace MemoryServer.DocumentSegmentation.Services;

/// <summary>
/// Implementation of structure-based document segmentation.
/// Uses document structure analysis and LLM enhancement for optimal structural boundary detection.
/// </summary>
public class StructureBasedSegmentationService : IStructureBasedSegmentationService
{
    private readonly ILlmProviderIntegrationService _llmService;
    private readonly ISegmentationPromptManager _promptManager;
    private readonly ILogger<StructureBasedSegmentationService> _logger;

    // Structural patterns for detecting document organization
    private static readonly Regex HeadingPattern = new(
        @"^#{1,6}\s+.+$|^[A-Z][A-Z\s\d\.\)]{2,50}$",
        RegexOptions.Multiline | RegexOptions.Compiled
    );
    private static readonly Regex MarkdownHeadingPattern = new(
        @"^(#{1,6})\s+(.+)$",
        RegexOptions.Multiline | RegexOptions.Compiled
    );
    private static readonly Regex SectionBreakPattern = new(
        @"^[\s]*(?:[-=_*]{3,}|[─═━]{3,})[\s]*$",
        RegexOptions.Multiline | RegexOptions.Compiled
    );
    private static readonly Regex ListPattern = new(
        @"^\s*[-*+•]\s+.+$|^\s*\d+[\.\)]\s+.+$",
        RegexOptions.Multiline | RegexOptions.Compiled
    );
    private static readonly Regex CodeBlockPattern = new(
        @"```[\s\S]*?```|`[^`]+`",
        RegexOptions.Compiled
    );
    private static readonly Regex TablePattern = new(
        @"\|.*?\|",
        RegexOptions.Multiline | RegexOptions.Compiled
    );
    private static readonly Regex BlockQuotePattern = new(
        @"^\s*>\s+.+$",
        RegexOptions.Multiline | RegexOptions.Compiled
    );

    // Strong formatting indicators for structural elements
    private static readonly HashSet<string> StructuralKeywords = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "chapter",
        "section",
        "part",
        "introduction",
        "conclusion",
        "summary",
        "overview",
        "methodology",
        "results",
        "discussion",
        "background",
        "literature",
        "references",
        "appendix",
        "abstract",
        "executive",
        "recommendations",
        "findings",
        "analysis",
        "objectives",
        "scope",
        "limitations",
        "future",
        "implementation",
        "evaluation",
    };

    public StructureBasedSegmentationService(
        ILlmProviderIntegrationService llmService,
        ISegmentationPromptManager promptManager,
        ILogger<StructureBasedSegmentationService> logger
    )
    {
        _llmService = llmService ?? throw new ArgumentNullException(nameof(llmService));
        _promptManager = promptManager ?? throw new ArgumentNullException(nameof(promptManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Segments document content based on structural elements like headings, sections, and hierarchical organization.
    /// </summary>
    public async Task<List<DocumentSegment>> SegmentByStructureAsync(
        string content,
        DocumentType documentType = DocumentType.Generic,
        StructureSegmentationOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Starting structure-based segmentation for document type {DocumentType}, content length: {Length}",
            documentType,
            content.Length
        );

        options ??= new StructureSegmentationOptions();

        try
        {
            // Step 1: Detect structural boundaries
            var boundaries = await DetectStructuralBoundariesAsync(
                content,
                documentType,
                cancellationToken
            );
            _logger.LogDebug("Detected {Count} structural boundaries", boundaries.Count);

            // Step 2: Create segments based on boundaries
            var segments = CreateSegmentsFromStructuralBoundaries(content, boundaries, options);
            _logger.LogDebug("Created {Count} segments from structural boundaries", segments.Count);

            // Step 3: Analyze and enhance segments with LLM if needed
            if (options.UseLlmEnhancement)
            {
                segments = await EnhanceSegmentsWithLlmAsync(
                    segments,
                    content,
                    documentType,
                    cancellationToken
                );
                _logger.LogDebug("Enhanced segments with LLM analysis");
            }

            // Step 4: Post-process segments (merge small sections if configured)
            if (options.MergeSmallSections)
            {
                segments = MergeSmallSections(segments, options);
                _logger.LogDebug("Merged small sections");
            }

            // Step 5: Apply final quality validation
            segments = ApplyFinalQualityChecks(segments, options);

            _logger.LogInformation(
                "Structure-based segmentation completed: {Count} segments created",
                segments.Count
            );
            return segments;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during structure-based segmentation");
            throw;
        }
    }

    /// <summary>
    /// Detects structural boundaries within the document content.
    /// </summary>
    public async Task<List<StructureBoundary>> DetectStructuralBoundariesAsync(
        string content,
        DocumentType documentType = DocumentType.Generic,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("Detecting structural boundaries for {DocumentType}", documentType);

        var boundaries = new List<StructureBoundary>();

        try
        {
            // Step 1: Rule-based boundary detection
            var ruleBoundaries = DetectRuleBasedStructuralBoundaries(content, documentType);
            boundaries.AddRange(ruleBoundaries);
            _logger.LogDebug(
                "Found {Count} rule-based structural boundaries",
                ruleBoundaries.Count
            );

            // Step 2: Enhance with LLM analysis for better accuracy
            var llmBoundaries = await DetectLlmEnhancedStructuralBoundariesAsync(
                content,
                documentType,
                cancellationToken
            );
            boundaries.AddRange(llmBoundaries);
            _logger.LogDebug(
                "Found {Count} LLM-enhanced structural boundaries",
                llmBoundaries.Count
            );

            // Step 3: Merge and validate boundaries
            var originalCount = boundaries.Count;
            boundaries = MergeAndValidateStructuralBoundaries(boundaries, content);
            _logger.LogDebug(
                "Final structural boundary count after merge: {Count} (was {Original})",
                boundaries.Count,
                originalCount
            );

            return boundaries.OrderBy(b => b.Position).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error detecting structural boundaries");
            // Return rule-based boundaries as fallback
            return DetectRuleBasedStructuralBoundaries(content, documentType);
        }
    }

    /// <summary>
    /// Analyzes the hierarchical structure of a document.
    /// </summary>
    public Task<HierarchicalStructureAnalysis> AnalyzeHierarchicalStructureAsync(
        string content,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Analyzing hierarchical structure for content length: {Length}",
            content.Length
        );

        try
        {
            // Step 1: Detect all headings and structural elements
            var headingMatches = MarkdownHeadingPattern.Matches(content);
            var outline = new List<StructuralOutlineItem>();
            var maxDepth = 0;
            var totalHeadings = headingMatches.Count;

            // Build document outline
            foreach (Match match in headingMatches)
            {
                var level = match.Groups[1].Value.Length; // Number of # characters
                var title = match.Groups[2].Value.Trim();
                maxDepth = Math.Max(maxDepth, level);

                outline.Add(
                    new StructuralOutlineItem
                    {
                        Title = title,
                        Level = level,
                        Position = match.Index,
                        ElementType = StructuralElementType.Heading,
                    }
                );
            }

            // Step 2: Analyze structural patterns
            var patterns = AnalyzeStructuralPatterns(content);

            // Step 3: Calculate metrics
            var structuralComplexity = CalculateStructuralComplexity(content, outline);
            var hierarchyConsistency = CalculateHierarchyConsistency(outline);
            var organizationQuality = CalculateOrganizationQuality(content, outline);

            return Task.FromResult(
                new HierarchicalStructureAnalysis
                {
                    StructuralComplexity = structuralComplexity,
                    MaxHeadingDepth = maxDepth,
                    TotalHeadings = totalHeadings,
                    MajorSections = outline.Count(item => item.Level <= 2),
                    HasClearHierarchy = hierarchyConsistency > 0.7 && maxDepth > 1,
                    HierarchyConsistency = hierarchyConsistency,
                    DocumentOutline = outline,
                    StructuralPatterns = patterns,
                    OrganizationQuality = organizationQuality,
                }
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error analyzing hierarchical structure");
            throw;
        }
    }

    /// <summary>
    /// Validates structure-based segments for quality and organization.
    /// </summary>
    public async Task<StructureSegmentationValidation> ValidateStructureSegmentsAsync(
        List<DocumentSegment> segments,
        string originalContent,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("Validating {Count} structure-based segments", segments.Count);

        var validation = new StructureSegmentationValidation();
        var segmentResults = new List<StructureSegmentValidationResult>();

        try
        {
            // Step 1: Validate each segment individually
            foreach (var segment in segments)
            {
                var result = await ValidateIndividualStructureSegmentAsync(
                    segment,
                    cancellationToken
                );
                segmentResults.Add(result);
            }

            // Step 2: Calculate overall metrics
            validation.SegmentResults = segmentResults;
            validation.StructuralClarity = segmentResults.Average(r => r.StructuralClarity);
            validation.HierarchyPreservation = segmentResults.Average(r => r.HierarchyConsistency);
            validation.SectionCompleteness = segmentResults.Average(r => r.SectionCompleteness);
            validation.BoundaryAccuracy = CalculateStructuralBoundaryAccuracy(
                segments,
                originalContent
            );

            // Step 3: Calculate overall quality score
            validation.OverallQuality = CalculateOverallStructuralQuality(validation);

            // Step 4: Aggregate issues from individual segment results
            validation.Issues = segmentResults.SelectMany(r => r.Issues).ToList();

            // Step 5: Add overall validation issues
            var overallIssues = IdentifyStructuralValidationIssues(segmentResults);
            validation.Issues.AddRange(overallIssues);

            // Step 6: Generate recommendations
            validation.Recommendations = GenerateStructuralRecommendations(validation);

            _logger.LogInformation(
                "Structure segment validation completed: overall quality {Quality:F2}",
                validation.OverallQuality
            );

            return validation;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating structure segments");
            throw;
        }
    }

    #region Private Helper Methods

    /// <summary>
    /// Detects structural boundaries using rule-based analysis.
    /// </summary>
    private List<StructureBoundary> DetectRuleBasedStructuralBoundaries(
        string content,
        DocumentType documentType
    )
    {
        System.Diagnostics.Debug.WriteLine(
            "=== DEBUGGING: DetectRuleBasedStructuralBoundaries START ==="
        );
        System.Diagnostics.Debug.WriteLine($"Content length: {content.Length}");
        System.Diagnostics.Debug.WriteLine(
            $"Content preview: {(content.Length > 200 ? content.Substring(0, 200) + "..." : content)}"
        );

        var boundaries = new List<StructureBoundary>();

        // 1. Detect markdown headings
        var headingMatches = MarkdownHeadingPattern.Matches(content);
        System.Diagnostics.Debug.WriteLine(
            $"DEBUG: Found {headingMatches.Count} heading matches for content"
        );

        foreach (Match match in headingMatches)
        {
            var level = match.Groups[1].Value.Length;
            var title = match.Groups[2].Value.Trim();

            System.Diagnostics.Debug.WriteLine(
                $"DEBUG: Detected heading - Level: {level}, Title: {title}, Position: {match.Index}"
            );

            boundaries.Add(
                new StructureBoundary
                {
                    Position = match.Index,
                    Confidence = 0.9, // High confidence for markdown headings
                    ElementType = StructuralElementType.Heading,
                    HeadingLevel = level,
                    ElementText = title,
                    HierarchicalLevel = level,
                    IsMajorSection = level <= 2,
                    FormattingIndicators = new List<string>
                    {
                        "markdown_heading",
                        $"level_{level}",
                    },
                }
            );
        }

        System.Diagnostics.Debug.WriteLine(
            $"DEBUG: Rule-based boundaries count after headings: {boundaries.Count}"
        );

        // 2. Detect section breaks
        var sectionBreaks = SectionBreakPattern.Matches(content);
        foreach (Match match in sectionBreaks)
        {
            boundaries.Add(
                new StructureBoundary
                {
                    Position = match.Index,
                    Confidence = 0.8,
                    ElementType = StructuralElementType.SectionBreak,
                    ElementText = match.Value,
                    HierarchicalLevel = 1,
                    IsMajorSection = true,
                    FormattingIndicators = new List<string> { "section_break", "separator" },
                }
            );
        }

        // 3. Detect strong formatting patterns that suggest structure
        var lines = content.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line))
                continue;

            // Check for structural keywords in formatted text
            if (ContainsStructuralKeywords(line) && IsLikelyStructuralHeading(line))
            {
                var position = GetLinePosition(content, i);
                boundaries.Add(
                    new StructureBoundary
                    {
                        Position = position,
                        Confidence = 0.7,
                        ElementType = StructuralElementType.FormattedParagraph,
                        ElementText = line,
                        HierarchicalLevel = 2,
                        IsMajorSection = false,
                        FormattingIndicators = new List<string>
                        {
                            "structural_keyword",
                            "formatted_text",
                        },
                    }
                );
            }
        }

        return boundaries;
    }

    /// <summary>
    /// Enhances boundary detection using LLM analysis.
    /// </summary>
    private Task<List<StructureBoundary>> DetectLlmEnhancedStructuralBoundariesAsync(
        string content,
        DocumentType documentType,
        CancellationToken cancellationToken
    )
    {
        try
        {
            // TEMPORARILY DISABLED FOR DEBUGGING
            _logger.LogDebug("DEBUG: LLM enhancement temporarily disabled for debugging");
            return Task.FromResult(new List<StructureBoundary>());

            // var prompt = await _promptManager.GetPromptAsync(SegmentationStrategy.StructureBased, "en", cancellationToken);

            // // Format prompt with content for structural boundary detection
            // var formattedPrompt = FormatStructuralBoundaryPrompt(prompt.UserPrompt, content, documentType);

            // // Use LLM to analyze structural boundaries
            // var response = await CallLlmForStructuralAnalysisAsync(formattedPrompt, cancellationToken);

            // return ParseStructuralBoundariesFromLlmResponse(response);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "LLM-enhanced structural boundary detection failed, falling back to rule-based"
            );
            return Task.FromResult(new List<StructureBoundary>());
        }
    }

    /// <summary>
    /// Creates document segments from detected structural boundaries.
    /// </summary>
    private List<DocumentSegment> CreateSegmentsFromStructuralBoundaries(
        string content,
        List<StructureBoundary> boundaries,
        StructureSegmentationOptions options
    )
    {
        System.Diagnostics.Debug.WriteLine(
            "=== DEBUGGING: CreateSegmentsFromStructuralBoundaries START ==="
        );
        System.Diagnostics.Debug.WriteLine($"Content length: {content.Length}");
        System.Diagnostics.Debug.WriteLine($"Boundaries count: {boundaries.Count}");

        var segments = new List<DocumentSegment>();
        var sortedBoundaries = boundaries
            .Where(b => b.Confidence >= options.MinStructuralBoundaryConfidence)
            .OrderBy(b => b.Position)
            .ToList();

        System.Diagnostics.Debug.WriteLine($"Filtered boundaries count: {sortedBoundaries.Count}");
        foreach (var boundary in sortedBoundaries)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Boundary: Position={boundary.Position}, Level={boundary.HeadingLevel}, Text='{boundary.ElementText}'"
            );
        }

        if (sortedBoundaries.Count == 0)
        {
            // No structural boundaries found, return the entire content as one segment
            if (content.Trim().Length >= options.MinSegmentSize)
            {
                segments.Add(CreateStructuralDocumentSegment(content.Trim(), 0, 0, null));
            }
            return segments;
        }

        int segmentIndex = 0;

        // Create segments starting from each boundary
        for (int i = 0; i < sortedBoundaries.Count; i++)
        {
            var currentBoundary = sortedBoundaries[i];
            var startPosition = currentBoundary.Position;

            // Determine end position (start of next boundary or end of content)
            var endPosition =
                (i + 1 < sortedBoundaries.Count)
                    ? sortedBoundaries[i + 1].Position
                    : content.Length;

            System.Diagnostics.Debug.WriteLine(
                $"Creating segment {segmentIndex}: start={startPosition}, end={endPosition}"
            );

            var segmentContent = content
                .Substring(startPosition, endPosition - startPosition)
                .Trim();

            if (segmentContent.Length >= options.MinSegmentSize)
            {
                var segment = CreateStructuralDocumentSegment(
                    segmentContent,
                    segmentIndex++,
                    startPosition,
                    currentBoundary
                );
                segments.Add(segment);
                System.Diagnostics.Debug.WriteLine(
                    $"Created segment: '{segmentContent.Substring(0, Math.Min(50, segmentContent.Length))}...'"
                );
            }
            else
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Skipped small segment (length {segmentContent.Length})"
                );
            }
        }

        System.Diagnostics.Debug.WriteLine($"Final segment count: {segments.Count}");
        System.Diagnostics.Debug.WriteLine(
            "=== DEBUGGING: CreateSegmentsFromStructuralBoundaries END ==="
        );
        return segments;
    }

    /// <summary>
    /// Creates a DocumentSegment from content and structural metadata.
    /// </summary>
    private DocumentSegment CreateStructuralDocumentSegment(
        string content,
        int index,
        int position,
        StructureBoundary? boundary
    )
    {
        var segment = new DocumentSegment
        {
            Id = Guid.NewGuid().ToString(),
            Content = content,
            SequenceNumber = index,
            Title = boundary?.ElementText ?? $"Section {index + 1}",
            Metadata = new Dictionary<string, object>
            {
                ["segmentation_strategy"] = SegmentationStrategy.StructureBased.ToString(),
                ["segment_index"] = index,
                ["structure_based"] = true,
                ["start_position"] = position,
                ["end_position"] = position + content.Length,
            },
        };

        if (boundary != null)
        {
            segment.Metadata["structural_element_type"] = boundary.ElementType.ToString();
            if (boundary.HeadingLevel.HasValue)
                segment.Metadata["heading_level"] = boundary.HeadingLevel.Value;
            segment.Metadata["hierarchical_level"] = boundary.HierarchicalLevel;
            segment.Metadata["is_major_section"] = boundary.IsMajorSection;
            segment.Metadata["confidence"] = boundary.Confidence;
            if (boundary.ParentSectionId != null)
                segment.Metadata["parent_section_id"] = boundary.ParentSectionId;
        }

        return segment;
    }

    /// <summary>
    /// Enhances segments with LLM analysis for better structural understanding.
    /// </summary>
    private async Task<List<DocumentSegment>> EnhanceSegmentsWithLlmAsync(
        List<DocumentSegment> segments,
        string originalContent,
        DocumentType documentType,
        CancellationToken cancellationToken
    )
    {
        foreach (var segment in segments)
        {
            try
            {
                var structuralAnalysis = await AnalyzeStructuralClarityAsync(
                    segment.Content,
                    cancellationToken
                );

                // Update segment metadata with structural information
                segment.Metadata["structural_clarity"] = structuralAnalysis.StructuralClarity;
                segment.Metadata["organization_quality"] = structuralAnalysis.OrganizationQuality;
                segment.Metadata["section_type"] = structuralAnalysis.SectionType;
                segment.Metadata["structural_keywords"] = structuralAnalysis.StructuralKeywords;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to enhance segment {SegmentId} with LLM",
                    segment.Id
                );
            }
        }

        return segments;
    }

    /// <summary>
    /// Analyzes structural clarity of a segment using LLM.
    /// </summary>
    private async Task<StructuralClarityAnalysis> AnalyzeStructuralClarityAsync(
        string content,
        CancellationToken cancellationToken
    )
    {
        // LLM-enhanced structural analysis would be implemented here
        await Task.Delay(1, cancellationToken); // Placeholder

        // For testing purposes, analyze structural indicators
        var hasHeadings = HeadingPattern.IsMatch(content);
        var hasList = ListPattern.IsMatch(content);
        var hasStructuralKeywords = ContainsStructuralKeywords(content);

        double clarityScore = 0.6; // Base score
        if (hasHeadings)
            clarityScore += 0.2;
        if (hasList)
            clarityScore += 0.1;
        if (hasStructuralKeywords)
            clarityScore += 0.1;

        return new StructuralClarityAnalysis
        {
            StructuralClarity = Math.Min(clarityScore, 1.0),
            OrganizationQuality = clarityScore * 0.9,
            SectionType = DetermineSectionType(content),
            StructuralKeywords = ExtractStructuralKeywords(content),
        };
    }

    #region Helper Methods

    private bool ContainsStructuralKeywords(string text)
    {
        return StructuralKeywords.Any(keyword =>
            text.Contains(keyword, StringComparison.OrdinalIgnoreCase)
        );
    }

    private bool IsLikelyStructuralHeading(string line)
    {
        // Check for formatting patterns that suggest headings
        return line.Length < 100
            && // Headings are usually short
            (
                line.All(char.IsUpper)
                || // All caps
                line.EndsWith(':')
                || // Ends with colon
                Regex.IsMatch(line, @"^\d+\.?\s")
            ); // Starts with number
    }

    private int GetLinePosition(string content, int lineIndex)
    {
        var lines = content.Split('\n');
        int position = 0;
        for (int i = 0; i < lineIndex && i < lines.Length; i++)
        {
            position += lines[i].Length + 1; // +1 for newline
        }
        return position;
    }

    private List<StructureBoundary> MergeAndValidateStructuralBoundaries(
        List<StructureBoundary> boundaries,
        string content
    )
    {
        System.Diagnostics.Debug.WriteLine(
            "=== DEBUGGING: MergeAndValidateStructuralBoundaries START ==="
        );
        System.Diagnostics.Debug.WriteLine($"Input boundaries count: {boundaries.Count}");

        foreach (var boundary in boundaries)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Input boundary: Position={boundary.Position}, Type={boundary.ElementType}, HeadingLevel={boundary.HeadingLevel}, Confidence={boundary.Confidence}, Text='{boundary.ElementText}'"
            );
        }

        // Merge nearby boundaries and validate
        var merged = new List<StructureBoundary>();
        var sorted = boundaries.OrderBy(b => b.Position).ToList();

        foreach (var boundary in sorted)
        {
            var nearby = merged.LastOrDefault();

            System.Diagnostics.Debug.WriteLine(
                $"Processing boundary at position {boundary.Position}, nearby boundary at position {nearby?.Position ?? -1}"
            );

            if (nearby != null)
            {
                var distance = Math.Abs(boundary.Position - nearby.Position);
                System.Diagnostics.Debug.WriteLine(
                    $"Distance between boundaries: {distance} characters"
                );

                if (distance < 50)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"CLOSE BOUNDARIES: Current={boundary.Position}/{boundary.HeadingLevel}/{boundary.ElementText}, Nearby={nearby.Position}/{nearby.HeadingLevel}/{nearby.ElementText}"
                    );

                    // For headings, only merge if they are truly duplicates (same level and type)
                    if (
                        boundary.ElementType == StructuralElementType.Heading
                        && nearby.ElementType == StructuralElementType.Heading
                    )
                    {
                        // Don't merge headings with different levels - they are legitimate hierarchical structures
                        if (boundary.HeadingLevel != nearby.HeadingLevel)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"DEBUG: Not merging headings with different levels: Level {nearby.HeadingLevel} and Level {boundary.HeadingLevel}"
                            );
                            merged.Add(boundary);
                            continue;
                        }

                        // Only merge if they are truly duplicates (same level, same text)
                        if (
                            boundary.HeadingLevel == nearby.HeadingLevel
                            && boundary.ElementText == nearby.ElementText
                        )
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"DEBUG: Merging duplicate headings: {boundary.ElementText}"
                            );
                            // Keep the one with higher confidence
                            if (boundary.Confidence > nearby.Confidence)
                            {
                                merged[merged.Count - 1] = boundary;
                            }
                        }
                        else
                        {
                            // Different text or different handling, keep both
                            System.Diagnostics.Debug.WriteLine(
                                $"DEBUG: Keeping both headings: '{nearby.ElementText}' and '{boundary.ElementText}'"
                            );
                            merged.Add(boundary);
                        }
                    }
                    else
                    {
                        // For non-heading elements, use original merge logic
                        System.Diagnostics.Debug.WriteLine(
                            "DEBUG: Non-heading merge logic applied"
                        );
                        if (boundary.Confidence > nearby.Confidence)
                        {
                            merged[merged.Count - 1] = boundary;
                        }
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"NOT MERGING: Distance {distance} >= 50, adding boundary"
                    );
                    merged.Add(boundary);
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("First boundary - adding to merged list");
                merged.Add(boundary);
            }
        }

        System.Diagnostics.Debug.WriteLine($"Final merged boundaries count: {merged.Count}");
        foreach (var boundary in merged)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Final boundary: Position={boundary.Position}, Type={boundary.ElementType}, HeadingLevel={boundary.HeadingLevel}, Text='{boundary.ElementText}'"
            );
        }

        System.Diagnostics.Debug.WriteLine(
            "=== DEBUGGING: MergeAndValidateStructuralBoundaries END ==="
        );
        return merged;
    }

    private List<DocumentSegment> MergeSmallSections(
        List<DocumentSegment> segments,
        StructureSegmentationOptions options
    )
    {
        _logger.LogDebug("=== MERGE SMALL SECTIONS DEBUG ===");
        _logger.LogDebug("Input segments count: {Count}", segments.Count);
        _logger.LogDebug("MinSectionSizeForMerging: {MinSize}", options.MinSectionSizeForMerging);

        for (int i = 0; i < segments.Count; i++)
        {
            _logger.LogDebug(
                "Segment {Index}: Length={Length}, Title='{Title}'",
                i,
                segments[i].Content.Length,
                segments[i].Title
            );
        }

        var merged = new List<DocumentSegment>();
        DocumentSegment? currentSegment = null;

        foreach (var segment in segments)
        {
            if (currentSegment == null)
            {
                _logger.LogDebug(
                    "Setting first segment: {Title} (Length: {Length})",
                    segment.Title,
                    segment.Content.Length
                );
                currentSegment = segment;
                continue;
            }

            // Merge if current segment is too small
            if (currentSegment.Content.Length < options.MinSectionSizeForMerging)
            {
                _logger.LogDebug(
                    "MERGING: {Title1} (Length: {Length1}) with {Title2} (Length: {Length2}) - too small!",
                    currentSegment.Title,
                    currentSegment.Content.Length,
                    segment.Title,
                    segment.Content.Length
                );
                currentSegment = MergeStructuralSegments(currentSegment, segment);
                _logger.LogDebug(
                    "Merged result: {Title} (Length: {Length})",
                    currentSegment.Title,
                    currentSegment.Content.Length
                );
            }
            else
            {
                _logger.LogDebug(
                    "Adding segment to output: {Title} (Length: {Length})",
                    currentSegment.Title,
                    currentSegment.Content.Length
                );
                merged.Add(currentSegment);
                currentSegment = segment;
            }
        }

        if (currentSegment != null)
        {
            _logger.LogDebug(
                "Adding final segment: {Title} (Length: {Length})",
                currentSegment.Title,
                currentSegment.Content.Length
            );
            merged.Add(currentSegment);
        }

        _logger.LogDebug("Output merged segments count: {Count}", merged.Count);
        return merged;
    }

    private DocumentSegment MergeStructuralSegments(
        DocumentSegment segment1,
        DocumentSegment segment2
    )
    {
        var startPos = (int)segment1.Metadata.GetValueOrDefault("start_position", 0);
        var endPos = (int)segment2.Metadata.GetValueOrDefault("end_position", 0);

        return new DocumentSegment
        {
            Id = Guid.NewGuid().ToString(),
            Content = segment1.Content + "\n\n" + segment2.Content,
            SequenceNumber = segment1.SequenceNumber,
            Title = segment1.Title, // Keep first segment's title
            Metadata = new Dictionary<string, object>(segment1.Metadata)
            {
                ["start_position"] = startPos,
                ["end_position"] = endPos,
                ["merged_from"] = new[] { segment1.Id, segment2.Id },
            },
        };
    }

    private List<DocumentSegment> ApplyFinalQualityChecks(
        List<DocumentSegment> segments,
        StructureSegmentationOptions options
    )
    {
        return segments
            .Where(s =>
                s.Content.Length >= options.MinSegmentSize
                && s.Content.Length <= options.MaxSegmentSize
            )
            .ToList();
    }

    private List<string> AnalyzeStructuralPatterns(string content)
    {
        var patterns = new List<string>();

        if (MarkdownHeadingPattern.IsMatch(content))
            patterns.Add("markdown_headings");

        if (ListPattern.IsMatch(content))
            patterns.Add("structured_lists");

        if (TablePattern.IsMatch(content))
            patterns.Add("tabular_data");

        if (CodeBlockPattern.IsMatch(content))
            patterns.Add("code_blocks");

        if (SectionBreakPattern.IsMatch(content))
            patterns.Add("section_breaks");

        return patterns;
    }

    private double CalculateStructuralComplexity(
        string content,
        List<StructuralOutlineItem> outline
    )
    {
        if (!outline.Any())
            return 0.2;

        var maxDepth = outline.Max(o => o.Level);
        var headingDiversity = outline.GroupBy(o => o.Level).Count();
        var avgSectionLength = outline.Average(o => o.SectionLength);

        // Normalize to 0-1 scale
        var depthScore = Math.Min(maxDepth / 6.0, 1.0);
        var diversityScore = Math.Min(headingDiversity / 4.0, 1.0);
        var lengthScore = avgSectionLength > 500 ? 0.8 : 0.4;

        return (depthScore + diversityScore + lengthScore) / 3.0;
    }

    private double CalculateHierarchyConsistency(List<StructuralOutlineItem> outline)
    {
        if (!outline.Any())
            return 0.0;

        // Check for logical progression of heading levels
        var inconsistencies = 0;
        for (int i = 1; i < outline.Count; i++)
        {
            var levelJump = outline[i].Level - outline[i - 1].Level;
            if (levelJump > 1) // Skipping levels is inconsistent
                inconsistencies++;
        }

        return Math.Max(0.0, 1.0 - (double)inconsistencies / outline.Count);
    }

    private double CalculateOrganizationQuality(string content, List<StructuralOutlineItem> outline)
    {
        if (!outline.Any())
            return 0.3;

        var hasIntroduction = outline.Any(o =>
            o.Title.Contains("introduction", StringComparison.OrdinalIgnoreCase)
            || o.Title.Contains("overview", StringComparison.OrdinalIgnoreCase)
        );

        var hasConclusion = outline.Any(o =>
            o.Title.Contains("conclusion", StringComparison.OrdinalIgnoreCase)
            || o.Title.Contains("summary", StringComparison.OrdinalIgnoreCase)
        );

        var baseScore = 0.5;
        if (hasIntroduction)
            baseScore += 0.2;
        if (hasConclusion)
            baseScore += 0.2;
        if (outline.Count >= 3)
            baseScore += 0.1; // Multiple sections

        return Math.Min(baseScore, 1.0);
    }

    private async Task<StructureSegmentValidationResult> ValidateIndividualStructureSegmentAsync(
        DocumentSegment segment,
        CancellationToken cancellationToken
    )
    {
        var structuralAnalysis = await AnalyzeStructuralClarityAsync(
            segment.Content,
            cancellationToken
        );

        var result = new StructureSegmentValidationResult
        {
            SegmentId = segment.Id,
            StructuralClarity = structuralAnalysis.StructuralClarity,
            SectionCompleteness = CalculateSectionCompleteness(segment),
            HierarchyConsistency = CalculateSegmentHierarchyConsistency(segment),
            OrganizationQuality = structuralAnalysis.OrganizationQuality,
        };

        // Add specific issues for this segment
        if (result.StructuralClarity < 0.6)
        {
            result.Issues.Add(
                new StructureValidationIssue
                {
                    Type = StructureValidationIssueType.PoorOrganization,
                    Severity = Models.ValidationSeverity.Warning,
                    Description = $"Low structural clarity: {result.StructuralClarity:F2}",
                }
            );
        }

        if (result.SectionCompleteness < 0.5)
        {
            result.Issues.Add(
                new StructureValidationIssue
                {
                    Type = StructureValidationIssueType.IncompleteSection,
                    Severity = Models.ValidationSeverity.Warning,
                    Description = $"Incomplete section: {result.SectionCompleteness:F2}",
                }
            );
        }

        return result;
    }

    private double CalculateSectionCompleteness(DocumentSegment segment)
    {
        // Check if segment appears to be a complete structural unit
        var content = segment.Content;

        // Look for indicators of completeness
        var hasHeading = HeadingPattern.IsMatch(content);
        var hasConclusion =
            content.Contains("conclusion", StringComparison.OrdinalIgnoreCase)
            || content.Contains("summary", StringComparison.OrdinalIgnoreCase);
        var reasonableLength = content.Length > 200;
        var endsCompletely = content.TrimEnd().EndsWith('.') || content.TrimEnd().EndsWith('!');

        double score = 0.4; // Base score
        if (hasHeading)
            score += 0.2;
        if (hasConclusion)
            score += 0.2;
        if (reasonableLength)
            score += 0.1;
        if (endsCompletely)
            score += 0.1;

        return Math.Min(score, 1.0);
    }

    private double CalculateSegmentHierarchyConsistency(DocumentSegment segment)
    {
        // Check for consistent heading levels within the segment
        var headingMatches = MarkdownHeadingPattern.Matches(segment.Content);
        if (headingMatches.Count <= 1)
            return 1.0; // Single or no heading is consistent

        var levels = headingMatches.Cast<Match>().Select(m => m.Groups[1].Value.Length).ToList();

        // Check for logical progression
        var inconsistencies = 0;
        for (int i = 1; i < levels.Count; i++)
        {
            var jump = levels[i] - levels[i - 1];
            if (jump > 1)
                inconsistencies++;
        }

        return Math.Max(0.0, 1.0 - (double)inconsistencies / levels.Count);
    }

    private double CalculateStructuralBoundaryAccuracy(
        List<DocumentSegment> segments,
        string originalContent
    )
    {
        // Calculate accuracy of structural boundaries
        return 0.85; // Placeholder - would implement detailed boundary analysis
    }

    private double CalculateOverallStructuralQuality(StructureSegmentationValidation validation)
    {
        return (
                validation.StructuralClarity
                + validation.HierarchyPreservation
                + validation.SectionCompleteness
                + validation.BoundaryAccuracy
            ) / 4;
    }

    private List<StructureValidationIssue> IdentifyStructuralValidationIssues(
        List<StructureSegmentValidationResult> results
    )
    {
        var issues = new List<StructureValidationIssue>();

        foreach (var result in results)
        {
            if (result.StructuralClarity < 0.6)
            {
                issues.Add(
                    new StructureValidationIssue
                    {
                        Type = StructureValidationIssueType.PoorOrganization,
                        Severity = Models.ValidationSeverity.Warning,
                        Description =
                            $"Segment {result.SegmentId} has poor structural organization: {result.StructuralClarity:F2}",
                    }
                );
            }

            if (result.HierarchyConsistency < 0.5)
            {
                issues.Add(
                    new StructureValidationIssue
                    {
                        Type = StructureValidationIssueType.InconsistentHeadingLevels,
                        Severity = Models.ValidationSeverity.Warning,
                        Description =
                            $"Segment {result.SegmentId} has inconsistent hierarchy: {result.HierarchyConsistency:F2}",
                    }
                );
            }
        }

        return issues;
    }

    private List<string> GenerateStructuralRecommendations(
        StructureSegmentationValidation validation
    )
    {
        var recommendations = new List<string>();

        if (validation.StructuralClarity < 0.7)
        {
            recommendations.Add("Consider using clearer heading structures and formatting");
        }

        if (validation.HierarchyPreservation < 0.8)
        {
            recommendations.Add("Review heading levels for consistent hierarchical organization");
        }

        if (validation.SectionCompleteness < 0.7)
        {
            recommendations.Add("Ensure segments represent complete structural units");
        }

        return recommendations;
    }

    private string DetermineSectionType(string content)
    {
        // Analyze content to determine section type
        if (content.Contains("introduction", StringComparison.OrdinalIgnoreCase))
            return "introduction";
        if (content.Contains("conclusion", StringComparison.OrdinalIgnoreCase))
            return "conclusion";
        if (content.Contains("methodology", StringComparison.OrdinalIgnoreCase))
            return "methodology";
        if (content.Contains("results", StringComparison.OrdinalIgnoreCase))
            return "results";

        return "content";
    }

    private List<string> ExtractStructuralKeywords(string content)
    {
        return StructuralKeywords
            .Where(keyword => content.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    // Placeholder methods for LLM integration
    private string FormatStructuralBoundaryPrompt(
        string template,
        string content,
        DocumentType documentType
    )
    {
        return template
            .Replace(
                "{DocumentContent}",
                content.Length > 2000 ? content.Substring(0, 2000) + "..." : content
            )
            .Replace("{DocumentType}", documentType.ToString())
            .Replace("{Language}", "en");
    }

    private async Task<string> CallLlmForStructuralAnalysisAsync(
        string prompt,
        CancellationToken cancellationToken
    )
    {
        // This would call the LLM service for structural analysis
        await Task.Delay(1, cancellationToken); // Placeholder
        return "{}"; // Placeholder response
    }

    private List<StructureBoundary> ParseStructuralBoundariesFromLlmResponse(string response)
    {
        // Parse LLM response to extract structural boundaries
        return new List<StructureBoundary>(); // Placeholder
    }

    #endregion

    #endregion
}

/// <summary>
/// Analysis of structural clarity within a text segment.
/// </summary>
public class StructuralClarityAnalysis
{
    public double StructuralClarity { get; set; }
    public double OrganizationQuality { get; set; }
    public string SectionType { get; set; } = string.Empty;
    public List<string> StructuralKeywords { get; set; } = new();
}
