using AchieveAi.LmDotnetTools.AgUi.Persistence.Database;
using AchieveAi.LmDotnetTools.AgUi.Persistence.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.AgUi.Persistence.Repositories;

/// <summary>
///     SQLite implementation of <see cref="IEventRepository" />.
/// </summary>
/// <remarks>
///     Thread-safe implementation using parameterized queries.
///     All database operations are async and support cancellation.
/// </remarks>
public sealed class EventRepository : IEventRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<EventRepository> _logger;

    /// <summary>
    ///     Initializes a new instance of the <see cref="EventRepository" /> class.
    /// </summary>
    /// <param name="connectionFactory">The connection factory.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public EventRepository(IDbConnectionFactory connectionFactory, ILogger<EventRepository>? logger = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? NullLogger<EventRepository>.Instance;
    }

    /// <inheritdoc />
    public async Task<EventEntity?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();

        cmd.CommandText =
            @"
            SELECT Id, SessionId, EventJson, Timestamp, EventType
            FROM Events
            WHERE Id = @Id";

        _ = cmd.Parameters.Add(new SqliteParameter("@Id", id));

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        return await reader.ReadAsync(ct) ? MapEventEntity(reader) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EventEntity>> GetBySessionIdAsync(string sessionId, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();

        cmd.CommandText =
            @"
            SELECT Id, SessionId, EventJson, Timestamp, EventType
            FROM Events
            WHERE SessionId = @SessionId
            ORDER BY Timestamp ASC";

        _ = cmd.Parameters.Add(new SqliteParameter("@SessionId", sessionId));

        var events = new List<EventEntity>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            events.Add(MapEventEntity(reader));
        }

        _logger.LogDebug("Retrieved {Count} events for session {SessionId}", events.Count, sessionId);

        return events;
    }

    /// <inheritdoc />
    public async Task CreateAsync(EventEntity evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        await using var connection = await _connectionFactory.CreateConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();

        cmd.CommandText =
            @"
            INSERT INTO Events (Id, SessionId, EventJson, Timestamp, EventType)
            VALUES (@Id, @SessionId, @EventJson, @Timestamp, @EventType)";

        _ = cmd.Parameters.Add(new SqliteParameter("@Id", evt.Id));
        _ = cmd.Parameters.Add(new SqliteParameter("@SessionId", evt.SessionId));
        _ = cmd.Parameters.Add(new SqliteParameter("@EventJson", evt.EventJson));
        _ = cmd.Parameters.Add(new SqliteParameter("@Timestamp", evt.Timestamp));
        _ = cmd.Parameters.Add(new SqliteParameter("@EventType", evt.EventType));

        _ = await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogTrace(
            "Created event {EventId} for session {SessionId} (type: {EventType})",
            evt.Id,
            evt.SessionId,
            evt.EventType
        );
    }

    /// <summary>
    ///     Maps a data reader row to an EventEntity.
    /// </summary>
    private static EventEntity MapEventEntity(SqliteDataReader reader)
    {
        return new EventEntity
        {
            Id = reader.GetString(0),
            SessionId = reader.GetString(1),
            EventJson = reader.GetString(2),
            Timestamp = reader.GetInt64(3),
            EventType = reader.GetString(4),
        };
    }
}
