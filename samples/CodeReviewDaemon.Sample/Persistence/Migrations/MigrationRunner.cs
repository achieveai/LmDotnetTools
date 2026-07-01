using Microsoft.Data.Sqlite;

namespace CodeReviewDaemon.Sample.Persistence.Migrations;

/// <summary>
/// Applies the app-owned <c>PRAGMA user_version</c> migrations (plan §10). Migrations are monotonic,
/// transactional, and crash-idempotent: each runs inside <c>BEGIN IMMEDIATE</c> (a single-writer lock
/// that, with <c>busy_timeout</c>, serializes concurrent migrators) and bumps <c>user_version</c> in
/// the same transaction, so a crash before COMMIT rolls the whole step back and a retry re-applies it
/// cleanly. An unsupported downgrade (database newer than this binary) fails clearly rather than
/// silently corrupting data. <c>user_version</c> is used deliberately instead of SQLite's internal
/// <c>schema_version</c>.
/// </summary>
internal static class MigrationRunner
{
    public static long LatestVersion => SchemaMigrations.LatestVersion;

    /// <summary>
    /// Brings <paramref name="connection"/>'s database up to <see cref="LatestVersion"/>. A no-op when
    /// already current (idempotent re-open). Throws <see cref="InvalidOperationException"/> when the
    /// database is at a higher version than this binary knows about (unsupported downgrade).
    /// </summary>
    public static void Migrate(SqliteConnection connection) =>
        Migrate(connection, SchemaMigrations.All);

    /// <summary>
    /// Core runner, parameterized on the migration set so tests can drive rollback/serialization behavior
    /// with a crafted (e.g. deliberately failing) set. Production calls the single-argument overload with
    /// <see cref="SchemaMigrations.All"/>. Latest is the max version in the set.
    /// </summary>
    internal static void Migrate(SqliteConnection connection, IReadOnlyList<Migration> migrations)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(migrations);

        var current = ReadUserVersion(connection);
        var latest = migrations.Count == 0 ? 0 : migrations.Max(m => m.Version);

        if (current == latest)
        {
            return;
        }

        if (current > latest)
        {
            throw new InvalidOperationException(
                $"Database schema version {current} is newer than this build supports "
                + $"(max {latest}). Downgrade is not supported; run a newer build of the daemon.");
        }

        foreach (var migration in migrations.Where(m => m.Version > current).OrderBy(m => m.Version))
        {
            ApplyOne(connection, migration);
        }
    }

    private static void ApplyOne(SqliteConnection connection, Migration migration)
    {
        // BEGIN IMMEDIATE takes the RESERVED write lock up front so two processes cannot migrate the
        // same step concurrently; busy_timeout makes the loser wait instead of erroring.
        using var transaction = connection.BeginTransaction(deferred: false);

        // Re-check inside the lock: a racing migrator may have already advanced past this step.
        if (ReadUserVersion(connection, transaction) >= migration.Version)
        {
            transaction.Rollback();
            return;
        }

        using (var ddl = connection.CreateCommand())
        {
            ddl.Transaction = transaction;
            ddl.CommandText = migration.Sql;
            _ = ddl.ExecuteNonQuery();
        }

        using (var bump = connection.CreateCommand())
        {
            bump.Transaction = transaction;
            // PRAGMA user_version cannot be parameterized; the value is a trusted long from our own
            // migration list, so string interpolation is safe here.
            bump.CommandText = $"PRAGMA user_version = {migration.Version};";
            _ = bump.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static long ReadUserVersion(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
