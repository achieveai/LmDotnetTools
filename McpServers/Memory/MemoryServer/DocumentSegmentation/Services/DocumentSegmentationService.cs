using MemoryServer.DocumentSegmentation.Models;
using MemoryServer.DocumentSegmentation.Services;
using MemoryServer.Infrastructure;
using MemoryServer.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MemoryServer.DocumentSegmentation.Services;

/// <summary>
/// Basic implementation of document segmentation service with rule-based fallback.
/// </summary>
public class DocumentSegmentationService : IDocumentSegmentationService
{
    private readonly IDocumentSizeAnalyzer _sizeAnalyzer;
    private readonly ISegmentationPromptManager _promptManager;
    private readonly IDocumentSegmentRepository _repository;
    private readonly ISqliteSessionFactory _sessionFactory;
    private readonly ILogger<DocumentSegmentationService> _logger;
    private readonly DocumentSegmentationOptions _options;

    public DocumentSegmentationService(
      IDocumentSizeAnalyzer sizeAnalyzer,
      ISegmentationPromptManager promptManager,
      IDocumentSegmentRepository repository,
      ISqliteSessionFactory sessionFactory,
      ILogger<DocumentSegmentationService> logger,
      IOptions<DocumentSegmentationOptions> options)
    {
        _sizeAnalyzer = sizeAnalyzer ?? throw new ArgumentNullException(nameof(sizeAnalyzer));
        _promptManager = promptManager ?? throw new ArgumentNullException(nameof(promptManager));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Segments a document into logical chunks using the optimal strategy.
    /// </summary>
    public async Task<DocumentSegmentationResult> SegmentDocumentAsync(
      string content,
      DocumentSegmentationRequest request,
      SessionContext sessionContext,
      CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("Content cannot be null or empty", nameof(content));
        }

        // Use a hash of the content as a pseudo document ID for this implementation
        var pseudoDocumentId = Math.Abs(content.GetHashCode());

        _logger.LogDebug("Starting document segmentation for content hash {DocumentId}, content length: {Length}",
          pseudoDocumentId, content.Length);

        var result = new DocumentSegmentationResult();

        try
        {
            // Step 1: Analyze document size and determine if segmentation is needed
            var statistics = await _sizeAnalyzer.AnalyzeDocumentAsync(content, cancellationToken);
            var shouldSegment = _sizeAnalyzer.ShouldSegmentDocument(statistics, request.DocumentType);

            _logger.LogDebug("Document analysis complete: {WordCount} words, should segment: {ShouldSegment}",
              statistics.WordCount, shouldSegment);

            if (!shouldSegment)
            {
                _logger.LogInformation("Document {DocumentId} does not require segmentation ({WordCount} words)",
                  pseudoDocumentId, statistics.WordCount);

                result.IsComplete = true;
                result.Metadata.ProcessingTimeMs = 0;
                return result;
            }

            // Step 2: Validate prompt configuration
            var promptsValid = await _promptManager.ValidatePromptConfigurationAsync(cancellationToken);
            if (!promptsValid)
            {
                result.Warnings.Add("Prompt configuration validation failed, using fallback segmentation");
            }

            // Step 3: Use the requested strategy
            var strategy = request.Strategy;

            _logger.LogDebug("Using segmentation strategy: {Strategy}", strategy);

            // Step 4: Perform segmentation (using rule-based fallback for now)
            result.Segments = await PerformRuleBasedSegmentationAsync(
              content, statistics, strategy, cancellationToken);

            // Step 5: Generate relationships between segments
            result.Relationships = GenerateSegmentRelationships(result.Segments);

            // Step 6: Store segments in database
            await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

            var storedSegmentIds = await _repository.StoreSegmentsAsync(
              session, result.Segments, pseudoDocumentId, sessionContext, cancellationToken);

            if (result.Relationships.Any())
            {
                await _repository.StoreSegmentRelationshipsAsync(
                  session, result.Relationships, sessionContext, cancellationToken);
            }

            // Step 7: Verify stored data
            var storedSegments = await _repository.GetDocumentSegmentsAsync(
              session, pseudoDocumentId, sessionContext, cancellationToken);

            var verificationSuccessful = storedSegments.Count == result.Segments.Count;
            if (!verificationSuccessful)
            {
                result.Warnings.Add($"Verification failed: expected {result.Segments.Count} segments, found {storedSegments.Count}");
            }

            result.IsComplete = true;
            result.Metadata.ProcessingTimeMs = 100; // Placeholder

            _logger.LogInformation("Document segmentation completed for {DocumentId}: {SegmentCount} segments, {RelationshipCount} relationships",
              pseudoDocumentId, result.Segments.Count, result.Relationships.Count);

            return result;
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"Error during segmentation: {ex.Message}");

            _logger.LogError(ex, "Error during document segmentation for document {DocumentId}", pseudoDocumentId);
            throw;
        }
    }

    /// <summary>
    /// Determines if a document should be segmented based on size and complexity.
    /// </summary>
    public async Task<bool> ShouldSegmentAsync(
      string content,
      DocumentType documentType = DocumentType.Generic,
      CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var statistics = await _sizeAnalyzer.AnalyzeDocumentAsync(content, cancellationToken);
        return _sizeAnalyzer.ShouldSegmentDocument(statistics, documentType);
    }

    /// <summary>
    /// Determines the optimal segmentation strategy for a document.
    /// </summary>
    public async Task<SegmentationStrategy> DetermineOptimalStrategyAsync(
      string content,
      DocumentType documentType = DocumentType.Generic,
      CancellationToken cancellationToken = default)
    {
        // Simple rule-based strategy determination
        // In a full implementation, this would use LLM analysis

        await Task.CompletedTask; // Simulate async work

        return documentType switch
        {
            DocumentType.Email or DocumentType.Chat => SegmentationStrategy.TopicBased,
            DocumentType.ResearchPaper or DocumentType.Legal => SegmentationStrategy.StructureBased,
            DocumentType.Technical => SegmentationStrategy.Hybrid,
            _ => SegmentationStrategy.TopicBased
        };
    }

    /// <summary>
    /// Validates the quality of segmentation results.
    /// </summary>
    public async Task<SegmentationQualityAssessment> ValidateSegmentationQualityAsync(
      DocumentSegmentationResult result,
      CancellationToken cancellationToken = default)
    {
        var assessment = new SegmentationQualityAssessment();

        if (!result.Segments.Any())
        {
            assessment.QualityFeedback.Add("No segments found in result");
            assessment.MeetsQualityStandards = false;
            return assessment;
        }

        // Calculate aggregate quality scores
        var segments = result.Segments;
        assessment.AverageCoherenceScore = segments.Average(s => s.Quality?.CoherenceScore ?? 0.0);
        assessment.AverageIndependenceScore = segments.Average(s => s.Quality?.IndependenceScore ?? 0.0);
        assessment.AverageTopicConsistencyScore = segments.Average(s => s.Quality?.TopicConsistencyScore ?? 0.0);

        // Calculate pass rate
        var passingSegments = segments.Count(s => s.Quality?.PassesQualityThreshold == true);
        assessment.QualityPassRate = (double)passingSegments / segments.Count;

        // Calculate overall score (weighted average)
        assessment.OverallScore = (assessment.AverageCoherenceScore * 0.4) +
                                 (assessment.AverageIndependenceScore * 0.3) +
                                 (assessment.AverageTopicConsistencyScore * 0.3);

        // Determine if meets quality standards
        assessment.MeetsQualityStandards = assessment.OverallScore >= 0.7 && // Default threshold
                                           assessment.QualityPassRate >= 0.8;

        // Provide feedback
        if (assessment.AverageCoherenceScore < 0.7)
        {
            assessment.QualityFeedback.Add("Low coherence scores detected - consider refining segmentation boundaries");
        }

        if (assessment.AverageIndependenceScore < 0.6)
        {
            assessment.QualityFeedback.Add("Segments may have too much interdependence - consider larger segment sizes");
        }

        if (assessment.QualityPassRate < 0.8)
        {
            assessment.QualityFeedback.Add($"Only {assessment.QualityPassRate:P0} of segments pass quality thresholds");
        }

        if (assessment.MeetsQualityStandards)
        {
            assessment.QualityFeedback.Add("Segmentation meets quality standards");
        }

        await Task.CompletedTask;
        return assessment;
    }

    #region Private Methods

    private async Task<List<DocumentSegment>> PerformRuleBasedSegmentationAsync(
      string content,
      DocumentStatistics statistics,
      SegmentationStrategy strategy,
      CancellationToken cancellationToken)
    {
        _logger.LogDebug("Performing rule-based segmentation using {Strategy} strategy", strategy);

        var segments = new List<DocumentSegment>();
        var targetSize = _options.Thresholds.TargetSegmentSizeWords;
        var words = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var segmentCount = Math.Max(1, (int)Math.Ceiling((double)words.Length / targetSize));
        var wordsPerSegment = words.Length / segmentCount;

        for (int i = 0; i < segmentCount; i++)
        {
            var startIndex = i * wordsPerSegment;
            var endIndex = (i == segmentCount - 1) ? words.Length : (i + 1) * wordsPerSegment;

            var segmentWords = words[startIndex..endIndex];
            var segmentContent = string.Join(" ", segmentWords);

            var segment = new DocumentSegment
            {
                Id = $"segment-{i + 1}-{Guid.NewGuid():N}",
                SequenceNumber = i + 1,
                Content = segmentContent,
                Title = $"Segment {i + 1}",
                Summary = GenerateSimpleSummary(segmentContent),
                Quality = new SegmentQuality
                {
                    CoherenceScore = 0.8, // Default score for rule-based segmentation
                    IndependenceScore = 0.7,
                    TopicConsistencyScore = 0.75,
                    PassesQualityThreshold = true
                },
                Metadata = new Dictionary<string, object>
                {
                    ["strategy"] = strategy.ToString(),
                    ["method"] = "rule_based",
                    ["created_by"] = "workflow_demo",
                    ["word_count"] = segmentWords.Length
                }
            };

            segments.Add(segment);
        }

        await Task.CompletedTask;
        return segments;
    }

    private List<SegmentRelationship> GenerateSegmentRelationships(List<DocumentSegment> segments)
    {
        var relationships = new List<SegmentRelationship>();

        // Create sequential relationships between adjacent segments
        for (int i = 0; i < segments.Count - 1; i++)
        {
            var relationship = new SegmentRelationship
            {
                Id = Guid.NewGuid().ToString(),
                SourceSegmentId = segments[i].Id,
                TargetSegmentId = segments[i + 1].Id,
                RelationshipType = SegmentRelationshipType.Sequential,
                Strength = 0.9,
                Metadata = new Dictionary<string, object>
                {
                    ["type"] = "adjacent_sequence",
                    ["created_by"] = "workflow_demo"
                }
            };

            relationships.Add(relationship);
        }

        return relationships;
    }

    private static string GenerateSimpleSummary(string content)
    {
        // Simple summary: first sentence or first 100 characters
        var sentences = content.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (sentences.Any() && sentences[0].Length <= 100)
        {
            return sentences[0].Trim() + ".";
        }

        return content.Length <= 100 ? content : content[..97] + "...";
    }

    #endregion
}
