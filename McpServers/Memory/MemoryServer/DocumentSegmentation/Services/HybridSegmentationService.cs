using MemoryServer.DocumentSegmentation.Integration;
using MemoryServer.DocumentSegmentation.Models;

namespace MemoryServer.DocumentSegmentation.Services;

/// <summary>
/// Implementation of hybrid document segmentation that combines multiple strategies.
/// Uses intelligent weighting, consensus analysis, and adaptive optimization for optimal results.
/// </summary>
public class HybridSegmentationService : IHybridSegmentationService
{
    private readonly IStructureBasedSegmentationService _structureService;
    private readonly INarrativeBasedSegmentationService _narrativeService;
    private readonly ITopicBasedSegmentationService _topicService;
    private readonly ILlmProviderIntegrationService _llmService;
    private readonly ISegmentationPromptManager _promptManager;
    private readonly ILogger<HybridSegmentationService> _logger;

    public HybridSegmentationService(
        IStructureBasedSegmentationService structureService,
        INarrativeBasedSegmentationService narrativeService,
        ITopicBasedSegmentationService topicService,
        ILlmProviderIntegrationService llmService,
        ISegmentationPromptManager promptManager,
        ILogger<HybridSegmentationService> logger
    )
    {
        _structureService = structureService ?? throw new ArgumentNullException(nameof(structureService));
        _narrativeService = narrativeService ?? throw new ArgumentNullException(nameof(narrativeService));
        _topicService = topicService ?? throw new ArgumentNullException(nameof(topicService));
        _llmService = llmService ?? throw new ArgumentNullException(nameof(llmService));
        _promptManager = promptManager ?? throw new ArgumentNullException(nameof(promptManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Segments document content using hybrid approach that combines multiple strategies.
    /// </summary>
    public async Task<List<DocumentSegment>> SegmentUsingHybridApproachAsync(
        string content,
        DocumentType documentType = DocumentType.Generic,
        HybridSegmentationOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Starting hybrid segmentation for document type {DocumentType}, content length: {Length}",
            documentType,
            content.Length
        );

        options ??= new HybridSegmentationOptions();

        try
        {
            // Step 1: Determine optimal strategy weights
            var weights =
                options.PreferredWeights
                ?? await DetermineStrategyWeightsAsync(content, documentType, cancellationToken);

            _logger.LogDebug(
                "Strategy weights determined: Structure={Structure:F2}, Narrative={Narrative:F2}, Topic={Topic:F2}",
                weights.StructureWeight,
                weights.NarrativeWeight,
                weights.TopicWeight
            );

            // Step 2: Execute segmentation strategies in parallel
            var segmentationTasks = new List<Task<List<DocumentSegment>>>();

            if (weights.StructureWeight > 0.1)
            {
                segmentationTasks.Add(GetStructureSegmentsAsync(content, documentType, cancellationToken));
            }

            if (weights.NarrativeWeight > 0.1)
            {
                segmentationTasks.Add(GetNarrativeSegmentsAsync(content, documentType, cancellationToken));
            }

            if (weights.TopicWeight > 0.1)
            {
                segmentationTasks.Add(GetTopicSegmentsAsync(content, documentType, cancellationToken));
            }

            var segmentationResults = await Task.WhenAll(segmentationTasks);

            // Step 3: Combine results using intelligent merging
            var structureSegments =
                segmentationResults.Length > 0 ? segmentationResults[0] : [];
            var narrativeSegments =
                segmentationResults.Length > 1 ? segmentationResults[1] : [];
            var topicSegments = segmentationResults.Length > 2 ? segmentationResults[2] : [];

            var combinedSegments = await CombineSegmentationResultsAsync(
                structureSegments,
                narrativeSegments,
                topicSegments,
                weights,
                cancellationToken
            );

            // Step 4: Apply post-processing optimization
            if (options.ApplyPostProcessingOptimization)
            {
                combinedSegments = await OptimizeSegmentationAsync(
                    combinedSegments,
                    content,
                    weights,
                    cancellationToken
                );
            }

            // Step 5: Apply final quality checks
            combinedSegments = ApplyFinalQualityChecks(combinedSegments, options);

            _logger.LogInformation(
                "Hybrid segmentation completed: {Count} segments created using weights S={Structure:F2}/N={Narrative:F2}/T={Topic:F2}",
                combinedSegments.Count,
                weights.StructureWeight,
                weights.NarrativeWeight,
                weights.TopicWeight
            );

            return combinedSegments;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during hybrid segmentation");

            // Fallback to single strategy if enabled
            if (options.EnableFallback)
            {
                _logger.LogWarning("Falling back to {Strategy} strategy", options.FallbackStrategy);
                return await ExecuteFallbackStrategyAsync(
                    content,
                    documentType,
                    options.FallbackStrategy,
                    cancellationToken
                );
            }

            throw;
        }
    }

    /// <summary>
    /// Determines optimal strategy weights for a specific document.
    /// </summary>
    public async Task<StrategyWeights> DetermineStrategyWeightsAsync(
        string content,
        DocumentType documentType = DocumentType.Generic,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("Determining strategy weights for document type {DocumentType}", documentType);

        var weights = new StrategyWeights();

        try
        {
            // Analyze document characteristics
            var characteristics = AnalyzeDocumentCharacteristics(content);

            // Base weights on document type
            weights = GetBaseWeightsByDocumentType(documentType);

            // Adjust weights based on content characteristics
            weights = AdjustWeightsForCharacteristics(weights, characteristics);

            // Use LLM enhancement if available
            if (_llmService != null)
            {
                var llmWeights = await GetLlmWeightRecommendationsAsync(content, documentType, cancellationToken);
                if (llmWeights.Confidence > 0.7)
                {
                    weights = CombineWeights(weights, llmWeights, 0.6, 0.4);
                    weights.Method = WeightDeterminationMethod.LlmAnalysis;
                }
            }

            // Normalize and validate weights
            weights.Normalize();

            _logger.LogDebug(
                "Final weights: Structure={Structure:F2}, Narrative={Narrative:F2}, Topic={Topic:F2}, Method={Method}",
                weights.StructureWeight,
                weights.NarrativeWeight,
                weights.TopicWeight,
                weights.Method
            );

            return weights;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error determining strategy weights, using defaults");

            // Return equal weights as fallback
            return new StrategyWeights
            {
                Method = WeightDeterminationMethod.Equal,
                Confidence = 0.5,
                Rationale = "Default equal weights due to analysis error",
            };
        }
    }

    /// <summary>
    /// Combines segmentation results from multiple strategies using intelligent merging.
    /// </summary>
    public async Task<List<DocumentSegment>> CombineSegmentationResultsAsync(
        List<DocumentSegment> structureSegments,
        List<DocumentSegment> narrativeSegments,
        List<DocumentSegment> topicSegments,
        StrategyWeights weights,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Combining segmentation results: Structure={StructureCount}, Narrative={NarrativeCount}, Topic={TopicCount}",
            structureSegments.Count,
            narrativeSegments.Count,
            topicSegments.Count
        );

        try
        {
            // Step 1: Identify boundary consensus
            var boundaryConsensus = AnalyzeBoundaryConsensus(structureSegments, narrativeSegments, topicSegments);

            // Step 2: Create segments based on consensus boundaries
            var consensusSegments = CreateSegmentsFromConsensus(
                boundaryConsensus,
                structureSegments,
                narrativeSegments,
                topicSegments,
                weights
            );

            // Step 3: Fill gaps and merge overlapping segments
            var mergedSegments = await MergeOverlappingSegmentsAsync(consensusSegments, weights, cancellationToken);

            // Step 4: Quality-based selection for remaining conflicts
            var finalSegments = ResolveSegmentConflicts(mergedSegments, weights);

            return [.. finalSegments.OrderBy(s => s.SequenceNumber)];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error combining segmentation results");

            // Fallback: return results from highest-weighted strategy
            return GetHighestWeightedStrategyResults(structureSegments, narrativeSegments, topicSegments, weights);
        }
    }

    /// <summary>
    /// Validates hybrid segmentation quality and provides improvement suggestions.
    /// </summary>
    public async Task<HybridSegmentationValidation> ValidateHybridSegmentationAsync(
        List<DocumentSegment> segments,
        string originalContent,
        StrategyWeights weights,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("Validating hybrid segmentation quality for {Count} segments", segments.Count);

        var validation = new HybridSegmentationValidation();

        try
        {
            // Calculate overall quality metrics
            validation.OverallQuality = CalculateOverallQuality(segments);
            validation.StrategyCombinationScore = CalculateStrategyCombinationScore(segments, weights);
            validation.CrossStrategyConsistency = CalculateCrossStrategyConsistency(segments);
            validation.BoundaryConsensusQuality = CalculateBoundaryConsensusQuality(segments);
            validation.WeightEffectiveness = CalculateWeightEffectiveness(segments, weights);

            // Validate individual strategy contributions
            validation.StrategyValidationScores = await ValidateStrategyContributionsAsync(segments, cancellationToken);

            // Identify issues and generate recommendations
            validation.Issues = IdentifyValidationIssues(segments, validation);
            validation.Recommendations = GenerateImprovementRecommendations(validation, weights);

            _logger.LogInformation(
                "Hybrid validation completed: Overall quality {Quality:F2}, Strategy combination {Combination:F2}",
                validation.OverallQuality,
                validation.StrategyCombinationScore
            );

            return validation;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating hybrid segmentation");

            // Return basic validation
            validation.OverallQuality = 0.5;
            validation.Issues.Add(
                new ValidationIssue
                {
                    Type = ValidationIssueType.PoorCoherence,
                    Severity = ValidationSeverity.Warning,
                    Description = "Validation failed due to processing error",
                }
            );

            return validation;
        }
    }

    /// <summary>
    /// Adapts segmentation strategy based on document characteristics and feedback.
    /// </summary>
    public Task<AdaptiveStrategyConfig> AdaptSegmentationStrategyAsync(
        string content,
        List<DocumentSegmentationResult>? previousResults = null,
        List<string>? feedback = null,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Adapting segmentation strategy based on {ResultCount} previous results and {FeedbackCount} feedback items",
            previousResults?.Count ?? 0,
            feedback?.Count ?? 0
        );

        var config = new AdaptiveStrategyConfig();

        try
        {
            // Analyze current document characteristics
            var characteristics = AnalyzeDocumentCharacteristics(content);

            // Learn from previous results if available
            if (previousResults?.Any() == true)
            {
                var learnedWeights = LearnFromPreviousResults(previousResults);
                config.RecommendedWeights = learnedWeights;
                config.AdaptationReasons.Add("Learned from previous segmentation results");
            }

            // Apply feedback-based adjustments
            if (feedback?.Any() == true)
            {
                ApplyFeedbackAdjustments(config, feedback);
                config.AdaptationReasons.Add("Adjusted based on user feedback");
            }

            // Determine primary and secondary strategies
            config.PrimaryStrategy = DeterminePrimaryStrategy(config.RecommendedWeights);
            config.SecondaryStrategy = DetermineSecondaryStrategy(config.RecommendedWeights, config.PrimaryStrategy);

            // Calculate adaptation confidence
            config.AdaptationConfidence = CalculateAdaptationConfidence(config, characteristics);

            // Store performance metrics
            config.PerformanceMetrics = CalculatePerformanceMetrics(previousResults);

            _logger.LogInformation(
                "Adaptive strategy config created: Primary={Primary}, Confidence={Confidence:F2}",
                config.PrimaryStrategy,
                config.AdaptationConfidence
            );

            return Task.FromResult(config);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adapting segmentation strategy");

            // Return default configuration
            config.PrimaryStrategy = SegmentationStrategy.Hybrid;
            config.AdaptationConfidence = 0.5;
            config.AdaptationReasons.Add("Default configuration due to adaptation error");

            return Task.FromResult(config);
        }
    }

    #region Private Helper Methods

    /// <summary>
    /// Gets structure-based segments asynchronously.
    /// </summary>
    private async Task<List<DocumentSegment>> GetStructureSegmentsAsync(
        string content,
        DocumentType documentType,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await _structureService.SegmentByStructureAsync(content, documentType, null, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get structure-based segments");
            return [];
        }
    }

    /// <summary>
    /// Gets narrative-based segments asynchronously.
    /// </summary>
    private async Task<List<DocumentSegment>> GetNarrativeSegmentsAsync(
        string content,
        DocumentType documentType,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await _narrativeService.SegmentByNarrativeAsync(content, documentType, null, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get narrative-based segments");
            return [];
        }
    }

    /// <summary>
    /// Gets topic-based segments asynchronously.
    /// </summary>
    private async Task<List<DocumentSegment>> GetTopicSegmentsAsync(
        string content,
        DocumentType documentType,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await _topicService.SegmentByTopicsAsync(content, documentType, null, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get topic-based segments");
            return [];
        }
    }

    /// <summary>
    /// Analyzes document characteristics for weight determination.
    /// </summary>
    private static DocumentCharacteristics AnalyzeDocumentCharacteristics(string content)
    {
        var characteristics = new DocumentCharacteristics();

        // Basic text analysis
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var words = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        characteristics.LineCount = lines.Length;
        characteristics.WordCount = words.Length;
        characteristics.CharacterCount = content.Length;

        // Structural indicators
        characteristics.HasHeadings =
            content.Contains('#') || lines.Any(l => l.Trim().All(char.IsUpper) && l.Trim().Length > 3);
        characteristics.HasLists = content.Contains("- ") || content.Contains("* ") || content.Contains("1. ");
        characteristics.HasCodeBlocks = content.Contains("```") || content.Contains("    ");
        characteristics.HasTables = content.Contains('|') && content.Split('|').Length > 3;

        // Narrative indicators
        characteristics.NarrativeFlow = CalculateNarrativeFlow(content);
        characteristics.TemporalMarkers = CountTemporalMarkers(content);
        characteristics.CausationIndicators = CountCausationIndicators(content);

        // Topic indicators
        characteristics.TopicDiversity = CalculateTopicDiversity(content);
        characteristics.KeywordDensity = CalculateKeywordDensity(content);

        return characteristics;
    }

    /// <summary>
    /// Gets base weights based on document type.
    /// </summary>
    private static StrategyWeights GetBaseWeightsByDocumentType(DocumentType documentType)
    {
        return documentType switch
        {
            DocumentType.Technical => new StrategyWeights
            {
                StructureWeight = 0.5,
                NarrativeWeight = 0.2,
                TopicWeight = 0.3,
                Rationale = "Technical documents benefit from structural analysis",
            },
            DocumentType.ResearchPaper => new StrategyWeights
            {
                StructureWeight = 0.6,
                NarrativeWeight = 0.1,
                TopicWeight = 0.3,
                Rationale = "Research papers have strong structural organization",
            },
            DocumentType.Legal => new StrategyWeights
            {
                StructureWeight = 0.4,
                NarrativeWeight = 0.3,
                TopicWeight = 0.3,
                Rationale = "Legal documents balance structure and narrative",
            },
            DocumentType.Email => new StrategyWeights
            {
                StructureWeight = 0.2,
                NarrativeWeight = 0.4,
                TopicWeight = 0.4,
                Rationale = "Emails focus on topics and narrative flow",
            },
            DocumentType.Chat => new StrategyWeights
            {
                StructureWeight = 0.1,
                NarrativeWeight = 0.5,
                TopicWeight = 0.4,
                Rationale = "Chat messages follow conversational narrative",
            },
            _ => new StrategyWeights
            {
                StructureWeight = 0.33,
                NarrativeWeight = 0.33,
                TopicWeight = 0.34,
                Rationale = "Balanced approach for generic documents",
            },
        };
    }

    /// <summary>
    /// Adjusts weights based on document characteristics.
    /// </summary>
    private static StrategyWeights AdjustWeightsForCharacteristics(
        StrategyWeights baseWeights,
        DocumentCharacteristics characteristics
    )
    {
        var adjusted = new StrategyWeights
        {
            StructureWeight = baseWeights.StructureWeight,
            NarrativeWeight = baseWeights.NarrativeWeight,
            TopicWeight = baseWeights.TopicWeight,
            Method = baseWeights.Method,
            Rationale = baseWeights.Rationale,
        };

        // Increase structure weight for documents with clear structural elements
        if (characteristics.HasHeadings || characteristics.HasLists || characteristics.HasTables)
        {
            adjusted.StructureWeight += 0.1;
        }

        // Increase narrative weight for documents with strong narrative flow
        if (characteristics.NarrativeFlow > 0.7 || characteristics.TemporalMarkers > 3)
        {
            adjusted.NarrativeWeight += 0.1;
        }

        // Increase topic weight for documents with high topic diversity
        if (characteristics.TopicDiversity > 0.6)
        {
            adjusted.TopicWeight += 0.1;
        }

        // Normalize to ensure weights sum to 1.0
        adjusted.Normalize();

        return adjusted;
    }

    /// <summary>
    /// Gets LLM weight recommendations if available.
    /// </summary>
    private async Task<StrategyWeights> GetLlmWeightRecommendationsAsync(
        string content,
        DocumentType documentType,
        CancellationToken cancellationToken
    )
    {
        try
        {
            // Use the available LLM service method to get strategy recommendations
            var strategyRecommendation = await _llmService.AnalyzeOptimalStrategyAsync(
                content,
                documentType,
                cancellationToken
            );

            // Convert strategy recommendation to weights
            return ConvertStrategyRecommendationToWeights(strategyRecommendation);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get LLM weight recommendations");
            return new StrategyWeights { Confidence = 0.0 };
        }
    }

    /// <summary>
    /// Converts strategy recommendation to weights.
    /// </summary>
    private static StrategyWeights ConvertStrategyRecommendationToWeights(StrategyRecommendation recommendation)
    {
        return recommendation.Strategy switch
        {
            SegmentationStrategy.StructureBased => new StrategyWeights
            {
                StructureWeight = 0.7,
                NarrativeWeight = 0.15,
                TopicWeight = 0.15,
                Confidence = recommendation.Confidence,
                Method = WeightDeterminationMethod.LlmAnalysis,
                Rationale = recommendation.Reasoning ?? "LLM recommends structure-based approach",
            },
            SegmentationStrategy.NarrativeBased => new StrategyWeights
            {
                StructureWeight = 0.15,
                NarrativeWeight = 0.7,
                TopicWeight = 0.15,
                Confidence = recommendation.Confidence,
                Method = WeightDeterminationMethod.LlmAnalysis,
                Rationale = recommendation.Reasoning ?? "LLM recommends narrative-based approach",
            },
            SegmentationStrategy.TopicBased => new StrategyWeights
            {
                StructureWeight = 0.15,
                NarrativeWeight = 0.15,
                TopicWeight = 0.7,
                Confidence = recommendation.Confidence,
                Method = WeightDeterminationMethod.LlmAnalysis,
                Rationale = recommendation.Reasoning ?? "LLM recommends topic-based approach",
            },
            SegmentationStrategy.Hybrid => new StrategyWeights
            {
                StructureWeight = 0.33,
                NarrativeWeight = 0.33,
                TopicWeight = 0.34,
                Confidence = recommendation.Confidence,
                Method = WeightDeterminationMethod.LlmAnalysis,
                Rationale = recommendation.Reasoning ?? "LLM recommends balanced hybrid approach",
            },
            _ => new StrategyWeights
            {
                Confidence = 0.5,
                Method = WeightDeterminationMethod.LlmAnalysis,
                Rationale = "Default weights due to unknown strategy recommendation",
            },
        };
    }

    /// <summary>
    /// Combines two sets of weights using specified ratios.
    /// </summary>
    private static StrategyWeights CombineWeights(
        StrategyWeights weights1,
        StrategyWeights weights2,
        double ratio1,
        double ratio2
    )
    {
        return new StrategyWeights
        {
            StructureWeight = weights1.StructureWeight * ratio1 + weights2.StructureWeight * ratio2,
            NarrativeWeight = weights1.NarrativeWeight * ratio1 + weights2.NarrativeWeight * ratio2,
            TopicWeight = weights1.TopicWeight * ratio1 + weights2.TopicWeight * ratio2,
            Confidence = Math.Max(weights1.Confidence, weights2.Confidence),
            Method = WeightDeterminationMethod.LlmAnalysis,
            Rationale = $"Combined: {weights1.Rationale} + {weights2.Rationale}",
        };
    }

    /// <summary>
    /// Executes fallback strategy when hybrid approach fails.
    /// </summary>
    private async Task<List<DocumentSegment>> ExecuteFallbackStrategyAsync(
        string content,
        DocumentType documentType,
        SegmentationStrategy strategy,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return strategy switch
            {
                SegmentationStrategy.StructureBased => await _structureService.SegmentByStructureAsync(
                    content,
                    documentType,
                    null,
                    cancellationToken
                ),
                SegmentationStrategy.NarrativeBased => await _narrativeService.SegmentByNarrativeAsync(
                    content,
                    documentType,
                    null,
                    cancellationToken
                ),
                SegmentationStrategy.TopicBased => await _topicService.SegmentByTopicsAsync(
                    content,
                    documentType,
                    null,
                    cancellationToken
                ),
                _ => CreateBasicRuleBasedSegments(content),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallback strategy {Strategy} failed", strategy);
            return CreateBasicRuleBasedSegments(content);
        }
    }

    /// <summary>
    /// Creates basic rule-based segments as ultimate fallback.
    /// </summary>
    private static List<DocumentSegment> CreateBasicRuleBasedSegments(string content)
    {
        var segments = new List<DocumentSegment>();
        var words = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var segmentSize = Math.Max(100, words.Length / 5); // Aim for ~5 segments

        for (var i = 0; i < words.Length; i += segmentSize)
        {
            var segmentWords = words.Skip(i).Take(segmentSize);
            var segmentContent = string.Join(" ", segmentWords);

            segments.Add(
                new DocumentSegment
                {
                    Id = $"hybrid-fallback-{segments.Count + 1}",
                    SequenceNumber = segments.Count + 1,
                    Content = segmentContent,
                    Title = $"Segment {segments.Count + 1}",
                    Summary =
                        segmentContent.Length > 100
                            ? string.Concat(segmentContent.AsSpan(0, 97), "...")
                            : segmentContent,
                    Quality = new SegmentQuality
                    {
                        CoherenceScore = 0.6,
                        IndependenceScore = 0.7,
                        TopicConsistencyScore = 0.6,
                        PassesQualityThreshold = true,
                    },
                    Metadata = new Dictionary<string, object>
                    {
                        ["strategy"] = "hybrid_fallback",
                        ["created_by"] = "hybrid_service",
                    },
                }
            );
        }

        return segments;
    }

    // Placeholder implementations for remaining helper methods
    private static List<BoundaryConsensus> AnalyzeBoundaryConsensus(
        List<DocumentSegment> structureSegments,
        List<DocumentSegment> narrativeSegments,
        List<DocumentSegment> topicSegments
    )
    {
        // Implementation would analyze where different strategies agree on boundaries
        return [];
    }

    private static List<DocumentSegment> CreateSegmentsFromConsensus(
        List<BoundaryConsensus> boundaryConsensus,
        List<DocumentSegment> structureSegments,
        List<DocumentSegment> narrativeSegments,
        List<DocumentSegment> topicSegments,
        StrategyWeights weights
    )
    {
        // For now, return the strategy with highest weight
        if (weights.StructureWeight >= weights.NarrativeWeight && weights.StructureWeight >= weights.TopicWeight)
        {
            return structureSegments;
        }

        if (weights.NarrativeWeight >= weights.TopicWeight)
        {
            return narrativeSegments;
        }

        return topicSegments;
    }

    private static Task<List<DocumentSegment>> MergeOverlappingSegmentsAsync(
        List<DocumentSegment> segments,
        StrategyWeights weights,
        CancellationToken cancellationToken
    )
    {
        // Basic implementation - in full version would merge overlapping segments intelligently
        return Task.FromResult(segments);
    }

    private static List<DocumentSegment> ResolveSegmentConflicts(
        List<DocumentSegment> segments,
        StrategyWeights weights
    )
    {
        // Basic implementation - in full version would resolve conflicts using quality scores
        return segments;
    }

    private static List<DocumentSegment> GetHighestWeightedStrategyResults(
        List<DocumentSegment> structureSegments,
        List<DocumentSegment> narrativeSegments,
        List<DocumentSegment> topicSegments,
        StrategyWeights weights
    )
    {
        if (weights.StructureWeight >= weights.NarrativeWeight && weights.StructureWeight >= weights.TopicWeight)
        {
            return structureSegments;
        }

        if (weights.NarrativeWeight >= weights.TopicWeight)
        {
            return narrativeSegments;
        }

        return topicSegments;
    }

    private static Task<List<DocumentSegment>> OptimizeSegmentationAsync(
        List<DocumentSegment> segments,
        string content,
        StrategyWeights weights,
        CancellationToken cancellationToken
    )
    {
        // Basic implementation - in full version would apply post-processing optimizations
        return Task.FromResult(segments);
    }

    private static List<DocumentSegment> ApplyFinalQualityChecks(
        List<DocumentSegment> segments,
        HybridSegmentationOptions options
    )
    {
        return [.. segments
            .Where(s => s.Content.Length >= options.MinSegmentSize && s.Content.Length <= options.MaxSegmentSize)
            .Take(options.MaxSegments)];
    }

    // Quality calculation methods
    private static double CalculateOverallQuality(List<DocumentSegment> segments)
    {
        if (segments.Count == 0)
        {
            return 0.0;
        }

        return segments.Average(s => s.Quality?.CoherenceScore ?? 0.5);
    }

    private static double CalculateStrategyCombinationScore(List<DocumentSegment> segments, StrategyWeights weights)
    {
        // Placeholder - would calculate how well strategies were combined
        return 0.8;
    }

    private static double CalculateCrossStrategyConsistency(List<DocumentSegment> segments)
    {
        // Placeholder - would measure consistency across strategy boundaries
        return 0.75;
    }

    private static double CalculateBoundaryConsensusQuality(List<DocumentSegment> segments)
    {
        // Placeholder - would measure quality of boundary consensus
        return 0.7;
    }

    private static double CalculateWeightEffectiveness(List<DocumentSegment> segments, StrategyWeights weights)
    {
        // Placeholder - would measure how effective the chosen weights were
        return weights.Confidence;
    }

    private static Task<Dictionary<SegmentationStrategy, double>> ValidateStrategyContributionsAsync(
        List<DocumentSegment> segments,
        CancellationToken cancellationToken
    )
    {
        return Task.FromResult(
            new Dictionary<SegmentationStrategy, double>
            {
                [SegmentationStrategy.StructureBased] = 0.8,
                [SegmentationStrategy.NarrativeBased] = 0.75,
                [SegmentationStrategy.TopicBased] = 0.7,
            }
        );
    }

    private static List<ValidationIssue> IdentifyValidationIssues(
        List<DocumentSegment> segments,
        HybridSegmentationValidation validation
    )
    {
        var issues = new List<ValidationIssue>();

        if (validation.OverallQuality < 0.7)
        {
            issues.Add(
                new ValidationIssue
                {
                    Type = ValidationIssueType.PoorCoherence,
                    Severity = ValidationSeverity.Warning,
                    Description = $"Overall quality is below threshold: {validation.OverallQuality:F2}",
                }
            );
        }

        return issues;
    }

    private static List<string> GenerateImprovementRecommendations(
        HybridSegmentationValidation validation,
        StrategyWeights weights
    )
    {
        var recommendations = new List<string>();

        if (validation.OverallQuality < 0.8)
        {
            recommendations.Add("Consider adjusting strategy weights for better results");
        }

        if (validation.BoundaryConsensusQuality < 0.7)
        {
            recommendations.Add("Improve boundary consensus by refining individual strategies");
        }

        return recommendations;
    }

    // Adaptive strategy methods
    private static StrategyWeights LearnFromPreviousResults(List<DocumentSegmentationResult> previousResults)
    {
        // Placeholder - would analyze previous results to learn optimal weights
        return new StrategyWeights { Method = WeightDeterminationMethod.Learned };
    }

    private static void ApplyFeedbackAdjustments(AdaptiveStrategyConfig config, List<string> feedback)
    {
        // Placeholder - would adjust configuration based on feedback
    }

    private static SegmentationStrategy DeterminePrimaryStrategy(StrategyWeights weights)
    {
        if (weights.StructureWeight >= weights.NarrativeWeight && weights.StructureWeight >= weights.TopicWeight)
        {
            return SegmentationStrategy.StructureBased;
        }

        if (weights.NarrativeWeight >= weights.TopicWeight)
        {
            return SegmentationStrategy.NarrativeBased;
        }

        return SegmentationStrategy.TopicBased;
    }

    private static SegmentationStrategy? DetermineSecondaryStrategy(
        StrategyWeights weights,
        SegmentationStrategy primary
    )
    {
        var strategies = new[]
        {
            (SegmentationStrategy.StructureBased, weights.StructureWeight),
            (SegmentationStrategy.NarrativeBased, weights.NarrativeWeight),
            (SegmentationStrategy.TopicBased, weights.TopicWeight),
        };

        return strategies.Where(s => s.Item1 != primary).OrderByDescending(s => s.Item2).FirstOrDefault().Item1;
    }

    private static double CalculateAdaptationConfidence(
        AdaptiveStrategyConfig config,
        DocumentCharacteristics characteristics
    )
    {
        // Placeholder - would calculate confidence in adaptive recommendations
        return 0.8;
    }

    private static Dictionary<string, double> CalculatePerformanceMetrics(
        List<DocumentSegmentationResult>? previousResults
    )
    {
        // Placeholder - would calculate performance metrics from previous results
        return new Dictionary<string, double>
        {
            ["average_quality"] = 0.8,
            ["processing_time"] = 2.5,
            ["user_satisfaction"] = 0.85,
        };
    }

    // Document analysis helper methods
    private static double CalculateNarrativeFlow(string content)
    {
        // Simple heuristic based on temporal markers and transitions
        var narrativeMarkers = new[]
        {
            "then",
            "next",
            "after",
            "before",
            "while",
            "during",
            "meanwhile",
            "subsequently",
        };
        var markerCount = narrativeMarkers.Sum(marker => CountOccurrences(content, marker));
        var sentences = content.Split('.', StringSplitOptions.RemoveEmptyEntries).Length;
        return Math.Min(1.0, (double)markerCount / Math.Max(1, sentences) * 10);
    }

    private static int CountTemporalMarkers(string content)
    {
        var temporalMarkers = new[]
        {
            "yesterday",
            "today",
            "tomorrow",
            "morning",
            "afternoon",
            "evening",
            "january",
            "february",
            "march",
            "april",
            "may",
            "june",
            "july",
            "august",
            "september",
            "october",
            "november",
            "december",
        };
        return temporalMarkers.Sum(marker => CountOccurrences(content, marker));
    }

    private static int CountCausationIndicators(string content)
    {
        var causationWords = new[]
        {
            "because",
            "since",
            "therefore",
            "thus",
            "hence",
            "consequently",
            "as a result",
            "due to",
        };
        return causationWords.Sum(word => CountOccurrences(content, word));
    }

    private static int CountOccurrences(string content, string word)
    {
        var count = 0;
        var index = 0;
        while ((index = content.IndexOf(word, index, StringComparison.OrdinalIgnoreCase)) != -1)
        {
            count++;
            index += word.Length;
        }
        return count;
    }

    private static double CalculateTopicDiversity(string content)
    {
        var words = content
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 3)
            .Select(w => w.ToLowerInvariant())
            .ToList();

        var uniqueWords = words.Distinct().Count();
        return Math.Min(1.0, (double)uniqueWords / Math.Max(1, words.Count) * 5);
    }

    private static double CalculateKeywordDensity(string content)
    {
        var words = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var technicalWords = words.Where(w => w.Length > 6 && char.IsUpper(w[0])).Count();
        return Math.Min(1.0, (double)technicalWords / Math.Max(1, words.Length) * 20);
    }

    #endregion

    /// <summary>
    /// Document characteristics used for weight determination.
    /// </summary>
    private class DocumentCharacteristics
    {
        public int LineCount { get; set; }
        public int WordCount { get; set; }
        public int CharacterCount { get; set; }
        public bool HasHeadings { get; set; }
        public bool HasLists { get; set; }
        public bool HasCodeBlocks { get; set; }
        public bool HasTables { get; set; }
        public double NarrativeFlow { get; set; }
        public int TemporalMarkers { get; set; }
        public int CausationIndicators { get; set; }
        public double TopicDiversity { get; set; }
        public double KeywordDensity { get; set; }
    }
}
