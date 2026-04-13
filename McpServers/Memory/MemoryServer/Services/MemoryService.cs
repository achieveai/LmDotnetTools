using MemoryServer.DocumentSegmentation.Models;
using MemoryServer.DocumentSegmentation.Services;
using MemoryServer.Models;
using Microsoft.Extensions.Options;

namespace MemoryServer.Services;

/// <summary>
///     Service for memory operations with business logic and validation.
/// </summary>
public class MemoryService : IMemoryService
{
    private readonly IDocumentSegmentationService? _documentSegmentationService;
    private readonly IEmbeddingManager _embeddingManager;
    private readonly EmbeddingOptions _embeddingOptions;
    private readonly IGraphMemoryService _graphMemoryService;
    private readonly LLMOptions _llmOptions;
    private readonly ILogger<MemoryService> _logger;
    private readonly IMemoryRepository _memoryRepository;
    private readonly MemoryOptions _options;

    public MemoryService(
        IMemoryRepository memoryRepository,
        IGraphMemoryService graphMemoryService,
        IEmbeddingManager embeddingManager,
        ILogger<MemoryService> logger,
        IOptions<MemoryServerOptions> options,
        IDocumentSegmentationService? documentSegmentationService = null
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        _memoryRepository = memoryRepository;
        _graphMemoryService = graphMemoryService;
        _embeddingManager = embeddingManager;
        _documentSegmentationService = documentSegmentationService; // Optional dependency
        _logger = logger;
        _options = options.Value.Memory;
        _llmOptions = options.Value.LLM;
        _embeddingOptions = options.Value.Embedding;
    }

    /// <summary>
    ///     Adds a new memory from content.
    /// </summary>
    public async Task<Memory> AddMemoryAsync(
        string content,
        SessionContext sessionContext,
        Dictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("Memory content cannot be empty", nameof(content));
        }

        if (content.Length > _options.MaxMemoryLength)
        {
            throw new ArgumentException(
                $"Memory content cannot exceed {_options.MaxMemoryLength} characters",
                nameof(content)
            );
        }

        _logger.LogDebug(
            "Adding memory for session {SessionContext}, content length: {Length}",
            sessionContext,
            content.Length
        );

        var memory = await _memoryRepository.AddAsync(content, sessionContext, metadata, cancellationToken);

        _logger.LogInformation("Added memory {Id} for session {SessionContext}", memory.Id, sessionContext);

        // Generate and store embedding if enabled
        if (_embeddingOptions.EnableVectorStorage && _embeddingOptions.AutoGenerateEmbeddings)
        {
            try
            {
                _logger.LogDebug("Generating embedding for memory {MemoryId}", memory.Id);
                var embedding = await _embeddingManager.GenerateEmbeddingAsync(content, cancellationToken);
                await _memoryRepository.StoreEmbeddingAsync(
                    memory.Id,
                    embedding,
                    _embeddingManager.ModelName,
                    cancellationToken
                );
                _logger.LogDebug(
                    "Stored embedding for memory {MemoryId} using model {ModelName}",
                    memory.Id,
                    _embeddingManager.ModelName
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to generate embedding for memory {MemoryId}. Memory was saved but embedding generation failed.",
                    memory.Id
                );
                // Don't throw - memory was successfully saved, embedding generation is supplementary
            }
        }

        // Process graph extraction if enabled
        if (_llmOptions.EnableGraphProcessing)
        {
            try
            {
                _logger.LogDebug("Starting graph processing for memory {MemoryId}", memory.Id);
                var graphSummary = await _graphMemoryService.ProcessMemoryAsync(
                    memory,
                    sessionContext,
                    cancellationToken
                );
                _logger.LogInformation(
                    "Graph processing completed for memory {MemoryId}: {EntitiesAdded} entities, {RelationshipsAdded} relationships added in {ProcessingTimeMs}ms",
                    memory.Id,
                    graphSummary.EntitiesAdded,
                    graphSummary.RelationshipsAdded,
                    graphSummary.ProcessingTimeMs
                );

                if (graphSummary.Warnings.Count != 0)
                {
                    _logger.LogWarning(
                        "Graph processing warnings for memory {MemoryId}: {Warnings}",
                        memory.Id,
                        string.Join("; ", graphSummary.Warnings)
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to process graph for memory {MemoryId}. Memory was saved but graph extraction failed.",
                    memory.Id
                );
                // Don't throw - memory was successfully saved, graph processing is supplementary
            }
        }

        // Process document segmentation if enabled and service is available
        if (_documentSegmentationService != null)
        {
            try
            {
                _logger.LogDebug("Checking if memory {MemoryId} should be segmented", memory.Id);

                var shouldSegment = await _documentSegmentationService.ShouldSegmentAsync(
                    content,
                    DocumentType.Generic,
                    cancellationToken
                );

                if (shouldSegment)
                {
                    _logger.LogDebug("Starting document segmentation for memory {MemoryId}", memory.Id);

                    var segmentationRequest = new DocumentSegmentationRequest
                    {
                        DocumentType = DocumentType.Generic,
                        Strategy = SegmentationStrategy.TopicBased, // Default strategy
                    };

                    var segmentationResult = await _documentSegmentationService.SegmentDocumentAsync(
                        content,
                        segmentationRequest,
                        sessionContext,
                        cancellationToken
                    );

                    if (segmentationResult.IsComplete && segmentationResult.Segments.Count != 0)
                    {
                        _logger.LogInformation(
                            "Document segmentation completed for memory {MemoryId}: {SegmentCount} segments created",
                            memory.Id,
                            segmentationResult.Segments.Count
                        );
                    }
                    else
                    {
                        _logger.LogInformation(
                            "Document segmentation was attempted for memory {MemoryId} but no segments were created",
                            memory.Id
                        );
                    }
                }
                else
                {
                    _logger.LogDebug("Memory {MemoryId} does not require segmentation", memory.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to process document segmentation for memory {MemoryId}. Memory was saved but segmentation failed.",
                    memory.Id
                );
                // Don't throw - memory was successfully saved, segmentation is supplementary
            }
        }
        else
        {
            _logger.LogDebug("Document segmentation service is not available for memory {MemoryId}", memory.Id);
        }

        return memory;
    }

    /// <summary>
    ///     Searches memories using text query.
    /// </summary>
    public async Task<List<Memory>> SearchMemoriesAsync(
        string query,
        SessionContext sessionContext,
        int limit = 10,
        float scoreThreshold = 0.7f,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        // Apply configured limits
        limit = Math.Min(limit, _options.DefaultSearchLimit * 2); // Allow up to 2x default limit
        scoreThreshold = Math.Max(scoreThreshold, 0.0f);

        _logger.LogDebug(
            "Searching memories for session {SessionContext}, query: '{Query}', limit: {Limit}, threshold: {Threshold}",
            sessionContext,
            query,
            limit,
            scoreThreshold
        );

        List<Memory> memories;

        // Use hybrid search if vector storage is enabled, otherwise use traditional search
        if (_embeddingOptions.EnableVectorStorage && _embeddingOptions.UseHybridSearch)
        {
            try
            {
                _logger.LogDebug("Using hybrid search for query: '{Query}'", query);

                // Generate embedding for the query
                var queryEmbedding = await _embeddingManager.GenerateEmbeddingAsync(query, cancellationToken);

                // Perform hybrid search
                memories = await _memoryRepository.SearchHybridAsync(
                    query,
                    queryEmbedding,
                    sessionContext,
                    limit,
                    _embeddingOptions.TraditionalSearchWeight,
                    _embeddingOptions.VectorSearchWeight,
                    cancellationToken
                );

                _logger.LogInformation(
                    "Hybrid search found {Count} memories for query '{Query}' in session {SessionContext}",
                    memories.Count,
                    query,
                    sessionContext
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Hybrid search failed for query '{Query}', falling back to traditional search",
                    query
                );

                // Fall back to traditional search
                memories = await _memoryRepository.SearchAsync(
                    query,
                    sessionContext,
                    limit,
                    scoreThreshold,
                    cancellationToken
                );

                _logger.LogInformation(
                    "Traditional search (fallback) found {Count} memories for query '{Query}' in session {SessionContext}",
                    memories.Count,
                    query,
                    sessionContext
                );
            }
        }
        else
        {
            // Use traditional FTS5 search
            memories = await _memoryRepository.SearchAsync(
                query,
                sessionContext,
                limit,
                scoreThreshold,
                cancellationToken
            );

            _logger.LogInformation(
                "Traditional search found {Count} memories for query '{Query}' in session {SessionContext}",
                memories.Count,
                query,
                sessionContext
            );
        }

        return memories;
    }

    /// <summary>
    ///     Gets all memories for a session.
    /// </summary>
    public async Task<List<Memory>> GetAllMemoriesAsync(
        SessionContext sessionContext,
        int limit = 100,
        int offset = 0,
        CancellationToken cancellationToken = default
    )
    {
        // Apply reasonable limits
        limit = Math.Min(limit, 1000); // Maximum 1000 memories at once
        offset = Math.Max(offset, 0);

        _logger.LogDebug(
            "Getting all memories for session {SessionContext}, limit: {Limit}, offset: {Offset}",
            sessionContext,
            limit,
            offset
        );

        var memories = await _memoryRepository.GetAllAsync(sessionContext, limit, offset, cancellationToken);

        _logger.LogInformation(
            "Retrieved {Count} memories for session {SessionContext}",
            memories.Count,
            sessionContext
        );
        return memories;
    }

    /// <summary>
    ///     Updates an existing memory.
    /// </summary>
    public async Task<Memory?> UpdateMemoryAsync(
        int id,
        string content,
        SessionContext sessionContext,
        Dictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("Memory content cannot be empty", nameof(content));
        }

        if (content.Length > _options.MaxMemoryLength)
        {
            throw new ArgumentException(
                $"Memory content cannot exceed {_options.MaxMemoryLength} characters",
                nameof(content)
            );
        }

        _logger.LogDebug(
            "Updating memory {Id} for session {SessionContext}, content length: {Length}",
            id,
            sessionContext,
            content.Length
        );

        var memory = await _memoryRepository.UpdateAsync(id, content, sessionContext, metadata, cancellationToken);

        if (memory != null)
        {
            _logger.LogInformation("Updated memory {Id} for session {SessionContext}", id, sessionContext);

            // Regenerate and store embedding if enabled
            if (_embeddingOptions.EnableVectorStorage && _embeddingOptions.AutoGenerateEmbeddings)
            {
                try
                {
                    _logger.LogDebug("Regenerating embedding for updated memory {MemoryId}", memory.Id);
                    var embedding = await _embeddingManager.GenerateEmbeddingAsync(content, cancellationToken);
                    await _memoryRepository.StoreEmbeddingAsync(
                        memory.Id,
                        embedding,
                        _embeddingManager.ModelName,
                        cancellationToken
                    );
                    _logger.LogDebug(
                        "Updated embedding for memory {MemoryId} using model {ModelName}",
                        memory.Id,
                        _embeddingManager.ModelName
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Failed to regenerate embedding for updated memory {MemoryId}. Memory was updated but embedding generation failed.",
                        memory.Id
                    );
                    // Don't throw - memory was successfully updated, embedding generation is supplementary
                }
            }

            // Process graph extraction if enabled
            if (_llmOptions.EnableGraphProcessing)
            {
                try
                {
                    _logger.LogDebug("Starting graph processing for updated memory {MemoryId}", memory.Id);
                    var graphSummary = await _graphMemoryService.ProcessMemoryAsync(
                        memory,
                        sessionContext,
                        cancellationToken
                    );
                    _logger.LogInformation(
                        "Graph processing completed for updated memory {MemoryId}: {EntitiesAdded} entities, {RelationshipsAdded} relationships added in {ProcessingTimeMs}ms",
                        memory.Id,
                        graphSummary.EntitiesAdded,
                        graphSummary.RelationshipsAdded,
                        graphSummary.ProcessingTimeMs
                    );

                    if (graphSummary.Warnings.Count != 0)
                    {
                        _logger.LogWarning(
                            "Graph processing warnings for updated memory {MemoryId}: {Warnings}",
                            memory.Id,
                            string.Join("; ", graphSummary.Warnings)
                        );
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Failed to process graph for updated memory {MemoryId}. Memory was updated but graph extraction failed.",
                        memory.Id
                    );
                    // Don't throw - memory was successfully updated, graph processing is supplementary
                }
            }
            else
            {
                _logger.LogDebug("Graph processing is disabled for updated memory {MemoryId}", memory.Id);
            }
        }
        else
        {
            _logger.LogWarning(
                "Failed to update memory {Id} for session {SessionContext} - not found or access denied",
                id,
                sessionContext
            );
        }

        return memory;
    }

    /// <summary>
    ///     Deletes a memory by ID.
    /// </summary>
    public async Task<bool> DeleteMemoryAsync(
        int id,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("Deleting memory {Id} for session {SessionContext}", id, sessionContext);

        var deleted = await _memoryRepository.DeleteAsync(id, sessionContext, cancellationToken);

        if (deleted)
        {
            _logger.LogInformation("Deleted memory {Id} for session {SessionContext}", id, sessionContext);
        }
        else
        {
            _logger.LogWarning(
                "Failed to delete memory {Id} for session {SessionContext} - not found or access denied",
                id,
                sessionContext
            );
        }

        return deleted;
    }

    /// <summary>
    ///     Deletes all memories for a session.
    /// </summary>
    public async Task<int> DeleteAllMemoriesAsync(
        SessionContext sessionContext,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("Deleting all memories for session {SessionContext}", sessionContext);

        var deletedCount = await _memoryRepository.DeleteAllAsync(sessionContext, cancellationToken);

        _logger.LogInformation("Deleted {Count} memories for session {SessionContext}", deletedCount, sessionContext);
        return deletedCount;
    }

    /// <summary>
    ///     Gets memory statistics for a session.
    /// </summary>
    public async Task<MemoryStats> GetMemoryStatsAsync(
        SessionContext sessionContext,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("Getting memory statistics for session {SessionContext}", sessionContext);

        var stats = await _memoryRepository.GetStatsAsync(sessionContext, cancellationToken);

        _logger.LogDebug(
            "Retrieved memory statistics for session {SessionContext}: {TotalMemories} memories",
            sessionContext,
            stats.TotalMemories
        );

        return stats;
    }

    /// <summary>
    ///     Gets memory history for a specific memory ID.
    /// </summary>
    public async Task<List<MemoryHistoryEntry>> GetMemoryHistoryAsync(
        int id,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("Getting memory history for memory {Id} in session {SessionContext}", id, sessionContext);

        var history = await _memoryRepository.GetHistoryAsync(id, sessionContext, cancellationToken);

        _logger.LogDebug(
            "Retrieved {Count} history entries for memory {Id} in session {SessionContext}",
            history.Count,
            id,
            sessionContext
        );

        return history;
    }

    /// <summary>
    ///     Gets all agents for a specific user.
    /// </summary>
    public async Task<List<string>> GetAgentsAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("UserId cannot be empty", nameof(userId));
        }

        _logger.LogDebug("Getting all agents for user {UserId}", userId);

        var agents = await _memoryRepository.GetAgentsAsync(userId, cancellationToken);

        _logger.LogDebug("Retrieved {Count} agents for user {UserId}", agents.Count, userId);

        return agents;
    }

    /// <summary>
    ///     Gets all run IDs for a specific user and agent.
    /// </summary>
    public async Task<List<string>> GetRunsAsync(
        string userId,
        string agentId,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("UserId cannot be empty", nameof(userId));
        }

        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new ArgumentException("AgentId cannot be empty", nameof(agentId));
        }

        _logger.LogDebug("Getting all runs for user {UserId} and agent {AgentId}", userId, agentId);

        var runs = await _memoryRepository.GetRunsAsync(userId, agentId, cancellationToken);

        _logger.LogDebug("Retrieved {Count} runs for user {UserId} and agent {AgentId}", runs.Count, userId, agentId);

        return runs;
    }
}
