using AchieveAi.LmDotnetTools.AgUi.Persistence.Database;
using AchieveAi.LmDotnetTools.AgUi.Persistence.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.AgUi.Persistence.Repositories;

/// <summary>
///     SQLite implementation of <see cref="IMessageRepository" />.
/// </summary>
/// <remarks>
///     Thread-safe implementation using parameterized queries.
///     All database operations are async and support cancellation.
/// </remarks>
public sealed class MessageRepository : IMessageRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<MessageRepository> _logger;

    /// <summary>
    ///     Initializes a new instance of the <see cref="MessageRepository" /> class.
    /// </summary>
    /// <param name="connectionFactory">The connection factory.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public MessageRepository(IDbConnectionFactory connectionFactory, ILogger<MessageRepository>? logger = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? NullLogger<MessageRepository>.Instance;
    }

    /// <inheritdoc />
    public async Task<MessageEntity?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();

        cmd.CommandText =
            @"
            SELECT Id, SessionId, MessageJson, Timestamp, MessageType
            FROM Messages
            WHERE Id = @Id";

        _ = cmd.Parameters.Add(new SqliteParameter("@Id", id));

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        return await reader.ReadAsync(ct) ? MapMessageEntity(reader) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MessageEntity>> GetMessagesBySessionIdAsync(
        string sessionId,
        int skip = 0,
        int take = 100,
        CancellationToken ct = default
    )
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();

        cmd.CommandText =
            @"
            SELECT Id, SessionId, MessageJson, Timestamp, MessageType
            FROM Messages
            WHERE SessionId = @SessionId
            ORDER BY Timestamp ASC
            LIMIT @Take OFFSET @Skip";

        _ = cmd.Parameters.Add(new SqliteParameter("@SessionId", sessionId));
        _ = cmd.Parameters.Add(new SqliteParameter("@Skip", skip));
        _ = cmd.Parameters.Add(new SqliteParameter("@Take", take));

        var messages = new List<MessageEntity>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            messages.Add(MapMessageEntity(reader));
        }

        _logger.LogDebug(
            "Retrieved {Count} messages for session {SessionId} (skip: {Skip}, take: {Take})",
            messages.Count,
            sessionId,
            skip,
            take
        );

        return messages;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MessageEntity>> GetMessagesByConversationIdAsync(
        string conversationId,
        int skip = 0,
        int take = 100,
        CancellationToken ct = default
    )
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();

        cmd.CommandText =
            @"
            SELECT m.Id, m.SessionId, m.MessageJson, m.Timestamp, m.MessageType
            FROM Messages m
            INNER JOIN Sessions s ON m.SessionId = s.Id
            WHERE s.ConversationId = @ConversationId
            ORDER BY m.Timestamp ASC
            LIMIT @Take OFFSET @Skip";

        _ = cmd.Parameters.Add(new SqliteParameter("@ConversationId", conversationId));
        _ = cmd.Parameters.Add(new SqliteParameter("@Skip", skip));
        _ = cmd.Parameters.Add(new SqliteParameter("@Take", take));

        var messages = new List<MessageEntity>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            messages.Add(MapMessageEntity(reader));
        }

        _logger.LogDebug(
            "Retrieved {Count} messages for conversation {ConversationId} (skip: {Skip}, take: {Take})",
            messages.Count,
            conversationId,
            skip,
            take
        );

        return messages;
    }

    /// <inheritdoc />
    public async Task CreateAsync(MessageEntity message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        await using var connection = await _connectionFactory.CreateConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();

        cmd.CommandText =
            @"
            INSERT INTO Messages (Id, SessionId, MessageJson, Timestamp, MessageType)
            VALUES (@Id, @SessionId, @MessageJson, @Timestamp, @MessageType)";

        _ = cmd.Parameters.Add(new SqliteParameter("@Id", message.Id));
        _ = cmd.Parameters.Add(new SqliteParameter("@SessionId", message.SessionId));
        _ = cmd.Parameters.Add(new SqliteParameter("@MessageJson", message.MessageJson));
        _ = cmd.Parameters.Add(new SqliteParameter("@Timestamp", message.Timestamp));
        _ = cmd.Parameters.Add(new SqliteParameter("@MessageType", message.MessageType));

        _ = await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogTrace(
            "Created message {MessageId} for session {SessionId} (type: {MessageType})",
            message.Id,
            message.SessionId,
            message.MessageType
        );
    }

    /// <summary>
    ///     Maps a data reader row to a MessageEntity.
    /// </summary>
    private static MessageEntity MapMessageEntity(SqliteDataReader reader)
    {
        return new MessageEntity
        {
            Id = reader.GetString(0),
            SessionId = reader.GetString(1),
            MessageJson = reader.GetString(2),
            Timestamp = reader.GetInt64(3),
            MessageType = reader.GetString(4),
        };
    }
}
