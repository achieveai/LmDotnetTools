using System.Text.RegularExpressions;
using MemoryServer.DocumentSegmentation.Integration;
using MemoryServer.DocumentSegmentation.Models;

namespace MemoryServer.DocumentSegmentation.Services;

/// <summary>
///     Implementation of narrative-based document segmentation.
///     Analyzes logical flow, temporal sequences, and causal relationships to create coherent narrative segments.
/// </summary>
public partial class NarrativeBasedSegmentationService : INarrativeBasedSegmentationService
{
    // Temporal sequence markers
    private static readonly HashSet<string> TemporalMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "first",
        "second",
        "third",
        "then",
        "next",
        "after",
        "before",
        "during",
        "while",
        "when",
        "since",
        "until",
        "finally",
        "lastly",
        "meanwhile",
        "simultaneously",
        "previously",
        "later",
        "earlier",
        "subsequently",
        "afterwards",
        "beforehand",
        "initially",
        "originally",
        "eventually",
        "ultimately",
        "immediately",
        "instantly",
    };

    // Causal relationship indicators
    private static readonly HashSet<string> CausalMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "because",
        "since",
        "due to",
        "as a result of",
        "caused by",
        "leads to",
        "therefore",
        "thus",
        "consequently",
        "as a result",
        "hence",
        "accordingly",
        "so",
        "if",
        "unless",
        "provided that",
        "in case",
        "given that",
        "assuming",
        "enables",
        "triggers",
        "results in",
        "brings about",
        "produces",
        "generates",
    };

    // Logical progression indicators
    private static readonly HashSet<string> LogicalMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "however",
        "but",
        "although",
        "despite",
        "nevertheless",
        "nonetheless",
        "on the other hand",
        "in contrast",
        "conversely",
        "alternatively",
        "furthermore",
        "moreover",
        "additionally",
        "besides",
        "also",
        "plus",
        "similarly",
        "likewise",
        "in the same way",
        "correspondingly",
        "for example",
        "for instance",
        "such as",
        "specifically",
        "namely",
    };

    // Narrative function indicators
    private static readonly Dictionary<NarrativeFunction, HashSet<string>> FunctionMarkers = new()
    {
        [NarrativeFunction.Setup] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "introduction",
            "context",
            "setting",
            "overview",
            "let me explain",
            "to understand",
            "at the beginning",
            "to start",
        },
        [NarrativeFunction.Background] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "background",
            "history",
            "context",
            "previously",
            "earlier",
            "before",
            "backdrop",
            "foundation",
            "past",
            "prior to",
            "background information",
        },
        [NarrativeFunction.Development] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "development",
            "progress",
            "advancement",
            "evolution",
            "growth",
            "expansion",
            "continuing",
            "proceeding",
            "moving forward",
        },
        [NarrativeFunction.Complication] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "however",
            "but",
            "problem",
            "issue",
            "challenge",
            "difficulty",
            "obstacle",
            "complication",
            "conflict",
            "tension",
            "unfortunately",
            "unexpectedly",
        },
        [NarrativeFunction.Climax] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "suddenly",
            "unexpectedly",
            "critically",
            "crucially",
            "key moment",
            "turning point",
            "breakthrough",
            "revelation",
            "realization",
            "peak",
        },
        [NarrativeFunction.Resolution] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "conclusion",
            "ultimately",
            "in the end",
            "resolution",
            "solution",
            "outcome",
            "result",
            "ending",
            "closure",
            "summary",
            "to summarize",
        },
    };

    private static readonly string[] sourceArray = ["then", "next", "after", "subsequently"];
    private static readonly string[] sourceArray0 = ["meanwhile", "simultaneously", "while"];
    private static readonly string[] sourceArray1 = ["previously", "earlier", "before"];
    private static readonly string[] sourceArray2 = ["because", "since", "due to"];
    private static readonly string[] sourceArray3 = ["if", "unless", "provided that"];
    private static readonly string[] sourceArray4 = ["enables", "allows", "permits"];
    private static readonly string[] sourceArray5 = ["however", "but", "although"];
    private static readonly string[] sourceArray6 = ["furthermore", "moreover", "additionally"];
    private static readonly string[] sourceArray7 = ["for example", "for instance", "such as"];
    private readonly ILlmProviderIntegrationService _llmService;
    private readonly ILogger<NarrativeBasedSegmentationService> _logger;
    private readonly ISegmentationPromptManager _promptManager;

    public NarrativeBasedSegmentationService(
        ILlmProviderIntegrationService llmService,
        ISegmentationPromptManager promptManager,
        ILogger<NarrativeBasedSegmentationService> logger
    )
    {
        _llmService = llmService ?? throw new ArgumentNullException(nameof(llmService));
        _promptManager = promptManager ?? throw new ArgumentNullException(nameof(promptManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    ///     Segments document content based on narrative flow and logical progression.
    /// </summary>
    public async Task<List<DocumentSegment>> SegmentByNarrativeAsync(
        string content,
        DocumentType documentType = DocumentType.Generic,
        NarrativeSegmentationOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(content);
        _logger.LogDebug(
            "Starting narrative-based segmentation for document type {DocumentType}, content length: {Length}",
            documentType,
            content.Length
        );

        options ??= new NarrativeSegmentationOptions();

        try
        {
            // Step 1: Detect narrative boundaries
            var boundaries = await DetectNarrativeTransitionsAsync(content, documentType, cancellationToken);
            _logger.LogDebug("Detected {Count} narrative boundaries", boundaries.Count);

            // Step 2: Create initial segments from boundaries
            var segments = CreateSegmentsFromBoundaries(content, boundaries, options);
            _logger.LogDebug("Created {Count} initial segments", segments.Count);

            // Step 3: Enhance with LLM analysis if enabled
            if (options.UseLlmEnhancement)
            {
                segments = await EnhanceSegmentsWithLlmAsync(segments, content, documentType, cancellationToken);
                _logger.LogDebug("Enhanced segments with LLM analysis");
            }

            // Step 4: Post-process segments (merge weak transitions if configured)
            if (options.MergeWeakTransitions)
            {
                segments = await MergeWeakNarrativeTransitionsAsync(segments, options, cancellationToken);
                _logger.LogDebug("Merged weak narrative transitions");
            }

            // Step 5: Apply final quality checks
            segments = ApplyFinalQualityChecks(segments, options);
            _logger.LogInformation(
                "Completed narrative-based segmentation with {Count} final segments",
                segments.Count
            );

            return segments;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during narrative-based segmentation");
            throw;
        }
    }

    /// <summary>
    ///     Detects narrative transitions and flow boundaries in the document.
    /// </summary>
    public Task<List<NarrativeBoundary>> DetectNarrativeTransitionsAsync(
        string content,
        DocumentType documentType = DocumentType.Generic,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(content);
        _logger.LogDebug("Detecting narrative transitions in content of length {Length}", content.Length);

        var boundaries = new List<NarrativeBoundary>();

        // Detect different types of narrative boundaries
        boundaries.AddRange(DetectTemporalBoundaries(content));
        boundaries.AddRange(DetectCausalBoundaries(content));
        boundaries.AddRange(DetectLogicalBoundaries(content));
        boundaries.AddRange(DetectFunctionalBoundaries(content));

        // Sort by position and remove duplicates
        boundaries =
        [
            .. boundaries
                .OrderBy(b => b.Position)
                .GroupBy(b => b.Position)
                .Select(g => g.OrderByDescending(b => b.Confidence).First()),
        ];

        _logger.LogDebug("Detected {Count} unique narrative boundaries", boundaries.Count);
        return Task.FromResult(boundaries);
    }

    /// <summary>
    ///     Analyzes the logical flow and narrative structure of the document.
    /// </summary>
    public async Task<NarrativeFlowAnalysis> AnalyzeLogicalFlowAsync(
        string content,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(content);
        _logger.LogDebug("Analyzing logical flow for content of length {Length}", content.Length);

        var analysis = new NarrativeFlowAnalysis
        {
            // Analyze overall narrative type
            OverallNarrativeType = DetermineNarrativeType(content),

            // Analyze temporal progression
            TemporalProgression = DetermineTemporalProgression(content),

            // Detect causal chains
            CausalChain = await DetectCausalRelationshipsAsync(content, cancellationToken),

            // Identify narrative elements
            NarrativeElements = await IdentifyNarrativeArcElementsAsync(content, cancellationToken: cancellationToken),

            // Calculate quality scores
            FlowCoherence = CalculateFlowCoherence(content),
        };
        analysis.LogicalConsistency = CalculateLogicalConsistency(content, analysis.CausalChain);
        analysis.TemporalConsistency = CalculateTemporalConsistency(content);
        analysis.NarrativeCompleteness = CalculateNarrativeCompleteness(content, analysis.NarrativeElements);

        // Identify narrative markers
        analysis.NarrativeMarkers = ExtractNarrativeMarkers(content);

        _logger.LogDebug(
            "Completed logical flow analysis with overall type: {NarrativeType}",
            analysis.OverallNarrativeType
        );
        return analysis;
    }

    /// <summary>
    ///     Validates narrative-based segments for quality and coherence.
    /// </summary>
    public async Task<NarrativeSegmentationValidation> ValidateNarrativeSegmentsAsync(
        List<DocumentSegment> segments,
        string originalContent,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(originalContent);
        _logger.LogDebug("Validating {Count} narrative segments", segments.Count);

        var validation = new NarrativeSegmentationValidation();

        // Validate each segment individually
        foreach (var segment in segments)
        {
            var segmentResult = await ValidateIndividualSegmentAsync(segment, cancellationToken);
            validation.SegmentResults.Add(segmentResult);
        }

        // Calculate overall metrics
        validation.FlowCoherence = CalculateOverallFlowCoherence(segments, originalContent);
        validation.LogicalConsistency = CalculateOverallLogicalConsistency(segments);
        validation.TemporalConsistency = CalculateOverallTemporalConsistency(segments);
        validation.NarrativeCompleteness = CalculateOverallNarrativeCompleteness(segments, originalContent);
        validation.TransitionQuality = CalculateOverallTransitionQuality(segments);

        // Calculate overall quality as weighted average
        validation.OverallQuality =
            (validation.FlowCoherence * 0.3)
            + (validation.LogicalConsistency * 0.25)
            + (validation.TemporalConsistency * 0.2)
            + (validation.NarrativeCompleteness * 0.15)
            + (validation.TransitionQuality * 0.1);

        // Identify issues and generate recommendations
        validation.Issues = IdentifyValidationIssues(validation);
        validation.Recommendations = GenerateValidationRecommendations(validation);

        _logger.LogDebug(
            "Completed narrative validation with overall quality: {Quality:F2}",
            validation.OverallQuality
        );
        return validation;
    }

    /// <summary>
    ///     Identifies temporal sequences and chronological patterns in the content.
    /// </summary>
    public Task<List<TemporalSequence>> IdentifyTemporalSequencesAsync(
        string content,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(content);
        _logger.LogDebug("Identifying temporal sequences in content");

        var sequences = new List<TemporalSequence>();
        var sentences = SplitIntoSentences(content);

        for (var i = 0; i < sentences.Length; i++)
        {
            var sentence = sentences[i];
            var position = GetSentencePosition(content, sentences, i);

            // Check for temporal markers
            var temporalMarkers = TemporalMarkers
                .Where(marker => sentence.Contains(marker, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (temporalMarkers.Count != 0)
            {
                var sequence = new TemporalSequence
                {
                    StartPosition = position,
                    EndPosition = position + sentence.Length,
                    TemporalMarkers = temporalMarkers,
                    SequentialOrder = i,
                    Confidence = CalculateTemporalConfidence(sentence, temporalMarkers),
                    Type = DetermineTemporalType(sentence, temporalMarkers),
                };

                sequences.Add(sequence);
            }
        }

        _logger.LogDebug("Identified {Count} temporal sequences", sequences.Count);
        return Task.FromResult(sequences);
    }

    /// <summary>
    ///     Detects causal relationships between different parts of the document.
    /// </summary>
    public Task<List<CausalRelation>> DetectCausalRelationshipsAsync(
        string content,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(content);
        _logger.LogDebug("Detecting causal relationships in content");

        var relations = new List<CausalRelation>();
        var sentences = SplitIntoSentences(content);

        for (var i = 0; i < sentences.Length; i++)
        {
            var sentence = sentences[i];
            var position = GetSentencePosition(content, sentences, i);

            // Check for causal markers
            foreach (var marker in CausalMarkers)
            {
                if (sentence.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    var markerIndex = sentence.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    var beforeCause = markerIndex;
                    var afterEffect = markerIndex + marker.Length;

                    var relation = new CausalRelation
                    {
                        CausePosition = position + beforeCause,
                        EffectPosition = position + afterEffect,
                        CausalIndicator = marker,
                        Strength = CalculateCausalStrength(sentence, marker),
                        Type = DetermineCausalType(marker),
                    };

                    relations.Add(relation);
                }
            }
        }

        _logger.LogDebug("Detected {Count} causal relationships", relations.Count);
        return Task.FromResult(relations);
    }

    /// <summary>
    ///     Identifies narrative arc elements (setup, development, climax, resolution) in the content.
    /// </summary>
    public Task<Dictionary<NarrativeFunction, List<int>>> IdentifyNarrativeArcElementsAsync(
        string content,
        DocumentType documentType = DocumentType.Generic,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(content);
        _logger.LogDebug("Identifying narrative arc elements for document type {DocumentType}", documentType);

        var elements = new Dictionary<NarrativeFunction, List<int>>();

        // Initialize all functions with empty lists
        foreach (var function in Enum.GetValues<NarrativeFunction>())
        {
            elements[function] = [];
        }

        var sentences = SplitIntoSentences(content);

        for (var i = 0; i < sentences.Length; i++)
        {
            var sentence = sentences[i];
            var position = GetSentencePosition(content, sentences, i);

            // Check each narrative function
            foreach (var functionKvp in FunctionMarkers)
            {
                var function = functionKvp.Key;
                var markers = functionKvp.Value;

                foreach (var marker in markers)
                {
                    if (sentence.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    {
                        elements[function].Add(position);
                        break; // Only add once per sentence
                    }
                }
            }
        }

        // Remove empty entries
        var nonEmptyElements = elements
            .Where(kvp => kvp.Value.Count != 0)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        _logger.LogDebug("Identified narrative elements for {Count} functions", nonEmptyElements.Count);
        return Task.FromResult(nonEmptyElements);
    }

    #region Private Helper Methods

    private static List<NarrativeBoundary> DetectTemporalBoundaries(string content)
    {
        var boundaries = new List<NarrativeBoundary>();
        var sentences = SplitIntoSentences(content);

        for (var i = 0; i < sentences.Length; i++)
        {
            var sentence = sentences[i];
            var position = GetSentencePosition(content, sentences, i);

            var foundMarkers = TemporalMarkers
                .Where(marker => sentence.Contains(marker, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (foundMarkers.Count != 0)
            {
                var boundary = new NarrativeBoundary
                {
                    Position = position,
                    TransitionType = NarrativeTransitionType.Temporal,
                    TriggerPhrases = foundMarkers,
                    Confidence = CalculateTemporalConfidence(sentence, foundMarkers),
                    LogicalRelationship = LogicalRelationship.Sequential,
                    TemporalRelationship = DetermineTemporalType(sentence, foundMarkers),
                };

                boundaries.Add(boundary);
            }
        }

        return boundaries;
    }

    private static List<NarrativeBoundary> DetectCausalBoundaries(string content)
    {
        var boundaries = new List<NarrativeBoundary>();
        var sentences = SplitIntoSentences(content);

        for (var i = 0; i < sentences.Length; i++)
        {
            var sentence = sentences[i];
            var position = GetSentencePosition(content, sentences, i);

            var foundMarkers = CausalMarkers
                .Where(marker => sentence.Contains(marker, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (foundMarkers.Count != 0)
            {
                var boundary = new NarrativeBoundary
                {
                    Position = position,
                    TransitionType = NarrativeTransitionType.Causal,
                    TriggerPhrases = foundMarkers,
                    Confidence = CalculateCausalBoundaryConfidence(sentence, foundMarkers),
                    LogicalRelationship = LogicalRelationship.Causal,
                };

                boundaries.Add(boundary);
            }
        }

        return boundaries;
    }

    private static List<NarrativeBoundary> DetectLogicalBoundaries(string content)
    {
        var boundaries = new List<NarrativeBoundary>();
        var sentences = SplitIntoSentences(content);

        for (var i = 0; i < sentences.Length; i++)
        {
            var sentence = sentences[i];
            var position = GetSentencePosition(content, sentences, i);

            var foundMarkers = LogicalMarkers
                .Where(marker => sentence.Contains(marker, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (foundMarkers.Count != 0)
            {
                var boundary = new NarrativeBoundary
                {
                    Position = position,
                    TransitionType = NarrativeTransitionType.Logical,
                    TriggerPhrases = foundMarkers,
                    Confidence = CalculateLogicalBoundaryConfidence(sentence, foundMarkers),
                    LogicalRelationship = DetermineLogicalRelationship(foundMarkers),
                };

                boundaries.Add(boundary);
            }
        }

        return boundaries;
    }

    private static List<NarrativeBoundary> DetectFunctionalBoundaries(string content)
    {
        var boundaries = new List<NarrativeBoundary>();
        var sentences = SplitIntoSentences(content);

        for (var i = 0; i < sentences.Length; i++)
        {
            var sentence = sentences[i];
            var position = GetSentencePosition(content, sentences, i);

            foreach (var functionKvp in FunctionMarkers)
            {
                var function = functionKvp.Key;
                var markers = functionKvp.Value;

                var foundMarkers = markers
                    .Where(marker => sentence.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (foundMarkers.Count != 0)
                {
                    var boundary = new NarrativeBoundary
                    {
                        Position = position,
                        TransitionType = NarrativeTransitionType.Structural,
                        Function = function,
                        TriggerPhrases = foundMarkers,
                        Confidence = CalculateFunctionalBoundaryConfidence(sentence, foundMarkers, function),
                        LogicalRelationship = GetLogicalRelationshipForFunction(function),
                    };

                    boundaries.Add(boundary);
                    break; // Only one function per boundary
                }
            }
        }

        return boundaries;
    }

    private static List<DocumentSegment> CreateSegmentsFromBoundaries(
        string content,
        List<NarrativeBoundary> boundaries,
        NarrativeSegmentationOptions options
    )
    {
        var segments = new List<DocumentSegment>();

        if (boundaries.Count == 0)
        {
            // No boundaries found, create a single segment
            var singleSegment = new DocumentSegment
            {
                Id = Guid.NewGuid().ToString(),
                Content = content.Trim(),
                SequenceNumber = 0,
                Quality = new SegmentQuality(),
                Metadata = new Dictionary<string, object>
                {
                    ["segmentation_strategy"] = SegmentationStrategy.NarrativeBased.ToString(),
                    ["narrative_based"] = true,
                    ["boundary_count"] = 0,
                    ["confidence"] = 0.5,
                },
            };
            return [singleSegment];
        }

        // Create segments from boundaries
        for (var i = 0; i < boundaries.Count; i++)
        {
            var startPos = i == 0 ? 0 : boundaries[i - 1].Position;
            var endPos = boundaries[i].Position;

            if (endPos > startPos)
            {
                var segmentContent = content[startPos..endPos].Trim();

                if (segmentContent.Length >= options.MinSegmentSize)
                {
                    var segment = new DocumentSegment
                    {
                        Id = Guid.NewGuid().ToString(),
                        Content = segmentContent,
                        SequenceNumber = segments.Count,
                        Quality = new SegmentQuality(),
                        Metadata = new Dictionary<string, object>
                        {
                            ["segmentation_strategy"] = SegmentationStrategy.NarrativeBased.ToString(),
                            ["narrative_based"] = true,
                            ["start_position"] = startPos,
                            ["end_position"] = endPos,
                            ["boundary_confidence"] = boundaries[i].Confidence,
                            ["transition_type"] = boundaries[i].TransitionType.ToString(),
                            ["narrative_function"] = boundaries[i].Function.ToString(),
                            ["logical_relationship"] = boundaries[i].LogicalRelationship.ToString(),
                        },
                    };

                    if (boundaries[i].TemporalRelationship.HasValue)
                    {
                        segment.Metadata["temporal_relationship"] = boundaries[i]
                            .TemporalRelationship!.Value.ToString();
                    }

                    segments.Add(segment);
                }
            }
        }

        // Add final segment if needed
        if (boundaries.Count != 0 && boundaries.Last().Position < content.Length)
        {
            var finalContent = content[boundaries.Last().Position..].Trim();
            if (finalContent.Length >= options.MinSegmentSize)
            {
                var finalSegment = new DocumentSegment
                {
                    Id = Guid.NewGuid().ToString(),
                    Content = finalContent,
                    SequenceNumber = segments.Count,
                    Quality = new SegmentQuality(),
                    Metadata = new Dictionary<string, object>
                    {
                        ["segmentation_strategy"] = SegmentationStrategy.NarrativeBased.ToString(),
                        ["narrative_based"] = true,
                        ["start_position"] = boundaries.Last().Position,
                        ["end_position"] = content.Length,
                        ["is_final_segment"] = true,
                    },
                };

                segments.Add(finalSegment);
            }
        }

        return segments;
    }

    private Task<List<DocumentSegment>> EnhanceSegmentsWithLlmAsync(
        List<DocumentSegment> segments,
        string content,
        DocumentType documentType,
        CancellationToken cancellationToken
    )
    {
        // Implementation for LLM enhancement would go here
        // For now, return segments as-is
        _logger.LogDebug("LLM enhancement not yet implemented for narrative segmentation");
        return Task.FromResult(segments);
    }

    private static Task<List<DocumentSegment>> MergeWeakNarrativeTransitionsAsync(
        List<DocumentSegment> segments,
        NarrativeSegmentationOptions options,
        CancellationToken cancellationToken
    )
    {
        var mergedSegments = new List<DocumentSegment>();
        DocumentSegment? currentSegment = null;

        foreach (var segment in segments)
        {
            var confidence = segment.Metadata.TryGetValue("boundary_confidence", out var confidenceValue)
                ? Convert.ToDouble(confidenceValue)
                : 1.0;

            if (confidence < options.MinNarrativeConfidence && currentSegment != null)
            {
                // Merge with current segment
                currentSegment.Content += " " + segment.Content;
                currentSegment.Metadata["merged_segments"] =
                    (currentSegment.Metadata.TryGetValue("merged_segments", out var mergedCount) ? (int)mergedCount : 1)
                    + 1;
            }
            else
            {
                if (currentSegment != null)
                {
                    mergedSegments.Add(currentSegment);
                }

                currentSegment = segment;
            }
        }

        if (currentSegment != null)
        {
            mergedSegments.Add(currentSegment);
        }

        // Update sequence numbers
        for (var i = 0; i < mergedSegments.Count; i++)
        {
            mergedSegments[i].SequenceNumber = i;
        }

        return Task.FromResult(mergedSegments);
    }

    private static List<DocumentSegment> ApplyFinalQualityChecks(
        List<DocumentSegment> segments,
        NarrativeSegmentationOptions options
    )
    {
        var qualitySegments = new List<DocumentSegment>();

        foreach (var segment in segments)
        {
            // Apply minimum size check
            if (segment.Content.Length >= options.MinSegmentSize)
            {
                // Calculate basic quality metrics
                segment.Quality = new SegmentQuality
                {
                    CoherenceScore = CalculateSegmentCoherence(segment.Content),
                    IndependenceScore = CalculateSegmentIndependence(segment.Content),
                    TopicConsistencyScore = CalculateNarrativeConsistency(segment.Content),
                };

                segment.Quality.PassesQualityThreshold = segment.Quality.CoherenceScore >= options.MinFlowCoherence;

                qualitySegments.Add(segment);
            }
        }

        return qualitySegments;
    }

    // Utility methods for analysis
    private static string[] SplitIntoSentences(string content)
    {
        return [.. MyRegex().Split(content).Where(s => !string.IsNullOrWhiteSpace(s))];
    }

    private static int GetSentencePosition(string content, string[] sentences, int index)
    {
        var position = 0;
        for (var i = 0; i < index; i++)
        {
            position = content.IndexOf(sentences[i], position) + sentences[i].Length;
        }

        return content.IndexOf(sentences[index], position);
    }

    private static double CalculateTemporalConfidence(string sentence, List<string> markers)
    {
        var baseConfidence = 0.75; // Increased from 0.6 to give temporal boundaries higher priority
        var markerBonus = Math.Min(markers.Count * 0.1, 0.2);
        return Math.Min(baseConfidence + markerBonus, 1.0);
    }

    private static double CalculateCausalBoundaryConfidence(string sentence, List<string> markers)
    {
        var baseConfidence = 0.7;
        var markerBonus = Math.Min(markers.Count * 0.1, 0.2);
        return Math.Min(baseConfidence + markerBonus, 1.0);
    }

    private static double CalculateLogicalBoundaryConfidence(string sentence, List<string> markers)
    {
        var baseConfidence = 0.65;
        var markerBonus = Math.Min(markers.Count * 0.1, 0.25);
        return Math.Min(baseConfidence + markerBonus, 1.0);
    }

    private static double CalculateFunctionalBoundaryConfidence(
        string sentence,
        List<string> markers,
        NarrativeFunction function
    )
    {
        var baseConfidence = function switch
        {
            NarrativeFunction.Setup => 0.8,
            NarrativeFunction.Resolution => 0.8,
            NarrativeFunction.Climax => 0.75,
            _ => 0.65,
        };

        var markerBonus = Math.Min(markers.Count * 0.05, 0.15);
        return Math.Min(baseConfidence + markerBonus, 1.0);
    }

    private static TemporalRelationship DetermineTemporalType(string sentence, List<string> markers)
    {
        if (markers.Any(m => sourceArray.Contains(m.ToLower())))
        {
            return TemporalRelationship.Chronological;
        }

        if (markers.Any(m => sourceArray0.Contains(m.ToLower())))
        {
            return TemporalRelationship.Simultaneous;
        }

        if (markers.Any(m => sourceArray1.Contains(m.ToLower())))
        {
            return TemporalRelationship.Flashback;
        }

        return TemporalRelationship.Chronological; // Default
    }

    private static CausalType DetermineCausalType(string marker)
    {
        var lowerMarker = marker.ToLower();
        if (sourceArray2.Contains(lowerMarker))
        {
            return CausalType.Direct;
        }

        if (sourceArray3.Contains(lowerMarker))
        {
            return CausalType.Conditional;
        }

        if (sourceArray4.Contains(lowerMarker))
        {
            return CausalType.Necessary;
        }

        return CausalType.Direct; // Default
    }

    private static LogicalRelationship DetermineLogicalRelationship(List<string> markers)
    {
        var lowerMarkers = markers.Select(m => m.ToLower()).ToList();

        if (lowerMarkers.Any(m => sourceArray5.Contains(m)))
        {
            return LogicalRelationship.Contrasting;
        }

        if (lowerMarkers.Any(m => sourceArray6.Contains(m)))
        {
            return LogicalRelationship.Supporting;
        }

        if (lowerMarkers.Any(m => sourceArray7.Contains(m)))
        {
            return LogicalRelationship.Explanatory;
        }

        return LogicalRelationship.Sequential; // Default
    }

    private static LogicalRelationship GetLogicalRelationshipForFunction(NarrativeFunction function)
    {
        return function switch
        {
            NarrativeFunction.Setup => LogicalRelationship.Sequential,
            NarrativeFunction.Development => LogicalRelationship.Sequential,
            NarrativeFunction.Complication => LogicalRelationship.Contrasting,
            NarrativeFunction.Resolution => LogicalRelationship.Causal,
            _ => LogicalRelationship.Sequential,
        };
    }

    private static double CalculateCausalStrength(string sentence, string marker)
    {
        var baseStrength = marker.ToLower() switch
        {
            "because" => 0.9,
            "therefore" => 0.85,
            "consequently" => 0.8,
            "if" => 0.6,
            _ => 0.7,
        };

        return baseStrength;
    }

    private static NarrativeType DetermineNarrativeType(string content)
    {
        var temporalCount = TemporalMarkers.Count(marker =>
            content.Contains(marker, StringComparison.OrdinalIgnoreCase)
        );
        var causalCount = CausalMarkers.Count(marker => content.Contains(marker, StringComparison.OrdinalIgnoreCase));
        var logicalCount = LogicalMarkers.Count(marker => content.Contains(marker, StringComparison.OrdinalIgnoreCase));

        if (temporalCount > causalCount && temporalCount > logicalCount)
        {
            return NarrativeType.Sequential;
        }

        if (causalCount > temporalCount && causalCount > logicalCount)
        {
            return NarrativeType.Causal;
        }

        if (logicalCount > 0)
        {
            return NarrativeType.Argumentative;
        }

        return NarrativeType.Descriptive; // Default
    }

    private static TemporalProgression DetermineTemporalProgression(string content)
    {
        var linearMarkers = new[] { "first", "then", "next", "finally" };
        var nonLinearMarkers = new[] { "previously", "meanwhile", "earlier" };

        var linearCount = linearMarkers.Count(marker => content.Contains(marker, StringComparison.OrdinalIgnoreCase));
        var nonLinearCount = nonLinearMarkers.Count(marker =>
            content.Contains(marker, StringComparison.OrdinalIgnoreCase)
        );

        if (linearCount > nonLinearCount * 2)
        {
            return TemporalProgression.Linear;
        }

        if (nonLinearCount > 0)
        {
            return TemporalProgression.NonLinear;
        }

        return TemporalProgression.Static; // Default
    }

    private static double CalculateFlowCoherence(string content)
    {
        // Simple heuristic based on transition markers
        var markerCount =
            TemporalMarkers.Count(marker => content.Contains(marker, StringComparison.OrdinalIgnoreCase))
            + CausalMarkers.Count(marker => content.Contains(marker, StringComparison.OrdinalIgnoreCase))
            + LogicalMarkers.Count(marker => content.Contains(marker, StringComparison.OrdinalIgnoreCase));

        var sentences = SplitIntoSentences(content);
        var markerDensity = sentences.Length > 0 ? (double)markerCount / sentences.Length : 0;

        return Math.Min(markerDensity * 2, 1.0); // Scale to 0-1
    }

    private static double CalculateLogicalConsistency(string content, List<CausalRelation> causalChain)
    {
        // Base score from causal chain strength
        var avgCausalStrength = causalChain.Count != 0 ? causalChain.Average(c => c.Strength) : 0.5;

        // Factor in logical marker presence
        var logicalMarkerCount = LogicalMarkers.Count(marker =>
            content.Contains(marker, StringComparison.OrdinalIgnoreCase)
        );
        var sentences = SplitIntoSentences(content);
        var logicalDensity = sentences.Length > 0 ? (double)logicalMarkerCount / sentences.Length : 0;

        return (avgCausalStrength * 0.7) + (Math.Min(logicalDensity * 2, 1.0) * 0.3);
    }

    private static double CalculateTemporalConsistency(string content)
    {
        var temporalMarkerCount = TemporalMarkers.Count(marker =>
            content.Contains(marker, StringComparison.OrdinalIgnoreCase)
        );
        var sentences = SplitIntoSentences(content);
        var temporalDensity = sentences.Length > 0 ? (double)temporalMarkerCount / sentences.Length : 0;

        return Math.Min(temporalDensity * 3, 1.0); // Scale to 0-1
    }

    private static double CalculateNarrativeCompleteness(
        string content,
        Dictionary<NarrativeFunction, List<int>> elements
    )
    {
        var essentialFunctions = new[]
        {
            NarrativeFunction.Setup,
            NarrativeFunction.Development,
            NarrativeFunction.Resolution,
        };
        var presentCount = essentialFunctions.Count(func => elements.ContainsKey(func) && elements[func].Count != 0);

        return (double)presentCount / essentialFunctions.Length;
    }

    private static List<string> ExtractNarrativeMarkers(string content)
    {
        var markers = new List<string>();

        markers.AddRange(TemporalMarkers.Where(marker => content.Contains(marker, StringComparison.OrdinalIgnoreCase)));
        markers.AddRange(CausalMarkers.Where(marker => content.Contains(marker, StringComparison.OrdinalIgnoreCase)));
        markers.AddRange(LogicalMarkers.Where(marker => content.Contains(marker, StringComparison.OrdinalIgnoreCase)));

        return [.. markers.Distinct()];
    }

    private static Task<NarrativeSegmentValidationResult> ValidateIndividualSegmentAsync(
        DocumentSegment segment,
        CancellationToken cancellationToken
    )
    {
        var result = new NarrativeSegmentValidationResult
        {
            SegmentId = segment.Id,
            FlowCoherence = CalculateSegmentCoherence(segment.Content),
            LogicalConsistency = CalculateSegmentLogicalConsistency(segment.Content),
            TemporalConsistency = CalculateSegmentTemporalConsistency(segment.Content),
            NarrativeFunctionClarity = CalculateNarrativeFunctionClarity(segment),
            TransitionQuality = CalculateSegmentTransitionQuality(segment),
        };

        // Add issues if scores are low
        if (result.FlowCoherence < 0.5)
        {
            result.Issues.Add(
                new ValidationIssue
                {
                    Type = ValidationIssueType.PoorCoherence,
                    Description = "Segment shows low narrative flow coherence",
                    Severity = ValidationSeverity.Warning,
                    Position = 0,
                }
            );
        }

        return Task.FromResult(result);
    }

    private static double CalculateSegmentCoherence(string content)
    {
        // Simple heuristic based on narrative markers
        var markerCount =
            TemporalMarkers.Count(marker => content.Contains(marker, StringComparison.OrdinalIgnoreCase))
            + CausalMarkers.Count(marker => content.Contains(marker, StringComparison.OrdinalIgnoreCase));

        var sentences = SplitIntoSentences(content);
        var coherenceScore = sentences.Length > 0 ? Math.Min((double)markerCount / sentences.Length * 2, 1.0) : 0.5;

        return coherenceScore;
    }

    private static double CalculateSegmentIndependence(string content)
    {
        // Segments with clear beginnings and endings score higher
        var hasIntro = FunctionMarkers[NarrativeFunction.Setup]
            .Any(marker => content.Contains(marker, StringComparison.OrdinalIgnoreCase));
        var hasConclusion = FunctionMarkers[NarrativeFunction.Resolution]
            .Any(marker => content.Contains(marker, StringComparison.OrdinalIgnoreCase));

        var baseScore = 0.6;
        if (hasIntro)
        {
            baseScore += 0.2;
        }

        if (hasConclusion)
        {
            baseScore += 0.2;
        }

        return Math.Min(baseScore, 1.0);
    }

    private static double CalculateNarrativeConsistency(string content)
    {
        // Check for consistent narrative voice and flow
        var sentences = SplitIntoSentences(content);
        if (sentences.Length <= 1)
        {
            return 1.0;
        }

        // Simple heuristic: presence of connecting words
        var connectingWords = new[] { "and", "but", "however", "therefore", "then", "also" };
        var connectionCount = connectingWords.Count(word => content.Contains(word, StringComparison.OrdinalIgnoreCase));

        return Math.Min((double)connectionCount / sentences.Length * 2, 1.0);
    }

    private static double CalculateSegmentLogicalConsistency(string content)
    {
        var logicalMarkerCount = LogicalMarkers.Count(marker =>
            content.Contains(marker, StringComparison.OrdinalIgnoreCase)
        );
        var sentences = SplitIntoSentences(content);

        return sentences.Length > 0 ? Math.Min((double)logicalMarkerCount / sentences.Length * 2, 1.0) : 0.5;
    }

    private static double CalculateSegmentTemporalConsistency(string content)
    {
        var temporalMarkerCount = TemporalMarkers.Count(marker =>
            content.Contains(marker, StringComparison.OrdinalIgnoreCase)
        );
        var sentences = SplitIntoSentences(content);

        return sentences.Length > 0 ? Math.Min((double)temporalMarkerCount / sentences.Length * 2, 1.0) : 0.5;
    }

    private static double CalculateNarrativeFunctionClarity(DocumentSegment segment)
    {
        // Check if the segment has clear narrative function indicators
        if (segment.Metadata.TryGetValue("narrative_function", out var value))
        {
            var function = Enum.Parse<NarrativeFunction>(value.ToString()!);
            var markers = FunctionMarkers[function];
            var markerCount = markers.Count(marker =>
                segment.Content.Contains(marker, StringComparison.OrdinalIgnoreCase)
            );

            return Math.Min(markerCount * 0.3, 1.0);
        }

        return 0.5; // Default if no function specified
    }

    private static double CalculateSegmentTransitionQuality(DocumentSegment segment)
    {
        // Check transition quality based on boundary confidence
        if (segment.Metadata.TryGetValue("boundary_confidence", out var value))
        {
            return Convert.ToDouble(value);
        }

        return 0.6; // Default
    }

    private static double CalculateOverallFlowCoherence(List<DocumentSegment> segments, string originalContent)
    {
        return segments.Count == 0 ? 0 : segments.Average(s => CalculateSegmentCoherence(s.Content));
    }

    private static double CalculateOverallLogicalConsistency(List<DocumentSegment> segments)
    {
        return segments.Count == 0 ? 0 : segments.Average(s => CalculateSegmentLogicalConsistency(s.Content));
    }

    private static double CalculateOverallTemporalConsistency(List<DocumentSegment> segments)
    {
        return segments.Count == 0 ? 0 : segments.Average(s => CalculateSegmentTemporalConsistency(s.Content));
    }

    private static double CalculateOverallNarrativeCompleteness(List<DocumentSegment> segments, string originalContent)
    {
        // Check if essential narrative functions are represented
        var functions = segments
            .Where(s => s.Metadata.ContainsKey("narrative_function"))
            .Select(s => Enum.Parse<NarrativeFunction>(s.Metadata["narrative_function"].ToString()!))
            .Distinct()
            .ToList();

        var essentialFunctions = new[]
        {
            NarrativeFunction.Setup,
            NarrativeFunction.Development,
            NarrativeFunction.Resolution,
        };
        var presentCount = essentialFunctions.Count(functions.Contains);

        return (double)presentCount / essentialFunctions.Length;
    }

    private static double CalculateOverallTransitionQuality(List<DocumentSegment> segments)
    {
        if (segments.Count == 0)
        {
            return 0;
        }

        var transitionScores = segments
            .Where(s => s.Metadata.ContainsKey("boundary_confidence"))
            .Select(s => Convert.ToDouble(s.Metadata["boundary_confidence"]))
            .ToList();

        return transitionScores.Count != 0 ? transitionScores.Average() : 0.6;
    }

    private static List<ValidationIssue> IdentifyValidationIssues(NarrativeSegmentationValidation validation)
    {
        var issues = new List<ValidationIssue>();

        if (validation.FlowCoherence < 0.5)
        {
            issues.Add(
                new ValidationIssue
                {
                    Type = ValidationIssueType.PoorCoherence,
                    Description = "Overall narrative flow coherence is low",
                    Severity = ValidationSeverity.Warning,
                    Position = 0,
                }
            );
        }

        if (validation.LogicalConsistency < 0.5)
        {
            issues.Add(
                new ValidationIssue
                {
                    Type = ValidationIssueType.UnclearBoundaries,
                    Description = "Logical consistency across segments needs improvement",
                    Severity = ValidationSeverity.Warning,
                    Position = 0,
                }
            );
        }

        if (validation.NarrativeCompleteness < 0.6)
        {
            issues.Add(
                new ValidationIssue
                {
                    Type = ValidationIssueType.MissingContext,
                    Description = "Narrative structure appears incomplete",
                    Severity = ValidationSeverity.Info,
                    Position = 0,
                }
            );
        }

        return issues;
    }

    private static List<string> GenerateValidationRecommendations(NarrativeSegmentationValidation validation)
    {
        var recommendations = new List<string>();

        if (validation.FlowCoherence < 0.6)
        {
            recommendations.Add("Consider adding more transitional phrases to improve narrative flow");
        }

        if (validation.LogicalConsistency < 0.6)
        {
            recommendations.Add("Review causal relationships and logical connections between segments");
        }

        if (validation.TemporalConsistency < 0.6)
        {
            recommendations.Add("Check temporal sequencing and add time markers where appropriate");
        }

        if (validation.NarrativeCompleteness < 0.7)
        {
            recommendations.Add("Ensure narrative has clear beginning, development, and conclusion");
        }

        return recommendations;
    }

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex MyRegex();

    #endregion
}
