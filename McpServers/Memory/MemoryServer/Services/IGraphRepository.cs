using MemoryServer.Models;

namespace MemoryServer.Services;

/// <summary>
///     Interface for graph database operations including entities and relationships.
///     Provides CRUD operations with session isolation and graph traversal capabilities.
/// </summary>
public interface IGraphRepository
{
    // Entity Operations

    /// <summary>
    ///     Adds a new entity to the graph database.
    /// </summary>
    /// <param name="entity">The entity to add.</param>
    /// <param name="sessionContext">Session context for isolation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The added entity with generated ID.</returns>
    Task<Entity> AddEntityAsync(
        Entity entity,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Gets an entity by its ID within the session context.
    /// </summary>
    /// <param name="entityId">The entity ID.</param>
    /// <param name="sessionContext">Session context for isolation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The entity if found, null otherwise.</returns>
    Task<Entity?> GetEntityByIdAsync(
        int entityId,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Gets an entity by its name within the session context.
    /// </summary>
    /// <param name="name">The entity name.</param>
    /// <param name="sessionContext">Session context for isolation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The entity if found, null otherwise.</returns>
    Task<Entity?> GetEntityByNameAsync(
        string name,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Gets all entities within the session context.
    /// </summary>
    /// <param name="sessionContext">Session context for isolation.</param>
    /// <param name="limit">Maximum number of entities to return.</param>
    /// <param name="offset">Number of entities to skip.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of entities.</returns>
    Task<IEnumerable<Entity>> GetEntitiesAsync(
        SessionContext sessionContext,
        int limit = 100,
        int offset = 0,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Updates an existing entity.
    /// </summary>
    /// <param name="entity">The entity to update.</param>
    /// <param name="sessionContext">Session context for validation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated entity.</returns>
    Task<Entity> UpdateEntityAsync(
        Entity entity,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Deletes an entity by ID.
    /// </summary>
    /// <param name="entityId">The entity ID to delete.</param>
    /// <param name="sessionContext">Session context for validation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if deleted, false if not found.</returns>
    Task<bool> DeleteEntityAsync(
        int entityId,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default
    );

    // Relationship Operations

    /// <summary>
    ///     Adds a new relationship to the graph database.
    /// </summary>
    /// <param name="relationship">The relationship to add.</param>
    /// <param name="sessionContext">Session context for isolation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The added relationship with generated ID.</returns>
    Task<Relationship> AddRelationshipAsync(
        Relationship relationship,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Gets a relationship by its ID within the session context.
    /// </summary>
    /// <param name="relationshipId">The relationship ID.</param>
    /// <param name="sessionContext">Session context for isolation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The relationship if found, null otherwise.</returns>
    Task<Relationship?> GetRelationshipByIdAsync(
        int relationshipId,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Gets all relationships within the session context.
    /// </summary>
    /// <param name="sessionContext">Session context for isolation.</param>
    /// <param name="limit">Maximum number of relationships to return.</param>
    /// <param name="offset">Number of relationships to skip.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of relationships.</returns>
    Task<IEnumerable<Relationship>> GetRelationshipsAsync(
        SessionContext sessionContext,
        int limit = 100,
        int offset = 0,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Gets relationships for a specific entity (as source or target).
    /// </summary>
    /// <param name="entityName">The entity name.</param>
    /// <param name="sessionContext">Session context for isolation.</param>
    /// <param name="asSource">
    ///     If true, get relationships where entity is source; if false, where entity is target; if null,
    ///     both.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of relationships.</returns>
    Task<IEnumerable<Relationship>> GetRelationshipsForEntityAsync(
        string entityName,
        SessionContext sessionContext,
        bool? asSource = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Updates an existing relationship.
    /// </summary>
    /// <param name="relationship">The relationship to update.</param>
    /// <param name="sessionContext">Session context for validation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated relationship.</returns>
    Task<Relationship> UpdateRelationshipAsync(
        Relationship relationship,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Deletes a relationship by ID.
    /// </summary>
    /// <param name="relationshipId">The relationship ID to delete.</param>
    /// <param name="sessionContext">Session context for validation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if deleted, false if not found.</returns>
    Task<bool> DeleteRelationshipAsync(
        int relationshipId,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default
    );

    // Graph Traversal Operations

    /// <summary>
    ///     Finds connected entities using graph traversal.
    /// </summary>
    /// <param name="startEntityName">The starting entity name.</param>
    /// <param name="sessionContext">Session context for isolation.</param>
    /// <param name="maxDepth">Maximum traversal depth.</param>
    /// <param name="relationshipTypes">Optional filter for relationship types.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of connected entities with their relationships.</returns>
    Task<IEnumerable<(Entity Entity, Relationship? Relationship, int Depth)>> TraverseGraphAsync(
        string startEntityName,
        SessionContext sessionContext,
        int maxDepth = 2,
        IEnumerable<string>? relationshipTypes = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Searches for relationships matching a query.
    /// </summary>
    /// <param name="query">Search query for relationship content.</param>
    /// <param name="sessionContext">Session context for isolation.</param>
    /// <param name="limit">Maximum number of results.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of matching relationships with relevance scores.</returns>
    Task<IEnumerable<Relationship>> SearchRelationshipsAsync(
        string query,
        SessionContext sessionContext,
        int limit = 10,
        CancellationToken cancellationToken = default
    );

    // Enhanced Search Operations for Phase 6

    /// <summary>
    ///     Searches for entities matching a query using FTS5 full-text search.
    /// </summary>
    /// <param name="query">Search query for entity content.</param>
    /// <param name="sessionContext">Session context for isolation.</param>
    /// <param name="limit">Maximum number of results.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of matching entities with relevance scores.</returns>
    Task<IEnumerable<Entity>> SearchEntitiesAsync(
        string query,
        SessionContext sessionContext,
        int limit = 10,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Performs vector similarity search to find entities similar to the query embedding.
    /// </summary>
    /// <param name="queryEmbedding">The query embedding vector.</param>
    /// <param name="sessionContext">Session context for isolation.</param>
    /// <param name="limit">Maximum number of results to return.</param>
    /// <param name="threshold">Minimum similarity threshold (0.0 to 1.0).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of entities with similarity scores.</returns>
    Task<List<EntityVectorSearchResult>> SearchEntitiesVectorAsync(
        float[] queryEmbedding,
        SessionContext sessionContext,
        int limit = 10,
        float threshold = 0.7f,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Performs vector similarity search to find relationships similar to the query embedding.
    /// </summary>
    /// <param name="queryEmbedding">The query embedding vector.</param>
    /// <param name="sessionContext">Session context for isolation.</param>
    /// <param name="limit">Maximum number of results to return.</param>
    /// <param name="threshold">Minimum similarity threshold (0.0 to 1.0).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of relationships with similarity scores.</returns>
    Task<List<RelationshipVectorSearchResult>> SearchRelationshipsVectorAsync(
        float[] queryEmbedding,
        SessionContext sessionContext,
        int limit = 10,
        float threshold = 0.7f,
        CancellationToken cancellationToken = default
    );

    // Embedding Storage Operations

    /// <summary>
    ///     Stores an embedding for an entity.
    /// </summary>
    /// <param name="entityId">The ID of the entity.</param>
    /// <param name="embedding">The embedding vector.</param>
    /// <param name="modelName">The name of the embedding model used.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task StoreEntityEmbeddingAsync(
        int entityId,
        float[] embedding,
        string modelName,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Stores an embedding for a relationship.
    /// </summary>
    /// <param name="relationshipId">The ID of the relationship.</param>
    /// <param name="embedding">The embedding vector.</param>
    /// <param name="modelName">The name of the embedding model used.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task StoreRelationshipEmbeddingAsync(
        int relationshipId,
        float[] embedding,
        string modelName,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Gets the embedding for a specific entity.
    /// </summary>
    /// <param name="entityId">The ID of the entity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The embedding vector or null if not found.</returns>
    Task<float[]?> GetEntityEmbeddingAsync(int entityId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets the embedding for a specific relationship.
    /// </summary>
    /// <param name="relationshipId">The ID of the relationship.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The embedding vector or null if not found.</returns>
    Task<float[]?> GetRelationshipEmbeddingAsync(int relationshipId, CancellationToken cancellationToken = default);

    // Utility Operations

    /// <summary>
    ///     Generates the next available integer ID for entities or relationships.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The next available integer ID.</returns>
    Task<int> GenerateNextIdAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets statistics about the graph database for a session.
    /// </summary>
    /// <param name="sessionContext">Session context for isolation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Graph statistics.</returns>
    Task<GraphStatistics> GetGraphStatisticsAsync(
        SessionContext sessionContext,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
///     Statistics about the graph database for a session.
/// </summary>
public class GraphStatistics
{
    /// <summary>
    ///     Total number of entities in the session.
    /// </summary>
    public int EntityCount { get; set; }

    /// <summary>
    ///     Total number of relationships in the session.
    /// </summary>
    public int RelationshipCount { get; set; }

    /// <summary>
    ///     Number of unique relationship types.
    /// </summary>
    public int UniqueRelationshipTypes { get; set; }

    /// <summary>
    ///     Most common relationship types with their counts.
    /// </summary>
    public Dictionary<string, int> TopRelationshipTypes { get; set; } = [];

    /// <summary>
    ///     Entities with the most connections.
    /// </summary>
    public Dictionary<string, int> TopConnectedEntities { get; set; } = [];
}

/// <summary>
///     Result of an entity vector similarity search.
/// </summary>
public class EntityVectorSearchResult
{
    /// <summary>
    ///     The entity that matched the search.
    /// </summary>
    public Entity Entity { get; set; } = new();

    /// <summary>
    ///     Similarity score (0.0 to 1.0, higher is more similar).
    /// </summary>
    public float Score { get; set; }

    /// <summary>
    ///     Distance value from the vector search.
    /// </summary>
    public float Distance { get; set; }
}

/// <summary>
///     Result of a relationship vector similarity search.
/// </summary>
public class RelationshipVectorSearchResult
{
    /// <summary>
    ///     The relationship that matched the search.
    /// </summary>
    public Relationship Relationship { get; set; } = new();

    /// <summary>
    ///     Similarity score (0.0 to 1.0, higher is more similar).
    /// </summary>
    public float Score { get; set; }

    /// <summary>
    ///     Distance value from the vector search.
    /// </summary>
    public float Distance { get; set; }
}
