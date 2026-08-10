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
        new Migration(2, V2Sql),
        new Migration(3, V3Sql),
        new Migration(4, V4Sql),
        new Migration(5, V5Sql),
        new Migration(6, V6Sql),
        new Migration(7, V7Sql),
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

    // ── v2: persist the confidentiality trust signals (Task 17, design §6 Risk B) ──────────────────
    // review_run.IsForkPr / IsTargetRepoPublic feed the cross-repo co-location gate. Both default 1
    // (true = fail closed): a pre-existing row (and any row nothing has positively marked as same-org /
    // private-target) reloads as untrusted, matching the model's in-memory defaults so a resumed run
    // never co-locates the sibling private submodule on the strength of a lost signal. Booleans are
    // stored as INTEGER 0/1 (SQLite has no native bool).
    private const string V2Sql = """
        ALTER TABLE review_run ADD COLUMN is_fork_pr            INTEGER NOT NULL DEFAULT 1;
        ALTER TABLE review_run ADD COLUMN is_target_repo_public INTEGER NOT NULL DEFAULT 1;
        """;

    // ── v3: the deep-link retention ledger ────────────────────────────────────────────────────────
    // Every hosted S2S conversation the daemon mints (the review itself plus its judge / A-B arms) is
    // recorded here at the moment it is minted, because only the PRIMARY review's thread id is ever
    // persisted onto an artifact — an artifact-keyed policy would leave the other arms alive forever.
    // The row IS the retention claim: it exists while the conversation should stay reachable behind the
    // posted comment's ?threadId= deep-link, and is deleted once the conversation has been discarded.
    // Deliberately NOT a child of review_run: the ledger outlives the review by design (a deep-link that
    // died with its run would defeat the whole point of the S2S path), and a mint has no run id in hand.
    // minted_at is a fixed-width UTC round-trip ("O") string, so lexicographic comparison is chronological.
    private const string V3Sql = """
        CREATE TABLE deep_link_conversation (
            thread_id  TEXT PRIMARY KEY,
            title      TEXT NULL,
            minted_at  TEXT NOT NULL
        );

        CREATE INDEX ix_deep_link_conversation_minted_at ON deep_link_conversation (minted_at);
        """;

    // ── v4: the PR author, for the per-developer review-feedback record ───────────────────────────
    // Who OPENED the PR (GitHub user.login / ADO createdBy.uniqueName). The at-close feedback
    // extraction runs long after the poll that observed the PR, and re-resolving the author then would
    // mean an extra provider call against a PR that may already be closed — so it is captured on the
    // run row when it is first seen.
    //
    // NULL-able with no default, deliberately: rows written before this migration genuinely have no
    // known author, and NULL is the value every consumer already treats as "no feedback record is
    // addressable". A default of '' would look like a real identity and could produce a record filed
    // under an empty name; a fabricated 'unknown' would collapse every distinct pre-migration author
    // into one shared public file. Neither is recoverable, so absence stays absence.
    private const string V4Sql = """
        ALTER TABLE review_run ADD COLUMN pr_author TEXT NULL;
        """;

    // ── v5: what the PR says it does — title, description, target branch ──────────────────────────
    // The review brief used to name only the repo, the PR number and the two shas, so the reviewer saw
    // WHAT changed but never what the change CLAIMED to do. That gap is not academic: a revert PR whose
    // 13 files were all binaries read, to the reviewer, as an unexplained blob rewrite — the fact that it
    // was a revert appeared nowhere except inside a quoted third-party bot comment. "Does the diff do
    // what the PR says?" is the first question a reviewer asks and the daemon could not ask it.
    //
    // Captured on the run row at poll time for the same reason as pr_author (v4): the review runs long
    // after the poll that observed the PR, and re-fetching then costs a provider call against a PR that
    // may have changed or closed. The description is also the ONE piece of PR content that is fully
    // author-written prose, so pinning the version that was actually reviewed matters for auditability.
    //
    // All three NULL-able with no default: rows written before this migration genuinely have no captured
    // title, and every consumer must render absence as absence. A '' default would be indistinguishable
    // from a PR whose author really left the description empty.
    private const string V5Sql = """
        ALTER TABLE review_run ADD COLUMN pr_title         TEXT NULL;
        ALTER TABLE review_run ADD COLUMN pr_description   TEXT NULL;
        ALTER TABLE review_run ADD COLUMN pr_target_branch TEXT NULL;
        """;

    // ── v6: run ownership, so an orphaned run can be told from a live one ────────────────────────
    // WorkflowStatus.Running claims "a process is working on this right now", and nothing ever
    // withdrew that claim: a process that died mid-run left the row Running forever, with no code
    // path that ever looked at a run by status again. Measured on the live store: four rows stranded
    // at ContextReady, two of them from before the day's first restart, one holding a real 158 KB
    // context artifact computed and then abandoned.
    //
    // Reclaiming such a row is only safe if we can tell it apart from a run some OTHER live daemon is
    // working on — steal one of those and two processes review the same PR into the same notes
    // branch, which is worse than the leak. Hence an owner identity plus a heartbeat: the owner is a
    // per-PROCESS id, deliberately not a pid, because a pid is only meaningful on one machine and
    // these columns have to stay sound when a database is reachable from more than one.
    //
    // Both columns are NULL-able with no default, so this is a metadata-only ALTER: no table rewrite,
    // no rows touched, size of the database irrelevant. Pre-existing rows arrive with a NULL owner,
    // which is precisely the signal that identifies them as orphans of a process that cannot still
    // hold them.
    private const string V6Sql = """
        ALTER TABLE review_run ADD COLUMN owner_instance     TEXT NULL;
        ALTER TABLE review_run ADD COLUMN owner_heartbeat_at TEXT NULL;
        """;

    // ── v7: the refusal ledger — what the daemon's capability gates actually stopped ──────────────
    // The only recorded evidence that a collect-only run posted nothing was the ABSENCE of a Posted row
    // in review_outbox. That evidence is structurally blind to the event it was being read as proof
    // against: a review sub-agent posting straight to the provider REST API over the sandbox egress
    // proxy never touches review_outbox at all, so eleven observed dispatches of a posting-capable
    // template across collect-only runs left that table looking exactly like a quiet week.
    //
    // A gate that denies and says nothing is the same shape of problem one level down: nothing
    // distinguishes "the daemon refused a write" from "no write was ever attempted", and those two are
    // the whole question. Hence a positive record per refusal.
    //
    // NOT a child of review_run, and it carries no run id at all — deliberately. The two enforcement
    // points that write here do not both have a run in hand: the outbound HTTP seam is a singleton shared
    // with the poller, whose calls belong to no run. An FK (or even a plain run column) would force either
    // a fabricated id or a silently dropped record at exactly the site that must never drop one. The run a
    // refusal belongs to, when there is one, is recoverable from `target` and `at_utc`; a refusal that was
    // not written is recoverable from nothing.
    //
    // at_utc is a fixed-width UTC round-trip ("O") string like every other timestamp in this schema, so
    // lexicographic comparison is chronological.
    private const string V7Sql = """
        CREATE TABLE policy_refusal (
            id       INTEGER PRIMARY KEY,
            at_utc   TEXT NOT NULL,
            kind     TEXT NOT NULL,
            provider TEXT NOT NULL,
            subject  TEXT NOT NULL,
            method   TEXT NOT NULL,
            target   TEXT NOT NULL,
            reason   TEXT NOT NULL
        );

        CREATE INDEX ix_policy_refusal_at_utc ON policy_refusal (at_utc);
        CREATE INDEX ix_policy_refusal_kind   ON policy_refusal (kind);
        """;
}
