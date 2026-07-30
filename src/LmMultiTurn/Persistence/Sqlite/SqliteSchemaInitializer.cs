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

    private const string CreateRunLedgerTableSql = """
        CREATE TABLE IF NOT EXISTS run_ledger (
            run_id TEXT PRIMARY KEY,
            thread_id TEXT NOT NULL,
            status TEXT NOT NULL,
            input_ids TEXT NOT NULL,
            created_at INTEGER NOT NULL,
            updated_at INTEGER NOT NULL
        );
        """;

    private const string CreateRunLedgerIndexSql = """
        CREATE INDEX IF NOT EXISTS idx_run_ledger_thread_id
        ON run_ledger (thread_id, created_at);
        """;

    private const string CreateAcceptedInputsTableSql = """
        CREATE TABLE IF NOT EXISTS accepted_inputs (
            thread_id TEXT NOT NULL,
            input_id TEXT NOT NULL,
            accepted_at INTEGER NOT NULL,
            PRIMARY KEY (thread_id, input_id)
        );
        """;

    // Kept beside run_ledger rather than added to it: the ledger's columns back the status API,
    // and lifecycle observation must be addable without migrating that table.
    private const string CreateRunLifecycleTableSql = """
        CREATE TABLE IF NOT EXISTS run_lifecycle (
            run_id TEXT PRIMARY KEY,
            thread_id TEXT NOT NULL,
            generation_id TEXT NOT NULL,
            parent_run_id TEXT,
            parent_thread_id TEXT,
            spawning_tool_call_id TEXT,
            sub_agent_id TEXT,
            cause_kind TEXT NOT NULL,
            cause_tool_call_id TEXT,
            phase TEXT NOT NULL,
            outcome TEXT,
            turn_count INTEGER NOT NULL,
            started_at INTEGER NOT NULL,
            updated_at INTEGER NOT NULL,
            terminal_at INTEGER
        );
        """;

    private const string CreateRunLifecycleIndexSql = """
        CREATE INDEX IF NOT EXISTS idx_run_lifecycle_thread
        ON run_lifecycle (thread_id, started_at);
        """;

    // A table rather than a JSON column on run_lifecycle, because resolving a deferred call is a
    // conditional single-row update keyed on (thread_id, tool_call_id) — the primary key below —
    // and that is what makes concurrent resolutions of the same call resolve exactly once.
    private const string CreateRunDeferredCallsTableSql = """
        CREATE TABLE IF NOT EXISTS run_deferred_calls (
            thread_id TEXT NOT NULL,
            tool_call_id TEXT NOT NULL,
            run_id TEXT NOT NULL,
            tool_name TEXT NOT NULL,
            generation_id TEXT,
            ordinal INTEGER NOT NULL,
            deferred_at INTEGER NOT NULL,
            resolved_at INTEGER,
            resolution_fingerprint TEXT,
            child_run_id TEXT,
            PRIMARY KEY (thread_id, tool_call_id)
        );
        """;

    private const string CreateRunDeferredCallsIndexSql = """
        CREATE INDEX IF NOT EXISTS idx_run_deferred_calls_run
        ON run_deferred_calls (run_id, ordinal);
        """;

    private const string CreateNotifyWaitsTableSql = """
        CREATE TABLE IF NOT EXISTS notify_waits (
            wait_id       TEXT NOT NULL,
            thread_id     TEXT NOT NULL,
            kind          TEXT NOT NULL,
            args          TEXT NOT NULL,
            label         TEXT NULL,
            max_fires     INTEGER NULL,
            fires_so_far  INTEGER NOT NULL DEFAULT 0,
            timeout_at    INTEGER NOT NULL,
            armed_at      INTEGER NOT NULL,
            status        TEXT NOT NULL,
            PRIMARY KEY (thread_id, wait_id)
        );
        """;

    private const string CreateNotifyWaitsIndexSql =
        "CREATE INDEX IF NOT EXISTS ix_notify_waits_thread ON notify_waits (thread_id);";

    /// <summary>
    /// Every schema statement, in dependency order, applied in one transaction. All are
    /// <c>IF NOT EXISTS</c>, so this is also the upgrade path for a database created by an earlier
    /// build: tables added here appear on next open without a migration step.
    /// </summary>
    private static readonly string[] SchemaStatements =
    [
        CreateMessagesTableSql,
        CreateMessagesIndexSql,
        CreateMetadataTableSql,
        CreateRunLedgerTableSql,
        CreateRunLedgerIndexSql,
        CreateAcceptedInputsTableSql,
        CreateRunLifecycleTableSql,
        CreateRunLifecycleIndexSql,
        CreateRunDeferredCallsTableSql,
        CreateRunDeferredCallsIndexSql,
        CreateNotifyWaitsTableSql,
        CreateNotifyWaitsIndexSql,
    ];

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
            foreach (var sql in SchemaStatements)
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.Transaction = transaction;
                _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            transaction.Commit();
        }
        catch
        {
            try
            {
                transaction.Rollback();
            }
            catch
            {
                // Swallow: a rollback failure (e.g. the connection is already broken) must
                // never replace the original schema-initialization exception being propagated
                // below - that exception is the one callers need to diagnose the real failure.
            }

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
