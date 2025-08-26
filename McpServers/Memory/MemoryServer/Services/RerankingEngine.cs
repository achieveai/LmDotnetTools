using System.Diagnostics;
using AchieveAi.LmDotnetTools.LmEmbeddings.Core;
using AchieveAi.LmDotnetTools.LmEmbeddings.Models;
using MemoryServer.Models;
using Microsoft.Extensions.Options;

namespace MemoryServer.Services;

/// <summary>
/// Intelligent reranking engine that applies semantic reranking to unified search results.
/// Integrates with LmEmbeddings RerankingService and provides multi-dimensional scoring.
/// </summary>
public class RerankingEngine : IRerankingEngine
{
    private readonly ILogger<RerankingEngine> _logger;
    private readonly MemoryServer.Models.RerankingOptions _options;
    private readonly RerankingService? _rerankingService;

    public RerankingEngine(IOptions<MemoryServerOptions> options, ILogger<RerankingEngine> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options?.Value?.Reranking ?? throw new ArgumentNullException(nameof(options));

        // Initialize reranking service if API key is available
        if (!string.IsNullOrEmpty(_options.ApiKey) && !_options.ApiKey.StartsWith("${"))
        {
            try
            {
                var serviceOptions =
                    new AchieveAi.LmDotnetTools.LmEmbeddings.Models.RerankingOptions
                    {
                        ApiKey = _options.ApiKey,
                        BaseUrl = _options.RerankingEndpoint,
                        DefaultModel = _options.RerankingModel,
                    };

                _rerankingService = new RerankingService(serviceOptions);

                _logger.LogInformation(
                    "RerankingEngine initialized with {Model} model",
                    _options.RerankingModel
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to initialize reranking service. Falling back to local scoring."
                );
                _rerankingService = null;
            }
        }
        else
        {
            _logger.LogInformation(
                "Reranking service not configured (API key not provided). Using local scoring only."
            );
            _rerankingService = null;
        }
    }

    public bool IsRerankingAvailable()
    {
        return _rerankingService != null && _options.EnableReranking;
    }

    public async Task<RerankingResults> RerankResultsAsync(
        string query,
        List<UnifiedSearchResult> results,
        SessionContext sessionContext,
        MemoryServer.Models.RerankingOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Query cannot be empty", nameof(query));

        if (results == null)
            throw new ArgumentNullException(nameof(results));

        options ??= _options;
        var totalStopwatch = Stopwatch.StartNew();
        var metrics = new RerankingMetrics();

        _logger.LogDebug(
            "Starting reranking for {ResultCount} results with query '{Query}'",
            results.Count,
            query
        );

        try
        {
            // If reranking is disabled or no results, return original results
            if (!options.EnableReranking || results.Count == 0)
            {
                return CreateFallbackResults(
                    results,
                    metrics,
                    totalStopwatch.Elapsed,
                    "Reranking disabled or no results"
                );
            }

            // Limit candidates to manage API costs
            var candidates = results.Take(options.MaxCandidates).ToList();
            metrics.CandidateCount = candidates.Count;

            // Store original positions for position change tracking
            var originalPositions = candidates
                .Select((r, i) => new { Result = r, Position = i })
                .ToDictionary(x => x.Result.Id, x => x.Position);

            // Attempt semantic reranking if service is available
            List<UnifiedSearchResult> rerankedResults;
            bool wasReranked = false;
            string? fallbackReason = null;

            if (IsRerankingAvailable())
            {
                try
                {
                    rerankedResults = await PerformSemanticRerankingAsync(
                        query,
                        candidates,
                        metrics,
                        cancellationToken
                    );
                    wasReranked = true;
                    _logger.LogDebug("Semantic reranking completed successfully");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Semantic reranking failed, falling back to local scoring"
                    );
                    metrics.HasFailures = true;
                    metrics.Errors.Add($"Semantic reranking failed: {ex.Message}");

                    if (!options.EnableGracefulFallback)
                        throw;

                    rerankedResults = await PerformLocalScoringAsync(
                        query,
                        candidates,
                        metrics,
                        cancellationToken
                    );
                    fallbackReason = "Semantic reranking failed";
                }
            }
            else
            {
                rerankedResults = await PerformLocalScoringAsync(
                    query,
                    candidates,
                    metrics,
                    cancellationToken
                );
                fallbackReason = "Semantic reranking service not available";
            }

            // Calculate position changes
            metrics.PositionChanges = CalculatePositionChanges(rerankedResults, originalPositions);

            // Calculate average score change
            metrics.AverageScoreChange = CalculateAverageScoreChange(results, rerankedResults);

            totalStopwatch.Stop();
            metrics.TotalDuration = totalStopwatch.Elapsed;
            metrics.RankedResultCount = rerankedResults.Count;

            _logger.LogInformation(
                "Reranking completed: {ResultCount} results, {PositionChanges} position changes, avg score change: {AvgScoreChange:F3} in {Duration}ms",
                rerankedResults.Count,
                metrics.PositionChanges,
                metrics.AverageScoreChange,
                metrics.TotalDuration.TotalMilliseconds
            );

            return new RerankingResults
            {
                Results = rerankedResults,
                Metrics = metrics,
                WasReranked = wasReranked,
                FallbackReason = fallbackReason,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reranking failed for query '{Query}'", query);
            totalStopwatch.Stop();
            metrics.HasFailures = true;
            metrics.Errors.Add($"Reranking failed: {ex.Message}");
            metrics.TotalDuration = totalStopwatch.Elapsed;

            if (!options.EnableGracefulFallback)
                throw;

            return CreateFallbackResults(
                results,
                metrics,
                totalStopwatch.Elapsed,
                $"Reranking failed: {ex.Message}"
            );
        }
    }

    private async Task<List<UnifiedSearchResult>> PerformSemanticRerankingAsync(
        string query,
        List<UnifiedSearchResult> candidates,
        RerankingMetrics metrics,
        CancellationToken cancellationToken
    )
    {
        if (_rerankingService == null)
            throw new InvalidOperationException("Reranking service not available");

        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Prepare documents for reranking
            var documents = candidates.Select(r => r.Content).ToList();

            // Call semantic reranking service
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken
            );
            timeoutCts.CancelAfter(_options.RerankingTimeout);

            var rankedDocuments = await _rerankingService.RerankAsync(
                query,
                documents,
                timeoutCts.Token
            );

            stopwatch.Stop();
            metrics.SemanticRerankingDuration = stopwatch.Elapsed;

            // Map ranked documents back to unified search results
            var rerankedResults = new List<UnifiedSearchResult>();

            foreach (var rankedDoc in rankedDocuments)
            {
                var originalResult = candidates[rankedDoc.Index];

                // Create new result with updated score
                var rerankedResult = new UnifiedSearchResult
                {
                    Type = originalResult.Type,
                    Id = originalResult.Id,
                    Content = originalResult.Content,
                    SecondaryContent = originalResult.SecondaryContent,
                    Score = rankedDoc.Score, // Use semantic relevance score
                    Source = $"{originalResult.Source}+Rerank",
                    CreatedAt = originalResult.CreatedAt,
                    Confidence = originalResult.Confidence,
                    Metadata = originalResult.Metadata,
                    OriginalMemory = originalResult.OriginalMemory,
                    OriginalEntity = originalResult.OriginalEntity,
                    OriginalRelationship = originalResult.OriginalRelationship,
                };

                // Apply multi-dimensional scoring
                ApplyMultiDimensionalScoring(rerankedResult, _options);

                rerankedResults.Add(rerankedResult);
            }

            return rerankedResults;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                $"Semantic reranking timed out after {_options.RerankingTimeout}"
            );
        }
    }

    private async Task<List<UnifiedSearchResult>> PerformLocalScoringAsync(
        string query,
        List<UnifiedSearchResult> candidates,
        RerankingMetrics metrics,
        CancellationToken cancellationToken
    )
    {
        var stopwatch = Stopwatch.StartNew();

        await Task.Yield(); // Make this method async for consistency

        // Apply local multi-dimensional scoring
        foreach (var result in candidates)
        {
            ApplyMultiDimensionalScoring(result, _options);
        }

        // Sort by updated scores
        var sortedResults = candidates.OrderByDescending(r => r.Score).ToList();

        stopwatch.Stop();
        metrics.LocalScoringDuration = stopwatch.Elapsed;

        return sortedResults;
    }

    private void ApplyMultiDimensionalScoring(
        UnifiedSearchResult result,
        MemoryServer.Models.RerankingOptions options
    )
    {
        var originalScore = result.Score;
        var newScore = originalScore;

        // Apply source weighting (hierarchical preference)
        if (options.SourceWeights.TryGetValue(result.Type, out var sourceWeight))
        {
            newScore *= sourceWeight;
        }

        // Apply recency boost if enabled
        if (options.EnableRecencyBoost)
        {
            var daysSinceCreation = (DateTime.UtcNow - result.CreatedAt).TotalDays;
            if (daysSinceCreation <= options.RecencyBoostDays)
            {
                var recencyBoost =
                    (1.0f - (float)(daysSinceCreation / options.RecencyBoostDays))
                    * options.RecencyWeight;
                newScore += recencyBoost;
            }
        }

        // Apply confidence boost for entities and relationships
        if (result.Confidence.HasValue && options.ConfidenceWeight > 0)
        {
            var confidenceBoost = result.Confidence.Value * options.ConfidenceWeight;
            newScore += confidenceBoost;
        }

        // Apply content quality scoring (simple heuristic based on content length and structure)
        if (options.ContentQualityWeight > 0)
        {
            var contentQuality = CalculateContentQuality(result.Content);
            var qualityBoost = contentQuality * options.ContentQualityWeight;
            newScore += qualityBoost;
        }

        result.Score = Math.Max(0, newScore); // Ensure score doesn't go negative
    }

    private float CalculateContentQuality(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return 0.0f;

        // Simple content quality heuristics
        var length = content.Length;
        var wordCount = content.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

        // Prefer content with reasonable length (not too short, not too long)
        var lengthScore = length switch
        {
            < 10 => 0.2f,
            < 50 => 0.5f,
            < 200 => 1.0f,
            < 1000 => 0.8f,
            _ => 0.6f,
        };

        // Prefer content with good word density
        var avgWordLength = wordCount > 0 ? (float)length / wordCount : 0;
        var wordDensityScore = avgWordLength switch
        {
            < 3 => 0.3f,
            < 8 => 1.0f,
            < 15 => 0.8f,
            _ => 0.5f,
        };

        return (lengthScore + wordDensityScore) / 2.0f;
    }

    private int CalculatePositionChanges(
        List<UnifiedSearchResult> rerankedResults,
        Dictionary<int, int> originalPositions
    )
    {
        var changes = 0;
        for (int i = 0; i < rerankedResults.Count; i++)
        {
            if (
                originalPositions.TryGetValue(rerankedResults[i].Id, out var originalPosition)
                && originalPosition != i
            )
            {
                changes++;
            }
        }
        return changes;
    }

    private float CalculateAverageScoreChange(
        List<UnifiedSearchResult> originalResults,
        List<UnifiedSearchResult> rerankedResults
    )
    {
        if (originalResults.Count == 0 || rerankedResults.Count == 0)
            return 0.0f;

        var originalScoreMap = originalResults.ToDictionary(r => r.Id, r => r.Score);
        var scoreChanges = new List<float>();

        foreach (var rerankedResult in rerankedResults)
        {
            if (originalScoreMap.TryGetValue(rerankedResult.Id, out var originalScore))
            {
                scoreChanges.Add(rerankedResult.Score - originalScore);
            }
        }

        return scoreChanges.Count > 0 ? scoreChanges.Average() : 0.0f;
    }

    private RerankingResults CreateFallbackResults(
        List<UnifiedSearchResult> results,
        RerankingMetrics metrics,
        TimeSpan duration,
        string reason
    )
    {
        metrics.TotalDuration = duration;
        metrics.CandidateCount = results.Count;
        metrics.RankedResultCount = results.Count;

        return new RerankingResults
        {
            Results = results,
            Metrics = metrics,
            WasReranked = false,
            FallbackReason = reason,
        };
    }

    public void Dispose()
    {
        _rerankingService?.Dispose();
    }
}
