using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Migrations;
using CodeReviewDaemon.Sample.Persistence.Models;
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
        ReadScalar(connection, "PRAGMA busy_timeout;")
            .Should()
            .Be(SqliteConnectionFactory.BusyTimeoutMilliseconds.ToString());
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
        ColumnExists(connection, "review_run", "is_target_repo_public")
            .Should()
            .BeTrue("migration v2 adds is_target_repo_public");
    }

    [Fact]
    public void Migration_v3_adds_the_deep_link_retention_ledger()
    {
        using var db = new TempSqliteDatabase();
        using var connection = SqliteConnectionFactory.Open(db.ConnectionString);

        MigrationRunner.Migrate(connection);

        TableExists(connection, "deep_link_conversation").Should().BeTrue("migration v3 creates the ledger");
        ColumnExists(connection, "deep_link_conversation", "minted_at")
            .Should()
            .BeTrue("age since minting is the retention sweep's only input");
        // Deliberately parentless. A foreign key to review_run would let a cascade take the conversation
        // down with its run — and the entire point of the ledger is that the deep-link outlives the review.
        ReadScalar(connection, "SELECT COUNT(*) FROM pragma_foreign_key_list('deep_link_conversation');")
            .Should()
            .Be("0");
    }

    [Fact]
    public void Migration_v8_adds_the_durable_park_columns_and_leaves_pre_existing_rows_unparked()
    {
        // The columns are the whole point of the fix: the retry budget has to survive both a restart and
        // StrandedRunReconciler's Reset, and a park has to be a fact in the row rather than a fact in a
        // dictionary. Applied to a database that already holds runs, so the defaults are asserted on a row
        // written BEFORE the migration existed — the shape every deployed daemon upgrades from. Those rows
        // must read as "never failed, never parked", because they are: no budget was ever charged to them.
        using var db = new TempSqliteDatabase();
        using var connection = SqliteConnectionFactory.Open(db.ConnectionString);
        MigrationRunner.Migrate(connection, [.. SchemaMigrations.All.Where(m => m.Version < 8)]);
        Execute(
            connection,
            """
            INSERT INTO repo (provider, normalized_key, display_name, org_or_owner, repo_name, created_at)
            VALUES ('github', 'k', 'd', 'achieveai', 'LmDotnetTools', '2026-08-30T00:00:00.0000000+00:00');
            INSERT INTO review_run (
                repo_id, pr_id, head_sha, base_sha, trigger_watermark, review_kind, variant_id, mode,
                stage, workflow_status, pr_lifecycle_state, created_at, updated_at)
            VALUES (1, '1', 'h', 'b', 'wm', 'full', 'primary', 'post',
                    'Discovered', 'RetryPending', 'Open',
                    '2026-08-30T00:00:00.0000000+00:00', '2026-08-30T00:00:00.0000000+00:00');
            """
        );

        MigrationRunner.Migrate(connection);

        ColumnExists(connection, "review_run", "governed_failure_count")
            .Should()
            .BeTrue("migration v8 adds the durable retry budget");
        ColumnExists(connection, "review_run", "parked_at").Should().BeTrue("migration v8 adds the park instant");
        ColumnExists(connection, "review_run", "park_reason").Should().BeTrue("migration v8 adds the park reason");
        ReadScalar(connection, "SELECT governed_failure_count FROM review_run WHERE id = 1;")
            .Should()
            .Be("0", "a row that predates the budget has spent none of it");
        ReadScalar(connection, "SELECT COUNT(*) FROM review_run WHERE id = 1 AND parked_at IS NULL;")
            .Should()
            .Be("1", "an upgrade must not park work that was merely in flight when it happened");
    }

    [Fact]
    public void Migration_v9_adds_run_owned_workspace_release_fields_without_changing_legacy_rows()
    {
        using var db = new TempSqliteDatabase();
        using var connection = SqliteConnectionFactory.Open(db.ConnectionString);
        MigrationRunner.Migrate(connection, [.. SchemaMigrations.All.Where(m => m.Version < 9)]);
        Execute(
            connection,
            """
            INSERT INTO deep_link_conversation (thread_id, title, minted_at)
            VALUES ('thread-legacy', 'Legacy review', '2026-09-01T00:00:00.0000000+00:00');
            """
        );

        MigrationRunner.Migrate(connection);

        ColumnExists(connection, "deep_link_conversation", "review_run_id")
            .Should()
            .BeTrue("migration v9 durably attributes hosted conversations to their review run");
        ColumnExists(connection, "deep_link_conversation", "released_at")
            .Should()
            .BeTrue("migration v9 records only positively confirmed workspace release");
        ReadScalar(
                connection,
                "SELECT COUNT(*) FROM deep_link_conversation WHERE thread_id = 'thread-legacy' "
                    + "AND review_run_id IS NULL AND released_at IS NULL;"
            )
            .Should()
            .Be("1", "legacy deep links remain readable without fabricated ownership or release evidence");
        ReadScalar(connection, "SELECT COUNT(*) FROM pragma_foreign_key_list('deep_link_conversation');")
            .Should()
            .Be("0", "deep-link rows must continue to outlive their review runs");
    }

    [Fact]
    public void Migration_v10_adds_append_only_slot_claims_and_exact_provision_intents()
    {
        using var db = new TempSqliteDatabase();
        using var connection = SqliteConnectionFactory.Open(db.ConnectionString);
        MigrationRunner.Migrate(connection, [.. SchemaMigrations.All.Where(m => m.Version < 10)]);

        MigrationRunner.Migrate(connection);

        TableExists(connection, "review_slot_claim")
            .Should()
            .BeTrue("the slot address must be durable before any hosted provision can begin");
        TableExists(connection, "review_provision_intent")
            .Should()
            .BeTrue("each provision attempt needs its own append-only completeness record");
        ColumnExists(connection, "review_slot_claim", "resolved_at").Should().BeTrue();
        ColumnExists(connection, "review_provision_intent", "thread_id").Should().BeTrue();
    }

    [Fact]
    public void Migration_v11_records_conversation_disablement_separately_from_backend_release()
    {
        using var db = new TempSqliteDatabase();
        using var connection = SqliteConnectionFactory.Open(db.ConnectionString);
        MigrationRunner.Migrate(connection, [.. SchemaMigrations.All.Where(m => m.Version < 11)]);
        Execute(
            connection,
            """
            INSERT INTO deep_link_conversation (
                thread_id, title, minted_at, released_at)
            VALUES (
                'thread-disabled', 'Disabled review',
                '2026-09-02T00:00:00.0000000+00:00',
                '2026-09-02T01:00:00.0000000+00:00');
            """
        );

        MigrationRunner.Migrate(connection);

        ColumnExists(connection, "deep_link_conversation", "conversation_released_at")
            .Should()
            .BeTrue("conversation disablement is not proof that the backend mount is quiescent");
        ReadScalar(connection, "SELECT released_at FROM deep_link_conversation WHERE thread_id = 'thread-disabled';")
            .Should()
            .Be("2026-09-02T01:00:00.0000000+00:00", "the existing backend-release fact survives unchanged");
        ReadScalar(
                connection,
                "SELECT COUNT(*) FROM deep_link_conversation WHERE thread_id = 'thread-disabled' "
                    + "AND conversation_released_at IS NULL;"
            )
            .Should()
            .Be("1", "migration cannot invent a conversation-release fact for an existing row");
        ReadScalar(connection, "SELECT COUNT(*) FROM pragma_foreign_key_list('deep_link_conversation');")
            .Should()
            .Be("0", "released deep links remain readable after their review run is gone");
    }

    [Fact]
    public void Migration_v12_adds_intent_retraction_without_inventing_it_for_existing_attempts()
    {
        using var db = new TempSqliteDatabase();
        using var connection = SqliteConnectionFactory.Open(db.ConnectionString);
        MigrationRunner.Migrate(connection, [.. SchemaMigrations.All.Where(m => m.Version < 12)]);
        Execute(
            connection,
            """
            INSERT INTO repo (provider, normalized_key, display_name, org_or_owner, repo_name, created_at)
            VALUES ('github', 'k', 'd', 'achieveai', 'LmDotnetTools', '2026-09-03T00:00:00.0000000+00:00');
            INSERT INTO review_run (
                repo_id, pr_id, head_sha, base_sha, trigger_watermark, review_kind, variant_id, mode,
                stage, workflow_status, pr_lifecycle_state, created_at, updated_at)
            VALUES (1, '1', 'h', 'b', 'wm', 'full', 'primary', 'post',
                    'Reviewed', 'Running', 'Open',
                    '2026-09-03T00:00:00.0000000+00:00', '2026-09-03T00:00:00.0000000+00:00');
            INSERT INTO review_slot_claim (review_run_id, slot_host_path, claimed_at)
            VALUES (1, 'B:\\review-pool\\review-slot-0', '2026-09-03T00:01:00.0000000+00:00');
            INSERT INTO review_provision_intent (slot_claim_id, review_run_id, provisioning_began_at)
            VALUES (1, 1, '2026-09-03T00:02:00.0000000+00:00');
            """
        );

        MigrationRunner.Migrate(connection);

        ColumnExists(connection, "review_provision_intent", "retracted_at")
            .Should()
            .BeTrue("a definitive pre-mint refusal must be recorded without deleting attempt history");
        ReadScalar(connection, "SELECT COUNT(*) FROM review_provision_intent WHERE id = 1 AND retracted_at IS NULL;")
            .Should()
            .Be("1", "migration cannot fabricate a refusal for an existing ambiguous attempt");
    }

    [Fact]
    public void Migration_v13_adds_engagement_rounds_and_preserves_v12_runs()
    {
        using var db = new TempSqliteDatabase();
        using var connection = SqliteConnectionFactory.Open(db.ConnectionString);
        MigrationRunner.Migrate(connection, [.. SchemaMigrations.All.Where(m => m.Version < 13)]);
        Execute(
            connection,
            """
            INSERT INTO repo (provider, normalized_key, display_name, org_or_owner, repo_name, created_at)
            VALUES ('github', 'k', 'd', 'achieveai', 'LmDotnetTools', '2026-09-02T00:00:00.0000000+00:00');
            INSERT INTO review_run (
                repo_id, pr_id, head_sha, base_sha, trigger_watermark, review_kind, variant_id, mode,
                stage, workflow_status, pr_lifecycle_state, created_at, updated_at)
            VALUES (1, '118', 'head-1', 'base-1', 'wm', 'full', 'primary', 'collect-only',
                    'Discovered', 'Pending', 'Open',
                    '2026-09-02T00:00:00.0000000+00:00', '2026-09-02T00:00:00.0000000+00:00');
            """
        );

        MigrationRunner.Migrate(connection);

        TableExists(connection, "pr_engagement").Should().BeTrue();
        TableExists(connection, "engagement_round").Should().BeTrue();
        ColumnExists(connection, "review_run", "engagement_round_id").Should().BeTrue();
        ReadScalar(connection, "SELECT head_sha FROM review_run WHERE id = 1;").Should().Be("head-1");
        ReadScalar(connection, "SELECT COUNT(*) FROM review_run WHERE id = 1 AND engagement_round_id IS NULL;")
            .Should()
            .Be("1", "pre-v13 runs have no fabricated round identity");
        ReadScalar(
                connection,
                "SELECT COUNT(*) FROM pragma_foreign_key_list('engagement_round') WHERE [table] = 'pr_engagement';"
            )
            .Should()
            .Be("1");
        ReadScalar(
                connection,
                "SELECT COUNT(*) FROM pragma_foreign_key_list('review_run') WHERE [table] = 'engagement_round';"
            )
            .Should()
            .Be("1");
        ReadScalar(
                connection,
                "SELECT COUNT(*) FROM pragma_foreign_key_list('engagement_round') WHERE [table] = 'review_run';"
            )
            .Should()
            .Be("1");
    }

    [Fact]
    public void Migration_v13_enforces_engagement_identity_intent_status_and_one_active_round()
    {
        using var db = new TempSqliteDatabase();
        using var connection = SqliteConnectionFactory.Open(db.ConnectionString);
        MigrationRunner.Migrate(connection);
        Execute(
            connection,
            """
            INSERT INTO repo (provider, normalized_key, display_name, org_or_owner, repo_name, created_at)
            VALUES ('github', 'k', 'd', 'achieveai', 'LmDotnetTools', '2026-09-02T00:00:00.0000000+00:00');
            INSERT INTO pr_engagement (
                repo_id, provider, pr_id, lifecycle, latest_head_sha, latest_base_sha,
                latest_activity_json, created_at, updated_at)
            VALUES (1, 'github', '118', 'Open', 'head-1', 'base-1',
                    '{"provider":"github","publishedAt":"2026-09-02T00:00:00.0000000+00:00","stableObjectId":"1"}',
                    '2026-09-02T00:00:00.0000000+00:00', '2026-09-02T00:00:00.0000000+00:00');
            """
        );

        var duplicateEngagement = () =>
            Execute(
                connection,
                """
                INSERT INTO pr_engagement (
                    repo_id, provider, pr_id, lifecycle, latest_head_sha, latest_base_sha,
                    latest_activity_json, created_at, updated_at)
                SELECT repo_id, provider, pr_id, lifecycle, latest_head_sha, latest_base_sha,
                       latest_activity_json, created_at, updated_at
                FROM pr_engagement WHERE id = 1;
                """
            );
        var invalidIntent = () =>
            Execute(
                connection,
                """
                INSERT INTO engagement_round (
                    pr_engagement_id, intent, status, head_sha, base_sha, prior_observation_boundary,
                    governed_failure_count, created_at, updated_at)
                VALUES (1, 'Unknown', 'Pending', 'head-1', 'base-1', 0, 0,
                        '2026-09-02T00:00:00.0000000+00:00', '2026-09-02T00:00:00.0000000+00:00');
                """
            );
        var invalidStatus = () =>
            Execute(
                connection,
                """
                INSERT INTO engagement_round (
                    pr_engagement_id, intent, status, head_sha, base_sha, prior_observation_boundary,
                    governed_failure_count, created_at, updated_at)
                VALUES (1, 'CodeReview', 'Unknown', 'head-1', 'base-1', 0, 0,
                        '2026-09-02T00:00:00.0000000+00:00', '2026-09-02T00:00:00.0000000+00:00');
                """
            );

        duplicateEngagement.Should().Throw<SqliteException>();
        invalidIntent.Should().Throw<SqliteException>();
        invalidStatus.Should().Throw<SqliteException>();

        Execute(
            connection,
            """
            INSERT INTO engagement_round (
                pr_engagement_id, intent, status, head_sha, base_sha, prior_observation_boundary,
                governed_failure_count, created_at, updated_at)
            VALUES (1, 'CodeReview', 'Pending', 'head-1', 'base-1', 0, 0,
                    '2026-09-02T00:00:00.0000000+00:00', '2026-09-02T00:00:00.0000000+00:00');
            """
        );
        var competingActiveRound = () =>
            Execute(
                connection,
                """
                INSERT INTO engagement_round (
                    pr_engagement_id, intent, status, head_sha, base_sha, prior_observation_boundary,
                    governed_failure_count, created_at, updated_at)
                VALUES (1, 'DiscussionFollowUp', 'Running', 'head-1', 'base-1', 0, 0,
                        '2026-09-02T00:01:00.0000000+00:00', '2026-09-02T00:01:00.0000000+00:00');
                """
            );

        competingActiveRound.Should().Throw<SqliteException>();
    }

    [Fact]
    public void Migration_v13_active_round_index_includes_retry_pending()
    {
        using var db = new TempSqliteDatabase();
        using var connection = SqliteConnectionFactory.Open(db.ConnectionString);
        MigrationRunner.Migrate(connection);
        Execute(
            connection,
            """
            INSERT INTO repo (provider, normalized_key, display_name, org_or_owner, repo_name, created_at)
            VALUES ('github', 'k', 'd', 'achieveai', 'LmDotnetTools', '2026-09-02T00:00:00.0000000+00:00');
            INSERT INTO pr_engagement (
                repo_id, provider, pr_id, lifecycle, latest_head_sha, latest_base_sha,
                latest_activity_json, created_at, updated_at)
            VALUES (1, 'github', '118', 'Open', 'head-1', 'base-1',
                    '{"provider":"github","publishedAt":"2026-09-02T00:00:00.0000000+00:00","stableObjectId":"1"}',
                    '2026-09-02T00:00:00.0000000+00:00', '2026-09-02T00:00:00.0000000+00:00');
            INSERT INTO engagement_round (
                pr_engagement_id, intent, status, head_sha, base_sha, prior_observation_boundary,
                governed_failure_count, created_at, updated_at)
            VALUES (1, 'CodeReview', 'RetryPending', 'head-1', 'base-1', 0, 1,
                    '2026-09-02T00:00:00.0000000+00:00', '2026-09-02T00:00:00.0000000+00:00');
            """
        );

        var competingPending = () =>
            Execute(
                connection,
                """
                INSERT INTO engagement_round (
                    pr_engagement_id, intent, status, head_sha, base_sha, prior_observation_boundary,
                    governed_failure_count, created_at, updated_at)
                VALUES (1, 'DiscussionFollowUp', 'Pending', 'head-1', 'base-1', 0, 0,
                        '2026-09-02T00:01:00.0000000+00:00', '2026-09-02T00:01:00.0000000+00:00');
                """
            );

        competingPending.Should().Throw<SqliteException>("RetryPending still owns the one-active-round lease");
    }

    [Fact]
    public void Migration_v13_canonical_watermark_json_is_readable_by_the_store()
    {
        using var db = new TempSqliteDatabase();
        using (var connection = SqliteConnectionFactory.Open(db.ConnectionString))
        {
            MigrationRunner.Migrate(connection);
            Execute(
                connection,
                """
                INSERT INTO repo (provider, normalized_key, display_name, org_or_owner, repo_name, created_at)
                VALUES ('github', 'k', 'd', 'achieveai', 'LmDotnetTools', '2026-09-02T00:00:00.0000000+00:00');
                INSERT INTO pr_engagement (
                    repo_id, provider, pr_id, lifecycle, latest_head_sha, latest_base_sha,
                    latest_activity_json, created_at, updated_at)
                VALUES (1, 'github', '118', 'Open', 'head-1', 'base-1',
                        '{"provider":"github","publishedAt":"2026-09-02T08:00:00.0000000+00:00","stableObjectId":"comment-1"}',
                        '2026-09-02T08:00:00.0000000+00:00', '2026-09-02T08:00:00.0000000+00:00');
                """
            );
        }

        using var store = new ReviewStore(db.ConnectionString);

        store
            .GetEngagement(1)!
            .LatestActivity.Should()
            .Be(
                new ProviderActivityWatermark(
                    "github",
                    new DateTimeOffset(2026, 9, 2, 8, 0, 0, TimeSpan.Zero),
                    "comment-1"
                )
            );
    }

    [Fact]
    public void Migration_v14_adds_chunked_audit_storage_and_preserves_v13_rows()
    {
        using var db = new TempSqliteDatabase();
        using var connection = SqliteConnectionFactory.Open(db.ConnectionString);
        MigrationRunner.Migrate(connection, [.. SchemaMigrations.All.Where(m => m.Version < 14)]);
        Execute(
            connection,
            """
            INSERT INTO repo (provider, normalized_key, display_name, org_or_owner, repo_name, created_at)
            VALUES ('github', 'k', 'd', 'achieveai', 'LmDotnetTools', '2026-09-02T00:00:00.0000000+00:00');
            INSERT INTO pr_engagement (
                repo_id, provider, pr_id, lifecycle, latest_head_sha, latest_base_sha,
                latest_activity_json, created_at, updated_at)
            VALUES (1, 'github', '118', 'Open', 'head-1', 'base-1',
                    '{"provider":"github","publishedAt":"2026-09-02T00:00:00.0000000+00:00","stableObjectId":"1"}',
                    '2026-09-02T00:00:00.0000000+00:00', '2026-09-02T00:00:00.0000000+00:00');
            INSERT INTO engagement_round (
                pr_engagement_id, intent, status, head_sha, base_sha, prior_observation_boundary,
                governed_failure_count, created_at, updated_at)
            VALUES (1, 'CodeReview', 'Completed', 'head-1', 'base-1', 0, 0,
                    '2026-09-02T00:00:00.0000000+00:00', '2026-09-02T00:00:00.0000000+00:00');
            """
        );

        MigrationRunner.Migrate(connection);

        foreach (
            var table in new[]
            {
                "audit_blob",
                "audit_source_record",
                "audit_source_chunk",
                "audit_redaction_record",
                "audit_redaction_chunk",
            }
        )
        {
            TableExists(connection, table).Should().BeTrue($"migration v14 creates the '{table}' table");
        }

        ReadScalar(connection, "SELECT head_sha FROM engagement_round WHERE id = 1;").Should().Be("head-1");
        ColumnExists(connection, "audit_source_record", "source_content_sha256").Should().BeTrue();
        ColumnExists(connection, "audit_source_record", "source_byte_count").Should().BeTrue();
    }

    [Fact]
    public void Migration_v15_adds_typed_engagement_evidence_with_exact_source_links_and_immutable_observations()
    {
        using var db = new TempSqliteDatabase();
        using var connection = SqliteConnectionFactory.Open(db.ConnectionString);
        MigrationRunner.Migrate(connection, [.. SchemaMigrations.All.Where(m => m.Version < 15)]);

        MigrationRunner.Migrate(connection);

        foreach (
            var table in new[]
            {
                "clarification_question",
                "clarification_question_source",
                "clarification_candidate_answer",
                "clarification_candidate_answer_source",
                "review_action",
                "review_action_source",
                "round_observation",
                "round_observation_source",
            }
        )
        {
            TableExists(connection, table).Should().BeTrue($"migration v15 creates the '{table}' table");
        }

        ReadScalar(
                connection,
                "SELECT COUNT(*) FROM pragma_index_list('audit_source_record') "
                    + "WHERE name = 'ux_audit_source_record_id_hash' AND [unique] = 1;"
            )
            .Should()
            .Be("1", "source links require the exact unique (id, content_sha256) parent index");
        ReadScalar(
                connection,
                "SELECT COUNT(*) FROM pragma_foreign_key_list('round_observation_source') WHERE [table] = 'audit_source_record';"
            )
            .Should()
            .Be("2", "the source ID and exact hash form one composite foreign key");
        TriggerExists(connection, "round_observation_no_update").Should().BeTrue();
        TriggerExists(connection, "round_observation_no_delete").Should().BeTrue();
        TriggerExists(connection, "round_observation_source_no_update").Should().BeTrue();
        TriggerExists(connection, "round_observation_source_no_delete").Should().BeTrue();
    }

    [Fact]
    public void Migration_v16_adds_verified_close_outcomes_and_idempotent_promotion_results()
    {
        using var db = new TempSqliteDatabase();
        using var connection = SqliteConnectionFactory.Open(db.ConnectionString);
        MigrationRunner.Migrate(connection, [.. SchemaMigrations.All.Where(m => m.Version < 16)]);

        MigrationRunner.Migrate(connection);

        TableExists(connection, "close_outcome_item").Should().BeTrue();
        TableExists(connection, "close_outcome_item_source").Should().BeTrue();
        TableExists(connection, "promotion_outcome").Should().BeTrue();
        ReadScalar(
                connection,
                "SELECT COUNT(*) FROM pragma_foreign_key_list('close_outcome_item_source') "
                    + "WHERE [table] = 'audit_source_record';"
            )
            .Should()
            .Be("2", "a close outcome source must retain the exact audit record ID and content hash");
        ReadScalar(
                connection,
                "SELECT COUNT(*) FROM pragma_table_info('promotion_outcome') "
                    + "WHERE name IN ('engagement_round_id', 'source_observation_id', 'destination_kind') "
                    + "AND pk > 0;"
            )
            .Should()
            .Be("3", "promotion replay identity includes the round, source observation, and destination");
        ReadUserVersion(connection).Should().Be(16);
    }

    [Fact]
    public void A_failing_v15_migration_rolls_back_all_objects_and_keeps_version_fourteen()
    {
        using var db = new TempSqliteDatabase();
        using var connection = SqliteConnectionFactory.Open(db.ConnectionString);
        MigrationRunner.Migrate(connection, [.. SchemaMigrations.All.Where(m => m.Version < 15)]);
        var migrations = SchemaMigrations
            .All.Where(m => m.Version < 15)
            .Append(new Migration(15, "CREATE TABLE v15_partial (id INTEGER); THIS_IS_NOT_VALID_SQL;"))
            .ToArray();

        var act = () => MigrationRunner.Migrate(connection, migrations);

        act.Should().Throw<SqliteException>();
        ReadUserVersion(connection).Should().Be(14);
        TableExists(connection, "v15_partial").Should().BeFalse();
    }

    [Fact]
    public void A_failing_v14_migration_rolls_back_all_objects_and_keeps_version_thirteen()
    {
        using var db = new TempSqliteDatabase();
        using var connection = SqliteConnectionFactory.Open(db.ConnectionString);
        MigrationRunner.Migrate(connection, [.. SchemaMigrations.All.Where(m => m.Version < 14)]);
        var migrations = SchemaMigrations
            .All.Where(m => m.Version < 14)
            .Append(new Migration(14, "CREATE TABLE v14_partial (id INTEGER); THIS_IS_NOT_VALID_SQL;"))
            .ToArray();

        var act = () => MigrationRunner.Migrate(connection, migrations);

        act.Should().Throw<SqliteException>();
        ReadUserVersion(connection).Should().Be(13);
        TableExists(connection, "v14_partial").Should().BeFalse();
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

        act.Should().Throw<InvalidOperationException>().WithMessage("*newer than this build*");
    }

    [Fact]
    public void A_failing_migration_rolls_back_atomically_and_leaves_user_version_unchanged()
    {
        // PR #121 M8 — a migration whose SQL fails partway must roll the WHOLE step back (even the
        // statements that ran before the failure) and must NOT advance user_version, so a retry re-applies
        // it cleanly. Driven with a crafted set via the internal overload.
        using var db = new TempSqliteDatabase();
        using var connection = SqliteConnectionFactory.Open(db.ConnectionString);
        var migrations = new List<Migration> { new(1, "CREATE TABLE good (id INTEGER); THIS_IS_NOT_VALID_SQL;") };

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

        var tasks = Enumerable
            .Range(0, 3)
            .Select(_ =>
                Task.Run(() =>
                {
                    using var connection = SqliteConnectionFactory.Open(db.ConnectionString);
                    MigrationRunner.Migrate(connection);
                })
            );

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

    private static bool TriggerExists(SqliteConnection connection, string trigger)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'trigger' AND name = $name;";
        _ = command.Parameters.AddWithValue("$name", trigger);
        return command.ExecuteScalar() is not null;
    }
}
