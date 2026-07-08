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
    public void Migration_v2_adds_the_confidentiality_trust_columns_to_review_run()
    {
        using var db = new TempSqliteDatabase();
        using var connection = SqliteConnectionFactory.Open(db.ConnectionString);

        MigrationRunner.Migrate(connection);

        ColumnExists(connection, "review_run", "is_fork_pr").Should().BeTrue("migration v2 adds is_fork_pr");
        ColumnExists(connection, "review_run", "is_target_repo_public").Should().BeTrue("migration v2 adds is_target_repo_public");
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

    [Fact]
    public void A_failing_migration_rolls_back_atomically_and_leaves_user_version_unchanged()
    {
        // PR #121 M8 — a migration whose SQL fails partway must roll the WHOLE step back (even the
        // statements that ran before the failure) and must NOT advance user_version, so a retry re-applies
        // it cleanly. Driven with a crafted set via the internal overload.
        using var db = new TempSqliteDatabase();
        using var connection = SqliteConnectionFactory.Open(db.ConnectionString);
        var migrations = new List<Migration>
        {
            new(1, "CREATE TABLE good (id INTEGER); THIS_IS_NOT_VALID_SQL;"),
        };

        var act = () => MigrationRunner.Migrate(connection, migrations);

        act.Should().Throw<SqliteException>();
        ReadUserVersion(connection).Should().Be(0, "the failed migration's transaction rolled back");
        TableExists(connection, "good").Should().BeFalse("the whole migration rolled back — even the valid CREATE");
        // The connection is still usable after the rolled-back migration.
        ReadScalar(connection, "SELECT 1;").Should().Be("1");
    }

    [Fact]
    public async Task Concurrent_migrators_serialize_and_converge_to_the_latest_version()
    {
        // PR #121 M8 — two migrators racing the same fresh DB must serialize on BEGIN IMMEDIATE (+
        // busy_timeout) and converge, without a double-apply or corruption. Each opens its own connection.
        using var db = new TempSqliteDatabase();

        var tasks = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            using var connection = SqliteConnectionFactory.Open(db.ConnectionString);
            MigrationRunner.Migrate(connection);
        }));

        var act = async () => await Task.WhenAll(tasks);

        await act.Should().NotThrowAsync("concurrent migrators serialize rather than collide");
        using var verify = SqliteConnectionFactory.Open(db.ConnectionString);
        ReadUserVersion(verify).Should().Be(MigrationRunner.LatestVersion);
        TableExists(verify, "review_run").Should().BeTrue("the schema is intact after concurrent migration");
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

    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM pragma_table_info($table) WHERE name = $column;";
        _ = command.Parameters.AddWithValue("$table", table);
        _ = command.Parameters.AddWithValue("$column", column);
        return command.ExecuteScalar() is not null;
    }
}
