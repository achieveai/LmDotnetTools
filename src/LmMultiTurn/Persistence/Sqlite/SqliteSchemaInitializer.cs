using Microsoft.Data.Sqlite;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;

/// <summary>
/// Handles SQLite schema initialization for conversation persistence.
/// </summary>
public static class SqliteSchemaInitializer
{
    private const string CreateMessagesTableSql = """
        CREATE TABLE IF NOT EXISTS messages (
            id TEXT PRIMARY KEY,
            thread_id TEXT NOT NULL,
            run_id TEXT NOT NULL,
            parent_run_id TEXT,
            generation_id TEXT,
            message_order_idx INTEGER,
            timestamp INTEGER NOT NULL,
            message_type TEXT NOT NULL,
            role TEXT NOT NULL,
            from_agent TEXT,
            message_json TEXT NOT NULL
        );
        """;

    private const string CreateMessagesIndexSql = """
        CREATE INDEX IF NOT EXISTS idx_messages_thread_id
        ON messages (thread_id, timestamp, message_order_idx);
        """;

    private const string CreateMetadataTableSql = """
        CREATE TABLE IF NOT EXISTS thread_metadata (
            thread_id TEXT PRIMARY KEY,
            current_run_id TEXT,
            last_updated INTEGER NOT NULL,
            metadata_json TEXT
        );
        """;

    /// <summary>
    /// Initializes the database schema if it doesn't exist.
    /// </summary>
    /// <param name="connection">An open SQLite connection.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task InitializeSchemaAsync(
        SqliteConnection connection,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var transaction = connection.BeginTransaction();

        try
        {
            using var createMessagesCmd = connection.CreateCommand();
            createMessagesCmd.CommandText = CreateMessagesTableSql;
            createMessagesCmd.Transaction = transaction;
            _ = await createMessagesCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            using var createIndexCmd = connection.CreateCommand();
            createIndexCmd.CommandText = CreateMessagesIndexSql;
            createIndexCmd.Transaction = transaction;
            _ = await createIndexCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            using var createMetadataCmd = connection.CreateCommand();
            createMetadataCmd.CommandText = CreateMetadataTableSql;
            createMetadataCmd.Transaction = transaction;
            _ = await createMetadataCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Initializes the database schema using a connection factory.
    /// </summary>
    /// <param name="connectionFactory">The connection factory.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task InitializeSchemaAsync(
        ISqliteConnectionFactory connectionFactory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);

        await using var connection = await connectionFactory.GetConnectionAsync(ct)
            .ConfigureAwait(false);
        await InitializeSchemaAsync(connection, ct).ConfigureAwait(false);
    }
}
