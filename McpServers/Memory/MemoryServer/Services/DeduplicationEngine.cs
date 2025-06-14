using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MemoryServer.Models;
using System.Diagnostics;

namespace MemoryServer.Services;

/// <summary>
/// Engine for intelligent deduplication of search results while preserving valuable context
/// </summary>
public class DeduplicationEngine : IDeduplicationEngine
{
    private readonly ILogger<DeduplicationEngine> _logger;
    private readonly DeduplicationOptions _options;

    public DeduplicationEngine(
        IOptions<MemoryServerOptions> options,
        ILogger<DeduplicationEngine> logger)
    {
        _options = options.Value.Deduplication;
        _logger = logger;
        
        _logger.LogInformation("DeduplicationEngine initialized with similarity threshold {SimilarityThreshold}", 
            _options.SimilarityThreshold);
    }

    public bool IsDeduplicationAvailable()
    {
        return _options.EnableDeduplication;
    }

    public async Task<DeduplicationResults> DeduplicateResultsAsync(
        List<UnifiedSearchResult> results,
        SessionContext sessionContext,
        DeduplicationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (results == null)
            throw new ArgumentNullException(nameof(results));

        var effectiveOptions = options ?? _options;
        var totalStopwatch = Stopwatch.StartNew();
        var metrics = new DeduplicationMetrics();

        _logger.LogDebug("Starting deduplication for {ResultCount} results", results.Count);

        try
        {
            // If deduplication is disabled or insufficient results, return original results
            if (!effectiveOptions.EnableDeduplication || results.Count <= 1)
            {
                _logger.LogDebug("Early exit - EnableDeduplication={EnableDeduplication}, ResultCount={ResultCount}",
                    effectiveOptions.EnableDeduplication, results.Count);
                return CreateFallbackResults(results, metrics, totalStopwatch.Elapsed, 
                    "Deduplication disabled or insufficient results");
            }

            // Perform deduplication with timeout protection
            using var timeoutCts = new CancellationTokenSource(effectiveOptions.DeduplicationTimeout);
            using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var deduplicatedResults = await PerformDeduplicationAsync(results, effectiveOptions, metrics, combinedCts.Token);
            
            totalStopwatch.Stop();
            metrics.TotalDuration = totalStopwatch.Elapsed;

            _logger.LogInformation("Deduplication completed: {OriginalCount} → {FinalCount} results, {DuplicatesRemoved} duplicates removed, {DuplicatesPreserved} preserved in {Duration}ms",
                results.Count, deduplicatedResults.Count, metrics.DuplicatesRemoved, metrics.DuplicatesPreserved, metrics.TotalDuration.TotalMilliseconds);

            return new DeduplicationResults
            {
                Results = deduplicatedResults,
                Metrics = metrics,
                WasDeduplicationPerformed = true
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Re-throw user cancellation
        }
        catch (OperationCanceledException)
        {
            // Timeout occurred
            totalStopwatch.Stop();
            _logger.LogWarning("Deduplication timed out after {Timeout}ms", effectiveOptions.DeduplicationTimeout.TotalMilliseconds);
            return CreateFallbackResults(results, metrics, totalStopwatch.Elapsed, "Deduplication timeout");
        }
        catch (Exception ex)
        {
            totalStopwatch.Stop();
            _logger.LogError(ex, "Deduplication failed");
            metrics.HasFailures = true;
            metrics.Errors.Add($"Deduplication failed: {ex.Message}");
            metrics.TotalDuration = totalStopwatch.Elapsed;

            if (!effectiveOptions.EnableGracefulFallback)
                throw;

            return CreateFallbackResults(results, metrics, totalStopwatch.Elapsed, $"Deduplication failed: {ex.Message}");
        }
    }

    private async Task<List<UnifiedSearchResult>> PerformDeduplicationAsync(
        List<UnifiedSearchResult> results,
        DeduplicationOptions options,
        DeduplicationMetrics metrics,
        CancellationToken cancellationToken)
    {
        var duplicateGroups = new List<List<UnifiedSearchResult>>();
        var deduplicatedResults = new List<UnifiedSearchResult>();

        // Phase 1: Content Similarity Analysis
        var similarityStopwatch = Stopwatch.StartNew();
        await AnalyzeContentSimilarityAsync(results, duplicateGroups, options, cancellationToken);
        similarityStopwatch.Stop();
        metrics.SimilarityAnalysisDuration = similarityStopwatch.Elapsed;

        // Phase 2: Source Relationship Analysis (if enabled)
        var sourceStopwatch = Stopwatch.StartNew();
        if (options.EnableSourceRelationshipAnalysis)
        {
            await AnalyzeSourceRelationshipsAsync(results, duplicateGroups, options, cancellationToken);
        }
        sourceStopwatch.Stop();
        metrics.SourceAnalysisDuration = sourceStopwatch.Elapsed;

        // Phase 3: Context Preservation Logic
        var processedIds = new HashSet<int>();
        
        foreach (var group in duplicateGroups)
        {
            if (group.Count <= 1)
            {
                // Not a duplicate group, add all results
                foreach (var result in group.Where(r => !processedIds.Contains(r.Id)))
                {
                    deduplicatedResults.Add(result);
                    processedIds.Add(result.Id);
                }
                continue;
            }

            metrics.PotentialDuplicatesFound += group.Count - 1;

            // Apply context preservation logic
            var preservedResults = ApplyContextPreservationLogic(group, options);
            
            foreach (var result in preservedResults.Where(r => !processedIds.Contains(r.Id)))
            {
                deduplicatedResults.Add(result);
                processedIds.Add(result.Id);
            }

            // Mark ALL results in the duplicate group as processed to prevent them from being added back later
            foreach (var result in group.Where(r => !processedIds.Contains(r.Id)))
            {
                processedIds.Add(result.Id);
            }

            metrics.DuplicatesRemoved += group.Count - preservedResults.Count;
            metrics.DuplicatesPreserved += preservedResults.Count - 1; // -1 because one is the original
        }

        // Add any results that weren't part of any duplicate group
        var unprocessedResults = results.Where(r => !processedIds.Contains(r.Id)).ToList();
        
        foreach (var result in unprocessedResults)
        {
            deduplicatedResults.Add(result);
        }

        // Sort by original score to maintain ranking
        return deduplicatedResults.OrderByDescending(r => r.Score).ToList();
    }

    private async Task AnalyzeContentSimilarityAsync(
        List<UnifiedSearchResult> results,
        List<List<UnifiedSearchResult>> duplicateGroups,
        DeduplicationOptions options,
        CancellationToken cancellationToken)
    {
        var processedIds = new HashSet<int>();

        for (int i = 0; i < results.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (processedIds.Contains(results[i].Id))
                continue;

            var currentGroup = new List<UnifiedSearchResult> { results[i] };
            processedIds.Add(results[i].Id);

            // Compare with remaining results
            for (int j = i + 1; j < results.Count; j++)
            {
                if (processedIds.Contains(results[j].Id))
                    continue;

                var similarity = CalculateContentSimilarity(results[i], results[j]);
                
                if (similarity >= options.SimilarityThreshold)
                {
                    currentGroup.Add(results[j]);
                    processedIds.Add(results[j].Id);
                }
            }

            duplicateGroups.Add(currentGroup);
        }

        await Task.CompletedTask; // Make method async for future enhancements
    }

    private async Task AnalyzeSourceRelationshipsAsync(
        List<UnifiedSearchResult> results,
        List<List<UnifiedSearchResult>> duplicateGroups,
        DeduplicationOptions options,
        CancellationToken cancellationToken)
    {
        // Analyze memory-entity-relationship overlaps
        var memoryResults = results.Where(r => r.Type == UnifiedResultType.Memory).ToList();
        var entityResults = results.Where(r => r.Type == UnifiedResultType.Entity).ToList();
        var relationshipResults = results.Where(r => r.Type == UnifiedResultType.Relationship).ToList();

        // Note: Memory-entity-relationship overlap detection would require additional
        // repository calls to get entities/relationships extracted from memories.
        // For now, we'll focus on content similarity detection which is the primary
        // deduplication mechanism. Source relationship analysis can be enhanced
        // in future iterations when memory-graph linkage is more established.

        await Task.CompletedTask; // Make method async for future enhancements
    }

    private float CalculateContentSimilarity(UnifiedSearchResult result1, UnifiedSearchResult result2)
    {
        // Normalize content for comparison
        var content1 = NormalizeContent(result1.Content);
        var content2 = NormalizeContent(result2.Content);

        if (string.IsNullOrEmpty(content1) || string.IsNullOrEmpty(content2))
            return 0.0f;

        // Use Jaccard similarity for text comparison
        var words1 = content1.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var words2 = content2.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

        if (words1.Count == 0 && words2.Count == 0)
            return 1.0f;

        if (words1.Count == 0 || words2.Count == 0)
            return 0.0f;

        var intersection = words1.Intersect(words2).Count();
        var union = words1.Union(words2).Count();
        var similarity = (float)intersection / union;

        return similarity;
    }

    private string NormalizeContent(string content)
    {
        if (string.IsNullOrEmpty(content))
            return string.Empty;

        // Remove extra whitespace, convert to lowercase, remove punctuation
        return System.Text.RegularExpressions.Regex.Replace(
            content.ToLowerInvariant().Trim(), 
            @"[^\w\s]", " ")
            .Replace("  ", " ");
    }

    private List<UnifiedSearchResult> ApplyContextPreservationLogic(
        List<UnifiedSearchResult> duplicateGroup,
        DeduplicationOptions options)
    {
        if (duplicateGroup.Count <= 1)
            return duplicateGroup;

        // Always keep the highest scoring result
        var sortedGroup = duplicateGroup.OrderByDescending(r => r.Score).ToList();
        var preservedResults = new List<UnifiedSearchResult> { sortedGroup[0] };

        // Apply hierarchical preference: Memory > Entity > Relationship
        var typePreference = new Dictionary<UnifiedResultType, int>
        {
            { UnifiedResultType.Memory, 3 },
            { UnifiedResultType.Entity, 2 },
            { UnifiedResultType.Relationship, 1 }
        };

        // Check if we should preserve additional results based on context
        if (options.PreserveComplementaryInfo)
        {
            foreach (var result in sortedGroup.Skip(1))
            {
                // Preserve if it provides complementary information
                if (ProvidesComplementaryInformation(preservedResults[0], result, options))
                {
                    preservedResults.Add(result);
                }
                // Or if it has higher type preference and similar score
                else if (typePreference[result.Type] > typePreference[preservedResults[0].Type] &&
                         result.Score >= preservedResults[0].Score * 0.9f)
                {
                    preservedResults.Insert(0, result); // Insert at beginning to maintain preference
                }
            }
        }

        return preservedResults;
    }

    private bool ProvidesComplementaryInformation(
        UnifiedSearchResult primary,
        UnifiedSearchResult candidate,
        DeduplicationOptions options)
    {
        // Different types often provide complementary information
        if (primary.Type != candidate.Type)
            return true;

        // Check if secondary content provides additional value
        if (!string.IsNullOrEmpty(candidate.SecondaryContent) && 
            string.IsNullOrEmpty(primary.SecondaryContent))
            return true;

        // Check if confidence levels are significantly different
        if (primary.Confidence.HasValue && candidate.Confidence.HasValue)
        {
            var confidenceDiff = Math.Abs(primary.Confidence.Value - candidate.Confidence.Value);
            if (confidenceDiff >= 0.2f) // Significant confidence difference
                return true;
        }

        // Check metadata for complementary information
        if (candidate.Metadata != null && primary.Metadata != null)
        {
            var candidateKeys = candidate.Metadata.Keys.ToHashSet();
            var primaryKeys = primary.Metadata.Keys.ToHashSet();
            
            // If candidate has unique metadata keys, it provides complementary info
            if (candidateKeys.Except(primaryKeys).Any())
                return true;
        }

        return false;
    }

    private DeduplicationResults CreateFallbackResults(
        List<UnifiedSearchResult> results,
        DeduplicationMetrics metrics,
        TimeSpan duration,
        string reason)
    {
        metrics.TotalDuration = duration;
        
        return new DeduplicationResults
        {
            Results = results,
            Metrics = metrics,
            WasDeduplicationPerformed = false,
            FallbackReason = reason
        };
    }
} 