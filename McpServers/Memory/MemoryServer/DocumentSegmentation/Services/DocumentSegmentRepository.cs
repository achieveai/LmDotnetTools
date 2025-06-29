using MemoryServer.DocumentSegmentation.Models;
using MemoryServer.Infrastructure;
using MemoryServer.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace MemoryServer.DocumentSegmentation.Services;

/// <summary>
/// Repository for document segment operations using the Database Session Pattern.
/// </summary>
public class DocumentSegmentRepository : IDocumentSegmentRepository
{
  private readonly ILogger<DocumentSegmentRepository> _logger;

  public DocumentSegmentRepository(ILogger<DocumentSegmentRepository> logger)
  {
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  }

  /// <summary>
  /// Stores document segments in the database.
  /// </summary>
  public async Task<List<int>> StoreSegmentsAsync(
    ISqliteSession session,
    List<DocumentSegment> segments,
    int parentDocumentId,
    SessionContext sessionContext,
    CancellationToken cancellationToken = default)
  {
    if (segments == null || !segments.Any())
    {
      return new List<int>();
    }

    var segmentIds = new List<int>();

    try
    {
      await session.ExecuteAsync(async connection =>
      {
        using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
          foreach (var segment in segments)
          {
            var segmentId = await InsertSegmentAsync(connection, segment, parentDocumentId, sessionContext, cancellationToken);
            segmentIds.Add(segmentId);
          }

          await transaction.CommitAsync(cancellationToken);
          
          _logger.LogInformation("Successfully stored {SegmentCount} segments for document {DocumentId} in session {UserId}/{AgentId}/{RunId}",
            segments.Count, parentDocumentId, sessionContext.UserId, sessionContext.AgentId, sessionContext.RunId);
        }
        catch
        {
          await transaction.RollbackAsync(cancellationToken);
          throw;
        }
      }, cancellationToken);

      return segmentIds;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to store segments for document {DocumentId}", parentDocumentId);
      throw;
    }
  }

  /// <summary>
  /// Retrieves all segments for a parent document.
  /// </summary>
  public async Task<List<DocumentSegment>> GetDocumentSegmentsAsync(
    ISqliteSession session,
    int parentDocumentId,
    SessionContext sessionContext,
    CancellationToken cancellationToken = default)
  {
    try
    {
      var segments = new List<DocumentSegment>();

      await session.ExecuteAsync(async connection =>
      {
        const string sql = @"
          SELECT id, segment_id, sequence_number, content, title, summary,
                 coherence_score, independence_score, topic_consistency_score,
                 created_at, updated_at, metadata
          FROM document_segments 
          WHERE parent_document_id = @parentDocumentId 
            AND user_id = @userId 
            AND agent_id = @agentId 
            AND (@runId IS NULL OR run_id = @runId)
          ORDER BY sequence_number";

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@parentDocumentId", parentDocumentId);
        command.Parameters.AddWithValue("@userId", sessionContext.UserId);
        command.Parameters.AddWithValue("@agentId", sessionContext.AgentId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@runId", sessionContext.RunId ?? (object)DBNull.Value);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        
        while (await reader.ReadAsync(cancellationToken))
        {
          var segment = new DocumentSegment
          {
            Id = reader.GetString(reader.GetOrdinal("segment_id")),
            SequenceNumber = reader.GetInt32(reader.GetOrdinal("sequence_number")),
            Content = reader.GetString(reader.GetOrdinal("content")),
            Title = reader.IsDBNull(reader.GetOrdinal("title")) ? string.Empty : reader.GetString(reader.GetOrdinal("title")),
            Summary = reader.IsDBNull(reader.GetOrdinal("summary")) ? string.Empty : reader.GetString(reader.GetOrdinal("summary")),
            Quality = new SegmentQuality
            {
              CoherenceScore = reader.IsDBNull(reader.GetOrdinal("coherence_score")) ? 0.0 : reader.GetDouble(reader.GetOrdinal("coherence_score")),
              IndependenceScore = reader.IsDBNull(reader.GetOrdinal("independence_score")) ? 0.0 : reader.GetDouble(reader.GetOrdinal("independence_score")),
              TopicConsistencyScore = reader.IsDBNull(reader.GetOrdinal("topic_consistency_score")) ? 0.0 : reader.GetDouble(reader.GetOrdinal("topic_consistency_score"))
            }
          };

          // Parse metadata if present
          var metadataOrdinal = reader.GetOrdinal("metadata");
          if (!reader.IsDBNull(metadataOrdinal))
          {
            var metadataJson = reader.GetString(metadataOrdinal);
            if (!string.IsNullOrEmpty(metadataJson))
            {
              try
              {
                segment.Metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(metadataJson) ?? new();
              }
              catch (JsonException ex)
              {
                _logger.LogWarning(ex, "Failed to parse segment metadata for segment {SegmentId}", segment.Id);
                segment.Metadata = new Dictionary<string, object>();
              }
            }
          }

          segments.Add(segment);
        }
      }, cancellationToken);

      _logger.LogDebug("Retrieved {SegmentCount} segments for document {DocumentId}", segments.Count, parentDocumentId);
      return segments;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to retrieve segments for document {DocumentId}", parentDocumentId);
      throw;
    }
  }

  /// <summary>
  /// Stores segment relationships in the database.
  /// </summary>
  public async Task<int> StoreSegmentRelationshipsAsync(
    ISqliteSession session,
    List<SegmentRelationship> relationships,
    SessionContext sessionContext,
    CancellationToken cancellationToken = default)
  {
    if (relationships == null || !relationships.Any())
    {
      return 0;
    }

    try
    {
      var storedCount = 0;

      await session.ExecuteAsync(async connection =>
      {
        using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
          foreach (var relationship in relationships)
          {
            await InsertRelationshipAsync(connection, relationship, sessionContext, cancellationToken);
            storedCount++;
          }

          await transaction.CommitAsync(cancellationToken);
          
          _logger.LogInformation("Successfully stored {RelationshipCount} segment relationships in session {UserId}/{AgentId}/{RunId}",
            relationships.Count, sessionContext.UserId, sessionContext.AgentId, sessionContext.RunId);
        }
        catch
        {
          await transaction.RollbackAsync(cancellationToken);
          throw;
        }
      }, cancellationToken);

      return storedCount;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to store segment relationships");
      throw;
    }
  }

  /// <summary>
  /// Retrieves segment relationships for a document.
  /// </summary>
  public async Task<List<SegmentRelationship>> GetSegmentRelationshipsAsync(
    ISqliteSession session,
    int parentDocumentId,
    SessionContext sessionContext,
    CancellationToken cancellationToken = default)
  {
    try
    {
      var relationships = new List<SegmentRelationship>();

      await session.ExecuteAsync(async connection =>
      {
        const string sql = @"
          SELECT sr.id, sr.source_segment_id, sr.target_segment_id, sr.relationship_type, 
                 sr.strength, sr.created_at, sr.updated_at, sr.metadata
          FROM segment_relationships sr
          INNER JOIN document_segments ds_source ON sr.source_segment_id = ds_source.segment_id
          INNER JOIN document_segments ds_target ON sr.target_segment_id = ds_target.segment_id
          WHERE ds_source.parent_document_id = @parentDocumentId
            AND ds_target.parent_document_id = @parentDocumentId
            AND sr.user_id = @userId 
            AND sr.agent_id = @agentId 
            AND (@runId IS NULL OR sr.run_id = @runId)
          ORDER BY sr.created_at";

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@parentDocumentId", parentDocumentId);
        command.Parameters.AddWithValue("@userId", sessionContext.UserId);
        command.Parameters.AddWithValue("@agentId", sessionContext.AgentId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@runId", sessionContext.RunId ?? (object)DBNull.Value);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        
        while (await reader.ReadAsync(cancellationToken))
        {
          var relationship = new SegmentRelationship
          {
            Id = reader.GetInt32(reader.GetOrdinal("id")).ToString(),
            SourceSegmentId = reader.GetString(reader.GetOrdinal("source_segment_id")),
            TargetSegmentId = reader.GetString(reader.GetOrdinal("target_segment_id")),
            RelationshipType = Enum.Parse<SegmentRelationshipType>(reader.GetString(reader.GetOrdinal("relationship_type")), true),
            Strength = reader.IsDBNull(reader.GetOrdinal("strength")) ? 1.0 : reader.GetDouble(reader.GetOrdinal("strength"))
          };

          // Parse metadata if present
          var metadataOrdinal = reader.GetOrdinal("metadata");
          if (!reader.IsDBNull(metadataOrdinal))
          {
            var metadataJson = reader.GetString(metadataOrdinal);
            if (!string.IsNullOrEmpty(metadataJson))
            {
              try
              {
                relationship.Metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(metadataJson) ?? new();
              }
              catch (JsonException ex)
              {
                _logger.LogWarning(ex, "Failed to parse relationship metadata for relationship {RelationshipId}", relationship.Id);
                relationship.Metadata = new Dictionary<string, object>();
              }
            }
          }

          relationships.Add(relationship);
        }
      }, cancellationToken);

      _logger.LogDebug("Retrieved {RelationshipCount} relationships for document {DocumentId}", relationships.Count, parentDocumentId);
      return relationships;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to retrieve segment relationships for document {DocumentId}", parentDocumentId);
      throw;
    }
  }

  /// <summary>
  /// Deletes all segments and relationships for a parent document.
  /// </summary>
  public async Task<int> DeleteDocumentSegmentsAsync(
    ISqliteSession session,
    int parentDocumentId,
    SessionContext sessionContext,
    CancellationToken cancellationToken = default)
  {
    try
    {
      var deletedCount = 0;

      await session.ExecuteAsync(async connection =>
      {
        using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
          // Delete relationships first (foreign key constraints)
          const string deleteRelationshipsSql = @"
            DELETE FROM segment_relationships 
            WHERE (source_segment_id IN (
              SELECT segment_id FROM document_segments 
              WHERE parent_document_id = @parentDocumentId 
                AND user_id = @userId 
                AND agent_id = @agentId 
                AND (@runId IS NULL OR run_id = @runId)
            ) OR target_segment_id IN (
              SELECT segment_id FROM document_segments 
              WHERE parent_document_id = @parentDocumentId 
                AND user_id = @userId 
                AND agent_id = @agentId 
                AND (@runId IS NULL OR run_id = @runId)
            ))
            AND user_id = @userId 
            AND agent_id = @agentId 
            AND (@runId IS NULL OR run_id = @runId)";

          using var deleteRelCmd = connection.CreateCommand();
          deleteRelCmd.CommandText = deleteRelationshipsSql;
          deleteRelCmd.Parameters.AddWithValue("@parentDocumentId", parentDocumentId);
          deleteRelCmd.Parameters.AddWithValue("@userId", sessionContext.UserId);
          deleteRelCmd.Parameters.AddWithValue("@agentId", sessionContext.AgentId ?? (object)DBNull.Value);
          deleteRelCmd.Parameters.AddWithValue("@runId", sessionContext.RunId ?? (object)DBNull.Value);

          await deleteRelCmd.ExecuteNonQueryAsync(cancellationToken);

          // Delete segments
          const string deleteSegmentsSql = @"
            DELETE FROM document_segments 
            WHERE parent_document_id = @parentDocumentId 
              AND user_id = @userId 
              AND agent_id = @agentId 
              AND (@runId IS NULL OR run_id = @runId)";

          using var deleteSegCmd = connection.CreateCommand();
          deleteSegCmd.CommandText = deleteSegmentsSql;
          deleteSegCmd.Parameters.AddWithValue("@parentDocumentId", parentDocumentId);
          deleteSegCmd.Parameters.AddWithValue("@userId", sessionContext.UserId);
          deleteSegCmd.Parameters.AddWithValue("@agentId", sessionContext.AgentId ?? (object)DBNull.Value);
          deleteSegCmd.Parameters.AddWithValue("@runId", sessionContext.RunId ?? (object)DBNull.Value);

          deletedCount = await deleteSegCmd.ExecuteNonQueryAsync(cancellationToken);

          await transaction.CommitAsync(cancellationToken);
          
          _logger.LogInformation("Successfully deleted {DeletedCount} segments for document {DocumentId} in session {UserId}/{AgentId}/{RunId}",
            deletedCount, parentDocumentId, sessionContext.UserId, sessionContext.AgentId, sessionContext.RunId);
        }
        catch
        {
          await transaction.RollbackAsync(cancellationToken);
          throw;
        }
      }, cancellationToken);

      return deletedCount;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to delete segments for document {DocumentId}", parentDocumentId);
      throw;
    }
  }

  /// <summary>
  /// Searches segments using full-text search.
  /// </summary>
  public async Task<List<DocumentSegment>> SearchSegmentsAsync(
    ISqliteSession session,
    string query,
    SessionContext sessionContext,
    int limit = 10,
    CancellationToken cancellationToken = default)
  {
    if (string.IsNullOrWhiteSpace(query))
    {
      return new List<DocumentSegment>();
    }

    try
    {
      var segments = new List<DocumentSegment>();

      await session.ExecuteAsync(async connection =>
      {
        string sql;
        try
        {
          // Try FTS search first
          using var testCommand = connection.CreateCommand();
          testCommand.CommandText = "SELECT 1 FROM document_segments_fts LIMIT 1";
          await testCommand.ExecuteScalarAsync(cancellationToken);

          sql = @"
            SELECT ds.id, ds.segment_id, ds.sequence_number, ds.content, ds.title, ds.summary,
                   ds.coherence_score, ds.independence_score, ds.topic_consistency_score,
                   ds.created_at, ds.updated_at, ds.metadata, bm25(document_segments_fts) as rank
            FROM document_segments_fts 
            INNER JOIN document_segments ds ON document_segments_fts.rowid = ds.id
            WHERE document_segments_fts MATCH @query
              AND ds.user_id = @userId 
              AND ds.agent_id = @agentId 
              AND (@runId IS NULL OR ds.run_id = @runId)
            ORDER BY bm25(document_segments_fts)
            LIMIT @limit";
        }
        catch (Exception)
        {
          // FTS table doesn't exist or isn't accessible, use regular search
          sql = @"
            SELECT id, segment_id, sequence_number, content, title, summary,
                   coherence_score, independence_score, topic_consistency_score,
                   created_at, updated_at, metadata, 1.0 as rank
            FROM document_segments 
            WHERE (content LIKE @queryLike OR title LIKE @queryLike OR summary LIKE @queryLike)
              AND user_id = @userId 
              AND agent_id = @agentId 
              AND (@runId IS NULL OR run_id = @runId)
            ORDER BY 
              CASE 
                WHEN title LIKE @queryLike THEN 1
                WHEN summary LIKE @queryLike THEN 2
                ELSE 3
              END,
              length(content) DESC
            LIMIT @limit";
        }

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        
        if (sql.Contains("@queryLike"))
        {
          command.Parameters.AddWithValue("@queryLike", $"%{query}%");
        }
        else
        {
          command.Parameters.AddWithValue("@query", query);
        }
        
        command.Parameters.AddWithValue("@userId", sessionContext.UserId);
        command.Parameters.AddWithValue("@agentId", sessionContext.AgentId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@runId", sessionContext.RunId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@limit", limit);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        
        while (await reader.ReadAsync(cancellationToken))
        {
          var segment = new DocumentSegment
          {
            Id = reader.GetString(reader.GetOrdinal("segment_id")),
            SequenceNumber = reader.GetInt32(reader.GetOrdinal("sequence_number")),
            Content = reader.GetString(reader.GetOrdinal("content")),
            Title = reader.IsDBNull(reader.GetOrdinal("title")) ? string.Empty : reader.GetString(reader.GetOrdinal("title")),
            Summary = reader.IsDBNull(reader.GetOrdinal("summary")) ? string.Empty : reader.GetString(reader.GetOrdinal("summary")),
            Quality = new SegmentQuality
            {
              CoherenceScore = reader.IsDBNull(reader.GetOrdinal("coherence_score")) ? 0.0 : reader.GetDouble(reader.GetOrdinal("coherence_score")),
              IndependenceScore = reader.IsDBNull(reader.GetOrdinal("independence_score")) ? 0.0 : reader.GetDouble(reader.GetOrdinal("independence_score")),
              TopicConsistencyScore = reader.IsDBNull(reader.GetOrdinal("topic_consistency_score")) ? 0.0 : reader.GetDouble(reader.GetOrdinal("topic_consistency_score"))
            }
          };

          // Parse metadata if present
          var metadataOrdinal = reader.GetOrdinal("metadata");
          if (!reader.IsDBNull(metadataOrdinal))
          {
            var metadataJson = reader.GetString(metadataOrdinal);
            if (!string.IsNullOrEmpty(metadataJson))
            {
              try
              {
                segment.Metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(metadataJson) ?? new();
              }
              catch (JsonException ex)
              {
                _logger.LogWarning(ex, "Failed to parse segment metadata for segment {SegmentId}", segment.Id);
                segment.Metadata = new Dictionary<string, object>();
              }
            }
          }

          // Add search rank to metadata
          segment.Metadata["search_rank"] = reader.GetDouble(reader.GetOrdinal("rank"));

          segments.Add(segment);
        }
      }, cancellationToken);

      _logger.LogDebug("Found {SegmentCount} segments matching query '{Query}'", segments.Count, query);
      return segments;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to search segments with query '{Query}'", query);
      throw;
    }
  }

  #region Private Methods

  private async Task<int> InsertSegmentAsync(
    SqliteConnection connection,
    DocumentSegment segment,
    int parentDocumentId,
    SessionContext sessionContext,
    CancellationToken cancellationToken)
  {
    const string sql = @"
      INSERT INTO document_segments (
        parent_document_id, segment_id, sequence_number, content, title, summary,
        coherence_score, independence_score, topic_consistency_score,
        user_id, agent_id, run_id, metadata
      ) VALUES (
        @parentDocumentId, @segmentId, @sequenceNumber, @content, @title, @summary,
        @coherenceScore, @independenceScore, @topicConsistencyScore,
        @userId, @agentId, @runId, @metadata
      )
      RETURNING id";

    using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.Parameters.AddWithValue("@parentDocumentId", parentDocumentId);
    command.Parameters.AddWithValue("@segmentId", segment.Id);
    command.Parameters.AddWithValue("@sequenceNumber", segment.SequenceNumber);
    command.Parameters.AddWithValue("@content", segment.Content);
    command.Parameters.AddWithValue("@title", segment.Title ?? (object)DBNull.Value);
    command.Parameters.AddWithValue("@summary", segment.Summary ?? (object)DBNull.Value);
    command.Parameters.AddWithValue("@coherenceScore", segment.Quality.CoherenceScore);
    command.Parameters.AddWithValue("@independenceScore", segment.Quality.IndependenceScore);
    command.Parameters.AddWithValue("@topicConsistencyScore", segment.Quality.TopicConsistencyScore);
    command.Parameters.AddWithValue("@userId", sessionContext.UserId);
    command.Parameters.AddWithValue("@agentId", sessionContext.AgentId ?? (object)DBNull.Value);
    command.Parameters.AddWithValue("@runId", sessionContext.RunId ?? (object)DBNull.Value);

    // Serialize metadata
    var metadataJson = segment.Metadata.Any() ? JsonSerializer.Serialize(segment.Metadata) : null;
    command.Parameters.AddWithValue("@metadata", metadataJson ?? (object)DBNull.Value);

    var result = await command.ExecuteScalarAsync(cancellationToken);
    return Convert.ToInt32(result);
  }

  private async Task InsertRelationshipAsync(
    SqliteConnection connection,
    SegmentRelationship relationship,
    SessionContext sessionContext,
    CancellationToken cancellationToken)
  {
    const string sql = @"
      INSERT INTO segment_relationships (
        source_segment_id, target_segment_id, relationship_type, strength,
        user_id, agent_id, run_id, metadata
      ) VALUES (
        @sourceSegmentId, @targetSegmentId, @relationshipType, @strength,
        @userId, @agentId, @runId, @metadata
      )";

    using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.Parameters.AddWithValue("@sourceSegmentId", relationship.SourceSegmentId);
    command.Parameters.AddWithValue("@targetSegmentId", relationship.TargetSegmentId);
    command.Parameters.AddWithValue("@relationshipType", relationship.RelationshipType.ToString());
    command.Parameters.AddWithValue("@strength", relationship.Strength);
    command.Parameters.AddWithValue("@userId", sessionContext.UserId);
    command.Parameters.AddWithValue("@agentId", sessionContext.AgentId ?? (object)DBNull.Value);
    command.Parameters.AddWithValue("@runId", sessionContext.RunId ?? (object)DBNull.Value);

    // Serialize metadata
    var metadataJson = relationship.Metadata.Any() ? JsonSerializer.Serialize(relationship.Metadata) : null;
    command.Parameters.AddWithValue("@metadata", metadataJson ?? (object)DBNull.Value);

    await command.ExecuteNonQueryAsync(cancellationToken);
  }

  #endregion
}
