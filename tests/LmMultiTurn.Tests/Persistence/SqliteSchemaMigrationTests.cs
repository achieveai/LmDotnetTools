using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LmMultiTurn.Tests.Persistence;

/// <summary>
/// Pins the <c>PRAGMA user_version</c> migration runner (P1 spec 8.1). The runner exists because
/// the <c>tenants</c> table of slice 1 is itself a migration step, and because
/// <c>CREATE TABLE IF NOT EXISTS</c> has no path for adding a column to an existing table.
/// </summary>
public sealed class SqliteSchemaMigrationTests : IAsyncLifetime
{
    private string _databasePath = null!;

    public Task InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"migration_{Guid.NewGuid():N}.db");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        await Task.Delay(50);
        TryDelete(_databasePath);
        TryDelete(_databasePath + "-wal");
        TryDelete(_databasePath + "-shm");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // The pool can still hold the handle briefly; a leaked temp file is not a test failure.
        }
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
            }.ToString()
        );
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<long> ReadUserVersionAsync(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
        _ = command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)
            > 0;
    }

    [Fact]
    public async Task FreshDatabase_ReachesTheLatestVersion()
    {
        await using var connection = await OpenAsync();

        await SqliteSchemaInitializer.InitializeSchemaAsync(connection);

        (await ReadUserVersionAsync(connection)).Should().Be(SqliteSchemaInitializer.LatestSchemaVersion);
        SqliteSchemaInitializer
            .LatestSchemaVersion.Should()
            .Be(
                5,
                "slice 2 adds the thread_metadata owner columns (3) and resource_grants (4) on top of "
                    + "slice 1's two steps, and #680 adds the messages seq column and index (5)"
            );
    }

    [Fact]
    public async Task FreshDatabase_CreatesTheStepOneTablesAndTheStepTwoTenantTables()
    {
        await using var connection = await OpenAsync();

        await SqliteSchemaInitializer.InitializeSchemaAsync(connection);

        // Step 1 - the eight tables that already shipped.
        (await TableExistsAsync(connection, "messages"))
            .Should()
            .BeTrue();
        (await TableExistsAsync(connection, "thread_metadata")).Should().BeTrue();
        (await TableExistsAsync(connection, "notify_waits")).Should().BeTrue();

        // Step 2 - the tenant registry.
        (await TableExistsAsync(connection, "tenants"))
            .Should()
            .BeTrue();
        (await TableExistsAsync(connection, "tenant_admins")).Should().BeTrue();
    }

    [Fact]
    public async Task DatabaseFromAnEarlierBuild_IsRecognisedAsVersionOne_AndUpgradesWithoutDataLoss()
    {
        // Simulate a database an earlier build created: the step-1 tables exist, a row is present,
        // and user_version was never set - so it reads 0 even though step 1 is effectively applied.
        await using (var seed = await OpenAsync())
        {
            using var create = seed.CreateCommand();
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS thread_metadata (
                    thread_id      TEXT PRIMARY KEY,
                    current_run_id TEXT,
                    last_updated   INTEGER NOT NULL,
                    metadata_json  TEXT
                );
                INSERT INTO thread_metadata (thread_id, last_updated) VALUES ('legacy-thread', 42);
                """;
            _ = await create.ExecuteNonQueryAsync();

            (await ReadUserVersionAsync(seed)).Should().Be(0, "an earlier build never wrote user_version");
        }

        await using var connection = await OpenAsync();
        await SqliteSchemaInitializer.InitializeSchemaAsync(connection);

        (await ReadUserVersionAsync(connection)).Should().Be(SqliteSchemaInitializer.LatestSchemaVersion);
        (await TableExistsAsync(connection, "tenants")).Should().BeTrue();
        (await TableExistsAsync(connection, "resource_grants")).Should().BeTrue();

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT last_updated FROM thread_metadata WHERE thread_id = 'legacy-thread';";
        var lastUpdated = await read.ExecuteScalarAsync();
        Convert
            .ToInt64(lastUpdated, System.Globalization.CultureInfo.InvariantCulture)
            .Should()
            .Be(42, "the upgrade must not lose a pre-existing row");
    }

    [Fact]
    public async Task RunningTwice_IsIdempotent_AndDoesNotAdvancePastTheLatestVersion()
    {
        await using var connection = await OpenAsync();

        await SqliteSchemaInitializer.InitializeSchemaAsync(connection);
        await SqliteSchemaInitializer.InitializeSchemaAsync(connection);

        (await ReadUserVersionAsync(connection)).Should().Be(SqliteSchemaInitializer.LatestSchemaVersion);
    }

    [Fact]
    public async Task AnAlreadyAppliedStep_IsSkippedEntirely_NotReRunAndFoundIdempotent()
    {
        // A step that is NOT expressed as CREATE ... IF NOT EXISTS - a future ALTER TABLE, or the
        // quarantine-tenant row of slice 2 - is only safe if the runner skips applied steps rather
        // than relying on every statement happening to be idempotent. Re-running the current steps
        // cannot distinguish those two behaviours, because every statement they contain IS
        // idempotent. So claim the version WITHOUT the tables: an empty database stamped at the
        // latest version must come back still empty. Only a runner that consults user_version
        // produces that; one that runs every statement would create the tables.
        await using (var stamp = await OpenAsync())
        {
            using var command = stamp.CreateCommand();
            command.CommandText = $"PRAGMA user_version = {SqliteSchemaInitializer.LatestSchemaVersion};";
            _ = await command.ExecuteNonQueryAsync();
        }

        await using var connection = await OpenAsync();
        await SqliteSchemaInitializer.InitializeSchemaAsync(connection);

        (await TableExistsAsync(connection, "tenants"))
            .Should()
            .BeFalse("step 2 was already recorded as applied, so it must not run again");
        (await TableExistsAsync(connection, "thread_metadata"))
            .Should()
            .BeFalse("step 1 was already recorded as applied, so it must not run again");
    }

    [Fact]
    public async Task APartiallyMigratedDatabase_AppliesOnlyTheStepsItHasNotTaken()
    {
        // The test above stops at the runner's already-at-latest fast path and so never reaches
        // the per-step guard inside the loop. This one does: stamped at 1, the runner must enter
        // the loop, skip step 1 because 1 <= 1, and apply the rest. A runner that dropped the
        // per-step guard would re-run step 1 as well, which `messages` below detects.
        //
        // The fixture creates thread_metadata and messages by hand rather than leaving the database
        // empty. Slice 2's step 3 ALTERs the first and #680's step 5 ALTERs the second, so an empty
        // database stamped at 1 is not a state the runner can migrate - and it is not a state that
        // exists: user_version 1 asserts step 1 ran. Before slice 2 the distinction did not matter,
        // because every step was a CREATE.
        await using (var stamp = await OpenAsync())
        {
            using var command = stamp.CreateCommand();
            command.CommandText = """
                CREATE TABLE thread_metadata (
                    thread_id      TEXT PRIMARY KEY,
                    current_run_id TEXT,
                    last_updated   INTEGER NOT NULL,
                    metadata_json  TEXT
                );
                CREATE TABLE messages (
                    id        TEXT PRIMARY KEY,
                    thread_id TEXT NOT NULL
                );
                PRAGMA user_version = 1;
                """;
            _ = await command.ExecuteNonQueryAsync();
        }

        await using var connection = await OpenAsync();
        await SqliteSchemaInitializer.InitializeSchemaAsync(connection);

        (await TableExistsAsync(connection, "tenants")).Should().BeTrue("step 2 had not been applied");
        (await TableExistsAsync(connection, "resource_grants")).Should().BeTrue("step 4 had not been applied");
        (await TableExistsAsync(connection, "notify_waits"))
            .Should()
            .BeFalse("step 1 was already recorded as applied, so it must be skipped");
        (await ReadUserVersionAsync(connection)).Should().Be(SqliteSchemaInitializer.LatestSchemaVersion);
    }

    [Fact]
    public async Task RunningTwiceInSequence_LeavesRowsWrittenBetweenTheRunsUntouched()
    {
        await using var connection = await OpenAsync();
        await SqliteSchemaInitializer.InitializeSchemaAsync(connection);

        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO tenants (tenant_id, entra_tenant_id, display_name, status, created_at, created_by)
                VALUES ('tnt_probe', 'entra-probe', 'Probe', 'active', 1, 'test');
                """;
            _ = await insert.ExecuteNonQueryAsync();
        }

        await SqliteSchemaInitializer.InitializeSchemaAsync(connection);

        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM tenants;";
        Convert
            .ToInt64(await count.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)
            .Should()
            .Be(1);
        (await ReadUserVersionAsync(connection)).Should().Be(SqliteSchemaInitializer.LatestSchemaVersion);
    }

    [Fact]
    public async Task ADatabaseWrittenByANewerBuild_IsRefused_NotOpenedOptimistically()
    {
        // The rollback case (#386). A deployment rolled back to an older binary opens a file a
        // newer one already migrated. `>= LatestSchemaVersion` reads that as "already current" and
        // hands the caller a connection to a schema this build has no model for - the older code
        // then queries tables and columns whose meaning it does not know.
        //
        // Refusing is the only safe answer: this build cannot migrate DOWN, and it cannot know what
        // the newer schema changed. The repo already takes this position for workflow snapshots
        // (WorkflowInstanceSnapshot refuses a newer SchemaVersion); this is the same rule for the
        // one store that persists customer conversations.
        await using (var seed = await OpenAsync())
        {
            await SqliteSchemaInitializer.InitializeSchemaAsync(seed);

            using var stamp = seed.CreateCommand();
            stamp.CommandText = FormattableString.Invariant(
                $"PRAGMA user_version = {SqliteSchemaInitializer.LatestSchemaVersion + 1};"
            );
            _ = await stamp.ExecuteNonQueryAsync();
        }

        await using var connection = await OpenAsync();

        var act = async () => await SqliteSchemaInitializer.InitializeSchemaAsync(connection);

        _ = (await act.Should().ThrowAsync<NotSupportedException>())
            .Which.Message.Should()
            .Contain(
                (SqliteSchemaInitializer.LatestSchemaVersion + 1).ToString(
                    System.Globalization.CultureInfo.InvariantCulture
                ),
                "an operator reading the crash needs to see which version the file is at"
            );
    }

    [Fact]
    public async Task ADatabaseAtTheLatestVersion_IsStillOpenedWithoutComplaint()
    {
        // Non-vacuity for the test above. A refusal that also rejected an ordinary already-current
        // database would pass that assertion and break every process on the deployment, and the
        // boundary between the two is exactly one integer.
        await using (var seed = await OpenAsync())
        {
            await SqliteSchemaInitializer.InitializeSchemaAsync(seed);
        }

        await using var connection = await OpenAsync();

        var act = async () => await SqliteSchemaInitializer.InitializeSchemaAsync(connection);

        _ = await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task UpgradingAPopulatedDatabase_AddsTheOwnerColumns_AndKeepsEveryExistingRow()
    {
        // The real upgrade path, which no existing test covers. Step 3 is `ALTER TABLE ... ADD
        // COLUMN`, the one step in the ladder that is structurally unsafe to run twice, and every
        // test that reaches it does so against an EMPTY table or exits at the fast path first.
        //
        // A customer's database is not empty. This one is stamped at version 2 - the last version
        // before the owner columns existed - with rows already in thread_metadata, which is the
        // exact state a deployment upgrading into slice 2 is in. The messages table is present too:
        // step 1 created it, and #680's step 5 ALTERs it.
        await using (var stamp = await OpenAsync())
        {
            using var command = stamp.CreateCommand();
            command.CommandText = """
                CREATE TABLE thread_metadata (
                    thread_id      TEXT PRIMARY KEY,
                    current_run_id TEXT,
                    last_updated   INTEGER NOT NULL,
                    metadata_json  TEXT
                );
                CREATE TABLE messages (
                    id        TEXT PRIMARY KEY,
                    thread_id TEXT NOT NULL
                );
                INSERT INTO thread_metadata (thread_id, current_run_id, last_updated, metadata_json)
                VALUES ('thread-kept-1', 'run-a', 111, '{"title":"first"}'),
                       ('thread-kept-2', NULL,    222, '{"title":"second"}');
                PRAGMA user_version = 2;
                """;
            _ = await command.ExecuteNonQueryAsync();
        }

        await using var connection = await OpenAsync();
        await SqliteSchemaInitializer.InitializeSchemaAsync(connection);

        (await ReadUserVersionAsync(connection)).Should().Be(SqliteSchemaInitializer.LatestSchemaVersion);

        // The columns step 3 adds are present...
        foreach (var column in new[] { "tenant_id", "owner_user_id", "owner_app_id", "visibility" })
        {
            (await ColumnExistsAsync(connection, "thread_metadata", column)).Should().BeTrue($"step 3 adds {column}");
        }

        // ...and the rows that were there before the ALTER are still there, unmodified, with the
        // new columns null rather than defaulted. Null is what the startup repair looks for; a
        // migration that defaulted tenant_id would make those conversations invisible to the repair
        // and therefore permanently untenanted.
        using var read = connection.CreateCommand();
        read.CommandText = """
            SELECT thread_id, current_run_id, last_updated, metadata_json, tenant_id
            FROM thread_metadata ORDER BY thread_id;
            """;

        var rows = new List<(string ThreadId, string? RunId, long Updated, string? Json, bool TenantNull)>();
        await using (var reader = await read.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                rows.Add(
                    (
                        reader.GetString(0),
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.GetInt64(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.IsDBNull(4)
                    )
                );
            }
        }

        _ = rows.Should()
            .Equal(
                ("thread-kept-1", "run-a", 111L, """{"title":"first"}""", true),
                ("thread-kept-2", null, 222L, """{"title":"second"}""", true)
            );
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = FormattableString.Invariant(
            $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $name;"
        );
        _ = command.Parameters.AddWithValue("$name", column);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)
            > 0;
    }

    [Fact]
    public async Task TenantsTable_RefusesTwoTenantsClaimingTheSameEntraDirectory()
    {
        // The partial unique index is what makes a cross-tenant identity collision structurally
        // impossible, so it is asserted at the schema level rather than trusted to the store.
        await using var connection = await OpenAsync();
        await SqliteSchemaInitializer.InitializeSchemaAsync(connection);

        async Task InsertAsync(string tenantId, string? entraTenantId)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO tenants (tenant_id, entra_tenant_id, display_name, status, created_at, created_by)
                VALUES ($t, $e, 'x', 'active', 1, 'test');
                """;
            _ = command.Parameters.AddWithValue("$t", tenantId);
            _ = command.Parameters.AddWithValue("$e", (object?)entraTenantId ?? DBNull.Value);
            _ = await command.ExecuteNonQueryAsync();
        }

        await InsertAsync("tnt_a", "shared-entra-tid");

        var act = async () => await InsertAsync("tnt_b", "shared-entra-tid");
        await act.Should().ThrowAsync<SqliteException>();

        // ... while still permitting more than one row with no Entra directory behind it at all,
        // which is what the legacy tenant of slice 2 needs.
        await InsertAsync("tnt_legacy_one", null);
        await InsertAsync("tnt_legacy_two", null);
    }
}
