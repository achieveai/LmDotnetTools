using Microsoft.Data.Sqlite;

namespace CodeReviewDaemon.Sample.Persistence;

/// <summary>
/// Opens SQLite connections for the daemon's orchestration store with the durability/concurrency
/// PRAGMAs the design mandates: <c>journal_mode=WAL</c> (concurrent readers alongside the single
/// writer), <c>busy_timeout</c> (wait rather than fail under the migration/write lock), and
/// <c>foreign_keys=ON</c> (the FK graph between repo → review_run → outbox/artifact is enforced).
/// <c>foreign_keys</c> and <c>busy_timeout</c> are per-connection, so they are re-applied on every
/// open.
/// </summary>
internal static class SqliteConnectionFactory
{
    /// <summary>Default lock-wait before SQLite returns <c>SQLITE_BUSY</c>.</summary>
    public const int BusyTimeoutMilliseconds = 5_000;

    public static SqliteConnection Open(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var connection = new SqliteConnection(connectionString);
        connection.Open();
        ApplyPragmas(connection);
        return connection;
    }

    private static void ApplyPragmas(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        // journal_mode is database-level/persistent; the others are per-connection. Applying all three
        // on every open keeps a freshly pooled connection correct.
        command.CommandText =
            $"PRAGMA journal_mode = WAL;"
            + $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds};"
            + "PRAGMA foreign_keys = ON;";
        _ = command.ExecuteNonQuery();
    }
}
