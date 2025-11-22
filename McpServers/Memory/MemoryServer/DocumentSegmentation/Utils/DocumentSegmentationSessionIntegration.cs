using MemoryServer.DocumentSegmentation.Models;
using MemoryServer.DocumentSegmentation.Services;
using MemoryServer.Infrastructure;
using MemoryServer.Models;

namespace MemoryServer.DocumentSegmentation.Utils;

/// <summary>
/// Integration utility for demonstrating session context integration with document segmentation services.
/// This shows how all services work together within the Database Session Pattern.
/// </summary>
public class DocumentSegmentationSessionIntegration
{
    private readonly IDocumentSizeAnalyzer _sizeAnalyzer;
    private readonly ISegmentationPromptManager _promptManager;
    private readonly IDocumentSegmentRepository _repository;
    private readonly ISqliteSessionFactory _sessionFactory;
    private readonly ILogger<DocumentSegmentationSessionIntegration> _logger;

    public DocumentSegmentationSessionIntegration(
        IDocumentSizeAnalyzer sizeAnalyzer,
        ISegmentationPromptManager promptManager,
        IDocumentSegmentRepository repository,
        ISqliteSessionFactory sessionFactory,
        ILogger<DocumentSegmentationSessionIntegration> logger
    )
    {
        _sizeAnalyzer = sizeAnalyzer ?? throw new ArgumentNullException(nameof(sizeAnalyzer));
        _promptManager = promptManager ?? throw new ArgumentNullException(nameof(promptManager));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Demonstrates the complete workflow from document analysis to segment storage within a session context.
    /// </summary>
    public async Task<DocumentSegmentationWorkflowResult> ProcessDocumentWorkflowAsync(
        string content,
        int parentDocumentId,
        SessionContext sessionContext,
        DocumentType documentType = DocumentType.Generic,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogInformation(
            "Starting document segmentation workflow for document {DocumentId} in session {UserId}/{AgentId}/{RunId}",
            parentDocumentId,
            sessionContext.UserId,
            sessionContext.AgentId,
            sessionContext.RunId
        );

        var result = new DocumentSegmentationWorkflowResult
        {
            ParentDocumentId = parentDocumentId,
            SessionContext = sessionContext,
            DocumentType = documentType,
        };

        try
        {
            // Step 1: Analyze document size
            _logger.LogDebug("Step 1: Analyzing document size...");
            result.DocumentStatistics = await _sizeAnalyzer.AnalyzeDocumentAsync(content, cancellationToken);
            result.ShouldSegment = _sizeAnalyzer.ShouldSegmentDocument(result.DocumentStatistics, documentType);

            if (!result.ShouldSegment)
            {
                _logger.LogInformation(
                    "Document {DocumentId} does not require segmentation ({WordCount} words)",
                    parentDocumentId,
                    result.DocumentStatistics.WordCount
                );
                result.IsComplete = true;
                return result;
            }

            // Step 2: Validate prompt configuration
            _logger.LogDebug("Step 2: Validating prompt configuration...");
            result.PromptsValid = await _promptManager.ValidatePromptConfigurationAsync(cancellationToken);

            if (!result.PromptsValid)
            {
                _logger.LogWarning(
                    "Prompt configuration validation failed for document {DocumentId}",
                    parentDocumentId
                );
                result.Warnings.Add("Prompt configuration validation failed - using fallback prompts");
            }

            // Step 3: Get prompts for different strategies
            _logger.LogDebug("Step 3: Loading segmentation prompts...");
            var strategies = new[]
            {
                SegmentationStrategy.TopicBased,
                SegmentationStrategy.StructureBased,
                SegmentationStrategy.Hybrid,
            };

            foreach (var strategy in strategies)
            {
                try
                {
                    var prompt = await _promptManager.GetPromptAsync(strategy, "en", cancellationToken);
                    result.AvailablePrompts.Add(strategy, prompt);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load prompt for strategy {Strategy}", strategy);
                }
            }

            // Step 4: Get domain-specific instructions
            _logger.LogDebug("Step 4: Getting domain-specific instructions...");
            result.DomainInstructions = await _promptManager.GetDomainInstructionsAsync(
                documentType,
                "en",
                cancellationToken
            );

            // Step 5: Create sample segments (in a real implementation, this would use LLM)
            _logger.LogDebug("Step 5: Creating sample segments...");
            result.Segments = CreateSampleSegments(content, result.DocumentStatistics, sessionContext);

            // Step 6: Store segments in database using session pattern
            _logger.LogDebug("Step 6: Storing segments in database...");
            await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

            result.StoredSegmentIds = await _repository.StoreSegmentsAsync(
                session,
                result.Segments,
                parentDocumentId,
                sessionContext,
                cancellationToken
            );

            // Step 7: Create and store sample relationships
            _logger.LogDebug("Step 7: Creating and storing segment relationships...");
            result.Relationships = CreateSampleRelationships(result.Segments);

            result.StoredRelationshipCount = await _repository.StoreSegmentRelationshipsAsync(
                session,
                result.Relationships,
                sessionContext,
                cancellationToken
            );

            // Step 8: Verify stored data by retrieving it
            _logger.LogDebug("Step 8: Verifying stored segments...");
            var retrievedSegments = await _repository.GetDocumentSegmentsAsync(
                session,
                parentDocumentId,
                sessionContext,
                cancellationToken
            );

            var retrievedRelationships = await _repository.GetSegmentRelationshipsAsync(
                session,
                parentDocumentId,
                sessionContext,
                cancellationToken
            );

            result.VerificationSuccessful =
                retrievedSegments.Count == result.Segments.Count
                && retrievedRelationships.Count == result.Relationships.Count;

            result.IsComplete = true;

            _logger.LogInformation(
                "Document segmentation workflow completed for document {DocumentId}. "
                    + "Created {SegmentCount} segments and {RelationshipCount} relationships",
                parentDocumentId,
                result.Segments.Count,
                result.Relationships.Count
            );

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Document segmentation workflow failed for document {DocumentId}", parentDocumentId);
            result.Error = ex.Message;
            return result;
        }
    }

    #region Private Helper Methods

    private static List<DocumentSegment> CreateSampleSegments(
        string content,
        DocumentStatistics statistics,
        SessionContext sessionContext
    )
    {
        // Simple demonstration segmentation - split by paragraphs or sentence groups
        var segments = new List<DocumentSegment>();
        var words = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var targetSegmentSize = 300; // words per segment for demo

        for (var i = 0; i < words.Length; i += targetSegmentSize)
        {
            var segmentWords = words.Skip(i).Take(targetSegmentSize).ToArray();
            var segmentContent = string.Join(" ", segmentWords);

            var segment = new DocumentSegment
            {
                Id = $"seg_{sessionContext.UserId}_{Guid.NewGuid():N}",
                SequenceNumber = (i / targetSegmentSize) + 1,
                Content = segmentContent,
                Title = $"Segment {(i / targetSegmentSize) + 1}",
                Summary = GenerateSimpleSummary(segmentContent),
                Quality = new SegmentQuality
                {
                    CoherenceScore = 0.85,
                    IndependenceScore = 0.75,
                    TopicConsistencyScore = 0.80,
                    PassesQualityThreshold = true,
                },
                Metadata = new Dictionary<string, object>
                {
                    ["word_count"] = segmentWords.Length,
                    ["created_by"] = "workflow_demo",
                    ["segment_type"] = "demo_segment",
                },
            };

            segments.Add(segment);
        }

        return segments;
    }

    private static List<SegmentRelationship> CreateSampleRelationships(List<DocumentSegment> segments)
    {
        var relationships = new List<SegmentRelationship>();

        // Create sequential relationships between adjacent segments
        for (var i = 0; i < segments.Count - 1; i++)
        {
            var relationship = new SegmentRelationship
            {
                Id = Guid.NewGuid().ToString("N"),
                SourceSegmentId = segments[i].Id,
                TargetSegmentId = segments[i + 1].Id,
                RelationshipType = SegmentRelationshipType.Sequential,
                Strength = 0.9,
                Metadata = new Dictionary<string, object>
                {
                    ["relationship_reason"] = "sequential_order",
                    ["created_by"] = "workflow_demo",
                },
            };

            relationships.Add(relationship);
        }

        return relationships;
    }

    private static string GenerateSimpleSummary(string content)
    {
        // Simple summary generation - take first sentence or first 100 characters
        var sentences = content.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (sentences.Length > 0)
        {
            var firstSentence = sentences[0].Trim();
            return firstSentence.Length > 100 ? firstSentence[..97] + "..." : firstSentence;
        }

        return content.Length > 100 ? content[..97] + "..." : content;
    }

    #endregion
}

/// <summary>
/// Result of the document segmentation workflow demonstration.
/// </summary>
public class DocumentSegmentationWorkflowResult
{
    public int ParentDocumentId { get; set; }
    public SessionContext SessionContext { get; set; } = new();
    public DocumentType DocumentType { get; set; }
    public DocumentStatistics DocumentStatistics { get; set; } = new();
    public bool ShouldSegment { get; set; }
    public bool PromptsValid { get; set; }
    public Dictionary<SegmentationStrategy, PromptTemplate> AvailablePrompts { get; set; } = [];
    public string DomainInstructions { get; set; } = string.Empty;
    public List<DocumentSegment> Segments { get; set; } = [];
    public List<SegmentRelationship> Relationships { get; set; } = [];
    public List<int> StoredSegmentIds { get; set; } = [];
    public int StoredRelationshipCount { get; set; }
    public bool VerificationSuccessful { get; set; }
    public bool IsComplete { get; set; }
    public List<string> Warnings { get; set; } = [];
    public string? Error { get; set; }
}
