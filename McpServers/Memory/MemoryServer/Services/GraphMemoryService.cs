using System.Diagnostics;
using MemoryServer.Models;

namespace MemoryServer.Services;

/// <summary>
/// Implementation of graph memory service that orchestrates graph processing and integrates with the memory system.
/// Implements the Facade pattern to provide a unified interface for graph operations.
/// </summary>
public class GraphMemoryService : IGraphMemoryService
{
    private readonly IGraphExtractionService _extractionService;
    private readonly IGraphDecisionEngine _decisionEngine;
    private readonly IGraphRepository _graphRepository;
    private readonly IMemoryRepository _memoryRepository;
    private readonly ILogger<GraphMemoryService> _logger;

    // Configuration constants
    private const float TraditionalSearchWeight = 0.6f;
    private const float GraphSearchWeight = 0.4f;
    private const int DefaultGraphSearchLimit = 50;

    public GraphMemoryService(
        IGraphExtractionService extractionService,
        IGraphDecisionEngine decisionEngine,
        IGraphRepository graphRepository,
        IMemoryRepository memoryRepository,
        ILogger<GraphMemoryService> logger)
    {
        _extractionService = extractionService ?? throw new ArgumentNullException(nameof(extractionService));
        _decisionEngine = decisionEngine ?? throw new ArgumentNullException(nameof(decisionEngine));
        _graphRepository = graphRepository ?? throw new ArgumentNullException(nameof(graphRepository));
        _memoryRepository = memoryRepository ?? throw new ArgumentNullException(nameof(memoryRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<GraphUpdateSummary> ProcessMemoryAsync(
        Memory memory,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var summary = new GraphUpdateSummary();

        try
        {
            _logger.LogDebug("Processing memory {MemoryId} for graph updates", memory.Id);

            // Extract entities and relationships from memory content using combined extraction for efficiency
            var (entities, relationships) = await _extractionService.ExtractGraphDataAsync(
                memory.Content,
                sessionContext,
                memory.Id,
                cancellationToken);

            _logger.LogDebug("Extracted {EntityCount} entities and {RelationshipCount} relationships",
                entities.Count(), relationships.Count());

            // Analyze what updates should be made
            var instructions = await _decisionEngine.AnalyzeGraphUpdatesAsync(
                entities.ToList(), relationships.ToList(), sessionContext, cancellationToken);

            _logger.LogDebug("Generated {InstructionCount} update instructions", instructions.Count);

            // Execute entity instructions first to ensure entities exist before relationships
            var entityInstructions = instructions.Where(i => i.EntityData != null).ToList();
            var relationshipInstructions = instructions.Where(i => i.RelationshipData != null).ToList();

            // Process entities first
            foreach (var instruction in entityInstructions)
            {
                if (!await _decisionEngine.ValidateGraphUpdateAsync(instruction, sessionContext, cancellationToken))
                {
                    summary.Warnings.Add($"Skipped invalid instruction: {instruction.Reasoning}");
                    continue;
                }

                await ExecuteGraphInstructionAsync(instruction, summary, cancellationToken);
            }

            // Process relationships after entities are committed
            foreach (var instruction in relationshipInstructions)
            {
                var isValid = await _decisionEngine.ValidateGraphUpdateAsync(instruction, sessionContext, cancellationToken);
                if (!isValid)
                {
                    var relationshipInfo = instruction.RelationshipData != null 
                        ? $"'{instruction.RelationshipData.Source}' --[{instruction.RelationshipData.RelationshipType}]--> '{instruction.RelationshipData.Target}' (confidence: {instruction.RelationshipData.Confidence:F2})"
                        : "null relationship data";
                    
                    _logger.LogWarning("RELATIONSHIP VALIDATION FAILED: {RelationshipInfo} | Operation: {Operation} | Reasoning: {Reasoning} | Confidence: {InstructionConfidence:F2}", 
                        relationshipInfo, instruction.Operation, instruction.Reasoning, instruction.Confidence);
                    
                    summary.Warnings.Add($"Skipped invalid instruction: {instruction.Reasoning}");
                    continue;
                }

                await ExecuteGraphInstructionAsync(instruction, summary, cancellationToken);
            }

            // Track processed entities and relationships
            summary.ProcessedEntities = entities.Select(e => e.Name).Distinct().ToList();
            summary.ProcessedRelationshipTypes = relationships.Select(r => r.RelationshipType).Distinct().ToList();

            stopwatch.Stop();
            summary.ProcessingTimeMs = stopwatch.ElapsedMilliseconds;

            _logger.LogInformation("Processed memory {MemoryId}: {EntitiesAdded} entities added, {RelationshipsAdded} relationships added",
                memory.Id, summary.EntitiesAdded, summary.RelationshipsAdded);

            return summary;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing memory {MemoryId} for graph updates", memory.Id);
            stopwatch.Stop();
            summary.ProcessingTimeMs = stopwatch.ElapsedMilliseconds;
            summary.Warnings.Add($"Processing failed: {ex.Message}");
            return summary;
        }
    }

    public async Task<HybridSearchResults> SearchMemoriesAsync(
        string query,
        SessionContext sessionContext,
        bool useGraphTraversal = true,
        int maxResults = 20,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var results = new HybridSearchResults();

        try
        {
            _logger.LogDebug("Performing hybrid search for query: '{Query}'", query);

            // Perform traditional search
            var traditionalTask = PerformTraditionalSearchAsync(query, sessionContext, maxResults, cancellationToken);

            // Perform graph-based search if enabled
            Task<List<Memory>> graphTask = useGraphTraversal
                ? PerformGraphSearchAsync(query, sessionContext, maxResults, cancellationToken)
                : Task.FromResult(new List<Memory>());

            await Task.WhenAll(traditionalTask, graphTask);

            results.TraditionalResults = await traditionalTask;
            results.GraphResults = await graphTask;

            // Combine and rank results
            results.CombinedResults = CombineSearchResults(results.TraditionalResults, results.GraphResults, maxResults);

            // Extract relevant entities and relationships
            if (useGraphTraversal)
            {
                await PopulateRelevantGraphDataAsync(query, sessionContext, results, cancellationToken);
            }

            stopwatch.Stop();
            results.SearchTimeMs = stopwatch.ElapsedMilliseconds;

            _logger.LogDebug("Hybrid search completed: {TraditionalCount} traditional, {GraphCount} graph, {CombinedCount} combined results",
                results.TraditionalResults.Count, results.GraphResults.Count, results.CombinedResults.Count);

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error performing hybrid search for query: '{Query}'", query);
            stopwatch.Stop();
            results.SearchTimeMs = stopwatch.ElapsedMilliseconds;
            return results;
        }
    }

    public async Task<GraphTraversalResult> GetRelatedEntitiesAsync(
        string entityName,
        SessionContext sessionContext,
        int maxDepth = 3,
        IEnumerable<string>? relationshipTypes = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new GraphTraversalResult();

        try
        {
            _logger.LogDebug("Getting related entities for '{EntityName}' with max depth {MaxDepth}", entityName, maxDepth);

            // Get the starting entity
            var startEntity = await _graphRepository.GetEntityByNameAsync(entityName, sessionContext, cancellationToken);
            if (startEntity == null)
            {
                _logger.LogWarning("Entity '{EntityName}' not found", entityName);
                stopwatch.Stop();
                result.TraversalTimeMs = stopwatch.ElapsedMilliseconds;
                return result;
            }

            result.StartEntity = startEntity;

            // Perform graph traversal
            var traversalResults = await _graphRepository.TraverseGraphAsync(
                entityName, sessionContext, maxDepth, relationshipTypes, cancellationToken);

            result.TraversalResults = traversalResults.ToList();
            result.AllEntities = traversalResults.Select(t => t.Entity).Distinct().ToList();
            result.AllRelationships = traversalResults.Where(t => t.Relationship != null)
                .Select(t => t.Relationship!).Distinct().ToList();
            result.MaxDepthReached = traversalResults.Any() ? traversalResults.Max(t => t.Depth) : 0;

            stopwatch.Stop();
            result.TraversalTimeMs = stopwatch.ElapsedMilliseconds;

            _logger.LogDebug("Graph traversal completed: {EntityCount} entities, {RelationshipCount} relationships, max depth {MaxDepth}",
                result.AllEntities.Count, result.AllRelationships.Count, result.MaxDepthReached);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting related entities for '{EntityName}'", entityName);
            stopwatch.Stop();
            result.TraversalTimeMs = stopwatch.ElapsedMilliseconds;
            return result;
        }
    }

    public async Task<GraphStatistics> GetGraphStatisticsAsync(
        SessionContext sessionContext,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Getting graph statistics");
            return await _graphRepository.GetGraphStatisticsAsync(sessionContext, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting graph statistics");
            throw;
        }
    }

    public async Task<GraphRebuildSummary> RebuildGraphAsync(
        SessionContext sessionContext,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var summary = new GraphRebuildSummary();

        try
        {
            _logger.LogInformation("Starting graph rebuild for session {UserId}/{AgentId}/{RunId}",
                sessionContext.UserId, sessionContext.AgentId, sessionContext.RunId);

            // Clear existing graph data
            await ClearGraphDataAsync(sessionContext, cancellationToken);

            // Get all memories for the session
            var memories = await _memoryRepository.GetAllAsync(sessionContext, limit: int.MaxValue, cancellationToken: cancellationToken);
            summary.MemoriesProcessed = memories.Count;

            _logger.LogDebug("Processing {MemoryCount} memories for graph rebuild", summary.MemoriesProcessed);

            // Process each memory
            foreach (var memory in memories)
            {
                try
                {
                    var updateSummary = await ProcessMemoryAsync(memory, sessionContext, cancellationToken);
                    summary.EntitiesCreated += updateSummary.EntitiesAdded;
                    summary.RelationshipsCreated += updateSummary.RelationshipsAdded;
                    summary.EntitiesMerged += updateSummary.EntitiesUpdated;
                    summary.RelationshipsMerged += updateSummary.RelationshipsUpdated;

                    if (updateSummary.Warnings.Any())
                    {
                        summary.Warnings.AddRange(updateSummary.Warnings);
                    }
                }
                catch (Exception ex)
                {
                    var error = $"Failed to process memory {memory.Id}: {ex.Message}";
                    summary.Errors.Add(error);
                    _logger.LogError(ex, "Error processing memory {MemoryId} during rebuild", memory.Id);
                }
            }

            stopwatch.Stop();
            summary.RebuildTimeMs = stopwatch.ElapsedMilliseconds;
            summary.CompletedAt = DateTime.UtcNow;

            _logger.LogInformation("Graph rebuild completed: {EntitiesCreated} entities, {RelationshipsCreated} relationships created in {ElapsedMs}ms",
                summary.EntitiesCreated, summary.RelationshipsCreated, summary.RebuildTimeMs);

            return summary;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during graph rebuild");
            stopwatch.Stop();
            summary.RebuildTimeMs = stopwatch.ElapsedMilliseconds;
            summary.Errors.Add($"Rebuild failed: {ex.Message}");
            return summary;
        }
    }

    public async Task<GraphValidationResult> ValidateGraphIntegrityAsync(
        SessionContext sessionContext,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new GraphValidationResult { IsValid = true };

        try
        {
            _logger.LogDebug("Validating graph integrity");

            // Get all entities and relationships
            var entities = await _graphRepository.GetEntitiesAsync(sessionContext, limit: int.MaxValue, cancellationToken: cancellationToken);
            var relationships = await _graphRepository.GetRelationshipsAsync(sessionContext, limit: int.MaxValue, cancellationToken: cancellationToken);

            result.EntitiesValidated = entities.Count();
            result.RelationshipsValidated = relationships.Count();

            var entityNames = new HashSet<string>(entities.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);

            // Check for broken relationships
            foreach (var relationship in relationships)
            {
                if (!entityNames.Contains(relationship.Source))
                {
                    result.BrokenRelationships.Add(relationship);
                    result.Errors.Add(new GraphValidationError
                    {
                        Type = GraphValidationErrorType.BrokenReference,
                        Description = $"Relationship {relationship.Id} references non-existent source entity '{relationship.Source}'",
                        RelationshipId = relationship.Id,
                        Severity = ValidationSeverity.High
                    });
                    result.IsValid = false;
                }

                if (!entityNames.Contains(relationship.Target))
                {
                    result.BrokenRelationships.Add(relationship);
                    result.Errors.Add(new GraphValidationError
                    {
                        Type = GraphValidationErrorType.BrokenReference,
                        Description = $"Relationship {relationship.Id} references non-existent target entity '{relationship.Target}'",
                        RelationshipId = relationship.Id,
                        Severity = ValidationSeverity.High
                    });
                    result.IsValid = false;
                }
            }

            // Check for orphaned entities
            var connectedEntities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var relationship in relationships)
            {
                connectedEntities.Add(relationship.Source);
                connectedEntities.Add(relationship.Target);
            }

            foreach (var entity in entities)
            {
                if (!connectedEntities.Contains(entity.Name))
                {
                    result.OrphanedEntities.Add(entity);
                    result.Warnings.Add(new GraphValidationWarning
                    {
                        Type = GraphValidationWarningType.OrphanedEntity,
                        Description = $"Entity '{entity.Name}' has no relationships",
                        EntityId = entity.Id
                    });
                }

                // Check for low confidence
                if (entity.Confidence < 0.5f)
                {
                    result.Warnings.Add(new GraphValidationWarning
                    {
                        Type = GraphValidationWarningType.LowConfidence,
                        Description = $"Entity '{entity.Name}' has low confidence score: {entity.Confidence:F2}",
                        EntityId = entity.Id
                    });
                }
            }

            // Check for duplicate entities
            var entityGroups = entities.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1);

            foreach (var group in entityGroups)
            {
                foreach (var entity in group.Skip(1))
                {
                    result.Errors.Add(new GraphValidationError
                    {
                        Type = GraphValidationErrorType.DuplicateEntity,
                        Description = $"Duplicate entity found: '{entity.Name}' (ID: {entity.Id})",
                        EntityId = entity.Id,
                        Severity = ValidationSeverity.Medium
                    });
                    result.IsValid = false;
                }
            }

            stopwatch.Stop();
            result.ValidationTimeMs = stopwatch.ElapsedMilliseconds;

            _logger.LogDebug("Graph validation completed: {IsValid}, {ErrorCount} errors, {WarningCount} warnings",
                result.IsValid, result.Errors.Count, result.Warnings.Count);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating graph integrity");
            stopwatch.Stop();
            result.ValidationTimeMs = stopwatch.ElapsedMilliseconds;
            result.IsValid = false;
            result.Errors.Add(new GraphValidationError
            {
                Type = GraphValidationErrorType.InvalidData,
                Description = $"Validation failed: {ex.Message}",
                Severity = ValidationSeverity.Critical
            });
            return result;
        }
    }

    #region Private Helper Methods

    private async Task ExecuteGraphInstructionAsync(
        GraphDecisionInstruction instruction,
        GraphUpdateSummary summary,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("EXECUTION START: Operation={Operation}, HasEntity={HasEntity}, HasRelationship={HasRelationship}",
                instruction.Operation, instruction.EntityData != null, instruction.RelationshipData != null);

            switch (instruction.Operation)
            {
                case GraphDecisionOperation.ADD:
                    if (instruction.EntityData != null)
                    {
                        _logger.LogDebug("EXECUTING: Adding entity '{EntityName}'", instruction.EntityData.Name);
                        await _graphRepository.AddEntityAsync(instruction.EntityData, instruction.SessionContext, cancellationToken);
                        summary.EntitiesAdded++;
                        _logger.LogDebug("EXECUTION SUCCESS: Entity '{EntityName}' added", instruction.EntityData.Name);
                    }
                    else if (instruction.RelationshipData != null)
                    {
                        var relationshipInfo = $"'{instruction.RelationshipData.Source}' --[{instruction.RelationshipData.RelationshipType}]--> '{instruction.RelationshipData.Target}'";
                        _logger.LogDebug("EXECUTING: Adding relationship {RelationshipInfo}", relationshipInfo);
                        await _graphRepository.AddRelationshipAsync(instruction.RelationshipData, instruction.SessionContext, cancellationToken);
                        summary.RelationshipsAdded++;
                        _logger.LogDebug("EXECUTION SUCCESS: Relationship {RelationshipInfo} added", relationshipInfo);
                    }
                    break;

                case GraphDecisionOperation.UPDATE:
                    if (instruction.EntityData != null)
                    {
                        await _graphRepository.UpdateEntityAsync(instruction.EntityData, instruction.SessionContext, cancellationToken);
                        summary.EntitiesUpdated++;
                    }
                    else if (instruction.RelationshipData != null)
                    {
                        await _graphRepository.UpdateRelationshipAsync(instruction.RelationshipData, instruction.SessionContext, cancellationToken);
                        summary.RelationshipsUpdated++;
                    }
                    break;

                case GraphDecisionOperation.DELETE:
                    if (instruction.EntityData != null)
                    {
                        await _graphRepository.DeleteEntityAsync(instruction.EntityData.Id, instruction.SessionContext, cancellationToken);
                    }
                    else if (instruction.RelationshipData != null)
                    {
                        await _graphRepository.DeleteRelationshipAsync(instruction.RelationshipData.Id, instruction.SessionContext, cancellationToken);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            var instructionInfo = instruction.EntityData != null 
                ? $"Entity: {instruction.EntityData.Name}"
                : instruction.RelationshipData != null
                    ? $"Relationship: '{instruction.RelationshipData.Source}' --[{instruction.RelationshipData.RelationshipType}]--> '{instruction.RelationshipData.Target}'"
                    : "Unknown instruction type";
            
            _logger.LogError(ex, "EXECUTION FAILED: {InstructionInfo} | Operation: {Operation} | Reasoning: {Reasoning} | Error: {ErrorMessage}", 
                instructionInfo, instruction.Operation, instruction.Reasoning, ex.Message);
            summary.Warnings.Add($"Failed to execute instruction: {instruction.Reasoning} - {ex.Message}");
        }
    }

    private async Task<List<Memory>> PerformTraditionalSearchAsync(
        string query,
        SessionContext sessionContext,
        int maxResults,
        CancellationToken cancellationToken)
    {
        try
        {
            // Use existing memory repository search functionality
            var memories = await _memoryRepository.SearchAsync(query, sessionContext, maxResults, cancellationToken: cancellationToken);
            return memories;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error performing traditional search");
            return new List<Memory>();
        }
    }

    private async Task<List<Memory>> PerformGraphSearchAsync(
        string query,
        SessionContext sessionContext,
        int maxResults,
        CancellationToken cancellationToken)
    {
        try
        {
            var memories = new List<Memory>();

            // Extract potential entity names from the query
            var queryEntities = await _extractionService.ExtractEntitiesAsync(query, sessionContext, memoryId: 0, cancellationToken);

            // For each extracted entity, find related entities and their memories
            foreach (var entity in queryEntities.Take(3)) // Limit to avoid too many traversals
            {
                var traversalResult = await GetRelatedEntitiesAsync(entity.Name, sessionContext, 2, cancellationToken: cancellationToken);

                // Get memories that reference these entities
                foreach (var relatedEntity in traversalResult.AllEntities.Take(DefaultGraphSearchLimit))
                {
                    if (relatedEntity.SourceMemoryIds != null)
                    {
                        foreach (var memoryId in relatedEntity.SourceMemoryIds)
                        {
                            try
                            {
                                var memory = await _memoryRepository.GetByIdAsync(memoryId, sessionContext, cancellationToken);
                                if (memory != null && !memories.Any(m => m.Id == memory.Id))
                                {
                                    memories.Add(memory);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Error retrieving memory {MemoryId} during graph search", memoryId);
                            }
                        }
                    }
                }
            }

            return memories.Take(maxResults).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error performing graph search");
            return new List<Memory>();
        }
    }

    private static List<HybridSearchResult> CombineSearchResults(
        List<Memory> traditionalResults,
        List<Memory> graphResults,
        int maxResults)
    {
        var combinedResults = new List<HybridSearchResult>();
        var processedMemoryIds = new HashSet<int>();

        // Add traditional results
        for (int i = 0; i < traditionalResults.Count; i++)
        {
            var memory = traditionalResults[i];
            var traditionalScore = 1.0f - (float)i / traditionalResults.Count; // Higher score for earlier results

            combinedResults.Add(new HybridSearchResult
            {
                Memory = memory,
                TraditionalScore = traditionalScore,
                GraphScore = 0f,
                CombinedScore = traditionalScore * TraditionalSearchWeight,
                Source = SearchResultSource.Traditional
            });

            processedMemoryIds.Add(memory.Id);
        }

        // Add graph results
        for (int i = 0; i < graphResults.Count; i++)
        {
            var memory = graphResults[i];
            var graphScore = 1.0f - (float)i / graphResults.Count;

            var existingResult = combinedResults.FirstOrDefault(r => r.Memory.Id == memory.Id);
            if (existingResult != null)
            {
                // Update existing result
                existingResult.GraphScore = graphScore;
                existingResult.CombinedScore = (existingResult.TraditionalScore * TraditionalSearchWeight) +
                                             (graphScore * GraphSearchWeight);
                existingResult.Source = SearchResultSource.Both;
            }
            else if (!processedMemoryIds.Contains(memory.Id))
            {
                // Add new graph-only result
                combinedResults.Add(new HybridSearchResult
                {
                    Memory = memory,
                    TraditionalScore = 0f,
                    GraphScore = graphScore,
                    CombinedScore = graphScore * GraphSearchWeight,
                    Source = SearchResultSource.Graph
                });
            }
        }

        // Sort by combined score and return top results
        return combinedResults
            .OrderByDescending(r => r.CombinedScore)
            .Take(maxResults)
            .ToList();
    }

    private async Task PopulateRelevantGraphDataAsync(
        string query,
        SessionContext sessionContext,
        HybridSearchResults results,
        CancellationToken cancellationToken)
    {
        try
        {
            // Extract entities from query
            var queryEntities = await _extractionService.ExtractEntitiesAsync(query, sessionContext, memoryId: 0, cancellationToken);
            results.RelevantEntities.AddRange(queryEntities);

            // Get entities from memory results
            var memoryIds = results.CombinedResults.Select(r => r.Memory.Id).ToList();
            var entities = await _graphRepository.GetEntitiesAsync(sessionContext, limit: 1000, cancellationToken: cancellationToken);
            var relevantEntities = entities.Where(e => e.SourceMemoryIds?.Any(id => memoryIds.Contains(id)) == true);
            
            foreach (var entity in relevantEntities)
            {
                if (!results.RelevantEntities.Any(e => e.Name.Equals(entity.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    results.RelevantEntities.Add(entity);
                }
            }

            // Get relationships for relevant entities
            var entityNames = results.RelevantEntities.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var relationships = await _graphRepository.GetRelationshipsAsync(sessionContext, limit: 1000, cancellationToken: cancellationToken);
            results.RelevantRelationships = relationships
                .Where(r => entityNames.Contains(r.Source) || entityNames.Contains(r.Target))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error populating relevant graph data");
        }
    }

    private async Task ClearGraphDataAsync(SessionContext sessionContext, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Clearing existing graph data");

            // Get all entities and relationships for the session
            var entities = await _graphRepository.GetEntitiesAsync(sessionContext, limit: int.MaxValue, cancellationToken: cancellationToken);
            var relationships = await _graphRepository.GetRelationshipsAsync(sessionContext, limit: int.MaxValue, cancellationToken: cancellationToken);

            // Delete all relationships first (to avoid foreign key constraints)
            foreach (var relationship in relationships)
            {
                await _graphRepository.DeleteRelationshipAsync(relationship.Id, sessionContext, cancellationToken);
            }

            // Delete all entities
            foreach (var entity in entities)
            {
                await _graphRepository.DeleteEntityAsync(entity.Id, sessionContext, cancellationToken);
            }

            _logger.LogDebug("Cleared {EntityCount} entities and {RelationshipCount} relationships",
                entities.Count(), relationships.Count());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing graph data");
            throw;
        }
    }

    #endregion
} 