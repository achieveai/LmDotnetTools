using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.AgUi.Persistence.Database;

/// <summary>
/// Initializes the SQLite database schema for AG-UI persistence.
/// </summary>
/// <remarks>
/// Creates three tables: Sessions, Messages, and Events with appropriate indexes.
/// Safe to call multiple times - uses CREATE TABLE IF NOT EXISTS.
/// </remarks>
public sealed class DatabaseInitializer
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<DatabaseInitializer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseInitializer"/> class.
    /// </summary>
    /// <param name="connectionFactory">The connection factory.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public DatabaseInitializer(IDbConnectionFactory connectionFactory, ILogger<DatabaseInitializer>? logger = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? NullLogger<DatabaseInitializer>.Instance;
    }

    /// <summary>
    /// Initializes the database schema by creating all required tables and indexes.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    /// <remarks>
    /// This method is idempotent and safe to call multiple times.
    /// Uses transactions to ensure schema consistency.
    /// </remarks>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Initializing AG-UI persistence database schema");

        await using var connection = await _connectionFactory.CreateConnectionAsync(ct);
        await using var transaction = connection.BeginTransaction();

        try
        {
            // Create Sessions table
            await using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText =
                    @"
                    CREATE TABLE IF NOT EXISTS Sessions (
                        Id TEXT PRIMARY KEY,
                        ConversationId TEXT,
                        StartTime INTEGER NOT NULL,
                        EndTime INTEGER,
                        Status TEXT NOT NULL,
                        MetadataJson TEXT
                    );";
                _ = await cmd.ExecuteNonQueryAsync(ct);
            }

            // Create indexes on Sessions
            await using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText =
                    @"
                    CREATE INDEX IF NOT EXISTS idx_sessions_conversation
                    ON Sessions(ConversationId);";
                _ = await cmd.ExecuteNonQueryAsync(ct);
            }

            await using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText =
                    @"
                    CREATE INDEX IF NOT EXISTS idx_sessions_status
                    ON Sessions(Status);";
                _ = await cmd.ExecuteNonQueryAsync(ct);
            }

            // Create Messages table
            await using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText =
                    @"
                    CREATE TABLE IF NOT EXISTS Messages (
                        Id TEXT PRIMARY KEY,
                        SessionId TEXT NOT NULL,
                        MessageJson TEXT NOT NULL,
                        Timestamp INTEGER NOT NULL,
                        MessageType TEXT NOT NULL,
                        FOREIGN KEY (SessionId) REFERENCES Sessions(Id) ON DELETE CASCADE
                    );";
                _ = await cmd.ExecuteNonQueryAsync(ct);
            }

            // Create indexes on Messages
            await using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText =
                    @"
                    CREATE INDEX IF NOT EXISTS idx_messages_session
                    ON Messages(SessionId, Timestamp);";
                _ = await cmd.ExecuteNonQueryAsync(ct);
            }

            await using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText =
                    @"
                    CREATE INDEX IF NOT EXISTS idx_messages_timestamp
                    ON Messages(Timestamp);";
                _ = await cmd.ExecuteNonQueryAsync(ct);
            }

            // Create Events table
            await using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText =
                    @"
                    CREATE TABLE IF NOT EXISTS Events (
                        Id TEXT PRIMARY KEY,
                        SessionId TEXT NOT NULL,
                        EventJson TEXT NOT NULL,
                        Timestamp INTEGER NOT NULL,
                        EventType TEXT NOT NULL,
                        FOREIGN KEY (SessionId) REFERENCES Sessions(Id) ON DELETE CASCADE
                    );";
                _ = await cmd.ExecuteNonQueryAsync(ct);
            }

            // Create indexes on Events
            await using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText =
                    @"
                    CREATE INDEX IF NOT EXISTS idx_events_session
                    ON Events(SessionId, Timestamp);";
                _ = await cmd.ExecuteNonQueryAsync(ct);
            }

            await using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText =
                    @"
                    CREATE INDEX IF NOT EXISTS idx_events_timestamp
                    ON Events(Timestamp);";
                _ = await cmd.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
            _logger.LogInformation("Database schema initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize database schema");
            await transaction.RollbackAsync(ct);
            throw;
        }
    }
}
