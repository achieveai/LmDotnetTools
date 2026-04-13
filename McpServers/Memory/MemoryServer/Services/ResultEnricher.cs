using System.Diagnostics;
using MemoryServer.Models;
using Microsoft.Extensions.Options;

namespace MemoryServer.Services;

/// <summary>
///     Result enrichment engine that provides minimal enrichment to enhance
///     user understanding without overwhelming the results (max 2 items per result).
/// </summary>
public class ResultEnricher : IResultEnricher
{
    private readonly IGraphRepository _graphRepository;
    private readonly ILogger<ResultEnricher> _logger;
    private readonly EnrichmentOptions _options;

    public ResultEnricher(
        IOptions<MemoryServerOptions> options,
        IGraphRepository graphRepository,
        ILogger<ResultEnricher> logger
    )
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options?.Value?.Enrichment ?? throw new ArgumentNullException(nameof(options));
        _graphRepository = graphRepository ?? throw new ArgumentNullException(nameof(graphRepository));

        _logger.LogInformation(
            "ResultEnricher initialized with max {MaxItems} related items per result",
            _options.MaxRelatedItems
        );
    }

    public bool IsEnrichmentAvailable()
    {
        return _options.EnableEnrichment;
    }

    public async Task<EnrichmentResults> EnrichResultsAsync(
        List<UnifiedSearchResult> results,
        SessionContext sessionContext,
        EnrichmentOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(results);

        options ??= _options;
        var totalStopwatch = Stopwatch.StartNew();
        var metrics = new EnrichmentMetrics();

        _logger.LogDebug("Starting enrichment for {ResultCount} results", results.Count);

        try
        {
            // If enrichment is disabled or no results, return original results as enriched
            if (!options.EnableEnrichment || results.Count == 0)
            {
                return CreateFallbackResults(
                    results,
                    metrics,
                    totalStopwatch.Elapsed,
                    "Enrichment disabled or no results"
                );
            }

            // Perform enrichment with timeout
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(options.EnrichmentTimeout);

            var enrichedResults = await PerformEnrichmentAsync(
                results,
                sessionContext,
                options,
                metrics,
                timeoutCts.Token
            );

            totalStopwatch.Stop();
            metrics.TotalDuration = totalStopwatch.Elapsed;

            _logger.LogInformation(
                "Enrichment completed: {ResultCount} results enriched, {RelatedItemsAdded} related items added in {Duration}ms",
                metrics.ResultsEnriched,
                metrics.RelatedItemsAdded,
                metrics.TotalDuration.TotalMilliseconds
            );

            return new EnrichmentResults
            {
                Results = enrichedResults,
                Metrics = metrics,
                WasEnrichmentPerformed = true,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Enrichment timed out after {Timeout}", options.EnrichmentTimeout);
            totalStopwatch.Stop();
            metrics.HasFailures = true;
            metrics.Errors.Add($"Enrichment timed out after {options.EnrichmentTimeout}");
            metrics.TotalDuration = totalStopwatch.Elapsed;

            if (!options.EnableGracefulFallback)
            {
                throw;
            }

            return CreateFallbackResults(results, metrics, totalStopwatch.Elapsed, "Enrichment timed out");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Enrichment failed");
            totalStopwatch.Stop();
            metrics.HasFailures = true;
            metrics.Errors.Add($"Enrichment failed: {ex.Message}");
            metrics.TotalDuration = totalStopwatch.Elapsed;

            if (!options.EnableGracefulFallback)
            {
                throw;
            }

            return CreateFallbackResults(results, metrics, totalStopwatch.Elapsed, $"Enrichment failed: {ex.Message}");
        }
    }

    private async Task<List<EnrichedSearchResult>> PerformEnrichmentAsync(
        List<UnifiedSearchResult> results,
        SessionContext sessionContext,
        EnrichmentOptions options,
        EnrichmentMetrics metrics,
        CancellationToken cancellationToken
    )
    {
        var enrichedResults = new List<EnrichedSearchResult>();

        foreach (var result in results)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var enrichedResult = new EnrichedSearchResult
            {
                Type = result.Type,
                Id = result.Id,
                Content = result.Content,
                SecondaryContent = result.SecondaryContent,
                Score = result.Score,
                Source = result.Source,
                CreatedAt = result.CreatedAt,
                Confidence = result.Confidence,
                Metadata = result.Metadata,
                OriginalMemory = result.OriginalMemory,
                OriginalEntity = result.OriginalEntity,
                OriginalRelationship = result.OriginalRelationship,
            };

            // Enrich based on result type
            var wasEnriched = result.Type switch
            {
                UnifiedResultType.Memory => await EnrichMemoryResultAsync(
                    enrichedResult,
                    sessionContext,
                    options,
                    metrics,
                    cancellationToken
                ),
                UnifiedResultType.Entity => await EnrichEntityResultAsync(
                    enrichedResult,
                    sessionContext,
                    options,
                    metrics,
                    cancellationToken
                ),
                UnifiedResultType.Relationship => await EnrichRelationshipResultAsync(
                    enrichedResult,
                    sessionContext,
                    options,
                    metrics,
                    cancellationToken
                ),
                _ => throw new NotSupportedException($"Unsupported result type: {result.Type}"),
            };

            if (wasEnriched)
            {
                metrics.ResultsEnriched++;
            }

            enrichedResults.Add(enrichedResult);
        }

        return enrichedResults;
    }

    private Task<bool> EnrichMemoryResultAsync(
        EnrichedSearchResult result,
        SessionContext sessionContext,
        EnrichmentOptions options,
        EnrichmentMetrics metrics,
        CancellationToken cancellationToken
    )
    {
        if (result.OriginalMemory == null)
        {
            return Task.FromResult(false);
        }

        var relationshipStopwatch = Stopwatch.StartNew();
        var wasEnriched = false;

        try
        {
            // Note: Memory enrichment with related entities and relationships would require
            // additional repository calls to get entities/relationships extracted from memories.
            // For now, we'll provide basic enrichment based on memory metadata and content.
            // This can be enhanced when memory-graph linkage is more established.

            // Generate relevance explanation
            if (options.GenerateRelevanceExplanations)
            {
                result.RelevanceExplanation = GenerateMemoryRelevanceExplanation(result);
                wasEnriched = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enrich memory result {MemoryId}", result.Id);
            metrics.Errors.Add($"Memory enrichment failed for ID {result.Id}: {ex.Message}");
        }
        finally
        {
            relationshipStopwatch.Stop();
            metrics.RelationshipDiscoveryDuration += relationshipStopwatch.Elapsed;
        }

        return Task.FromResult(wasEnriched);
    }

    private async Task<bool> EnrichEntityResultAsync(
        EnrichedSearchResult result,
        SessionContext sessionContext,
        EnrichmentOptions options,
        EnrichmentMetrics metrics,
        CancellationToken cancellationToken
    )
    {
        if (result.OriginalEntity == null)
        {
            return false;
        }

        var contextStopwatch = Stopwatch.StartNew();
        var wasEnriched = false;

        try
        {
            // Find relationships involving this entity
            var relationships = await _graphRepository.GetRelationshipsForEntityAsync(
                result.OriginalEntity.Name,
                sessionContext,
                null,
                cancellationToken
            );

            if (relationships?.Any() == true)
            {
                var topRelationships = relationships
                    .Where(r => r.Confidence >= options.MinRelevanceScore)
                    .OrderByDescending(r => r.Confidence)
                    .Take(Math.Min(options.MaxRelatedItems, 2))
                    .ToList();

                foreach (var relationship in topRelationships)
                {
                    result.RelatedRelationships.Add(
                        new RelatedItem
                        {
                            Type = UnifiedResultType.Relationship,
                            Id = relationship.Id,
                            Content = $"{relationship.Source} {relationship.RelationshipType} {relationship.Target}",
                            RelevanceScore = relationship.Confidence,
                            Confidence = relationship.Confidence,
                            RelationshipExplanation = "Direct relationship involving this entity",
                        }
                    );

                    metrics.RelatedItemsAdded++;
                    wasEnriched = true;
                }
            }

            // Generate relevance explanation
            if (options.GenerateRelevanceExplanations)
            {
                result.RelevanceExplanation = GenerateEntityRelevanceExplanation(result);
                wasEnriched = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enrich entity result {EntityId}", result.Id);
            metrics.Errors.Add($"Entity enrichment failed for ID {result.Id}: {ex.Message}");

            // Still generate relevance explanation even if repository calls fail
            if (options.GenerateRelevanceExplanations)
            {
                result.RelevanceExplanation = GenerateEntityRelevanceExplanation(result);
                wasEnriched = true;
            }
        }
        finally
        {
            contextStopwatch.Stop();
            metrics.ContextAnalysisDuration += contextStopwatch.Elapsed;
        }

        return wasEnriched;
    }

    private async Task<bool> EnrichRelationshipResultAsync(
        EnrichedSearchResult result,
        SessionContext sessionContext,
        EnrichmentOptions options,
        EnrichmentMetrics metrics,
        CancellationToken cancellationToken
    )
    {
        if (result.OriginalRelationship == null)
        {
            return false;
        }

        var contextStopwatch = Stopwatch.StartNew();
        var wasEnriched = false;

        try
        {
            // Find entities involved in this relationship
            var sourceEntity = await _graphRepository.GetEntityByNameAsync(
                result.OriginalRelationship.Source,
                sessionContext,
                cancellationToken
            );
            var targetEntity = await _graphRepository.GetEntityByNameAsync(
                result.OriginalRelationship.Target,
                sessionContext,
                cancellationToken
            );

            var relatedEntities = new List<Entity>();
            if (sourceEntity != null)
            {
                relatedEntities.Add(sourceEntity);
            }

            if (targetEntity != null)
            {
                relatedEntities.Add(targetEntity);
            }

            var topEntities = relatedEntities
                .Where(e => e.Confidence >= options.MinRelevanceScore)
                .OrderByDescending(e => e.Confidence)
                .Take(Math.Min(options.MaxRelatedItems, 2))
                .ToList();

            foreach (var entity in topEntities)
            {
                result.RelatedEntities.Add(
                    new RelatedItem
                    {
                        Type = UnifiedResultType.Entity,
                        Id = entity.Id,
                        Content = entity.Name,
                        RelevanceScore = entity.Confidence,
                        Confidence = entity.Confidence,
                        RelationshipExplanation = "Entity involved in this relationship",
                    }
                );

                metrics.RelatedItemsAdded++;
                wasEnriched = true;
            }

            // Generate relevance explanation
            if (options.GenerateRelevanceExplanations)
            {
                result.RelevanceExplanation = GenerateRelationshipRelevanceExplanation(result);
                wasEnriched = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enrich relationship result {RelationshipId}", result.Id);
            metrics.Errors.Add($"Relationship enrichment failed for ID {result.Id}: {ex.Message}");

            // Still generate relevance explanation even if repository calls fail
            if (options.GenerateRelevanceExplanations)
            {
                result.RelevanceExplanation = GenerateRelationshipRelevanceExplanation(result);
                wasEnriched = true;
            }
        }
        finally
        {
            contextStopwatch.Stop();
            metrics.ContextAnalysisDuration += contextStopwatch.Elapsed;
        }

        return wasEnriched;
    }

    private static string GenerateMemoryRelevanceExplanation(EnrichedSearchResult result)
    {
        var explanations = new List<string>();

        if (result.RelatedEntities.Count != 0)
        {
            explanations.Add($"Contains {result.RelatedEntities.Count} related entities");
        }

        if (result.RelatedRelationships.Count != 0)
        {
            explanations.Add($"Contains {result.RelatedRelationships.Count} relationships");
        }

        if (result.Score > 0.8f)
        {
            explanations.Add("High relevance match");
        }

        return explanations.Count != 0
            ? $"Memory relevant because: {string.Join(", ", explanations)}"
            : "Memory matches search criteria";
    }

    private static string GenerateEntityRelevanceExplanation(EnrichedSearchResult result)
    {
        var explanations = new List<string>();

        if (result.RelatedRelationships.Count != 0)
        {
            explanations.Add($"Connected through {result.RelatedRelationships.Count} relationships");
        }

        if (result.Confidence.HasValue && result.Confidence.Value > 0.8f)
        {
            explanations.Add("High confidence entity");
        }

        return explanations.Count != 0
            ? $"Entity relevant because: {string.Join(", ", explanations)}"
            : "Entity matches search criteria";
    }

    private static string GenerateRelationshipRelevanceExplanation(EnrichedSearchResult result)
    {
        var explanations = new List<string>();

        if (result.RelatedEntities.Count != 0)
        {
            explanations.Add($"Involves {result.RelatedEntities.Count} relevant entities");
        }

        if (result.Confidence.HasValue && result.Confidence.Value > 0.8f)
        {
            explanations.Add("High confidence relationship");
        }

        return explanations.Count != 0
            ? $"Relationship relevant because: {string.Join(", ", explanations)}"
            : "Relationship matches search criteria";
    }

    private static EnrichmentResults CreateFallbackResults(
        List<UnifiedSearchResult> results,
        EnrichmentMetrics metrics,
        TimeSpan duration,
        string reason
    )
    {
        metrics.TotalDuration = duration;

        // Convert UnifiedSearchResult to EnrichedSearchResult without enrichment
        var enrichedResults = results
            .Select(r => new EnrichedSearchResult
            {
                Type = r.Type,
                Id = r.Id,
                Content = r.Content,
                SecondaryContent = r.SecondaryContent,
                Score = r.Score,
                Source = r.Source,
                CreatedAt = r.CreatedAt,
                Confidence = r.Confidence,
                Metadata = r.Metadata,
                OriginalMemory = r.OriginalMemory,
                OriginalEntity = r.OriginalEntity,
                OriginalRelationship = r.OriginalRelationship,
            })
            .ToList();

        return new EnrichmentResults
        {
            Results = enrichedResults,
            Metrics = metrics,
            WasEnrichmentPerformed = false,
            FallbackReason = reason,
        };
    }
}
