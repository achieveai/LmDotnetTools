using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;
using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;
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
        SqliteTestPools.Clear(_databasePath);
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
                7,
                "slice 2 adds the thread_metadata owner columns (3) and resource_grants (4) on top of "
                    + "slice 1's two steps, #680 adds the messages seq column and index (5), and "
                    + "conversation fork adds the messages parent_message_id column (6), while "
                    + "compact listing projections and their indexes are step 7"
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
    public async Task VersionSixUpgrade_CreatesListingSchema_AndBackfillsEveryProjection()
    {
        await SeedVersionSixDatabaseAsync();

        await using (var store = new SqliteConversationStore(_databasePath))
        {
            _ = await store.ListThreadsAsync(limit: 1);
        }

        await using var connection = await OpenAsync();
        (await ReadUserVersionAsync(connection)).Should().Be(7);
        (await TableExistsAsync(connection, "schema_data_migrations")).Should().BeTrue();
        (
            await ReadNamesAsync(
                connection,
                """
                SELECT name
                FROM pragma_table_info('thread_metadata')
                WHERE name IN ('deleted_at', 'forked_from_thread_id', 'subagent_parent_thread_id')
                ORDER BY name;
                """
            )
        ).Should().Equal("deleted_at", "forked_from_thread_id", "subagent_parent_thread_id");
        (
            await ReadNamesAsync(
                connection,
                """
                SELECT name
                FROM sqlite_master
                WHERE type = 'index'
                  AND name IN (
                    'idx_thread_metadata_fork_parent',
                    'idx_thread_metadata_last_used_cover',
                    'idx_thread_metadata_subagent_parent')
                ORDER BY name;
                """
            )
        ).Should().Equal("idx_thread_metadata_fork_parent", "idx_thread_metadata_last_used_cover", "idx_thread_metadata_subagent_parent");
        (await ReadListingProjectionsAsync(connection))
            .Should()
            .Equal(
                ("a-null", null, null, null),
                ("b-deleted", 123L, null, null),
                ("c-fork", null, "source-thread", null),
                ("d-parent", null, null, "root-thread")
            );

        using var progress = connection.CreateCommand();
        progress.CommandText = """
            SELECT cursor, completed_at
            FROM schema_data_migrations
            WHERE name = 'thread-metadata-v7';
            """;
        await using var reader = await progress.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be("d-parent");
        reader.IsDBNull(1).Should().BeFalse();
    }

    [Fact]
    public async Task VersionSixUpgrade_MigratesCanonicalUsage_RemovesLegacyKeys_AndPinsAggregateOnlyRows()
    {
        var first = UsageAttempt("a-canonical", "a-1", revision: 1, inputTokens: 10);
        var second = UsageAttempt("a-canonical", "a-2", revision: 2, inputTokens: 20);
        var recordsOnly = UsageAttempt("b-records-only", "b-1", revision: 4, inputTokens: 40);
        var aggregateOnlyRecord = UsageAttempt("c-aggregate-only", "legacy", revision: 7, inputTokens: 70);
        var aggregateOnly = ConversationUsageAggregate.Fold(
            "c-aggregate-only",
            [aggregateOnlyRecord],
            7,
            UsageCompleteness.Partial
        );
        await SeedVersionSixUsageDatabaseAsync([
            (
                "a-canonical",
                UsageMetadata(
                    ConversationUsageAggregate.Fold("a-canonical", [first, second], 2, UsageCompleteness.Complete),
                    [first, second]
                )
            ),
            ("b-records-only", UsageMetadata(null, [recordsOnly])),
            ("c-aggregate-only", UsageMetadata(aggregateOnly, null)),
        ]);

        await using (var store = new SqliteConversationStore(_databasePath))
        {
            _ = await store.ListThreadsAsync(limit: 1);

            var canonical = await store.LoadUsageAsync("a-canonical");
            canonical!.Records.Select(record => record.ProviderAttemptId).Should().Equal("a-1", "a-2");
            canonical.FoldedRevision.Should().Be(2);
            canonical.Completeness.Should().Be(UsageCompleteness.Complete);

            var inferred = await store.LoadUsageAsync("b-records-only");
            inferred!.Records.Should().ContainSingle().Which.ProviderAttemptId.Should().Be("b-1");
            inferred.FoldedRevision.Should().Be(4);

            var aggregateOnlySnapshot = await store.LoadUsageAsync("c-aggregate-only");
            aggregateOnlySnapshot!.Records.Should().BeEmpty();
            aggregateOnlySnapshot.Aggregate.Should().BeEquivalentTo(aggregateOnly);
            (await ConversationUsageProjection.LoadAsync(store, "c-aggregate-only"))!
                .Should()
                .BeEquivalentTo(aggregateOnly);
        }

        await using var connection = await OpenAsync();
        (await TableExistsAsync(connection, "conversation_usage_state")).Should().BeTrue();
        (await TableExistsAsync(connection, "conversation_usage_records")).Should().BeTrue();
        (await ReadScalarAsync(connection, "SELECT COUNT(*) FROM conversation_usage_state;")).Should().Be(3);
        (await ReadScalarAsync(connection, "SELECT COUNT(*) FROM conversation_usage_records;")).Should().Be(3);
        (
            await ReadScalarAsync(
                connection,
                """
                SELECT COUNT(*)
                FROM thread_metadata
                WHERE json_type(metadata_json, '$.properties."usage.aggregate"') IS NOT NULL
                   OR json_type(metadata_json, '$.properties."usage.records"') IS NOT NULL;
                """
            )
        ).Should().Be(0);
        var state = await ReadBackfillStateAsync(connection);
        state.Cursor.Should().Be("c-aggregate-only");
        state.CompletedAt.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task UsageMigration_FailureAfterUpserts_RollsBackBatch_ThenResumesWithoutDuplicates()
    {
        var uninterruptedPath = _databasePath + ".uninterrupted";
        try
        {
            var rows = Enumerable
                .Range(0, 201)
                .Select(index =>
                {
                    var threadId = $"thread-{index:D3}";
                    var record = UsageAttempt(threadId, $"attempt-{index:D3}", index + 1, index + 1);
                    return (
                        threadId,
                        UsageMetadata(ConversationUsageAggregate.Fold(threadId, [record], index + 1), [record])
                    );
                })
                .ToArray();
            await SeedVersionSixUsageDatabaseAsync(rows);
            File.Copy(_databasePath, uninterruptedPath);

            await using (var uninterrupted = new SqliteConversationStore(uninterruptedPath))
            {
                _ = await uninterrupted.ListThreadsAsync(limit: 1);
            }

            await using (var inject = await OpenAsync())
            {
                using var trigger = inject.CreateCommand();
                trigger.CommandText = """
                    CREATE TRIGGER fail_after_usage_upserts
                    BEFORE UPDATE ON thread_metadata
                    WHEN OLD.thread_id = 'thread-150'
                    BEGIN
                        SELECT RAISE(ABORT, 'injected after usage upserts');
                    END;
                    """;
                _ = await trigger.ExecuteNonQueryAsync();
            }

            await using (var interrupted = new SqliteConversationStore(_databasePath))
            {
                var migrate = async () => await interrupted.ListThreadsAsync(limit: 1);
                _ = (await migrate.Should().ThrowAsync<SqliteException>())
                    .Which.Message.Should()
                    .Contain("injected after usage upserts");
            }

            await using (var verifyRollback = await OpenAsync())
            {
                (await ReadScalarAsync(verifyRollback, "SELECT COUNT(*) FROM conversation_usage_state;"))
                    .Should()
                    .Be(100);
                (
                    await ReadScalarAsync(
                        verifyRollback,
                        "SELECT COUNT(*) FROM conversation_usage_state WHERE thread_id >= 'thread-100';"
                    )
                )
                    .Should()
                    .Be(0, "the entire second batch must roll back");
                var progress = await ReadBackfillProgressAsync(verifyRollback);
                progress.Cursor.Should().Be("thread-099");
                progress.CompletedAt.Should().BeNull();

                using var drop = verifyRollback.CreateCommand();
                drop.CommandText = "DROP TRIGGER fail_after_usage_upserts;";
                _ = await drop.ExecuteNonQueryAsync();
            }

            await using (var resumed = new SqliteConversationStore(_databasePath))
            {
                _ = await resumed.ListThreadsAsync(limit: 1);
            }

            (await ReadCanonicalUsageRowsAsync(_databasePath))
                .Should()
                .Equal(await ReadCanonicalUsageRowsAsync(uninterruptedPath));
            await using var final = await OpenAsync();
            (await ReadScalarAsync(final, "SELECT COUNT(*) FROM conversation_usage_state;")).Should().Be(201);
            (await ReadScalarAsync(final, "SELECT COUNT(*) FROM conversation_usage_records;")).Should().Be(201);
            (await ReadBackfillStateAsync(final)).Cursor.Should().Be("thread-200");
        }
        finally
        {
            SqliteTestPools.Clear(uninterruptedPath);
            TryDelete(uninterruptedPath);
            TryDelete(uninterruptedPath + "-wal");
            TryDelete(uninterruptedPath + "-shm");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UsageMigration_CorruptArtifact_NamesThread_AndCommitsNoneOfFailingBatch(bool corruptOuterMetadata)
    {
        var rows = Enumerable
            .Range(0, 100)
            .Select(index =>
            {
                var threadId = $"valid-{index:D3}";
                var record = UsageAttempt(threadId, $"attempt-{index:D3}", index + 1, index + 1);
                return (
                    threadId,
                    UsageMetadata(ConversationUsageAggregate.Fold(threadId, [record], index + 1), [record])
                );
            })
            .Append(
                (
                    "z-invalid-thread",
                    corruptOuterMetadata
                        ? "{ not valid metadata"
                        : """{"properties":{"usage.records":"{ not valid records"}}"""
                )
            )
            .Append(
                (
                    "zz-valid-after-invalid",
                    UsageMetadata(
                        ConversationUsageAggregate.Fold(
                            "zz-valid-after-invalid",
                            [UsageAttempt("zz-valid-after-invalid", "after", 101, 101)],
                            101
                        ),
                        [UsageAttempt("zz-valid-after-invalid", "after", 101, 101)]
                    )
                )
            )
            .ToArray();
        await SeedVersionSixUsageDatabaseAsync(rows);

        await using (var store = new SqliteConversationStore(_databasePath))
        {
            var migrate = async () => await store.ListThreadsAsync(limit: 1);
            _ = (await migrate.Should().ThrowAsync<InvalidDataException>())
                .Which.Message.Should()
                .Contain("z-invalid-thread");
        }

        await using var connection = await OpenAsync();
        (await ReadScalarAsync(connection, "SELECT COUNT(*) FROM conversation_usage_state;")).Should().Be(100);
        (
            await ReadScalarAsync(
                connection,
                "SELECT COUNT(*) FROM conversation_usage_state WHERE thread_id = 'zz-valid-after-invalid';"
            )
        )
            .Should()
            .Be(0);
        var state = await ReadBackfillProgressAsync(connection);
        state.Cursor.Should().Be("valid-099");
        state.CompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task InterruptedListingBackfill_ResumesAfterCursor_AndCompletedReopenDoesNotRewriteState()
    {
        await SeedVersionSixDatabaseAsync();
        await using (var prepare = await OpenAsync())
        {
            await SqliteSchemaInitializer.InitializeSchemaAsync(prepare);
            using var seedProgress = prepare.CreateCommand();
            seedProgress.CommandText = """
                UPDATE thread_metadata
                SET deleted_at = CASE thread_id WHEN 'b-deleted' THEN 123 END
                WHERE thread_id <= 'b-deleted';
                INSERT INTO schema_data_migrations (name, cursor, completed_at)
                VALUES ('thread-metadata-v7', 'b-deleted', NULL);
                CREATE TRIGGER reject_replayed_listing_rows
                BEFORE UPDATE OF deleted_at, forked_from_thread_id, subagent_parent_thread_id
                ON thread_metadata
                WHEN OLD.thread_id <= 'b-deleted'
                BEGIN
                    SELECT RAISE(ABORT, 'backfill replayed a committed row');
                END;
                """;
            _ = await seedProgress.ExecuteNonQueryAsync();
        }

        await using (var resumed = new SqliteConversationStore(_databasePath))
        {
            _ = await resumed.ListThreadsAsync(limit: 1);
        }

        (string Cursor, long CompletedAt) completedState;
        await using (var verify = await OpenAsync())
        {
            (await ReadListingProjectionsAsync(verify))
                .Should()
                .Equal(
                    ("a-null", null, null, null),
                    ("b-deleted", 123L, null, null),
                    ("c-fork", null, "source-thread", null),
                    ("d-parent", null, null, "root-thread")
                );
            completedState = await ReadBackfillStateAsync(verify);

            using var rejectRewrite = verify.CreateCommand();
            rejectRewrite.CommandText = """
                CREATE TRIGGER reject_completed_marker_rewrite
                BEFORE UPDATE ON schema_data_migrations
                BEGIN
                    SELECT RAISE(ABORT, 'completed marker was rewritten');
                END;
                """;
            _ = await rejectRewrite.ExecuteNonQueryAsync();
        }

        await using (var reopened = new SqliteConversationStore(_databasePath))
        {
            _ = await reopened.ListThreadsAsync(limit: 1);
        }

        await using var final = await OpenAsync();
        (await ReadBackfillStateAsync(final)).Should().Be(completedState);
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

    private async Task SeedVersionSixDatabaseAsync()
    {
        await using var connection = await OpenAsync();
        using var create = connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE thread_metadata (
                thread_id      TEXT PRIMARY KEY,
                current_run_id TEXT,
                last_updated   INTEGER NOT NULL,
                metadata_json  TEXT,
                tenant_id      TEXT,
                owner_user_id  TEXT,
                owner_app_id   TEXT,
                visibility     TEXT
            );
            INSERT INTO thread_metadata (thread_id, last_updated, metadata_json)
            VALUES
                ('a-null', 1, NULL),
                (
                    'b-deleted',
                    2,
                    '{"properties":{"fork.lineage":"{\"schema_version\":1,\"deleted_at\":123}"}}'
                ),
                (
                    'c-fork',
                    3,
                    '{"properties":{"fork.lineage":"{\"schema_version\":1,\"forked_from\":{\"thread_id\":\"source-thread\",\"message_id\":\"m1\",\"seq\":1}}"}}'
                ),
                (
                    'd-parent',
                    4,
                    '{"properties":{"sample.subAgentOf":"root-thread"}}'
                );
            PRAGMA user_version = 6;
            """;
        _ = await create.ExecuteNonQueryAsync();
    }

    private async Task SeedVersionSixUsageDatabaseAsync(IEnumerable<(string ThreadId, string MetadataJson)> rows)
    {
        await using var connection = await OpenAsync();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE thread_metadata (
                    thread_id      TEXT PRIMARY KEY,
                    current_run_id TEXT,
                    last_updated   INTEGER NOT NULL,
                    metadata_json  TEXT,
                    tenant_id      TEXT,
                    owner_user_id  TEXT,
                    owner_app_id   TEXT,
                    visibility     TEXT
                );
                PRAGMA user_version = 6;
                """;
            _ = await create.ExecuteNonQueryAsync();
        }

        foreach (var (threadId, metadataJson) in rows)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO thread_metadata (thread_id, last_updated, metadata_json)
                VALUES ($thread_id, 1, $metadata_json);
                """;
            _ = insert.Parameters.AddWithValue("$thread_id", threadId);
            _ = insert.Parameters.AddWithValue("$metadata_json", metadataJson);
            _ = await insert.ExecuteNonQueryAsync();
        }
    }

    private static string UsageMetadata(ConversationUsageAggregate? aggregate, IReadOnlyList<UsageRecord>? records)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        if (aggregate is not null)
        {
            properties[ConversationUsageProjection.PropertyKey] = JsonSerializer.Serialize(aggregate);
        }

        if (records is not null)
        {
            properties[ConversationUsageProjection.RecordsPropertyKey] = JsonSerializer.Serialize(records);
        }

        return JsonSerializer.Serialize(new { properties });
    }

    private static UsageRecord UsageAttempt(string threadId, string attemptId, long revision, int inputTokens) =>
        new()
        {
            LogicalCallId = attemptId,
            ProviderAttemptId = attemptId,
            RootConversationId = threadId,
            RequestedModel = "model",
            InputTokens = inputTokens,
            OutputTokens = 1,
            Revision = revision,
            OccurredAtUtc = new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero).AddMinutes(revision),
        };

    private static async Task<long> ReadScalarAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<string>> ReadCanonicalUsageRowsAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.thread_id, s.schema_version, s.folded_revision, s.completeness,
                   r.provider_attempt_id, r.revision, r.occurred_at_utc_ticks, r.record_json
            FROM conversation_usage_state AS s
            LEFT JOIN conversation_usage_records AS r ON r.thread_id = s.thread_id
            ORDER BY s.thread_id, r.provider_attempt_id;
            """;
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(
                string.Join(
                    "|",
                    Enumerable
                        .Range(0, reader.FieldCount)
                        .Select(index => reader.IsDBNull(index) ? "<null>" : reader.GetValue(index).ToString())
                )
            );
        }

        return rows;
    }

    private static async Task<IReadOnlyList<string>> ReadNamesAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<
        IReadOnlyList<(string ThreadId, long? DeletedAt, string? ForkedFrom, string? Parent)>
    > ReadListingProjectionsAsync(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT thread_id, deleted_at, forked_from_thread_id, subagent_parent_thread_id
            FROM thread_metadata
            ORDER BY thread_id;
            """;
        var rows = new List<(string, long?, string?, string?)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(
                (
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)
                )
            );
        }

        return rows;
    }

    private static async Task<(string Cursor, long CompletedAt)> ReadBackfillStateAsync(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT cursor, completed_at
            FROM schema_data_migrations
            WHERE name = 'thread-metadata-v7';
            """;
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return (reader.GetString(0), reader.GetInt64(1));
    }

    private static async Task<(string? Cursor, long? CompletedAt)> ReadBackfillProgressAsync(
        SqliteConnection connection
    )
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT cursor, completed_at
            FROM schema_data_migrations
            WHERE name = 'thread-metadata-v7';
            """;
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt64(1));
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
