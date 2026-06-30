using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Migrations;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Data.Sqlite;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P2.1 — the <c>PRAGMA user_version</c> migration runner (plan §10) and the connection PRAGMAs the
/// store depends on for durability/concurrency.
/// </summary>
public sealed class MigrationTests
{
    [Fact]
    public void Open_applies_wal_busy_timeout_and_foreign_keys()
    {
        using var db = new TempSqliteDatabase();

        using var connection = SqliteConnectionFactory.Open(db.ConnectionString);

        ReadScalar(connection, "PRAGMA journal_mode;").Should().Be("wal");
        ReadScalar(connection, "PRAGMA busy_timeout;").Should().Be(SqliteConnectionFactory.BusyTimeoutMilliseconds.ToString());
        ReadScalar(connection, "PRAGMA foreign_keys;").Should().Be("1");
    }

    [Fact]
    public void Fresh_database_migrates_to_latest_version_and_creates_all_tables()
    {
        using var db = new TempSqliteDatabase();
        using var connection = SqliteConnectionFactory.Open(db.ConnectionString);

        MigrationRunner.Migrate(connection);

        ReadUserVersion(connection).Should().Be(MigrationRunner.LatestVersion);
        foreach (var table in new[] { "repo", "review_run", "poll_cursor", "review_outbox", "review_artifact" })
        {
            TableExists(connection, table).Should().BeTrue($"migration v1 creates the '{table}' table");
        }
    }

    [Fact]
    public void Re_running_migrate_on_a_current_database_is_a_noop()
    {
        using var db = new TempSqliteDatabase();
        using var connection = SqliteConnectionFactory.Open(db.ConnectionString);

        MigrationRunner.Migrate(connection);
        var afterFirst = ReadUserVersion(connection);

        // Re-open + re-migrate, mirroring a daemon restart against an already-migrated DB.
        using var reopened = SqliteConnectionFactory.Open(db.ConnectionString);
        MigrationRunner.Migrate(reopened);

        ReadUserVersion(reopened).Should().Be(afterFirst).And.Be(MigrationRunner.LatestVersion);
    }

    [Fact]
    public void Migrating_an_older_database_preserves_pre_existing_data()
    {
        using var db = new TempSqliteDatabase();

        // Simulate an older deployment: a DB file that exists with user_version = 0 and an unrelated
        // legacy table holding data. Forward migration must be additive — it must not drop this.
        using (var legacy = SqliteConnectionFactory.Open(db.ConnectionString))
        {
            Execute(legacy, "CREATE TABLE legacy_marker (note TEXT NOT NULL);");
            Execute(legacy, "INSERT INTO legacy_marker (note) VALUES ('pre-existing');");
            ReadUserVersion(legacy).Should().Be(0, "the legacy DB predates user_version migrations");
        }

        using var connection = SqliteConnectionFactory.Open(db.ConnectionString);
        MigrationRunner.Migrate(connection);

        ReadUserVersion(connection).Should().Be(MigrationRunner.LatestVersion);
        TableExists(connection, "review_run").Should().BeTrue("v1 tables are added");
        TableExists(connection, "legacy_marker").Should().BeTrue("forward migration is non-destructive");
        ReadScalar(connection, "SELECT note FROM legacy_marker;").Should().Be("pre-existing");
    }

    [Fact]
    public void A_database_newer_than_this_build_fails_clearly()
    {
        using var db = new TempSqliteDatabase();
        using var connection = SqliteConnectionFactory.Open(db.ConnectionString);
        Execute(connection, $"PRAGMA user_version = {MigrationRunner.LatestVersion + 100};");

        var act = () => MigrationRunner.Migrate(connection);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*newer than this build*");
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }

    private static string? ReadScalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()?.ToString();
    }

    private static long ReadUserVersion(SqliteConnection connection) =>
        Convert.ToInt64(ReadScalar(connection, "PRAGMA user_version;"));

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;";
        _ = command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }
}
