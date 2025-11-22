using AchieveAi.LmDotnetTools.AgUi.Persistence.Database;
using AchieveAi.LmDotnetTools.AgUi.Persistence.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.AgUi.Persistence.Repositories;

/// <summary>
/// SQLite implementation of <see cref="ISessionRepository"/>.
/// </summary>
/// <remarks>
/// Thread-safe implementation using parameterized queries.
/// All database operations are async and support cancellation.
/// </remarks>
public sealed class SessionRepository : ISessionRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<SessionRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionRepository"/> class.
    /// </summary>
    /// <param name="connectionFactory">The connection factory.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public SessionRepository(IDbConnectionFactory connectionFactory, ILogger<SessionRepository>? logger = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? NullLogger<SessionRepository>.Instance;
    }

    /// <inheritdoc/>
    public async Task<SessionEntity?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();

        cmd.CommandText =
            @"
            SELECT Id, ConversationId, StartTime, EndTime, Status, MetadataJson
            FROM Sessions
            WHERE Id = @Id";

        _ = cmd.Parameters.Add(new SqliteParameter("@Id", id));

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (await reader.ReadAsync(ct))
        {
            return MapSessionEntity(reader);
        }

        return null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SessionEntity>> GetByConversationIdAsync(
        string conversationId,
        CancellationToken ct = default
    )
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();

        cmd.CommandText =
            @"
            SELECT Id, ConversationId, StartTime, EndTime, Status, MetadataJson
            FROM Sessions
            WHERE ConversationId = @ConversationId
            ORDER BY StartTime DESC";

        _ = cmd.Parameters.Add(new SqliteParameter("@ConversationId", conversationId));

        var sessions = new List<SessionEntity>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            sessions.Add(MapSessionEntity(reader));
        }

        return sessions;
    }

    /// <inheritdoc/>
    public async Task CreateAsync(SessionEntity session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        await using var connection = await _connectionFactory.CreateConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();

        cmd.CommandText =
            @"
            INSERT INTO Sessions (Id, ConversationId, StartTime, EndTime, Status, MetadataJson)
            VALUES (@Id, @ConversationId, @StartTime, @EndTime, @Status, @MetadataJson)";

        _ = cmd.Parameters.Add(new SqliteParameter("@Id", session.Id));
        _ = cmd.Parameters.Add(new SqliteParameter("@ConversationId", (object?)session.ConversationId ?? DBNull.Value));
        _ = cmd.Parameters.Add(new SqliteParameter("@StartTime", session.StartTime));
        _ = cmd.Parameters.Add(new SqliteParameter("@EndTime", (object?)session.EndTime ?? DBNull.Value));
        _ = cmd.Parameters.Add(new SqliteParameter("@Status", session.Status));
        _ = cmd.Parameters.Add(new SqliteParameter("@MetadataJson", (object?)session.MetadataJson ?? DBNull.Value));

        _ = await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogDebug("Created session {SessionId} with status {Status}", session.Id, session.Status);
    }

    /// <inheritdoc/>
    public async Task UpdateAsync(SessionEntity session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        await using var connection = await _connectionFactory.CreateConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();

        cmd.CommandText =
            @"
            UPDATE Sessions
            SET ConversationId = @ConversationId,
                StartTime = @StartTime,
                EndTime = @EndTime,
                Status = @Status,
                MetadataJson = @MetadataJson
            WHERE Id = @Id";

        _ = cmd.Parameters.Add(new SqliteParameter("@Id", session.Id));
        _ = cmd.Parameters.Add(new SqliteParameter("@ConversationId", (object?)session.ConversationId ?? DBNull.Value));
        _ = cmd.Parameters.Add(new SqliteParameter("@StartTime", session.StartTime));
        _ = cmd.Parameters.Add(new SqliteParameter("@EndTime", (object?)session.EndTime ?? DBNull.Value));
        _ = cmd.Parameters.Add(new SqliteParameter("@Status", session.Status));
        _ = cmd.Parameters.Add(new SqliteParameter("@MetadataJson", (object?)session.MetadataJson ?? DBNull.Value));

        _ = await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogDebug("Updated session {SessionId} to status {Status}", session.Id, session.Status);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SessionEntity>> GetIncompleteSessionsAsync(CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();

        cmd.CommandText =
            @"
            SELECT Id, ConversationId, StartTime, EndTime, Status, MetadataJson
            FROM Sessions
            WHERE Status NOT IN ('Completed', 'Failed')
            ORDER BY StartTime DESC";

        var sessions = new List<SessionEntity>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            sessions.Add(MapSessionEntity(reader));
        }

        _logger.LogInformation("Found {Count} incomplete sessions", sessions.Count);
        return sessions;
    }

    /// <inheritdoc/>
    public async Task MarkSessionAsFailedAsync(string sessionId, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();

        cmd.CommandText =
            @"
            UPDATE Sessions
            SET Status = 'Failed',
                EndTime = @EndTime
            WHERE Id = @Id";

        _ = cmd.Parameters.Add(new SqliteParameter("@Id", sessionId));
        _ = cmd.Parameters.Add(new SqliteParameter("@EndTime", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        _ = await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogWarning("Marked session {SessionId} as failed", sessionId);
    }

    /// <summary>
    /// Maps a data reader row to a SessionEntity.
    /// </summary>
    private static SessionEntity MapSessionEntity(SqliteDataReader reader)
    {
        return new SessionEntity
        {
            Id = reader.GetString(0),
            ConversationId = reader.IsDBNull(1) ? null : reader.GetString(1),
            StartTime = reader.GetInt64(2),
            EndTime = reader.IsDBNull(3) ? null : reader.GetInt64(3),
            Status = reader.GetString(4),
            MetadataJson = reader.IsDBNull(5) ? null : reader.GetString(5),
        };
    }
}
