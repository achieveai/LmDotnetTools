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
            }.ToString());
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
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    [Fact]
    public async Task FreshDatabase_ReachesTheLatestVersion()
    {
        await using var connection = await OpenAsync();

        await SqliteSchemaInitializer.InitializeSchemaAsync(connection);

        (await ReadUserVersionAsync(connection))
            .Should().Be(SqliteSchemaInitializer.LatestSchemaVersion);
        SqliteSchemaInitializer.LatestSchemaVersion.Should().Be(2, "slice 1 takes the database to user_version 2");
    }

    [Fact]
    public async Task FreshDatabase_CreatesTheStepOneTablesAndTheStepTwoTenantTables()
    {
        await using var connection = await OpenAsync();

        await SqliteSchemaInitializer.InitializeSchemaAsync(connection);

        // Step 1 - the eight tables that already shipped.
        (await TableExistsAsync(connection, "messages")).Should().BeTrue();
        (await TableExistsAsync(connection, "thread_metadata")).Should().BeTrue();
        (await TableExistsAsync(connection, "notify_waits")).Should().BeTrue();

        // Step 2 - the tenant registry.
        (await TableExistsAsync(connection, "tenants")).Should().BeTrue();
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

        (await ReadUserVersionAsync(connection)).Should().Be(2);
        (await TableExistsAsync(connection, "tenants")).Should().BeTrue();

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT last_updated FROM thread_metadata WHERE thread_id = 'legacy-thread';";
        var lastUpdated = await read.ExecuteScalarAsync();
        Convert.ToInt64(lastUpdated, System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(42, "the upgrade must not lose a pre-existing row");
    }

    [Fact]
    public async Task RunningTwice_IsIdempotent_AndDoesNotAdvancePastTheLatestVersion()
    {
        await using var connection = await OpenAsync();

        await SqliteSchemaInitializer.InitializeSchemaAsync(connection);
        await SqliteSchemaInitializer.InitializeSchemaAsync(connection);

        (await ReadUserVersionAsync(connection))
            .Should().Be(SqliteSchemaInitializer.LatestSchemaVersion);
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
            command.CommandText =
                $"PRAGMA user_version = {SqliteSchemaInitializer.LatestSchemaVersion};";
            _ = await command.ExecuteNonQueryAsync();
        }

        await using var connection = await OpenAsync();
        await SqliteSchemaInitializer.InitializeSchemaAsync(connection);

        (await TableExistsAsync(connection, "tenants"))
            .Should().BeFalse("step 2 was already recorded as applied, so it must not run again");
        (await TableExistsAsync(connection, "thread_metadata"))
            .Should().BeFalse("step 1 was already recorded as applied, so it must not run again");
    }

    [Fact]
    public async Task APartiallyMigratedDatabase_AppliesOnlyTheStepsItHasNotTaken()
    {
        // The test above stops at the runner's already-at-latest fast path and so never reaches
        // the per-step guard inside the loop. This one does: stamped at 1 with no tables at all,
        // the runner must enter the loop, skip step 1 because 1 <= 1, and apply step 2 only. A
        // runner that dropped the per-step guard would create the step-1 tables as well.
        await using (var stamp = await OpenAsync())
        {
            using var command = stamp.CreateCommand();
            command.CommandText = "PRAGMA user_version = 1;";
            _ = await command.ExecuteNonQueryAsync();
        }

        await using var connection = await OpenAsync();
        await SqliteSchemaInitializer.InitializeSchemaAsync(connection);

        (await TableExistsAsync(connection, "tenants"))
            .Should().BeTrue("step 2 had not been applied");
        (await TableExistsAsync(connection, "thread_metadata"))
            .Should().BeFalse("step 1 was already recorded as applied, so it must be skipped");
        (await ReadUserVersionAsync(connection)).Should().Be(2);
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
        Convert.ToInt64(await count.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(1);
        (await ReadUserVersionAsync(connection)).Should().Be(2);
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
