namespace CodeReviewDaemon.Sample.Persistence.Migrations;

/// <summary>
/// The ordered, append-only list of schema migrations. New schema changes are added as a new
/// <see cref="Migration"/> with the next version number — existing migrations are never edited
/// (that would break already-migrated databases). Destructive changes must follow expand → migrate →
/// contract across multiple versioned migrations rather than dropping/rewriting in place.
/// </summary>
internal static class SchemaMigrations
{
    /// <summary>Highest version any migration brings the database to.</summary>
    public static long LatestVersion => All[^1].Version;

    /// <summary>All migrations, ascending by <see cref="Migration.Version"/>.</summary>
    public static readonly IReadOnlyList<Migration> All =
    [
        new Migration(1, V1Sql),
    ];

    // ── v1: initial orchestration schema ─────────────────────────────────────────────────────────
    // repo (§7) → review_run (§6) → review_outbox (§11) / review_artifact (§14); poll_cursor (§12).
    // External ids are TEXT; axes/status are TEXT (readable + forward-tolerant). FKs are declared so
    // PRAGMA foreign_keys = ON enforces the graph.
    private const string V1Sql = """
        CREATE TABLE repo (
            id             INTEGER PRIMARY KEY,
            provider       TEXT NOT NULL,
            normalized_key TEXT NOT NULL,
            display_name   TEXT NOT NULL,
            org_or_owner   TEXT NOT NULL,
            project        TEXT NULL,
            repo_name      TEXT NOT NULL,
            repo_stable_id TEXT NULL,
            created_at     TEXT NOT NULL,
            UNIQUE (normalized_key)
        );

        CREATE TABLE review_run (
            id                   INTEGER PRIMARY KEY,
            repo_id              INTEGER NOT NULL REFERENCES repo (id),
            pr_id                TEXT NOT NULL,
            head_sha             TEXT NOT NULL,
            base_sha             TEXT NOT NULL,
            trigger_watermark    TEXT NOT NULL,
            review_kind          TEXT NOT NULL,
            variant_id           TEXT NOT NULL,
            mode                 TEXT NOT NULL,
            merge_sha            TEXT NULL,
            model_provider       TEXT NULL,
            model_id             TEXT NULL,
            prompt_template_hash TEXT NULL,
            policy_bundle_version TEXT NULL,
            feature_flag_snapshot TEXT NULL,
            stage                TEXT NOT NULL,
            workflow_status      TEXT NOT NULL,
            pr_lifecycle_state   TEXT NOT NULL,
            created_at           TEXT NOT NULL,
            updated_at           TEXT NOT NULL,
            UNIQUE (
                repo_id, pr_id, head_sha, base_sha, trigger_watermark,
                review_kind, variant_id, mode
            )
        );

        CREATE TABLE poll_cursor (
            provider        TEXT NOT NULL,
            scope           TEXT NOT NULL,
            cursor_version  INTEGER NOT NULL,
            cursor_payload  TEXT NOT NULL,
            high_water_mark TEXT NULL,
            etag            TEXT NULL,
            continuation    TEXT NULL,
            since_timestamp TEXT NULL,
            updated_at      TEXT NOT NULL,
            PRIMARY KEY (provider, scope)
        );

        CREATE TABLE review_outbox (
            id                   INTEGER PRIMARY KEY,
            idempotency_key      TEXT NOT NULL,
            provider             TEXT NOT NULL,
            review_run_id        INTEGER NOT NULL REFERENCES review_run (id),
            operation            TEXT NOT NULL,
            artifact_kind        TEXT NOT NULL,
            status               TEXT NOT NULL,
            body_hash            TEXT NULL,
            provider_response_id TEXT NULL,
            created_at           TEXT NOT NULL,
            updated_at           TEXT NOT NULL,
            UNIQUE (idempotency_key)
        );

        CREATE TABLE review_artifact (
            id                     INTEGER PRIMARY KEY,
            review_run_id          INTEGER NOT NULL REFERENCES review_run (id),
            artifact_schema_version INTEGER NOT NULL,
            artifact_kind          TEXT NOT NULL,
            provider               TEXT NOT NULL,
            payload                TEXT NOT NULL,
            created_at             TEXT NOT NULL
        );
        """;
}
