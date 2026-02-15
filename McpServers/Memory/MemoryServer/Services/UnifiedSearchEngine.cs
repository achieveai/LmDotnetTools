using System.Diagnostics;
using MemoryServer.Models;

namespace MemoryServer.Services;

/// <summary>
///     Unified multi-source search engine that searches across memories, entities, and relationships.
///     Executes all 6 search operations in parallel (Memory FTS5/Vector, Entity FTS5/Vector, Relationship FTS5/Vector).
/// </summary>
public class UnifiedSearchEngine : IUnifiedSearchEngine
{
    private readonly IDeduplicationEngine _deduplicationEngine;
    private readonly IEmbeddingManager _embeddingManager;
    private readonly IGraphRepository _graphRepository;
    private readonly ILogger<UnifiedSearchEngine> _logger;
    private readonly IMemoryRepository _memoryRepository;
    private readonly IRerankingEngine _rerankingEngine;
    private readonly IResultEnricher _resultEnricher;

    public UnifiedSearchEngine(
        IMemoryRepository memoryRepository,
        IGraphRepository graphRepository,
        IEmbeddingManager embeddingManager,
        IRerankingEngine rerankingEngine,
        IDeduplicationEngine deduplicationEngine,
        IResultEnricher resultEnricher,
        ILogger<UnifiedSearchEngine> logger
    )
    {
        _memoryRepository = memoryRepository ?? throw new ArgumentNullException(nameof(memoryRepository));
        _graphRepository = graphRepository ?? throw new ArgumentNullException(nameof(graphRepository));
        _embeddingManager = embeddingManager ?? throw new ArgumentNullException(nameof(embeddingManager));
        _rerankingEngine = rerankingEngine ?? throw new ArgumentNullException(nameof(rerankingEngine));
        _deduplicationEngine = deduplicationEngine ?? throw new ArgumentNullException(nameof(deduplicationEngine));
        _resultEnricher = resultEnricher ?? throw new ArgumentNullException(nameof(resultEnricher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<UnifiedSearchResults> SearchAllSourcesAsync(
        string query,
        SessionContext sessionContext,
        UnifiedSearchOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("Query cannot be empty", nameof(query));
        }

        options ??= new UnifiedSearchOptions();
        var totalStopwatch = Stopwatch.StartNew();
        var metrics = new UnifiedSearchMetrics();

        _logger.LogDebug(
            "Starting unified search for query '{Query}' with options: FTS={EnableFts}, Vector={EnableVector}",
            query,
            options.EnableFtsSearch,
            options.EnableVectorSearch
        );

        try
        {
            // Generate embedding for vector searches if enabled
            float[]? queryEmbedding = null;
            if (options.EnableVectorSearch)
            {
                try
                {
                    queryEmbedding = await _embeddingManager.GenerateEmbeddingAsync(query, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to generate embedding for query '{Query}', vector search will be disabled",
                        query
                    );
                    metrics.Errors.Add($"Embedding generation failed: {ex.Message}");
                    if (!options.EnableGracefulFallback)
                    {
                        throw;
                    }
                }
            }

            return await SearchAllSourcesAsync(query, queryEmbedding, sessionContext, options, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unified search failed for query '{Query}'", query);
            metrics.HasFailures = true;
            metrics.Errors.Add($"Search failed: {ex.Message}");
            metrics.TotalDuration = totalStopwatch.Elapsed;

            if (!options.EnableGracefulFallback)
            {
                throw;
            }

            return new UnifiedSearchResults { Metrics = metrics };
        }
    }

    public async Task<UnifiedSearchResults> SearchAllSourcesAsync(
        string query,
        float[]? queryEmbedding,
        SessionContext sessionContext,
        UnifiedSearchOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("Query cannot be empty", nameof(query));
        }

        options ??= new UnifiedSearchOptions();
        var totalStopwatch = Stopwatch.StartNew();
        var metrics = new UnifiedSearchMetrics();

        var searchOperationCount = GetSearchOperationCount(options, queryEmbedding != null);
        _logger.LogDebug(
            "Starting unified search with {SearchCount} parallel operations. EnableFts: {EnableFts}, EnableVector: {EnableVector}, HasEmbedding: {HasEmbedding}",
            searchOperationCount,
            options.EnableFtsSearch,
            options.EnableVectorSearch,
            queryEmbedding != null
        );

        try
        {
            // Create tasks for all search operations
            var searchTasks = new List<Task>();
            var results = new List<UnifiedSearchResult>();

            // Memory searches
            if (options.EnableFtsSearch)
            {
                _logger.LogTrace("Adding memory FTS search task");
                searchTasks.Add(
                    ExecuteMemoryFtsSearchAsync(query, sessionContext, options, metrics, results, cancellationToken)
                );
            }

            if (options.EnableVectorSearch && queryEmbedding != null)
            {
                _logger.LogTrace("Adding memory vector search task");
                searchTasks.Add(
                    ExecuteMemoryVectorSearchAsync(
                        queryEmbedding,
                        sessionContext,
                        options,
                        metrics,
                        results,
                        cancellationToken
                    )
                );
            }

            // Entity searches
            if (options.EnableFtsSearch)
            {
                _logger.LogTrace("Adding entity FTS search task");
                searchTasks.Add(
                    ExecuteEntityFtsSearchAsync(query, sessionContext, options, metrics, results, cancellationToken)
                );
            }

            if (options.EnableVectorSearch && queryEmbedding != null)
            {
                _logger.LogTrace("Adding entity vector search task");
                searchTasks.Add(
                    ExecuteEntityVectorSearchAsync(
                        queryEmbedding,
                        sessionContext,
                        options,
                        metrics,
                        results,
                        cancellationToken
                    )
                );
            }

            // Relationship searches
            if (options.EnableFtsSearch)
            {
                _logger.LogTrace("Adding relationship FTS search task");
                searchTasks.Add(
                    ExecuteRelationshipFtsSearchAsync(
                        query,
                        sessionContext,
                        options,
                        metrics,
                        results,
                        cancellationToken
                    )
                );
            }

            if (options.EnableVectorSearch && queryEmbedding != null)
            {
                _logger.LogTrace("Adding relationship vector search task");
                searchTasks.Add(
                    ExecuteRelationshipVectorSearchAsync(
                        queryEmbedding,
                        sessionContext,
                        options,
                        metrics,
                        results,
                        cancellationToken
                    )
                );
            }

            // Execute all searches in parallel with timeout
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(options.SearchTimeout);

            try
            {
                await Task.WhenAll(searchTasks);
                _logger.LogTrace(
                    "All {TaskCount} search tasks completed. Total results: {ResultCount}",
                    searchTasks.Count,
                    results.Count
                );
            }
            catch (OperationCanceledException)
                when (timeoutCts.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Unified search timed out after {Timeout}", options.SearchTimeout);
                metrics.HasFailures = true;
                metrics.Errors.Add($"Search timed out after {options.SearchTimeout}");
            }

            // Apply type weights to results
            ApplyTypeWeights(results, options.TypeWeights);
            _logger.LogTrace("Applied type weights. Results after weighting: {ResultCount}", results.Count);

            // Apply reranking BEFORE result cutoffs (Phase 7 requirement)
            var finalResults = results;
            if (results.Count > 0)
            {
                _logger.LogTrace("Starting reranking with {ResultCount} results", results.Count);
                try
                {
                    var rerankingResults = await _rerankingEngine.RerankResultsAsync(
                        query,
                        results,
                        sessionContext,
                        null,
                        cancellationToken
                    );
                    finalResults = rerankingResults.Results;

                    // Update metrics with reranking information
                    metrics.RerankingDuration = rerankingResults.Metrics.TotalDuration;
                    metrics.WasReranked = rerankingResults.WasReranked;
                    metrics.RerankingPositionChanges = rerankingResults.Metrics.PositionChanges;

                    if (rerankingResults.Metrics.HasFailures)
                    {
                        metrics.Errors.AddRange(rerankingResults.Metrics.Errors);
                    }

                    _logger.LogDebug(
                        "Reranking completed: {WasReranked}, {PositionChanges} position changes in {Duration}ms",
                        rerankingResults.WasReranked,
                        rerankingResults.Metrics.PositionChanges,
                        rerankingResults.Metrics.TotalDuration.TotalMilliseconds
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Reranking failed, using original results");
                    metrics.Errors.Add($"Reranking failed: {ex.Message}");
                    // Continue with original results
                }
            }

            // Apply deduplication AFTER reranking but BEFORE final cutoffs (Phase 8)
            if (finalResults.Count > 0)
            {
                _logger.LogTrace("Starting deduplication with {ResultCount} results", finalResults.Count);
                try
                {
                    var deduplicationResults = await _deduplicationEngine.DeduplicateResultsAsync(
                        finalResults,
                        sessionContext,
                        null,
                        cancellationToken
                    );
                    finalResults = deduplicationResults.Results;

                    // Update metrics with deduplication information
                    metrics.DeduplicationDuration = deduplicationResults.Metrics.TotalDuration;
                    metrics.DuplicatesRemoved = deduplicationResults.Metrics.DuplicatesRemoved;

                    if (deduplicationResults.Metrics.HasFailures)
                    {
                        metrics.Errors.AddRange(deduplicationResults.Metrics.Errors);
                    }

                    _logger.LogDebug(
                        "Deduplication completed: {DuplicatesRemoved} duplicates removed in {Duration}ms",
                        deduplicationResults.Metrics.DuplicatesRemoved,
                        deduplicationResults.Metrics.TotalDuration.TotalMilliseconds
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Deduplication failed, using original results");
                    metrics.Errors.Add($"Deduplication failed: {ex.Message}");
                    // Continue with original results
                }
            }

            // Apply enrichment as final step in search pipeline (Phase 8)
            List<EnrichedSearchResult> enrichedResults = [];
            if (finalResults.Count > 0)
            {
                _logger.LogTrace("Starting enrichment with {ResultCount} results", finalResults.Count);
                try
                {
                    var enrichmentResults = await _resultEnricher.EnrichResultsAsync(
                        finalResults,
                        sessionContext,
                        null,
                        cancellationToken
                    );
                    enrichedResults = enrichmentResults.Results;

                    // Update metrics with enrichment information
                    metrics.EnrichmentDuration = enrichmentResults.Metrics.TotalDuration;
                    metrics.ItemsEnriched = enrichmentResults.Metrics.ResultsEnriched;

                    if (enrichmentResults.Metrics.HasFailures)
                    {
                        metrics.Errors.AddRange(enrichmentResults.Metrics.Errors);
                    }

                    _logger.LogDebug(
                        "Enrichment completed: {ItemsEnriched} results enriched, {RelatedItemsAdded} related items added in {Duration}ms",
                        enrichmentResults.Metrics.ResultsEnriched,
                        enrichmentResults.Metrics.RelatedItemsAdded,
                        enrichmentResults.Metrics.TotalDuration.TotalMilliseconds
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Enrichment failed, using original results");
                    metrics.Errors.Add($"Enrichment failed: {ex.Message}");
                    // Convert to enriched results without enrichment
                    enrichedResults =
                    [
                        .. finalResults.Select(r => new EnrichedSearchResult
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
                        }),
                    ];
                }
            }

            totalStopwatch.Stop();
            metrics.TotalDuration = totalStopwatch.Elapsed;

            // Sort final results by score
            var sortedResults = enrichedResults.OrderByDescending(r => r.Score).ToList();

            _logger.LogInformation(
                "Unified search completed: {TotalResults} results ({MemoryCount} memories, {EntityCount} entities, {RelationshipCount} relationships) in {Duration}ms",
                sortedResults.Count,
                sortedResults.Count(r => r.Type == UnifiedResultType.Memory),
                sortedResults.Count(r => r.Type == UnifiedResultType.Entity),
                sortedResults.Count(r => r.Type == UnifiedResultType.Relationship),
                metrics.TotalDuration.TotalMilliseconds
            );

            return new UnifiedSearchResults { Results = [.. sortedResults], Metrics = metrics };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unified search failed for query '{Query}'", query);
            totalStopwatch.Stop();
            metrics.HasFailures = true;
            metrics.Errors.Add($"Search failed: {ex.Message}");
            metrics.TotalDuration = totalStopwatch.Elapsed;

            if (!options.EnableGracefulFallback)
            {
                throw;
            }

            return new UnifiedSearchResults { Metrics = metrics };
        }
    }

    private async Task ExecuteMemoryFtsSearchAsync(
        string query,
        SessionContext sessionContext,
        UnifiedSearchOptions options,
        UnifiedSearchMetrics metrics,
        List<UnifiedSearchResult> results,
        CancellationToken cancellationToken
    )
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var memories = await _memoryRepository.SearchAsync(
                query,
                sessionContext,
                options.MaxResultsPerSource,
                0.0f,
                cancellationToken
            );

            lock (results)
            {
                foreach (var memory in memories)
                {
                    results.Add(
                        new UnifiedSearchResult
                        {
                            Type = UnifiedResultType.Memory,
                            Id = memory.Id,
                            Content = memory.Content,
                            Score = 1.0f, // FTS5 doesn't provide scores, use default
                            Source = "FTS5",
                            CreatedAt = memory.CreatedAt,
                            Metadata = memory.Metadata,
                            OriginalMemory = memory,
                        }
                    );
                }

                metrics.MemoryFtsResultCount = memories.Count;
            }

            _logger.LogDebug(
                "Memory FTS5 search returned {Count} results in {Duration}ms",
                memories.Count,
                stopwatch.ElapsedMilliseconds
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Memory FTS5 search failed");
            metrics.HasFailures = true;
            metrics.Errors.Add($"Memory FTS5 search failed: {ex.Message}");
        }
        finally
        {
            stopwatch.Stop();
            metrics.MemoryFtsSearchDuration = stopwatch.Elapsed;
        }
    }

    private async Task ExecuteMemoryVectorSearchAsync(
        float[] queryEmbedding,
        SessionContext sessionContext,
        UnifiedSearchOptions options,
        UnifiedSearchMetrics metrics,
        List<UnifiedSearchResult> results,
        CancellationToken cancellationToken
    )
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var vectorResults = await _memoryRepository.SearchVectorAsync(
                queryEmbedding,
                sessionContext,
                options.MaxResultsPerSource,
                options.VectorSimilarityThreshold,
                cancellationToken
            );

            lock (results)
            {
                foreach (var vectorResult in vectorResults)
                {
                    results.Add(
                        new UnifiedSearchResult
                        {
                            Type = UnifiedResultType.Memory,
                            Id = vectorResult.Memory.Id,
                            Content = vectorResult.Memory.Content,
                            Score = vectorResult.Score,
                            Source = "Vector",
                            CreatedAt = vectorResult.Memory.CreatedAt,
                            Metadata = vectorResult.Memory.Metadata,
                            OriginalMemory = vectorResult.Memory,
                        }
                    );
                }

                metrics.MemoryVectorResultCount = vectorResults.Count;
            }

            _logger.LogDebug(
                "Memory vector search returned {Count} results in {Duration}ms",
                vectorResults.Count,
                stopwatch.ElapsedMilliseconds
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Memory vector search failed");
            metrics.HasFailures = true;
            metrics.Errors.Add($"Memory vector search failed: {ex.Message}");
        }
        finally
        {
            stopwatch.Stop();
            metrics.MemoryVectorSearchDuration = stopwatch.Elapsed;
        }
    }

    private async Task ExecuteEntityFtsSearchAsync(
        string query,
        SessionContext sessionContext,
        UnifiedSearchOptions options,
        UnifiedSearchMetrics metrics,
        List<UnifiedSearchResult> results,
        CancellationToken cancellationToken
    )
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var entities = await _graphRepository.SearchEntitiesAsync(
                query,
                sessionContext,
                options.MaxResultsPerSource,
                cancellationToken
            );

            lock (results)
            {
                foreach (var entity in entities)
                {
                    results.Add(
                        new UnifiedSearchResult
                        {
                            Type = UnifiedResultType.Entity,
                            Id = entity.Id,
                            Content = entity.Name,
                            SecondaryContent = entity.Type,
                            Score = 1.0f, // FTS5 doesn't provide scores, use default
                            Source = "FTS5",
                            CreatedAt = entity.CreatedAt,
                            Confidence = entity.Confidence,
                            Metadata = entity.Metadata,
                            OriginalEntity = entity,
                        }
                    );
                }

                metrics.EntityFtsResultCount = entities.Count();
            }

            _logger.LogDebug(
                "Entity FTS5 search returned {Count} results in {Duration}ms",
                entities.Count(),
                stopwatch.ElapsedMilliseconds
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Entity FTS5 search failed");
            metrics.HasFailures = true;
            metrics.Errors.Add($"Entity FTS5 search failed: {ex.Message}");
        }
        finally
        {
            stopwatch.Stop();
            metrics.EntityFtsSearchDuration = stopwatch.Elapsed;
        }
    }

    private async Task ExecuteEntityVectorSearchAsync(
        float[] queryEmbedding,
        SessionContext sessionContext,
        UnifiedSearchOptions options,
        UnifiedSearchMetrics metrics,
        List<UnifiedSearchResult> results,
        CancellationToken cancellationToken
    )
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var vectorResults = await _graphRepository.SearchEntitiesVectorAsync(
                queryEmbedding,
                sessionContext,
                options.MaxResultsPerSource,
                options.VectorSimilarityThreshold,
                cancellationToken
            );

            lock (results)
            {
                foreach (var vectorResult in vectorResults)
                {
                    results.Add(
                        new UnifiedSearchResult
                        {
                            Type = UnifiedResultType.Entity,
                            Id = vectorResult.Entity.Id,
                            Content = vectorResult.Entity.Name,
                            SecondaryContent = vectorResult.Entity.Type,
                            Score = vectorResult.Score,
                            Source = "Vector",
                            CreatedAt = vectorResult.Entity.CreatedAt,
                            Confidence = vectorResult.Entity.Confidence,
                            Metadata = vectorResult.Entity.Metadata,
                            OriginalEntity = vectorResult.Entity,
                        }
                    );
                }

                metrics.EntityVectorResultCount = vectorResults.Count;
            }

            _logger.LogDebug(
                "Entity vector search returned {Count} results in {Duration}ms",
                vectorResults.Count,
                stopwatch.ElapsedMilliseconds
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Entity vector search failed");
            metrics.HasFailures = true;
            metrics.Errors.Add($"Entity vector search failed: {ex.Message}");
        }
        finally
        {
            stopwatch.Stop();
            metrics.EntityVectorSearchDuration = stopwatch.Elapsed;
        }
    }

    private async Task ExecuteRelationshipFtsSearchAsync(
        string query,
        SessionContext sessionContext,
        UnifiedSearchOptions options,
        UnifiedSearchMetrics metrics,
        List<UnifiedSearchResult> results,
        CancellationToken cancellationToken
    )
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var relationships = await _graphRepository.SearchRelationshipsAsync(
                query,
                sessionContext,
                options.MaxResultsPerSource,
                cancellationToken
            );

            lock (results)
            {
                foreach (var relationship in relationships)
                {
                    results.Add(
                        new UnifiedSearchResult
                        {
                            Type = UnifiedResultType.Relationship,
                            Id = relationship.Id,
                            Content = $"{relationship.Source} {relationship.RelationshipType} {relationship.Target}",
                            SecondaryContent = relationship.TemporalContext,
                            Score = 1.0f, // FTS5 doesn't provide scores, use default
                            Source = "FTS5",
                            CreatedAt = relationship.CreatedAt,
                            Confidence = relationship.Confidence,
                            Metadata = relationship.Metadata,
                            OriginalRelationship = relationship,
                        }
                    );
                }

                metrics.RelationshipFtsResultCount = relationships.Count();
            }

            _logger.LogDebug(
                "Relationship FTS5 search returned {Count} results in {Duration}ms",
                relationships.Count(),
                stopwatch.ElapsedMilliseconds
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Relationship FTS5 search failed");
            metrics.HasFailures = true;
            metrics.Errors.Add($"Relationship FTS5 search failed: {ex.Message}");
        }
        finally
        {
            stopwatch.Stop();
            metrics.RelationshipFtsSearchDuration = stopwatch.Elapsed;
        }
    }

    private async Task ExecuteRelationshipVectorSearchAsync(
        float[] queryEmbedding,
        SessionContext sessionContext,
        UnifiedSearchOptions options,
        UnifiedSearchMetrics metrics,
        List<UnifiedSearchResult> results,
        CancellationToken cancellationToken
    )
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var vectorResults = await _graphRepository.SearchRelationshipsVectorAsync(
                queryEmbedding,
                sessionContext,
                options.MaxResultsPerSource,
                options.VectorSimilarityThreshold,
                cancellationToken
            );

            lock (results)
            {
                foreach (var vectorResult in vectorResults)
                {
                    results.Add(
                        new UnifiedSearchResult
                        {
                            Type = UnifiedResultType.Relationship,
                            Id = vectorResult.Relationship.Id,
                            Content =
                                $"{vectorResult.Relationship.Source} {vectorResult.Relationship.RelationshipType} {vectorResult.Relationship.Target}",
                            SecondaryContent = vectorResult.Relationship.TemporalContext,
                            Score = vectorResult.Score,
                            Source = "Vector",
                            CreatedAt = vectorResult.Relationship.CreatedAt,
                            Confidence = vectorResult.Relationship.Confidence,
                            Metadata = vectorResult.Relationship.Metadata,
                            OriginalRelationship = vectorResult.Relationship,
                        }
                    );
                }

                metrics.RelationshipVectorResultCount = vectorResults.Count;
            }

            _logger.LogDebug(
                "Relationship vector search returned {Count} results in {Duration}ms",
                vectorResults.Count,
                stopwatch.ElapsedMilliseconds
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Relationship vector search failed");
            metrics.HasFailures = true;
            metrics.Errors.Add($"Relationship vector search failed: {ex.Message}");
        }
        finally
        {
            stopwatch.Stop();
            metrics.RelationshipVectorSearchDuration = stopwatch.Elapsed;
        }
    }

    private static void ApplyTypeWeights(
        List<UnifiedSearchResult> results,
        Dictionary<UnifiedResultType, float> typeWeights
    )
    {
        foreach (var result in results)
        {
            if (typeWeights.TryGetValue(result.Type, out var weight))
            {
                result.Score *= weight;
            }
        }
    }

    private static int GetSearchOperationCount(UnifiedSearchOptions options, bool hasEmbedding)
    {
        var count = 0;
        if (options.EnableFtsSearch)
        {
            count += 3; // Memory, Entity, Relationship FTS5
        }

        if (options.EnableVectorSearch && hasEmbedding)
        {
            count += 3; // Memory, Entity, Relationship Vector
        }

        return count;
    }
}
