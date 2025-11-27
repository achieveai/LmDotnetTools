using System.Data;
using System.Globalization;
using System.Text.Json;
using MemoryServer.Infrastructure;
using MemoryServer.Models;
using Microsoft.Data.Sqlite;

namespace MemoryServer.Services;

/// <summary>
///     Implementation of graph database operations using SQLite with Database Session Pattern.
///     Provides CRUD operations for entities and relationships with session isolation.
/// </summary>
public class GraphRepository : IGraphRepository
{
    private readonly ILogger<GraphRepository> _logger;
    private readonly ISqliteSessionFactory _sessionFactory;

    public GraphRepository(ISqliteSessionFactory sessionFactory, ILogger<GraphRepository> logger)
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #region Entity Operations

    public async Task<Entity> AddEntityAsync(
        Entity entity,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

        return await session.ExecuteInTransactionAsync(
            async (connection, transaction) =>
            {
                // Generate ID if not provided
                if (entity.Id == 0)
                {
                    entity.Id = await GenerateNextIdAsync(connection, transaction, cancellationToken);
                }

                // Set session context
                entity.UserId = sessionContext.UserId;
                entity.AgentId = sessionContext.AgentId;
                entity.RunId = sessionContext.RunId;
                entity.CreatedAt = DateTime.UtcNow;
                entity.UpdatedAt = DateTime.UtcNow;

                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    @"
                INSERT INTO entities (id, name, type, aliases, user_id, agent_id, run_id, created_at, updated_at, 
                                    confidence, source_memory_ids, metadata, version)
                VALUES (@id, @name, @type, @aliases, @userId, @agentId, @runId, @createdAt, @updatedAt, 
                        @confidence, @sourceMemoryIds, @metadata, @version)";

                _ = command.Parameters.AddWithValue("@id", entity.Id);
                _ = command.Parameters.AddWithValue("@name", entity.Name);
                _ = command.Parameters.AddWithValue("@type", entity.Type ?? (object)DBNull.Value);
                _ = command.Parameters.AddWithValue("@aliases", JsonSerializer.Serialize(entity.Aliases ?? []));
                _ = command.Parameters.AddWithValue("@userId", entity.UserId);
                _ = command.Parameters.AddWithValue("@agentId", entity.AgentId ?? (object)DBNull.Value);
                _ = command.Parameters.AddWithValue("@runId", entity.RunId ?? (object)DBNull.Value);
                _ = command.Parameters.AddWithValue("@createdAt", entity.CreatedAt);
                _ = command.Parameters.AddWithValue("@updatedAt", entity.UpdatedAt);
                _ = command.Parameters.AddWithValue("@confidence", entity.Confidence);
                _ = command.Parameters.AddWithValue(
                    "@sourceMemoryIds",
                    JsonSerializer.Serialize(entity.SourceMemoryIds ?? [])
                );
                _ = command.Parameters.AddWithValue("@metadata", JsonSerializer.Serialize(entity.Metadata ?? []));
                _ = command.Parameters.AddWithValue("@version", entity.Version);

                _ = await command.ExecuteNonQueryAsync(cancellationToken);

                _logger.LogInformation(
                    "Added entity {EntityId} '{EntityName}' for session {SessionContext}",
                    entity.Id,
                    entity.Name,
                    sessionContext
                );
                return entity;
            },
            cancellationToken
        );
    }

    public async Task<Entity?> GetEntityByIdAsync(
        int entityId,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

        return await session.ExecuteAsync(
            async connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    @"
                SELECT id, name, type, aliases, user_id, agent_id, run_id, created_at, updated_at, 
                       confidence, source_memory_ids, metadata, version
                FROM entities 
                WHERE id = @entityId 
                  AND user_id = @userId 
                  AND (@agentId IS NULL OR agent_id = @agentId)
                  AND (@runId IS NULL OR run_id = @runId)";

                _ = command.Parameters.AddWithValue("@entityId", entityId);
                _ = command.Parameters.AddWithValue("@userId", sessionContext.UserId);
                _ = command.Parameters.AddWithValue("@agentId", sessionContext.AgentId ?? (object)DBNull.Value);
                _ = command.Parameters.AddWithValue("@runId", sessionContext.RunId ?? (object)DBNull.Value);

                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                return await reader.ReadAsync(cancellationToken) ? MapEntityFromReader(reader) : null;
            },
            cancellationToken
        );
    }

    public async Task<Entity?> GetEntityByNameAsync(
        string name,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

        return await session.ExecuteAsync(
            async connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    @"
                SELECT id, name, type, aliases, user_id, agent_id, run_id, created_at, updated_at, 
                       confidence, source_memory_ids, metadata, version
                FROM entities 
                WHERE name = @name 
                  AND user_id = @userId 
                  AND (@agentId IS NULL OR agent_id = @agentId)
                  AND (@runId IS NULL OR run_id = @runId)
                ORDER BY created_at DESC
                LIMIT 1";

                _ = command.Parameters.AddWithValue("@name", name);
                _ = command.Parameters.AddWithValue("@userId", sessionContext.UserId);
                _ = command.Parameters.AddWithValue("@agentId", sessionContext.AgentId ?? (object)DBNull.Value);
                _ = command.Parameters.AddWithValue("@runId", sessionContext.RunId ?? (object)DBNull.Value);

                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                return await reader.ReadAsync(cancellationToken) ? MapEntityFromReader(reader) : null;
            },
            cancellationToken
        );
    }

    public async Task<IEnumerable<Entity>> GetEntitiesAsync(
        SessionContext sessionContext,
        int limit = 100,
        int offset = 0,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

        return await session.ExecuteAsync(
            async connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    @"
                SELECT id, name, type, aliases, user_id, agent_id, run_id, created_at, updated_at, 
                       confidence, source_memory_ids, metadata, version
                FROM entities 
                WHERE user_id = @userId 
                  AND (@agentId IS NULL OR agent_id = @agentId)
                  AND (@runId IS NULL OR run_id = @runId)
                ORDER BY created_at DESC
                LIMIT @limit OFFSET @offset";

                _ = command.Parameters.AddWithValue("@userId", sessionContext.UserId);
                _ = command.Parameters.AddWithValue("@agentId", sessionContext.AgentId ?? (object)DBNull.Value);
                _ = command.Parameters.AddWithValue("@runId", sessionContext.RunId ?? (object)DBNull.Value);
                _ = command.Parameters.AddWithValue("@limit", limit);
                _ = command.Parameters.AddWithValue("@offset", offset);

                var entities = new List<Entity>();
                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    entities.Add(MapEntityFromReader(reader));
                }

                return entities;
            },
            cancellationToken
        );
    }

    public async Task<Entity> UpdateEntityAsync(
        Entity entity,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

        return await session.ExecuteInTransactionAsync(
            async (connection, transaction) =>
            {
                // Verify entity exists and belongs to session
                var existing =
                    await GetEntityByIdInternalAsync(connection, entity.Id, sessionContext, cancellationToken)
                    ?? throw new InvalidOperationException(
                        $"Entity {entity.Id} not found or does not belong to session"
                    );
                entity.UpdatedAt = DateTime.UtcNow;
                entity.Version = existing.Version + 1;

                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    @"
                UPDATE entities 
                SET name = @name, type = @type, aliases = @aliases, updated_at = @updatedAt, 
                    confidence = @confidence, source_memory_ids = @sourceMemoryIds, metadata = @metadata, version = @version
                WHERE id = @id 
                  AND user_id = @userId 
                  AND (@agentId IS NULL OR agent_id = @agentId)
                  AND (@runId IS NULL OR run_id = @runId)
                  AND version = @currentVersion";

                _ = command.Parameters.AddWithValue("@id", entity.Id);
                _ = command.Parameters.AddWithValue("@name", entity.Name);
                _ = command.Parameters.AddWithValue("@type", entity.Type ?? (object)DBNull.Value);
                _ = command.Parameters.AddWithValue("@aliases", JsonSerializer.Serialize(entity.Aliases ?? []));
                _ = command.Parameters.AddWithValue("@updatedAt", entity.UpdatedAt);
                _ = command.Parameters.AddWithValue("@confidence", entity.Confidence);
                _ = command.Parameters.AddWithValue(
                    "@sourceMemoryIds",
                    JsonSerializer.Serialize(entity.SourceMemoryIds ?? [])
                );
                _ = command.Parameters.AddWithValue("@metadata", JsonSerializer.Serialize(entity.Metadata ?? []));
                _ = command.Parameters.AddWithValue("@version", entity.Version);
                _ = command.Parameters.AddWithValue("@userId", sessionContext.UserId);
                _ = command.Parameters.AddWithValue("@agentId", sessionContext.AgentId ?? (object)DBNull.Value);
                _ = command.Parameters.AddWithValue("@runId", sessionContext.RunId ?? (object)DBNull.Value);
                _ = command.Parameters.AddWithValue("@currentVersion", existing.Version);

                var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);
                if (rowsAffected == 0)
                {
                    throw new InvalidOperationException($"Entity {entity.Id} could not be updated");
                }

                _logger.LogInformation(
                    "Updated entity {EntityId} '{EntityName}' for session {SessionContext}",
                    entity.Id,
                    entity.Name,
                    sessionContext
                );
                return entity;
            },
            cancellationToken
        );
    }

    public async Task<bool> DeleteEntityAsync(
        int entityId,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

        return await session.ExecuteInTransactionAsync(
            async (connection, transaction) =>
            {
                // First delete related relationships
                using var deleteRelationshipsCommand = connection.CreateCommand();
                deleteRelationshipsCommand.Transaction = transaction;
                deleteRelationshipsCommand.CommandText =
                    @"
                DELETE FROM relationships 
                WHERE (source_entity_name = (SELECT name FROM entities WHERE id = @entityId) 
                       OR target_entity_name = (SELECT name FROM entities WHERE id = @entityId))
                  AND user_id = @userId 
                  AND (@agentId IS NULL OR agent_id = @agentId)
                  AND (@runId IS NULL OR run_id = @runId)";

                _ = deleteRelationshipsCommand.Parameters.AddWithValue("@entityId", entityId);
                _ = deleteRelationshipsCommand.Parameters.AddWithValue("@userId", sessionContext.UserId);
                _ = deleteRelationshipsCommand.Parameters.AddWithValue(
                    "@agentId",
                    sessionContext.AgentId ?? (object)DBNull.Value
                );
                _ = deleteRelationshipsCommand.Parameters.AddWithValue(
                    "@runId",
                    sessionContext.RunId ?? (object)DBNull.Value
                );

                _ = await deleteRelationshipsCommand.ExecuteNonQueryAsync(cancellationToken);

                // Then delete the entity
                using var deleteEntityCommand = connection.CreateCommand();
                deleteEntityCommand.Transaction = transaction;
                deleteEntityCommand.CommandText =
                    @"
                DELETE FROM entities 
                WHERE id = @entityId 
                  AND user_id = @userId 
                  AND (@agentId IS NULL OR agent_id = @agentId)
                  AND (@runId IS NULL OR run_id = @runId)";

                _ = deleteEntityCommand.Parameters.AddWithValue("@entityId", entityId);
                _ = deleteEntityCommand.Parameters.AddWithValue("@userId", sessionContext.UserId);
                _ = deleteEntityCommand.Parameters.AddWithValue(
                    "@agentId",
                    sessionContext.AgentId ?? (object)DBNull.Value
                );
                _ = deleteEntityCommand.Parameters.AddWithValue("@runId", sessionContext.RunId ?? (object)DBNull.Value);

                var rowsAffected = await deleteEntityCommand.ExecuteNonQueryAsync(cancellationToken);

                if (rowsAffected > 0)
                {
                    _logger.LogInformation(
                        "Deleted entity {EntityId} for session {SessionContext}",
                        entityId,
                        sessionContext
                    );
                    return true;
                }

                return false;
            },
            cancellationToken
        );
    }

    #endregion

    #region Relationship Operations

    public async Task<Relationship> AddRelationshipAsync(
        Relationship relationship,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

        return await session.ExecuteInTransactionAsync(
            async (connection, transaction) =>
            {
                // Generate ID if not provided
                if (relationship.Id == 0)
                {
                    relationship.Id = await GenerateNextIdAsync(connection, transaction, cancellationToken);
                }

                // Set session context
                relationship.UserId = sessionContext.UserId;
                relationship.AgentId = sessionContext.AgentId;
                relationship.RunId = sessionContext.RunId;
                relationship.CreatedAt = DateTime.UtcNow;
                relationship.UpdatedAt = DateTime.UtcNow;

                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    @"
                INSERT INTO relationships (id, source_entity_name, relationship_type, target_entity_name, user_id, agent_id, run_id,
                                         created_at, updated_at, confidence, source_memory_id, temporal_context, metadata, version)
                VALUES (@id, @source, @relationshipType, @target, @userId, @agentId, @runId,
                        @createdAt, @updatedAt, @confidence, @sourceMemoryId, @temporalContext, @metadata, @version)";

                _ = command.Parameters.AddWithValue("@id", relationship.Id);
                _ = command.Parameters.AddWithValue("@source", relationship.Source);
                _ = command.Parameters.AddWithValue("@relationshipType", relationship.RelationshipType);
                _ = command.Parameters.AddWithValue("@target", relationship.Target);
                _ = command.Parameters.AddWithValue("@userId", relationship.UserId);
                _ = command.Parameters.AddWithValue("@agentId", relationship.AgentId ?? (object)DBNull.Value);
                _ = command.Parameters.AddWithValue("@runId", relationship.RunId ?? (object)DBNull.Value);
                _ = command.Parameters.AddWithValue("@createdAt", relationship.CreatedAt);
                _ = command.Parameters.AddWithValue("@updatedAt", relationship.UpdatedAt);
                _ = command.Parameters.AddWithValue("@confidence", relationship.Confidence);
                _ = command.Parameters.AddWithValue(
                    "@sourceMemoryId",
                    relationship.SourceMemoryId ?? (object)DBNull.Value
                );
                _ = command.Parameters.AddWithValue(
                    "@temporalContext",
                    relationship.TemporalContext ?? (object)DBNull.Value
                );
                _ = command.Parameters.AddWithValue("@metadata", JsonSerializer.Serialize(relationship.Metadata ?? []));
                _ = command.Parameters.AddWithValue("@version", relationship.Version);

                _ = await command.ExecuteNonQueryAsync(cancellationToken);

                _logger.LogInformation(
                    "Added relationship {RelationshipId} '{Source}' -> '{Target}' for session {SessionContext}",
                    relationship.Id,
                    relationship.Source,
                    relationship.Target,
                    sessionContext
                );
                return relationship;
            },
            cancellationToken
        );
    }

    public async Task<Relationship?> GetRelationshipByIdAsync(
        int relationshipId,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

        return await session.ExecuteAsync(
            async connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    @"
                SELECT id, source_entity_name, relationship_type, target_entity_name, user_id, agent_id, run_id, created_at, updated_at,
                       confidence, source_memory_id, temporal_context, metadata, version
                FROM relationships 
                WHERE id = @relationshipId 
                  AND user_id = @userId 
                  AND (@agentId IS NULL OR agent_id = @agentId)
                  AND (@runId IS NULL OR run_id = @runId)";

                _ = command.Parameters.AddWithValue("@relationshipId", relationshipId);
                _ = command.Parameters.AddWithValue("@userId", sessionContext.UserId);
                _ = command.Parameters.AddWithValue("@agentId", sessionContext.AgentId ?? (object)DBNull.Value);
                _ = command.Parameters.AddWithValue("@runId", sessionContext.RunId ?? (object)DBNull.Value);

                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                return await reader.ReadAsync(cancellationToken) ? MapRelationshipFromReader(reader) : null;
            },
            cancellationToken
        );
    }

    public async Task<IEnumerable<Relationship>> GetRelationshipsAsync(
        SessionContext sessionContext,
        int limit = 100,
        int offset = 0,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

        return await session.ExecuteAsync(
            async connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    @"
                SELECT id, source_entity_name, relationship_type, target_entity_name, user_id, agent_id, run_id, created_at, updated_at,
                       confidence, source_memory_id, temporal_context, metadata, version
                FROM relationships 
                WHERE user_id = @userId 
                  AND (@agentId IS NULL OR agent_id = @agentId)
                  AND (@runId IS NULL OR run_id = @runId)
                ORDER BY created_at DESC
                LIMIT @limit OFFSET @offset";

                _ = command.Parameters.AddWithValue("@userId", sessionContext.UserId);
                _ = command.Parameters.AddWithValue("@agentId", sessionContext.AgentId ?? (object)DBNull.Value);
                _ = command.Parameters.AddWithValue("@runId", sessionContext.RunId ?? (object)DBNull.Value);
                _ = command.Parameters.AddWithValue("@limit", limit);
                _ = command.Parameters.AddWithValue("@offset", offset);

                var relationships = new List<Relationship>();
                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    relationships.Add(MapRelationshipFromReader(reader));
                }

                return relationships;
            },
            cancellationToken
        );
    }

    public async Task<IEnumerable<Relationship>> GetRelationshipsForEntityAsync(
        string entityName,
        SessionContext sessionContext,
        bool? asSource = null,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

        return await session.ExecuteAsync(
            async connection =>
            {
                var whereClause = asSource switch
                {
                    true => "source_entity_name = @entityName",
                    false => "target_entity_name = @entityName",
                    null => "(source_entity_name = @entityName OR target_entity_name = @entityName)",
                };

                using var command = connection.CreateCommand();
                command.CommandText =
                    $@"
                SELECT id, source_entity_name, relationship_type, target_entity_name, user_id, agent_id, run_id, created_at, updated_at,
                       confidence, source_memory_id, temporal_context, metadata, version
                FROM relationships 
                WHERE {whereClause}
                  AND user_id = @userId 
                  AND (@agentId IS NULL OR agent_id = @agentId)
                  AND (@runId IS NULL OR run_id = @runId)
                ORDER BY created_at DESC";

                _ = command.Parameters.AddWithValue("@entityName", entityName);
                _ = command.Parameters.AddWithValue("@userId", sessionContext.UserId);
                _ = command.Parameters.AddWithValue("@agentId", sessionContext.AgentId ?? (object)DBNull.Value);
                _ = command.Parameters.AddWithValue("@runId", sessionContext.RunId ?? (object)DBNull.Value);

                var relationships = new List<Relationship>();
                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    relationships.Add(MapRelationshipFromReader(reader));
                }

                return relationships;
            },
            cancellationToken
        );
    }

    public async Task<Relationship> UpdateRelationshipAsync(
        Relationship relationship,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

        return await session.ExecuteInTransactionAsync(
            async (connection, transaction) =>
            {
                // Verify relationship exists and belongs to session
                var existing =
                    await GetRelationshipByIdAsync(relationship.Id, sessionContext, cancellationToken)
                    ?? throw new InvalidOperationException(
                        $"Relationship {relationship.Id} not found or does not belong to session"
                    );
                relationship.UpdatedAt = DateTime.UtcNow;
                relationship.Version = existing.Version + 1;

                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    @"
                UPDATE relationships 
                SET source_entity_name = @source, relationship_type = @relationshipType, target_entity_name = @target,
                    confidence = @confidence, source_memory_id = @sourceMemoryId, 
                    temporal_context = @temporalContext, metadata = @metadata,
                    updated_at = @updatedAt, version = @version
                WHERE id = @id AND user_id = @userId 
                  AND (@agentId IS NULL OR agent_id = @agentId)
                  AND (@runId IS NULL OR run_id = @runId)";

                _ = command.Parameters.AddWithValue("@id", relationship.Id);
                _ = command.Parameters.AddWithValue("@source", relationship.Source);
                _ = command.Parameters.AddWithValue("@relationshipType", relationship.RelationshipType);
                _ = command.Parameters.AddWithValue("@target", relationship.Target);
                _ = command.Parameters.AddWithValue("@confidence", relationship.Confidence);
                _ = command.Parameters.AddWithValue(
                    "@sourceMemoryId",
                    relationship.SourceMemoryId ?? (object)DBNull.Value
                );
                _ = command.Parameters.AddWithValue(
                    "@temporalContext",
                    relationship.TemporalContext ?? (object)DBNull.Value
                );
                _ = command.Parameters.AddWithValue("@metadata", JsonSerializer.Serialize(relationship.Metadata ?? []));
                _ = command.Parameters.AddWithValue("@updatedAt", relationship.UpdatedAt);
                _ = command.Parameters.AddWithValue("@version", relationship.Version);
                _ = command.Parameters.AddWithValue("@userId", sessionContext.UserId);
                _ = command.Parameters.AddWithValue("@agentId", sessionContext.AgentId ?? (object)DBNull.Value);
                _ = command.Parameters.AddWithValue("@runId", sessionContext.RunId ?? (object)DBNull.Value);

                var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);
                if (rowsAffected == 0)
                {
                    throw new InvalidOperationException($"Relationship {relationship.Id} could not be updated");
                }

                _logger.LogInformation(
                    "Updated relationship {RelationshipId} '{Source} {RelationshipType} {Target}' for session {SessionContext}",
                    relationship.Id,
                    relationship.Source,
                    relationship.RelationshipType,
                    relationship.Target,
                    sessionContext
                );
                return relationship;
            },
            cancellationToken
        );
    }

    public async Task<bool> DeleteRelationshipAsync(
        int relationshipId,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

        return await session.ExecuteInTransactionAsync(
            async (connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    @"
                DELETE FROM relationships 
                WHERE id = @relationshipId 
                  AND user_id = @userId 
                  AND (@agentId IS NULL OR agent_id = @agentId)
                  AND (@runId IS NULL OR run_id = @runId)";

                _ = command.Parameters.AddWithValue("@relationshipId", relationshipId);
                _ = command.Parameters.AddWithValue("@userId", sessionContext.UserId);
                _ = command.Parameters.AddWithValue("@agentId", sessionContext.AgentId ?? (object)DBNull.Value);
                _ = command.Parameters.AddWithValue("@runId", sessionContext.RunId ?? (object)DBNull.Value);

                var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);

                if (rowsAffected > 0)
                {
                    _logger.LogInformation(
                        "Deleted relationship {RelationshipId} for session {SessionContext}",
                        relationshipId,
                        sessionContext
                    );
                    return true;
                }

                return false;
            },
            cancellationToken
        );
    }

    #endregion

    #region Graph Traversal Operations

    public async Task<IEnumerable<(Entity Entity, Relationship? Relationship, int Depth)>> TraverseGraphAsync(
        string startEntityName,
        SessionContext sessionContext,
        int maxDepth = 2,
        IEnumerable<string>? relationshipTypes = null,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

        return await session.ExecuteAsync(
            async connection =>
            {
                var relationshipFilter =
                    relationshipTypes != null && relationshipTypes.Any()
                        ? $"AND r.relationship_type IN ({string.Join(",", relationshipTypes.Select((_, i) => $"@relType{i}"))})"
                        : "";

                using var command = connection.CreateCommand();
                command.CommandText =
                    $@"
                WITH RECURSIVE graph_traversal(entity_name, relationship_id, depth) AS (
                    -- Base case: start with the initial entity
                    SELECT @startEntity as entity_name, NULL as relationship_id, 0 as depth
                    
                    UNION ALL
                    
                    -- Recursive case: find connected entities
                    SELECT 
                        CASE 
                            WHEN r.source_entity_name = gt.entity_name THEN r.target_entity_name
                            ELSE r.source_entity_name
                        END as entity_name,
                        r.id as relationship_id,
                        gt.depth + 1
                    FROM graph_traversal gt
                    JOIN relationships r ON (r.source_entity_name = gt.entity_name OR r.target_entity_name = gt.entity_name)
                    WHERE gt.depth < @maxDepth
                      AND r.user_id = @userId 
                      AND (@agentId IS NULL OR r.agent_id = @agentId)
                      AND (@runId IS NULL OR r.run_id = @runId)
                      {relationshipFilter}
                )
                SELECT DISTINCT
                    e.id, e.name, e.type, e.aliases, e.user_id, e.agent_id, e.run_id, 
                    e.created_at, e.updated_at, e.confidence, e.source_memory_ids, e.metadata, e.version,
                    r.id as rel_id, r.source_entity_name as rel_source, r.relationship_type as rel_relationship_type, 
                    r.target_entity_name as rel_target, r.user_id as rel_user_id, r.agent_id as rel_agent_id, 
                    r.run_id as rel_run_id, r.created_at as rel_created_at, r.updated_at as rel_updated_at, 
                    r.confidence as rel_confidence, r.source_memory_id as rel_source_memory_id, 
                    r.temporal_context as rel_temporal_context, r.metadata as rel_metadata, r.version as rel_version,
                    gt.depth
                FROM graph_traversal gt
                JOIN entities e ON e.name = gt.entity_name 
                  AND e.user_id = @userId 
                  AND (@agentId IS NULL OR e.agent_id = @agentId)
                  AND (@runId IS NULL OR e.run_id = @runId)
                LEFT JOIN relationships r ON r.id = gt.relationship_id
                ORDER BY gt.depth, e.name";

                _ = command.Parameters.AddWithValue("@startEntity", startEntityName);
                _ = command.Parameters.AddWithValue("@maxDepth", maxDepth);
                _ = command.Parameters.AddWithValue("@userId", sessionContext.UserId);
                _ = command.Parameters.AddWithValue("@agentId", sessionContext.AgentId ?? (object)DBNull.Value);
                _ = command.Parameters.AddWithValue("@runId", sessionContext.RunId ?? (object)DBNull.Value);

                // Add relationship type filters if provided
                if (relationshipTypes != null)
                {
                    var relTypes = relationshipTypes.ToArray();
                    for (var i = 0; i < relTypes.Length; i++)
                    {
                        _ = command.Parameters.AddWithValue($"@relType{i}", relTypes[i]);
                    }
                }

                var results = new List<(Entity Entity, Relationship? Relationship, int Depth)>();
                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var entity = MapEntityFromReader(reader);
                    var relationship = reader.IsDBNull("rel_id") ? null : MapRelationshipFromReader(reader, "rel_");
                    var depth = Convert.ToInt32(reader["depth"]);

                    results.Add((entity, relationship, depth));
                }

                return results;
            },
            cancellationToken
        );
    }

    public async Task<IEnumerable<Relationship>> SearchRelationshipsAsync(
        string query,
        SessionContext sessionContext,
        int limit = 10,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

        return await session.ExecuteAsync(
            async connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    @"
                SELECT id, source, relationship_type, target, user_id, agent_id, run_id, created_at, updated_at,
                       confidence, source_memory_id, temporal_context, metadata, version
                FROM relationships 
                WHERE (source LIKE @query OR target LIKE @query OR relationship_type LIKE @query)
                  AND user_id = @userId 
                  AND (@agentId IS NULL OR agent_id = @agentId)
                  AND (@runId IS NULL OR run_id = @runId)
                ORDER BY 
                    CASE 
                        WHEN source = @exactQuery OR target = @exactQuery THEN 1
                        WHEN relationship_type = @exactQuery THEN 2
                        ELSE 3
                    END,
                    confidence DESC,
                    created_at DESC
                LIMIT @limit";

                var likeQuery = $"%{query}%";
                _ = command.Parameters.AddWithValue("@query", likeQuery);
                _ = command.Parameters.AddWithValue("@exactQuery", query);
                _ = command.Parameters.AddWithValue("@userId", sessionContext.UserId);
                _ = command.Parameters.AddWithValue("@agentId", sessionContext.AgentId ?? (object)DBNull.Value);
                _ = command.Parameters.AddWithValue("@runId", sessionContext.RunId ?? (object)DBNull.Value);
                _ = command.Parameters.AddWithValue("@limit", limit);

                var relationships = new List<Relationship>();
                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    relationships.Add(MapRelationshipFromReader(reader));
                }

                return relationships;
            },
            cancellationToken
        );
    }

    // Enhanced Search Operations for Phase 6

    public async Task<IEnumerable<Entity>> SearchEntitiesAsync(
        string query,
        SessionContext sessionContext,
        int limit = 10,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

        return await session.ExecuteAsync(
            async connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    @"
                SELECT e.id, e.name, e.type, e.aliases, e.user_id, e.agent_id, e.run_id, e.created_at, e.updated_at,
                       e.confidence, e.source_memory_ids, e.metadata, e.version
                FROM entities e
                JOIN entities_fts fts ON e.id = fts.rowid
                WHERE entities_fts MATCH @query
                  AND e.user_id = @userId 
                  AND (@agentId IS NULL OR e.agent_id = @agentId)
                  AND (@runId IS NULL OR e.run_id = @runId)
                ORDER BY bm25(entities_fts), e.confidence DESC, e.created_at DESC
                LIMIT @limit";

                _ = command.Parameters.AddWithValue("@query", query);
                _ = command.Parameters.AddWithValue("@userId", sessionContext.UserId);
                _ = command.Parameters.AddWithValue("@agentId", sessionContext.AgentId ?? (object)DBNull.Value);
                _ = command.Parameters.AddWithValue("@runId", sessionContext.RunId ?? (object)DBNull.Value);
                _ = command.Parameters.AddWithValue("@limit", limit);

                var entities = new List<Entity>();
                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    entities.Add(MapEntityFromReader(reader));
                }

                _logger.LogDebug("Entity FTS5 search for '{Query}' returned {Count} results", query, entities.Count);
                return entities;
            },
            cancellationToken
        );
    }

    public async Task<List<EntityVectorSearchResult>> SearchEntitiesVectorAsync(
        float[] queryEmbedding,
        SessionContext sessionContext,
        int limit = 10,
        float threshold = 0.7f,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

        return await session.ExecuteAsync(
            async connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    @"
                SELECT e.id, e.name, e.type, e.user_id, e.agent_id, e.run_id, e.created_at, e.updated_at,
                       e.description, e.confidence, e.source_memory_id, e.metadata, e.version,
                       vec_distance_cosine(ee.embedding, @queryEmbedding) as distance
                FROM entities e
                JOIN entity_embeddings ee ON e.id = ee.entity_id
                WHERE e.user_id = @userId 
                  AND (@agentId IS NULL OR e.agent_id = @agentId)
                  AND (@runId IS NULL OR e.run_id = @runId)
                  AND vec_distance_cosine(ee.embedding, @queryEmbedding) <= @distanceThreshold
                ORDER BY distance ASC
                LIMIT @limit";

                var distanceThreshold = 1.0f - threshold; // Convert similarity to distance

                // Convert query embedding to JSON format for sqlite-vec (more reliable than byte conversion)
                var queryEmbeddingJson =
                    "["
                    + string.Join(",", queryEmbedding.Select(f => f.ToString("G", CultureInfo.InvariantCulture)))
                    + "]";

                _ = command.Parameters.AddWithValue("@queryEmbedding", queryEmbeddingJson);
                _ = command.Parameters.AddWithValue("@userId", sessionContext.UserId);
                _ = command.Parameters.AddWithValue("@agentId", sessionContext.AgentId ?? (object)DBNull.Value);
                _ = command.Parameters.AddWithValue("@runId", sessionContext.RunId ?? (object)DBNull.Value);
                _ = command.Parameters.AddWithValue("@distanceThreshold", distanceThreshold);
                _ = command.Parameters.AddWithValue("@limit", limit);

                var results = new List<EntityVectorSearchResult>();
                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var entity = MapEntityFromReader(reader);
                    var distance = Convert.ToSingle(reader["distance"]);
                    var score = 1.0f - distance; // Convert distance back to similarity

                    results.Add(
                        new EntityVectorSearchResult
                        {
                            Entity = entity,
                            Distance = distance,
                            Score = score,
                        }
                    );
                }

                _logger.LogDebug(
                    "Entity vector search returned {Count} results with threshold {Threshold}",
                    results.Count,
                    threshold
                );
                return results;
            },
            cancellationToken
        );
    }

    public async Task<List<RelationshipVectorSearchResult>> SearchRelationshipsVectorAsync(
        float[] queryEmbedding,
        SessionContext sessionContext,
        int limit = 10,
        float threshold = 0.7f,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

        return await session.ExecuteAsync(
            async connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    @"
                SELECT r.id, r.source, r.relationship_type, r.target, r.user_id, r.agent_id, r.run_id, r.created_at, r.updated_at,
                       r.confidence, r.source_memory_id, r.temporal_context, r.metadata, r.version,
                       vec_distance_cosine(re.embedding, @queryEmbedding) as distance
                FROM relationships r
                JOIN relationship_embeddings re ON r.id = re.relationship_id
                WHERE r.user_id = @userId 
                  AND (@agentId IS NULL OR r.agent_id = @agentId)
                  AND (@runId IS NULL OR r.run_id = @runId)
                  AND vec_distance_cosine(re.embedding, @queryEmbedding) <= @distanceThreshold
                ORDER BY distance ASC
                LIMIT @limit";

                var distanceThreshold = 1.0f - threshold; // Convert similarity to distance

                // Convert query embedding to JSON format for sqlite-vec (more reliable than byte conversion)
                var queryEmbeddingJson =
                    "["
                    + string.Join(",", queryEmbedding.Select(f => f.ToString("G", CultureInfo.InvariantCulture)))
                    + "]";

                _ = command.Parameters.AddWithValue("@queryEmbedding", queryEmbeddingJson);
                _ = command.Parameters.AddWithValue("@userId", sessionContext.UserId);
                _ = command.Parameters.AddWithValue("@agentId", sessionContext.AgentId ?? (object)DBNull.Value);
                _ = command.Parameters.AddWithValue("@runId", sessionContext.RunId ?? (object)DBNull.Value);
                _ = command.Parameters.AddWithValue("@distanceThreshold", distanceThreshold);
                _ = command.Parameters.AddWithValue("@limit", limit);

                var results = new List<RelationshipVectorSearchResult>();
                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var relationship = MapRelationshipFromReader(reader);
                    var distance = Convert.ToSingle(reader["distance"]);
                    var score = 1.0f - distance; // Convert distance back to similarity

                    results.Add(
                        new RelationshipVectorSearchResult
                        {
                            Relationship = relationship,
                            Distance = distance,
                            Score = score,
                        }
                    );
                }

                _logger.LogDebug(
                    "Relationship vector search returned {Count} results with threshold {Threshold}",
                    results.Count,
                    threshold
                );
                return results;
            },
            cancellationToken
        );
    }

    // Embedding Storage Operations

    public async Task StoreEntityEmbeddingAsync(
        int entityId,
        float[] embedding,
        string modelName,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

        await session.ExecuteInTransactionAsync(
            async (connection, transaction) =>
            {
                // Store the embedding in the vec0 table
                using var embeddingCommand = connection.CreateCommand();
                embeddingCommand.Transaction = transaction;
                embeddingCommand.CommandText =
                    @"
                INSERT OR REPLACE INTO entity_embeddings (entity_id, embedding)
                VALUES (@entityId, @embedding)";

                // Convert embedding to JSON format for sqlite-vec (more reliable than byte conversion)
                var embeddingJson =
                    "[" + string.Join(",", embedding.Select(f => f.ToString("G", CultureInfo.InvariantCulture))) + "]";

                _ = embeddingCommand.Parameters.AddWithValue("@entityId", entityId);
                _ = embeddingCommand.Parameters.AddWithValue("@embedding", embeddingJson);

                _ = await embeddingCommand.ExecuteNonQueryAsync(cancellationToken);

                // Store metadata
                using var metadataCommand = connection.CreateCommand();
                metadataCommand.Transaction = transaction;
                metadataCommand.CommandText =
                    @"
                INSERT OR REPLACE INTO entity_embedding_metadata (entity_id, model_name, embedding_dimension, created_at)
                VALUES (@entityId, @modelName, @dimension, @createdAt)";

                _ = metadataCommand.Parameters.AddWithValue("@entityId", entityId);
                _ = metadataCommand.Parameters.AddWithValue("@modelName", modelName);
                _ = metadataCommand.Parameters.AddWithValue("@dimension", embedding.Length);
                _ = metadataCommand.Parameters.AddWithValue("@createdAt", DateTime.UtcNow);

                _ = await metadataCommand.ExecuteNonQueryAsync(cancellationToken);

                _logger.LogDebug("Stored embedding for entity {EntityId} using model {ModelName}", entityId, modelName);
            },
            cancellationToken
        );
    }

    public async Task StoreRelationshipEmbeddingAsync(
        int relationshipId,
        float[] embedding,
        string modelName,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

        await session.ExecuteInTransactionAsync(
            async (connection, transaction) =>
            {
                // Store the embedding in the vec0 table
                using var embeddingCommand = connection.CreateCommand();
                embeddingCommand.Transaction = transaction;
                embeddingCommand.CommandText =
                    @"
                INSERT OR REPLACE INTO relationship_embeddings (relationship_id, embedding)
                VALUES (@relationshipId, @embedding)";

                // Convert embedding to JSON format for sqlite-vec (more reliable than byte conversion)
                var embeddingJson =
                    "[" + string.Join(",", embedding.Select(f => f.ToString("G", CultureInfo.InvariantCulture))) + "]";

                _ = embeddingCommand.Parameters.AddWithValue("@relationshipId", relationshipId);
                _ = embeddingCommand.Parameters.AddWithValue("@embedding", embeddingJson);

                _ = await embeddingCommand.ExecuteNonQueryAsync(cancellationToken);

                // Store metadata
                using var metadataCommand = connection.CreateCommand();
                metadataCommand.Transaction = transaction;
                metadataCommand.CommandText =
                    @"
                INSERT OR REPLACE INTO relationship_embedding_metadata (relationship_id, model_name, embedding_dimension, created_at)
                VALUES (@relationshipId, @modelName, @dimension, @createdAt)";

                _ = metadataCommand.Parameters.AddWithValue("@relationshipId", relationshipId);
                _ = metadataCommand.Parameters.AddWithValue("@modelName", modelName);
                _ = metadataCommand.Parameters.AddWithValue("@dimension", embedding.Length);
                _ = metadataCommand.Parameters.AddWithValue("@createdAt", DateTime.UtcNow);

                _ = await metadataCommand.ExecuteNonQueryAsync(cancellationToken);

                _logger.LogDebug(
                    "Stored embedding for relationship {RelationshipId} using model {ModelName}",
                    relationshipId,
                    modelName
                );
            },
            cancellationToken
        );
    }

    public async Task<float[]?> GetEntityEmbeddingAsync(int entityId, CancellationToken cancellationToken = default)
    {
        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

        return await session.ExecuteAsync(
            async connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    @"
                SELECT embedding FROM entity_embeddings WHERE entity_id = @entityId";

                _ = command.Parameters.AddWithValue("@entityId", entityId);

                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    var embeddingOrdinal = reader.GetOrdinal("embedding");
                    if (!reader.IsDBNull(embeddingOrdinal))
                    {
                        // sqlite-vec stores embeddings as BLOB, convert to float array
                        var embeddingBytes = (byte[])reader.GetValue(embeddingOrdinal);
                        var embedding = new float[embeddingBytes.Length / sizeof(float)];
                        Buffer.BlockCopy(embeddingBytes, 0, embedding, 0, embeddingBytes.Length);
                        return embedding;
                    }
                }

                return null;
            },
            cancellationToken
        );
    }

    public async Task<float[]?> GetRelationshipEmbeddingAsync(
        int relationshipId,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

        return await session.ExecuteAsync(
            async connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    @"
                SELECT embedding FROM relationship_embeddings WHERE relationship_id = @relationshipId";

                _ = command.Parameters.AddWithValue("@relationshipId", relationshipId);

                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    var embeddingOrdinal = reader.GetOrdinal("embedding");
                    if (!reader.IsDBNull(embeddingOrdinal))
                    {
                        // sqlite-vec stores embeddings as BLOB, convert to float array
                        var embeddingBytes = (byte[])reader.GetValue(embeddingOrdinal);
                        var embedding = new float[embeddingBytes.Length / sizeof(float)];
                        Buffer.BlockCopy(embeddingBytes, 0, embedding, 0, embeddingBytes.Length);
                        return embedding;
                    }
                }

                return null;
            },
            cancellationToken
        );
    }

    #endregion

    #region Utility Operations

    public async Task<int> GenerateNextIdAsync(CancellationToken cancellationToken = default)
    {
        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

        return await session.ExecuteInTransactionAsync(
            async (connection, transaction) => await GenerateNextIdAsync(connection, transaction, cancellationToken),
            cancellationToken
        );
    }

    private static async Task<int> GenerateNextIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken
    )
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            @"
            INSERT INTO memory_id_sequence DEFAULT VALUES;
            SELECT last_insert_rowid();";

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    private static async Task<Entity?> GetEntityByIdInternalAsync(
        SqliteConnection connection,
        int entityId,
        SessionContext sessionContext,
        CancellationToken cancellationToken
    )
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            @"
            SELECT id, name, type, aliases, user_id, agent_id, run_id, created_at, updated_at, 
                   confidence, source_memory_ids, metadata, version
            FROM entities 
            WHERE id = @entityId 
              AND user_id = @userId 
              AND (@agentId IS NULL OR agent_id = @agentId)
              AND (@runId IS NULL OR run_id = @runId)";

        _ = command.Parameters.AddWithValue("@entityId", entityId);
        _ = command.Parameters.AddWithValue("@userId", sessionContext.UserId);
        _ = command.Parameters.AddWithValue("@agentId", sessionContext.AgentId ?? (object)DBNull.Value);
        _ = command.Parameters.AddWithValue("@runId", sessionContext.RunId ?? (object)DBNull.Value);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapEntityFromReader(reader) : null;
    }

    public async Task<GraphStatistics> GetGraphStatisticsAsync(
        SessionContext sessionContext,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

        return await session.ExecuteAsync(
            async connection =>
            {
                var stats = new GraphStatistics();

                // Get entity count
                using var entityCountCommand = connection.CreateCommand();
                entityCountCommand.CommandText =
                    @"
                SELECT COUNT(*) FROM entities 
                WHERE user_id = @userId 
                  AND (@agentId IS NULL OR agent_id = @agentId)
                  AND (@runId IS NULL OR run_id = @runId)";

                _ = entityCountCommand.Parameters.AddWithValue("@userId", sessionContext.UserId);
                _ = entityCountCommand.Parameters.AddWithValue(
                    "@agentId",
                    sessionContext.AgentId ?? (object)DBNull.Value
                );
                _ = entityCountCommand.Parameters.AddWithValue("@runId", sessionContext.RunId ?? (object)DBNull.Value);

                stats.EntityCount = Convert.ToInt32(await entityCountCommand.ExecuteScalarAsync(cancellationToken));

                // Get relationship count and statistics
                using var relationshipStatsCommand = connection.CreateCommand();
                relationshipStatsCommand.CommandText =
                    @"
                SELECT 
                    COUNT(*) as total_relationships,
                    COUNT(DISTINCT relationship_type) as unique_types
                FROM relationships 
                WHERE user_id = @userId 
                  AND (@agentId IS NULL OR agent_id = @agentId)
                  AND (@runId IS NULL OR run_id = @runId)";

                _ = relationshipStatsCommand.Parameters.AddWithValue("@userId", sessionContext.UserId);
                _ = relationshipStatsCommand.Parameters.AddWithValue(
                    "@agentId",
                    sessionContext.AgentId ?? (object)DBNull.Value
                );
                _ = relationshipStatsCommand.Parameters.AddWithValue(
                    "@runId",
                    sessionContext.RunId ?? (object)DBNull.Value
                );

                using var reader = await relationshipStatsCommand.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    stats.RelationshipCount = Convert.ToInt32(reader["total_relationships"]);
                    stats.UniqueRelationshipTypes = Convert.ToInt32(reader["unique_types"]);
                }

                // Get top relationship types
                using var topRelTypesCommand = connection.CreateCommand();
                topRelTypesCommand.CommandText =
                    @"
                SELECT relationship_type, COUNT(*) as count
                FROM relationships 
                WHERE user_id = @userId 
                  AND (@agentId IS NULL OR agent_id = @agentId)
                  AND (@runId IS NULL OR run_id = @runId)
                GROUP BY relationship_type
                ORDER BY count DESC
                LIMIT 10";

                _ = topRelTypesCommand.Parameters.AddWithValue("@userId", sessionContext.UserId);
                _ = topRelTypesCommand.Parameters.AddWithValue(
                    "@agentId",
                    sessionContext.AgentId ?? (object)DBNull.Value
                );
                _ = topRelTypesCommand.Parameters.AddWithValue("@runId", sessionContext.RunId ?? (object)DBNull.Value);

                using var topRelTypesReader = await topRelTypesCommand.ExecuteReaderAsync(cancellationToken);
                while (await topRelTypesReader.ReadAsync(cancellationToken))
                {
                    var relType = topRelTypesReader["relationship_type"].ToString()!;
                    var count = Convert.ToInt32(topRelTypesReader["count"]);
                    stats.TopRelationshipTypes[relType] = count;
                }

                // Get top connected entities
                using var topEntitiesCommand = connection.CreateCommand();
                topEntitiesCommand.CommandText =
                    @"
                SELECT entity_name, COUNT(*) as connection_count
                FROM (
                    SELECT source_entity_name as entity_name FROM relationships 
                    WHERE user_id = @userId 
                      AND (@agentId IS NULL OR agent_id = @agentId)
                      AND (@runId IS NULL OR run_id = @runId)
                    UNION ALL
                    SELECT target_entity_name as entity_name FROM relationships 
                    WHERE user_id = @userId 
                      AND (@agentId IS NULL OR agent_id = @agentId)
                      AND (@runId IS NULL OR run_id = @runId)
                ) entity_connections
                GROUP BY entity_name
                ORDER BY connection_count DESC
                LIMIT 10";

                _ = topEntitiesCommand.Parameters.AddWithValue("@userId", sessionContext.UserId);
                _ = topEntitiesCommand.Parameters.AddWithValue(
                    "@agentId",
                    sessionContext.AgentId ?? (object)DBNull.Value
                );
                _ = topEntitiesCommand.Parameters.AddWithValue("@runId", sessionContext.RunId ?? (object)DBNull.Value);

                using var topEntitiesReader = await topEntitiesCommand.ExecuteReaderAsync(cancellationToken);
                while (await topEntitiesReader.ReadAsync(cancellationToken))
                {
                    var entityName = topEntitiesReader["entity_name"].ToString()!;
                    var count = Convert.ToInt32(topEntitiesReader["connection_count"]);
                    stats.TopConnectedEntities[entityName] = count;
                }

                return stats;
            },
            cancellationToken
        );
    }

    #endregion

    #region Mapping Methods

    private static Entity MapEntityFromReader(SqliteDataReader reader)
    {
        return new Entity
        {
            Id = Convert.ToInt32(reader["id"]),
            Name = reader["name"].ToString()!,
            Type = reader.IsDBNull("type") ? null : reader["type"].ToString(),
            Aliases = reader.IsDBNull("aliases")
                ? null
                : JsonSerializer.Deserialize<List<string>>(reader["aliases"].ToString()!),
            UserId = reader["user_id"].ToString()!,
            AgentId = reader.IsDBNull("agent_id") ? null : reader["agent_id"].ToString(),
            RunId = reader.IsDBNull("run_id") ? null : reader["run_id"].ToString(),
            CreatedAt = Convert.ToDateTime(reader["created_at"]),
            UpdatedAt = Convert.ToDateTime(reader["updated_at"]),
            Confidence = Convert.ToSingle(reader["confidence"]),
            SourceMemoryIds = reader.IsDBNull("source_memory_ids")
                ? null
                : JsonSerializer.Deserialize<List<int>>(reader["source_memory_ids"].ToString()!),
            Metadata = reader.IsDBNull("metadata")
                ? null
                : JsonSerializer.Deserialize<Dictionary<string, object>>(reader["metadata"].ToString()!),
            Version = Convert.ToInt32(reader["version"]),
        };
    }

    private static Relationship MapRelationshipFromReader(SqliteDataReader reader, string prefix = "")
    {
        return new Relationship
        {
            Id = Convert.ToInt32(reader[$"{prefix}id"]),
            Source = reader[$"{prefix}source_entity_name"].ToString()!,
            RelationshipType = reader[$"{prefix}relationship_type"].ToString()!,
            Target = reader[$"{prefix}target_entity_name"].ToString()!,
            UserId = reader[$"{prefix}user_id"].ToString()!,
            AgentId = reader.IsDBNull($"{prefix}agent_id") ? null : reader[$"{prefix}agent_id"].ToString(),
            RunId = reader.IsDBNull($"{prefix}run_id") ? null : reader[$"{prefix}run_id"].ToString(),
            CreatedAt = Convert.ToDateTime(reader[$"{prefix}created_at"]),
            UpdatedAt = Convert.ToDateTime(reader[$"{prefix}updated_at"]),
            Confidence = Convert.ToSingle(reader[$"{prefix}confidence"]),
            SourceMemoryId = reader.IsDBNull($"{prefix}source_memory_id")
                ? null
                : Convert.ToInt32(reader[$"{prefix}source_memory_id"]),
            TemporalContext = reader.IsDBNull($"{prefix}temporal_context")
                ? null
                : reader[$"{prefix}temporal_context"].ToString(),
            Metadata = reader.IsDBNull($"{prefix}metadata")
                ? null
                : JsonSerializer.Deserialize<Dictionary<string, object>>(reader[$"{prefix}metadata"].ToString()!),
            Version = Convert.ToInt32(reader[$"{prefix}version"]),
        };
    }

    #endregion
}
