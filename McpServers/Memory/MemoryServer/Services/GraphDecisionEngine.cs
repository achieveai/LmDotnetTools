using MemoryServer.Models;

namespace MemoryServer.Services;

/// <summary>
/// Implementation of graph decision engine that handles conflict resolution and update logic.
/// Uses business rules and heuristics to make intelligent decisions about graph updates.
/// </summary>
public class GraphDecisionEngine : IGraphDecisionEngine
{
    private readonly IGraphRepository _graphRepository;
    private readonly ILogger<GraphDecisionEngine> _logger;

    // Configuration constants for decision making
    private const float MinimumConfidenceThreshold = 0.1f;
    private const float HighConfidenceThreshold = 0.8f;
    private const int MaxAliasesPerEntity = 10;
    private const float SimilarityThreshold = 0.7f;

    public GraphDecisionEngine(
        IGraphRepository graphRepository,
        ILogger<GraphDecisionEngine> logger)
    {
        _graphRepository = graphRepository ?? throw new ArgumentNullException(nameof(graphRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<List<GraphDecisionInstruction>> AnalyzeGraphUpdatesAsync(
        List<Entity> extractedEntities,
        List<Relationship> extractedRelationships,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Analyzing graph updates for {EntityCount} entities and {RelationshipCount} relationships",
            extractedEntities.Count, extractedRelationships.Count);

        var instructions = new List<GraphDecisionInstruction>();

        try
        {
            // Process entities first
            foreach (var entity in extractedEntities)
            {
                var instruction = await AnalyzeEntityUpdateAsync(entity, sessionContext, cancellationToken);
                if (instruction != null)
                {
                    instructions.Add(instruction);
                }
            }

            // Process relationships after entities are resolved
            foreach (var relationship in extractedRelationships)
            {
                var instruction = await AnalyzeRelationshipUpdateAsync(relationship, sessionContext, cancellationToken);
                if (instruction != null)
                {
                    instructions.Add(instruction);
                }
            }

            _logger.LogDebug("Generated {InstructionCount} graph update instructions", instructions.Count);
            return instructions;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error analyzing graph updates");
            throw;
        }
    }

    public Task<GraphDecisionInstruction> ResolveEntityConflictAsync(
        Entity existingEntity,
        Entity newEntity,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Resolving entity conflict between existing '{ExistingName}' and new '{NewName}'",
            existingEntity.Name, newEntity.Name);

        try
        {
            // Check if entities have the same name (exact match)
            if (existingEntity.Name.Equals(newEntity.Name, StringComparison.OrdinalIgnoreCase))
            {
                // Same entity - decide based on confidence and data quality
                if (newEntity.Confidence < MinimumConfidenceThreshold)
                {
                    return Task.FromResult(new GraphDecisionInstruction
                    {
                        Operation = GraphDecisionOperation.NONE,
                        EntityData = existingEntity,
                        Reasoning = "New entity has low confidence below threshold.",
                        Confidence = existingEntity.Confidence,
                        SessionContext = sessionContext
                    });
                }

                if (newEntity.Confidence > existingEntity.Confidence)
                {
                    // Higher confidence - update
                    var mergedEntity = MergeEntities(existingEntity, newEntity);
                    return Task.FromResult(new GraphDecisionInstruction
                    {
                        Operation = GraphDecisionOperation.UPDATE,
                        EntityData = mergedEntity,
                        Reasoning = "New entity has higher confidence. Updating existing entity.",
                        Confidence = newEntity.Confidence,
                        SessionContext = sessionContext
                    });
                }
                else if (newEntity.Confidence < existingEntity.Confidence)
                {
                    // Lower confidence - keep existing
                    return Task.FromResult(new GraphDecisionInstruction
                    {
                        Operation = GraphDecisionOperation.NONE,
                        EntityData = existingEntity,
                        Reasoning = "New entity has lower confidence. Keeping existing entity.",
                        Confidence = existingEntity.Confidence,
                        SessionContext = sessionContext
                    });
                }
                else
                {
                    // Same confidence - check for type refinement or other improvements
                    if (IsTypeRefinement(existingEntity, newEntity) || HasBetterData(existingEntity, newEntity))
                    {
                        var mergedEntity = MergeEntities(existingEntity, newEntity);
                        return Task.FromResult(new GraphDecisionInstruction
                        {
                            Operation = GraphDecisionOperation.UPDATE,
                            EntityData = mergedEntity,
                            Reasoning = "Same confidence but new entity provides type refinement or better data.",
                            Confidence = newEntity.Confidence,
                            SessionContext = sessionContext
                        });
                    }
                    else
                    {
                        return Task.FromResult(new GraphDecisionInstruction
                        {
                            Operation = GraphDecisionOperation.NONE,
                            EntityData = existingEntity,
                            Reasoning = "Same confidence and no significant improvements.",
                            Confidence = existingEntity.Confidence,
                            SessionContext = sessionContext
                        });
                    }
                }
            }
            else
            {
                // Different entities - calculate similarity
                var similarity = CalculateEntitySimilarity(existingEntity, newEntity);

                if (similarity >= SimilarityThreshold)
                {
                    // Merge entities - update existing with new information
                    var mergedEntity = MergeEntities(existingEntity, newEntity);
                    var confidence = CalculateMergeConfidence(existingEntity, newEntity, similarity);

                    return Task.FromResult(new GraphDecisionInstruction
                    {
                        Operation = GraphDecisionOperation.UPDATE,
                        EntityData = mergedEntity,
                        Reasoning = $"Merged entities based on {similarity:P1} similarity. Combined aliases and metadata.",
                        Confidence = confidence,
                        SessionContext = sessionContext
                    });
                }
                else
                {
                    // Keep as separate entities
                    return Task.FromResult(new GraphDecisionInstruction
                    {
                        Operation = GraphDecisionOperation.ADD,
                        EntityData = newEntity,
                        Reasoning = $"Entities are distinct ({similarity:P1} similarity). Adding as new entity.",
                        Confidence = newEntity.Confidence,
                        SessionContext = sessionContext
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resolving entity conflict");
            throw;
        }
    }

    public Task<GraphDecisionInstruction> ResolveRelationshipConflictAsync(
        Relationship existingRelationship,
        Relationship newRelationship,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Resolving relationship conflict between existing '{ExistingType}' and new '{NewType}'",
            existingRelationship.RelationshipType, newRelationship.RelationshipType);

        try
        {
            // Check if relationships are essentially the same (same source, target, and type)
            if (AreRelationshipsEquivalent(existingRelationship, newRelationship))
            {
                // Same relationship - decide based on confidence and data quality
                if (newRelationship.Confidence < MinimumConfidenceThreshold)
                {
                    return Task.FromResult(new GraphDecisionInstruction
                    {
                        Operation = GraphDecisionOperation.NONE,
                        RelationshipData = existingRelationship,
                        Reasoning = "New relationship has low confidence below threshold.",
                        Confidence = existingRelationship.Confidence,
                        SessionContext = sessionContext
                    });
                }

                if (newRelationship.Confidence > existingRelationship.Confidence)
                {
                    // Higher confidence - update
                    var mergedRelationship = MergeRelationships(existingRelationship, newRelationship);
                    return Task.FromResult(new GraphDecisionInstruction
                    {
                        Operation = GraphDecisionOperation.UPDATE,
                        RelationshipData = mergedRelationship,
                        Reasoning = "New relationship has higher confidence. Updating existing relationship.",
                        Confidence = newRelationship.Confidence,
                        SessionContext = sessionContext
                    });
                }
                else if (newRelationship.Confidence < existingRelationship.Confidence)
                {
                    // Lower confidence - keep existing
                    return Task.FromResult(new GraphDecisionInstruction
                    {
                        Operation = GraphDecisionOperation.NONE,
                        RelationshipData = existingRelationship,
                        Reasoning = "New relationship has lower confidence. Keeping existing relationship.",
                        Confidence = existingRelationship.Confidence,
                        SessionContext = sessionContext
                    });
                }
                else
                {
                    // Same confidence - check for temporal context or other improvements
                    if (HasBetterRelationshipData(existingRelationship, newRelationship))
                    {
                        var mergedRelationship = MergeRelationships(existingRelationship, newRelationship);
                        return Task.FromResult(new GraphDecisionInstruction
                        {
                            Operation = GraphDecisionOperation.UPDATE,
                            RelationshipData = mergedRelationship,
                            Reasoning = "Merged equivalent relationships with updated temporal context and metadata.",
                            Confidence = newRelationship.Confidence,
                            SessionContext = sessionContext
                        });
                    }
                    else
                    {
                        return Task.FromResult(new GraphDecisionInstruction
                        {
                            Operation = GraphDecisionOperation.NONE,
                            RelationshipData = existingRelationship,
                            Reasoning = "Same confidence and no significant improvements.",
                            Confidence = existingRelationship.Confidence,
                            SessionContext = sessionContext
                        });
                    }
                }
            }
            else
            {
                // Different relationships - add as new
                return Task.FromResult(new GraphDecisionInstruction
                {
                    Operation = GraphDecisionOperation.ADD,
                    RelationshipData = newRelationship,
                    Reasoning = "Relationships are distinct. Adding as new relationship.",
                    Confidence = newRelationship.Confidence,
                    SessionContext = sessionContext
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resolving relationship conflict");
            throw;
        }
    }

    public Task<bool> ValidateGraphUpdateAsync(
        GraphDecisionInstruction instruction,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("VALIDATION START: Operation={Operation}, Confidence={Confidence}, HasEntity={HasEntity}, HasRelationship={HasRelationship}",
                instruction.Operation, instruction.Confidence, instruction.EntityData != null, instruction.RelationshipData != null);

            // Basic validation rules
            if (instruction.Confidence < MinimumConfidenceThreshold)
            {
                _logger.LogWarning("VALIDATION FAILED: confidence {Confidence} below threshold {Threshold}",
                    instruction.Confidence, MinimumConfidenceThreshold);
                return Task.FromResult(false);
            }

            // Validate entity data if present
            if (instruction.EntityData != null)
            {
                if (string.IsNullOrWhiteSpace(instruction.EntityData.Name))
                {
                    _logger.LogDebug("Update rejected: entity name is empty");
                    return Task.FromResult(false);
                }

                if (instruction.EntityData.Aliases?.Count > MaxAliasesPerEntity)
                {
                    _logger.LogDebug("Update rejected: too many aliases ({Count} > {Max})",
                        instruction.EntityData.Aliases.Count, MaxAliasesPerEntity);
                    return Task.FromResult(false);
                }
            }

            // Validate relationship data if present
            if (instruction.RelationshipData != null)
            {
                _logger.LogDebug("RELATIONSHIP VALIDATION: Source='{Source}', Target='{Target}', Type='{Type}'",
                    instruction.RelationshipData.Source ?? "NULL",
                    instruction.RelationshipData.Target ?? "NULL",
                    instruction.RelationshipData.RelationshipType ?? "NULL");

                if (string.IsNullOrWhiteSpace(instruction.RelationshipData.Source) ||
                    string.IsNullOrWhiteSpace(instruction.RelationshipData.Target) ||
                    string.IsNullOrWhiteSpace(instruction.RelationshipData.RelationshipType))
                {
                    _logger.LogWarning("VALIDATION FAILED: relationship has empty required fields - Source: '{Source}', Target: '{Target}', Type: '{Type}'",
                        instruction.RelationshipData.Source ?? "NULL",
                        instruction.RelationshipData.Target ?? "NULL",
                        instruction.RelationshipData.RelationshipType ?? "NULL");
                    return Task.FromResult(false);
                }

                // Check for self-referential relationships
                if (instruction.RelationshipData.Source.Equals(instruction.RelationshipData.Target, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("VALIDATION FAILED: self-referential relationship - Source: '{Source}', Target: '{Target}'",
                        instruction.RelationshipData.Source, instruction.RelationshipData.Target);
                    return Task.FromResult(false);
                }
            }

            _logger.LogDebug("VALIDATION PASSED: Operation={Operation}, Confidence={Confidence}",
                instruction.Operation, instruction.Confidence);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VALIDATION ERROR: Exception during validation for Operation={Operation}, Confidence={Confidence}",
                instruction.Operation, instruction.Confidence);
            return Task.FromResult(false);
        }
    }

    public Task<float> CalculateUpdateConfidenceAsync(
        GraphDecisionInstruction instruction,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var baseConfidence = instruction.Confidence;
            var adjustments = 0f;
            var adjustmentCount = 0;

            // Adjust based on operation type
            switch (instruction.Operation)
            {
                case GraphDecisionOperation.ADD:
                    adjustments += 0.1f; // Slight boost for new information
                    break;
                case GraphDecisionOperation.UPDATE:
                    adjustments += 0.05f; // Small boost for updates
                    break;
                case GraphDecisionOperation.DELETE:
                    adjustments -= 0.1f; // Penalty for deletions
                    break;
            }
            adjustmentCount++;

            // Adjust based on data quality
            if (instruction.EntityData != null)
            {
                // Boost confidence for entities with multiple aliases
                if (instruction.EntityData.Aliases?.Count > 1)
                {
                    adjustments += 0.05f;
                }

                // Boost confidence for entities with metadata
                if (instruction.EntityData.Metadata?.Count > 0)
                {
                    adjustments += 0.05f;
                }

                adjustmentCount++;
            }

            if (instruction.RelationshipData != null)
            {
                // Boost confidence for relationships with temporal context
                if (!string.IsNullOrWhiteSpace(instruction.RelationshipData.TemporalContext))
                {
                    adjustments += 0.05f;
                }

                // Boost confidence for relationships with metadata
                if (instruction.RelationshipData.Metadata?.Count > 0)
                {
                    adjustments += 0.05f;
                }

                adjustmentCount++;
            }

            // Apply adjustments
            var finalConfidence = baseConfidence + (adjustments / Math.Max(adjustmentCount, 1));

            // Ensure confidence stays within bounds
            return Task.FromResult(Math.Clamp(finalConfidence, 0f, 1f));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating update confidence");
            return Task.FromResult(instruction.Confidence); // Return original confidence on error
        }
    }

    #region Private Helper Methods

    private async Task<GraphDecisionInstruction?> AnalyzeEntityUpdateAsync(
        Entity entity,
        SessionContext sessionContext,
        CancellationToken cancellationToken)
    {
        // Check if entity already exists
        var existingEntity = await _graphRepository.GetEntityByNameAsync(entity.Name, sessionContext, cancellationToken);

        if (existingEntity != null)
        {
            return await ResolveEntityConflictAsync(existingEntity, entity, sessionContext, cancellationToken);
        }
        else
        {
            // New entity
            return new GraphDecisionInstruction
            {
                Operation = GraphDecisionOperation.ADD,
                EntityData = entity,
                Reasoning = "New entity not found in graph.",
                Confidence = entity.Confidence,
                SessionContext = sessionContext
            };
        }
    }

    private async Task<GraphDecisionInstruction?> AnalyzeRelationshipUpdateAsync(
        Relationship relationship,
        SessionContext sessionContext,
        CancellationToken cancellationToken)
    {
        // Check if similar relationship already exists
        var existingRelationships = await _graphRepository.GetRelationshipsAsync(sessionContext, limit: 1000, cancellationToken: cancellationToken);

        var conflictingRelationship = existingRelationships
            .Where(r => r.RelationshipType.Equals(relationship.RelationshipType, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(r =>
                r.Source.Equals(relationship.Source, StringComparison.OrdinalIgnoreCase) &&
                r.Target.Equals(relationship.Target, StringComparison.OrdinalIgnoreCase));

        if (conflictingRelationship != null)
        {
            return await ResolveRelationshipConflictAsync(conflictingRelationship, relationship, sessionContext, cancellationToken);
        }
        else
        {
            // New relationship
            return new GraphDecisionInstruction
            {
                Operation = GraphDecisionOperation.ADD,
                RelationshipData = relationship,
                Reasoning = "New relationship not found in graph.",
                Confidence = relationship.Confidence,
                SessionContext = sessionContext
            };
        }
    }

    private static float CalculateEntitySimilarity(Entity entity1, Entity entity2)
    {
        var score = 0f;
        var factors = 0;

        // Name similarity (most important)
        if (entity1.Name.Equals(entity2.Name, StringComparison.OrdinalIgnoreCase))
        {
            score += 0.5f;
        }
        else
        {
            score += CalculateStringSimilarity(entity1.Name, entity2.Name) * 0.5f;
        }
        factors++;

        // Type similarity
        if (!string.IsNullOrEmpty(entity1.Type) && !string.IsNullOrEmpty(entity2.Type))
        {
            if (entity1.Type.Equals(entity2.Type, StringComparison.OrdinalIgnoreCase))
            {
                score += 0.3f;
            }
            factors++;
        }

        // Alias overlap
        if (entity1.Aliases?.Count > 0 && entity2.Aliases?.Count > 0)
        {
            var commonAliases = entity1.Aliases.Intersect(entity2.Aliases, StringComparer.OrdinalIgnoreCase).Count();
            var totalAliases = entity1.Aliases.Union(entity2.Aliases, StringComparer.OrdinalIgnoreCase).Count();
            score += (float)commonAliases / totalAliases * 0.2f;
            factors++;
        }

        return factors > 0 ? score / factors : 0f;
    }

    private static float CalculateStringSimilarity(string str1, string str2)
    {
        if (string.IsNullOrEmpty(str1) || string.IsNullOrEmpty(str2))
            return 0f;

        // Simple Levenshtein distance-based similarity
        var distance = LevenshteinDistance(str1.ToLowerInvariant(), str2.ToLowerInvariant());
        var maxLength = Math.Max(str1.Length, str2.Length);
        return 1f - (float)distance / maxLength;
    }

    private static int LevenshteinDistance(string s1, string s2)
    {
        var matrix = new int[s1.Length + 1, s2.Length + 1];

        for (int i = 0; i <= s1.Length; i++)
            matrix[i, 0] = i;

        for (int j = 0; j <= s2.Length; j++)
            matrix[0, j] = j;

        for (int i = 1; i <= s1.Length; i++)
        {
            for (int j = 1; j <= s2.Length; j++)
            {
                var cost = s1[i - 1] == s2[j - 1] ? 0 : 1;
                matrix[i, j] = Math.Min(
                    Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
                    matrix[i - 1, j - 1] + cost);
            }
        }

        return matrix[s1.Length, s2.Length];
    }

    private static bool IsTypeRefinement(Entity existing, Entity newEntity)
    {
        // Check if new entity provides a more specific type
        if (string.IsNullOrEmpty(existing.Type) && !string.IsNullOrEmpty(newEntity.Type))
            return true;

        if (existing.Type == "unknown" && !string.IsNullOrEmpty(newEntity.Type) && newEntity.Type != "unknown")
            return true;

        return false;
    }

    private static bool HasBetterData(Entity existing, Entity newEntity)
    {
        // Check if new entity has more aliases
        var existingAliasCount = existing.Aliases?.Count ?? 0;
        var newAliasCount = newEntity.Aliases?.Count ?? 0;
        if (newAliasCount > existingAliasCount)
            return true;

        // Check if new entity has more metadata
        var existingMetadataCount = existing.Metadata?.Count ?? 0;
        var newMetadataCount = newEntity.Metadata?.Count ?? 0;
        if (newMetadataCount > existingMetadataCount)
            return true;

        // Check if new entity has more source memory IDs
        var existingSourceCount = existing.SourceMemoryIds?.Count ?? 0;
        var newSourceCount = newEntity.SourceMemoryIds?.Count ?? 0;
        if (newSourceCount > existingSourceCount)
            return true;

        return false;
    }

    private static Entity MergeEntities(Entity existing, Entity newEntity)
    {
        var merged = new Entity
        {
            Id = existing.Id,
            Name = existing.Name, // Keep existing name as primary
            Type = newEntity.Type ?? existing.Type, // Prefer new type if available
            UserId = existing.UserId,
            AgentId = existing.AgentId,
            RunId = existing.RunId,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = DateTime.UtcNow,
            Confidence = Math.Max(existing.Confidence, newEntity.Confidence),
            Version = existing.Version + 1
        };

        // Merge aliases
        var allAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (existing.Aliases != null)
            foreach (var alias in existing.Aliases)
                allAliases.Add(alias);
        if (newEntity.Aliases != null)
            foreach (var alias in newEntity.Aliases)
                allAliases.Add(alias);

        merged.Aliases = allAliases.Count > 0 ? allAliases.ToList() : null;

        // Merge source memory IDs
        var allSourceIds = new HashSet<int>();
        if (existing.SourceMemoryIds != null)
            foreach (var id in existing.SourceMemoryIds)
                allSourceIds.Add(id);
        if (newEntity.SourceMemoryIds != null)
            foreach (var id in newEntity.SourceMemoryIds)
                allSourceIds.Add(id);

        merged.SourceMemoryIds = allSourceIds.Count > 0 ? allSourceIds.ToList() : null;

        // Merge metadata
        merged.Metadata = new Dictionary<string, object>();
        if (existing.Metadata != null)
            foreach (var kvp in existing.Metadata)
                merged.Metadata[kvp.Key] = kvp.Value;
        if (newEntity.Metadata != null)
            foreach (var kvp in newEntity.Metadata)
                merged.Metadata[kvp.Key] = kvp.Value; // New metadata overwrites existing

        return merged;
    }

    private static Relationship MergeRelationships(Relationship existing, Relationship newRelationship)
    {
        return new Relationship
        {
            Id = existing.Id,
            Source = existing.Source,
            RelationshipType = existing.RelationshipType,
            Target = existing.Target,
            UserId = existing.UserId,
            AgentId = existing.AgentId,
            RunId = existing.RunId,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = DateTime.UtcNow,
            Confidence = Math.Max(existing.Confidence, newRelationship.Confidence),
            SourceMemoryId = newRelationship.SourceMemoryId ?? existing.SourceMemoryId,
            TemporalContext = newRelationship.TemporalContext ?? existing.TemporalContext,
            Metadata = MergeMetadata(existing.Metadata, newRelationship.Metadata),
            Version = existing.Version + 1
        };
    }

    private static Dictionary<string, object>? MergeMetadata(
        Dictionary<string, object>? existing,
        Dictionary<string, object>? newMetadata)
    {
        if (existing == null && newMetadata == null)
            return null;

        var merged = new Dictionary<string, object>();

        if (existing != null)
            foreach (var kvp in existing)
                merged[kvp.Key] = kvp.Value;

        if (newMetadata != null)
            foreach (var kvp in newMetadata)
                merged[kvp.Key] = kvp.Value; // New metadata overwrites existing

        return merged.Count > 0 ? merged : null;
    }

    private static bool AreRelationshipsEquivalent(Relationship rel1, Relationship rel2)
    {
        return rel1.Source.Equals(rel2.Source, StringComparison.OrdinalIgnoreCase) &&
               rel1.Target.Equals(rel2.Target, StringComparison.OrdinalIgnoreCase) &&
               rel1.RelationshipType.Equals(rel2.RelationshipType, StringComparison.OrdinalIgnoreCase);
    }

    private static float CalculateMergeConfidence(Entity existing, Entity newEntity, float similarity)
    {
        var baseConfidence = Math.Max(existing.Confidence, newEntity.Confidence);
        var similarityBonus = similarity * 0.1f; // Up to 10% bonus for high similarity
        return Math.Clamp(baseConfidence + similarityBonus, 0f, 1f);
    }

    private static bool HasBetterRelationshipData(Relationship existing, Relationship newRelationship)
    {
        // Check if new relationship has temporal context when existing doesn't
        if (string.IsNullOrEmpty(existing.TemporalContext) && !string.IsNullOrEmpty(newRelationship.TemporalContext))
            return true;

        // Check if new relationship has more metadata
        var existingMetadataCount = existing.Metadata?.Count ?? 0;
        var newMetadataCount = newRelationship.Metadata?.Count ?? 0;
        if (newMetadataCount > existingMetadataCount)
            return true;

        return false;
    }

    #endregion
}