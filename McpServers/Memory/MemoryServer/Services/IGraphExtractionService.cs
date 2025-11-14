using MemoryServer.Models;

namespace MemoryServer.Services;

/// <summary>
/// Interface for extracting entities and relationships from conversation content.
/// Uses LLM providers to analyze text and build knowledge graphs.
/// </summary>
public interface IGraphExtractionService
{
    /// <summary>
    /// Extracts entities from conversation content.
    /// </summary>
    /// <param name="content">The conversation content to analyze.</param>
    /// <param name="sessionContext">Session context for isolation.</param>
    /// <param name="memoryId">The memory ID this content belongs to.</param>
    /// <param name="modelId">Optional specific model ID to use instead of automatic selection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of extracted entities.</returns>
    Task<IEnumerable<Entity>> ExtractEntitiesAsync(
        string content,
        SessionContext sessionContext,
        int memoryId,
        string? modelId = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Extracts both entities and relationships from conversation content in a single operation.
    /// More efficient than calling both methods separately.
    /// </summary>
    /// <param name="content">The conversation content to analyze.</param>
    /// <param name="sessionContext">Session context for isolation.</param>
    /// <param name="memoryId">The memory ID this content belongs to.</param>
    /// <param name="modelId">Optional specific model ID to use instead of automatic selection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Tuple containing extracted entities and relationships.</returns>
    Task<(IEnumerable<Entity> Entities, IEnumerable<Relationship> Relationships)> ExtractGraphDataAsync(
        string content,
        SessionContext sessionContext,
        int memoryId,
        string? modelId = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Analyzes existing graph data and suggests updates based on new conversation content.
    /// </summary>
    /// <param name="content">The new conversation content.</param>
    /// <param name="existingEntities">Existing entities in the session.</param>
    /// <param name="existingRelationships">Existing relationships in the session.</param>
    /// <param name="sessionContext">Session context for isolation.</param>
    /// <param name="memoryId">The memory ID this content belongs to.</param>
    /// <param name="modelId">Optional specific model ID to use instead of automatic selection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Graph update instructions.</returns>
    Task<GraphUpdateInstructions> AnalyzeGraphUpdatesAsync(
        string content,
        IEnumerable<Entity> existingEntities,
        IEnumerable<Relationship> existingRelationships,
        SessionContext sessionContext,
        int memoryId,
        string? modelId = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Validates and cleans extracted entities to ensure consistency.
    /// </summary>
    /// <param name="entities">Raw extracted entities.</param>
    /// <param name="sessionContext">Session context for validation.</param>
    /// <param name="modelId">Optional specific model ID to use instead of automatic selection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Validated and cleaned entities.</returns>
    Task<IEnumerable<Entity>> ValidateAndCleanEntitiesAsync(
        IEnumerable<Entity> entities,
        SessionContext sessionContext,
        string? modelId = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Validates and cleans extracted relationships to ensure consistency.
    /// </summary>
    /// <param name="relationships">Raw extracted relationships.</param>
    /// <param name="sessionContext">Session context for validation.</param>
    /// <param name="modelId">Optional specific model ID to use instead of automatic selection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Validated and cleaned relationships.</returns>
    Task<IEnumerable<Relationship>> ValidateAndCleanRelationshipsAsync(
        IEnumerable<Relationship> relationships,
        SessionContext sessionContext,
        string? modelId = null,
        CancellationToken cancellationToken = default
    );
}
