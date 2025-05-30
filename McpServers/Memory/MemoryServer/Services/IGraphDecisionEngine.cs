using MemoryServer.Models;

namespace MemoryServer.Services;

/// <summary>
/// Interface for the graph decision engine that handles conflict resolution and update logic.
/// Implements the Strategy pattern for different decision-making approaches.
/// </summary>
public interface IGraphDecisionEngine
{
    /// <summary>
    /// Analyzes extracted entities and relationships to determine what updates should be made to the graph.
    /// </summary>
    /// <param name="extractedEntities">Entities extracted from conversation content.</param>
    /// <param name="extractedRelationships">Relationships extracted from conversation content.</param>
    /// <param name="sessionContext">Session context for isolation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of graph decision instructions.</returns>
    Task<List<GraphDecisionInstruction>> AnalyzeGraphUpdatesAsync(
        List<Entity> extractedEntities,
        List<Relationship> extractedRelationships,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves conflicts when multiple entities or relationships refer to the same real-world object.
    /// </summary>
    /// <param name="existingEntity">The existing entity in the graph.</param>
    /// <param name="newEntity">The newly extracted entity.</param>
    /// <param name="sessionContext">Session context for isolation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Graph decision instruction for resolving the conflict.</returns>
    Task<GraphDecisionInstruction> ResolveEntityConflictAsync(
        Entity existingEntity,
        Entity newEntity,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves conflicts when multiple relationships refer to the same connection.
    /// </summary>
    /// <param name="existingRelationship">The existing relationship in the graph.</param>
    /// <param name="newRelationship">The newly extracted relationship.</param>
    /// <param name="sessionContext">Session context for isolation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Graph decision instruction for resolving the conflict.</returns>
    Task<GraphDecisionInstruction> ResolveRelationshipConflictAsync(
        Relationship existingRelationship,
        Relationship newRelationship,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates that a proposed graph update is consistent and doesn't violate business rules.
    /// </summary>
    /// <param name="instruction">The graph decision instruction to validate.</param>
    /// <param name="sessionContext">Session context for isolation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the update is valid, false otherwise.</returns>
    Task<bool> ValidateGraphUpdateAsync(
        GraphDecisionInstruction instruction,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines the confidence score for a proposed graph update based on various factors.
    /// </summary>
    /// <param name="instruction">The graph decision instruction to score.</param>
    /// <param name="sessionContext">Session context for isolation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Confidence score between 0.0 and 1.0.</returns>
    Task<float> CalculateUpdateConfidenceAsync(
        GraphDecisionInstruction instruction,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default);
} 