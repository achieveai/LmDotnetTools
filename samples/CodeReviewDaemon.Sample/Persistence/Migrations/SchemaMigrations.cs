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
        new Migration(8, V8Sql),
        new Migration(9, V9Sql),
        new Migration(10, V10Sql),
        new Migration(11, V11Sql),
        new Migration(12, V12Sql),
        new Migration(13, V13Sql),
        new Migration(14, V14Sql),
        new Migration(15, V15Sql),
        new Migration(16, V16Sql),
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

    // ── v5: the delta-review cutoff and the dedup-context signal (#225 items 1 + 2) ───────────────
    //
    // last_posted_review_at — when THIS run's review was proven to reach the PR. The "new since my last
    // review" cutoff used to be derived by scanning comment BODIES for a bracketed bot prefix, which is a
    // formatting convention pretending to be a fact: a summary posted without the prefix left the cutoff
    // null, and a null cutoff classifies every later human reply as PAST, defeating exactly the
    // new-comment handling it feeds. This column is the provider-agnostic fact the scan was standing in
    // for. It is stamped ONLY on proven delivery (see IsDeliveryProven), never on an attempt: a timestamp
    // claiming a post that did not happen would move the cutoff PAST real comments and hide them, which is
    // strictly worse than the null it replaces.
    //
    // Written per run but read per PR (MAX over repo_id + pr_id). A run's identity includes head_sha, so a
    // new head starts a new row while the CUTOFF must span heads — the bot's last word on the PR is the
    // bot's last word regardless of which head it was reviewing. "O"-formatted UTC keeps MAX lexicographic
    // and chronological at once, per the v3 note above.
    //
    // NULL-able with no default, for v4's reason: a pre-migration row genuinely does not know when — or
    // whether — it posted, and NULL already means "no stamped evidence" to every reader. A fabricated
    // instant would silently push the cutoff forward and suppress comments the bot is required to answer.
    //
    // dedup_context_lost — this run synthesized its review WITHOUT the list of comments already on the PR,
    // because the provider listing failed. Posting such a review onto a PR that already carries one is how
    // the duplicate-review spam #224 removed comes back. The flag is durable rather than in-memory
    // precisely because the decision is made at the Reviewed stage and consumed at the Posted stage, which
    // may be a different process after a restart — an in-memory flag would post blind on exactly the retry
    // path that most needs to know.
    //
    // NOT NULL DEFAULT 0, which is the opposite choice from the column above, and deliberately so. 0 means
    // "context was not lost", so every pre-migration row and every run in flight across the upgrade keeps
    // posting. Defaulting to 1 would fail closed in the abstract and, in practice, silence delivery for
    // every run mid-flight at deploy time on no evidence at all. The accepted residual is bounded to runs
    // that crossed the migration boundary having genuinely lost their dedup context — at most one review
    // round, on a signal that did not exist when they ran.
    private const string V5Sql = """
        ALTER TABLE review_run ADD COLUMN last_posted_review_at TEXT NULL;
        ALTER TABLE review_run ADD COLUMN dedup_context_lost    INTEGER NOT NULL DEFAULT 0;
        """;

    // ── v6: the refusal ledger — what the daemon's capability gates actually stopped ──────────────
    // The only recorded evidence that a collect-only run posted nothing was the ABSENCE of a Posted row
    // in review_outbox. That evidence is structurally blind to the event it was being read as proof
    // against: a review sub-agent posting straight to the provider REST API over the sandbox egress
    // proxy never touches review_outbox at all, so a posting-capable template dispatched on a
    // collect-only run leaves that table looking exactly like a quiet week.
    //
    // A gate that denies and says nothing is the same shape of problem one level down: nothing
    // distinguishes "the daemon refused a write" from "no write was ever attempted", and those two are
    // the whole question. Hence a positive record per refusal.
    //
    // NOT a child of review_run, and it carries no run id at all — deliberately. The two enforcement
    // points that write here do not both have a run in hand: the outbound HTTP seam is a singleton shared
    // with the poller, whose calls belong to no run. An FK (or even a plain run column) would force either
    // a fabricated id or a silently dropped record at exactly the site that must never drop one. A refusal
    // that was not written is recoverable from nothing, which is the trade being made.
    //
    // The consequence, stated plainly rather than waved at: a writer that HAS a run must put the run id in
    // `target` itself, because nothing else here will. The spawn gate does (`run {id} thread {…}`); the HTTP
    // seam records the request URI, which names the PR route but not the run, and no table maps a thread id
    // or a URI back to a run. So an egress refusal is attributable to a PR and an interval, not to a run.
    //
    // at_utc is a fixed-width UTC round-trip ("O") string like every other timestamp in this schema, so
    // lexicographic comparison is chronological.
    //
    // Numbered v6 against main's v5 head. It was authored as v7 on the source branch, where a lease
    // migration this fix does not carry had already taken v6; re-using that number here would have left
    // one statement running under a version another change also claims.
    private const string V6Sql = """
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

    // ── v7: what the PR SAYS it does ─────────────────────────────────────────────────────────────
    // Knowledge retrieval ranked entries purely on token overlap with the PR's own changed paths. Sibling
    // PRs applying one architectural pattern routinely touch entirely different files and share no path
    // token at all, so the same lesson was retrieved for one and not the other — which is how one leftover
    // defect came to be blocked on one PR and declined as out of scope on its sibling. The pattern is
    // named in the title, and the title was never persisted.
    //
    // Captured at poll time, on the run, rather than re-fetched at review time: the Reviewed stage runs
    // long after the poll page is gone, and on a resumed run it may be a different process entirely.
    //
    // NULL-able with no default, matching v4's pr_author. NULL means "not captured" — for every
    // pre-migration row, and for any provider payload that omits it — and every reader degrades to
    // path-only ranking, which is exactly the behaviour that existed before these columns. A blank string
    // is normalized to NULL at the provider seam so "the author wrote no description" and "we captured
    // none" do not rank differently.
    private const string V7Sql = """
        ALTER TABLE review_run ADD COLUMN pr_title       TEXT NULL;
        ALTER TABLE review_run ADD COLUMN pr_description TEXT NULL;
        """;

    // ── v8: the DURABLE retry budget and the permanent park ──────────────────────────────────────
    // The daemon already bounded retries — RetryGovernor counts attempts, backs off, and parks after K.
    // It held that count in a dictionary, and StrandedRunReconciler resumes a stuck run every ~45 minutes
    // through PrOrchestrator.ReconcileAsync, which resets the governor for the run it is handed. So the
    // count never survived long enough to reach K. Measured on the mcqdb daemon: 19 parks on 2026-08-28,
    // then zero from 08-29 onward, the transition landing exactly on the reconciler's first resume — three
    // pull requests re-reviewed on a loop for 33 hours, 30 minutes of model work discarded each round.
    //
    // governed_failure_count is that count made durable. It counts only the failures
    // PrOrchestrator.IsGovernedFailure already judged to be stuck rather than transient, and it is cleared
    // by a governed stage SUCCEEDING — never by a resume, which is the whole difference from the in-memory
    // one. NOT NULL DEFAULT 0 because a row written before this column genuinely has spent none of the
    // budget, and 0 is what every reader already means by that.
    //
    // parked_at is the terminal state, and it is a COLUMN rather than a workflow_status value because
    // status alone cannot carry it: ListStrandedRuns selects `workflow_status <> 'Completed'`, so a run
    // marked Failed still matches and would be resumed on the next pass exactly as before. `parked_at IS
    // NULL` is the one predicate that drops a parked run out of the listings, and the same instant is what
    // makes parking once-only (`WHERE ... AND parked_at IS NULL`), which is what stops a re-park posting a
    // second notice. Both NULL-able with no default, for v4's reason: not-parked is genuinely the absence
    // of a park instant, not a fabricated one.
    //
    // A park is per RUN, and a run's identity includes head_sha, so a new commit opens a new row and starts
    // with a full budget. That is the intended escape hatch and it needs no operator action.
    //
    // "O" round-trip UTC text like every other timestamp here, so lexicographic order is chronological.
    private const string V8Sql = """
        ALTER TABLE review_run ADD COLUMN governed_failure_count INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE review_run ADD COLUMN parked_at              TEXT NULL;
        ALTER TABLE review_run ADD COLUMN park_reason            TEXT NULL;
        """;

    // ── v9: which RUN owns a hosted conversation, and whether its workspace was released ──────────
    // The deep-link ledger already knew every conversation the daemon mints — it is written at the single
    // mint choke point precisely because the judge and A/B arms never reach an artifact. What it did not
    // know is which REVIEW RUN minted each one, and that is the question the pooled slot's safety turns on:
    // an S2S conversation is a live container with the leased slot mounted into it, so returning that slot
    // (or running host git over it, which CommitPooledNotesAsync does) while any of the run's conversations
    // still holds the mount is the duplicate-mount / index.lock family we have been chasing. The terminal
    // stage has a run id in hand and needs the thread ids; nothing mapped one to the other.
    //
    // review_run_id is NULL-able with no default and NO foreign key, both deliberately. NULL-able for v4's
    // reason: every pre-migration row genuinely has no known owner, and a fabricated id would attribute
    // someone else's conversation to a run and get it released underneath a live review. No FK because the
    // ledger deliberately OUTLIVES the review (see v3) — a row whose run is later pruned must keep ageing
    // out on its own clock rather than being cascaded away, and the mint choke point may legitimately
    // record a conversation no run claims.
    //
    // released_at is the RELEASE claim, and it is a timestamp rather than a boolean so an operator can tell
    // "released during this run's teardown" from "released by some later sweep". NULL means the workspace
    // release is UNCONFIRMED — which is the fail-closed reading everywhere it is consumed: an unconfirmed
    // release retires the slot instead of returning it. Stamped only on a positive, explicit host
    // confirmation of a backend unmount; never on a bare 200, because the host's own delete path deletes
    // its database row before a best-effort backend teardown and answers 200 either way, so a 200 alone is
    // not evidence the mount is gone.
    //
    // Release is NOT deletion. The row survives release and keeps its retention clock: the posted comment's
    // ?threadId= deep-link must go on resolving, and retention expiry stays the only thing that DELETEs a
    // conversation.
    //
    // "O" round-trip UTC text like every other timestamp here, so lexicographic order is chronological.
    private const string V9Sql = """
        ALTER TABLE deep_link_conversation ADD COLUMN review_run_id INTEGER NULL;
        ALTER TABLE deep_link_conversation ADD COLUMN released_at   TEXT NULL;

        CREATE INDEX ix_deep_link_conversation_run ON deep_link_conversation (review_run_id);
        """;

    // ── v10: append-only pooled-slot ownership across daemon restarts ──────────────────────────────
    // A hosted provision can mount the leased slot before its returned thread id reaches the daemon. The
    // slot claim therefore exists BEFORE preparation or provision, while each provision call gets its own
    // append-only intent. A null thread_id means provision may have begun but its result was lost; it is not
    // evidence that no thread exists. Claims remain unresolved until every possible mount is positively
    // released, or the daemon durably proves provisioning never began.
    private const string V10Sql = """
        CREATE TABLE review_slot_claim (
            id             INTEGER PRIMARY KEY,
            review_run_id  INTEGER NOT NULL REFERENCES review_run (id),
            slot_host_path TEXT NOT NULL,
            claimed_at     TEXT NOT NULL,
            resolved_at    TEXT NULL
        );

        CREATE INDEX ix_review_slot_claim_unresolved
            ON review_slot_claim (resolved_at, id);
        CREATE INDEX ix_review_slot_claim_run
            ON review_slot_claim (review_run_id, id);

        CREATE TABLE review_provision_intent (
            id                   INTEGER PRIMARY KEY,
            slot_claim_id        INTEGER NOT NULL REFERENCES review_slot_claim (id),
            review_run_id        INTEGER NOT NULL REFERENCES review_run (id),
            provisioning_began_at TEXT NOT NULL,
            thread_id            TEXT NULL,
            associated_at        TEXT NULL
        );

        CREATE INDEX ix_review_provision_intent_claim
            ON review_provision_intent (slot_claim_id, id);
        CREATE UNIQUE INDEX ux_review_provision_intent_thread
            ON review_provision_intent (thread_id) WHERE thread_id IS NOT NULL;
        """;

    // ── v11: distinguish conversation disablement from backend mount quiescence ─────────────────────
    // The review host can durably disable a conversation so it accepts no send and can never remount while
    // still being unable to observe whether the gateway tore down the old backend mount. That fact permits a
    // resumed run to continue on a fresh address, but does not make the old address reusable. Keep it separate
    // from released_at, whose stronger meaning remains positive backend mount quiescence.
    //
    // This is the next LOCAL migration after restart ownership v10. Integration must append it after the target
    // branch's actual migration tip if another lane lands first; renumbering this isolated migration is mechanical.
    private const string V11Sql = """
        ALTER TABLE deep_link_conversation
        ADD COLUMN conversation_released_at TEXT NULL;
        """;

    // ── v12: retain definitive pre-mint refusals without erasing attempt history ────────────────────
    // An intent is appended before contacting the host. A stable host refusal can prove that request minted
    // no conversation, but deleting its intent would erase the evidence used to make that address reusable.
    // Retraction is therefore a separate terminal timestamp. Null remains ambiguous, while association and
    // retraction are mutually exclusive first writes in ReviewStore.
    private const string V12Sql = """
        ALTER TABLE review_provision_intent
        ADD COLUMN retracted_at TEXT NULL;
        """;

    // ── v13: one PR-level coordinator and its typed engagement rounds ───────────────────────────────
    // review_run remains commit-identified. Its nullable round link associates only an admitted code-review
    // round without changing that identity; discussion and merged-close rounds therefore need no fabricated run.
    // Activity positions are canonical JSON because providers do not share one watermark shape, while the
    // required provider/timestamp/stable-object tuple remains exact and round-trippable.
    //
    // The partial unique index is the database authority for the per-PR lease. Pending work has already been
    // admitted, Running owns execution, and RetryPending owns the same round identity while awaiting recovery;
    // no second active row may exist in any of those states.
    private const string V13Sql = """
        CREATE TABLE pr_engagement (
            id                        INTEGER PRIMARY KEY,
            repo_id                   INTEGER NOT NULL REFERENCES repo (id),
            provider                  TEXT NOT NULL,
            pr_id                     TEXT NOT NULL,
            lifecycle                 TEXT NOT NULL CHECK (lifecycle IN ('Open', 'Merged', 'Closed', 'Abandoned')),
            latest_head_sha           TEXT NOT NULL,
            latest_base_sha           TEXT NOT NULL,
            last_reviewed_head_sha    TEXT NULL,
            latest_activity_json      TEXT NOT NULL,
            consumed_activity_json    TEXT NULL,
            last_completed_at         TEXT NULL,
            next_eligible_at          TEXT NULL,
            active_round_id           INTEGER NULL,
            latest_round_id           INTEGER NULL,
            root_summary_receipt_json TEXT NULL,
            created_at                TEXT NOT NULL,
            updated_at                TEXT NOT NULL,
            UNIQUE (provider, repo_id, pr_id)
        );

        CREATE TABLE engagement_round (
            id                         INTEGER PRIMARY KEY,
            pr_engagement_id           INTEGER NOT NULL REFERENCES pr_engagement (id),
            intent                     TEXT NOT NULL CHECK (intent IN ('CodeReview', 'DiscussionFollowUp', 'MergedClose')),
            status                     TEXT NOT NULL CHECK (status IN ('Pending', 'Running', 'RetryPending', 'Completed', 'Superseded', 'Parked')),
            head_sha                   TEXT NOT NULL,
            base_sha                   TEXT NOT NULL,
            activity_lower_bound_json  TEXT NULL,
            activity_upper_bound_json  TEXT NULL,
            prior_observation_boundary INTEGER NOT NULL,
            review_run_id              INTEGER NULL REFERENCES review_run (id),
            governed_failure_count     INTEGER NOT NULL DEFAULT 0 CHECK (governed_failure_count >= 0),
            started_at                 TEXT NULL,
            completed_at               TEXT NULL,
            superseded_at              TEXT NULL,
            parked_at                  TEXT NULL,
            park_reason                TEXT NULL,
            created_at                 TEXT NOT NULL,
            updated_at                 TEXT NOT NULL
        );

        CREATE UNIQUE INDEX ux_engagement_round_one_active
            ON engagement_round (pr_engagement_id)
            WHERE status IN ('Pending', 'Running', 'RetryPending');
        CREATE INDEX ix_engagement_round_engagement ON engagement_round (pr_engagement_id, id);

        ALTER TABLE review_run ADD COLUMN engagement_round_id INTEGER NULL REFERENCES engagement_round (id);
        CREATE UNIQUE INDEX ux_review_run_engagement_round
            ON review_run (engagement_round_id)
            WHERE engagement_round_id IS NOT NULL;

        CREATE TRIGGER fk_pr_engagement_active_round_update
        BEFORE UPDATE OF active_round_id ON pr_engagement
        WHEN NEW.active_round_id IS NOT NULL
         AND NOT EXISTS (
             SELECT 1 FROM engagement_round
             WHERE id = NEW.active_round_id AND pr_engagement_id = NEW.id)
        BEGIN
            SELECT RAISE(ABORT, 'active round must belong to engagement');
        END;

        CREATE TRIGGER fk_pr_engagement_latest_round_update
        BEFORE UPDATE OF latest_round_id ON pr_engagement
        WHEN NEW.latest_round_id IS NOT NULL
         AND NOT EXISTS (
             SELECT 1 FROM engagement_round
             WHERE id = NEW.latest_round_id AND pr_engagement_id = NEW.id)
        BEGIN
            SELECT RAISE(ABORT, 'latest round must belong to engagement');
        END;

        """;

    // ── v14: immutable, chunked model-turn audit source and versioned redactions ────────────────────
    private const string V14Sql = """
        CREATE TABLE audit_blob (
            sha256     TEXT PRIMARY KEY,
            byte_count INTEGER NOT NULL CHECK (byte_count >= 0),
            content    BLOB NOT NULL
        );

        CREATE TABLE audit_source_record (
            id                  TEXT PRIMARY KEY,
            engagement_round_id INTEGER NOT NULL REFERENCES engagement_round (id),
            thread_id           TEXT NOT NULL,
            run_id              TEXT NOT NULL,
            generation_id       TEXT NOT NULL,
            parent_turn_id      TEXT NULL,
            sequence            INTEGER NOT NULL CHECK (sequence >= 0),
            record_type         TEXT NOT NULL,
            role                TEXT NULL,
            model_id            TEXT NULL,
            provider_id         TEXT NULL,
            content_sha256        TEXT NOT NULL,
            byte_count            INTEGER NOT NULL CHECK (byte_count >= 0),
            source_content_sha256 TEXT NOT NULL,
            source_byte_count     INTEGER NOT NULL CHECK (source_byte_count >= 0),
            capture_outcome       TEXT NOT NULL CHECK (capture_outcome IN ('Complete', 'Gap', 'Redacted')),
            gap_reason_code     TEXT NULL,
            captured_at         TEXT NOT NULL,
            completed_at        TEXT NULL,
            CHECK (
                (capture_outcome = 'Complete' AND gap_reason_code IS NULL)
                OR (capture_outcome IN ('Gap', 'Redacted') AND byte_count = 0 AND gap_reason_code IS NOT NULL)
            ),
            UNIQUE (engagement_round_id, thread_id, run_id, generation_id, sequence, record_type)
        );

        CREATE TABLE audit_source_chunk (
            source_record_id TEXT NOT NULL REFERENCES audit_source_record (id),
            chunk_index      INTEGER NOT NULL CHECK (chunk_index >= 0),
            blob_sha256      TEXT NOT NULL REFERENCES audit_blob (sha256),
            byte_offset      INTEGER NOT NULL CHECK (byte_offset >= 0),
            byte_count       INTEGER NOT NULL CHECK (byte_count >= 0),
            PRIMARY KEY (source_record_id, chunk_index)
        );

        CREATE TABLE audit_redaction_record (
            id                      TEXT PRIMARY KEY,
            source_record_id        TEXT NOT NULL REFERENCES audit_source_record (id),
            version                 INTEGER NOT NULL CHECK (version > 0),
            redacted_content_sha256 TEXT NOT NULL,
            redacted_byte_count     INTEGER NOT NULL CHECK (redacted_byte_count >= 0),
            affected_ranges_json    TEXT NOT NULL,
            reason_code             TEXT NOT NULL,
            created_at              TEXT NOT NULL,
            UNIQUE (source_record_id, version)
        );

        CREATE TABLE audit_redaction_chunk (
            redaction_record_id TEXT NOT NULL REFERENCES audit_redaction_record (id),
            chunk_index         INTEGER NOT NULL CHECK (chunk_index >= 0),
            blob_sha256         TEXT NOT NULL REFERENCES audit_blob (sha256),
            byte_offset         INTEGER NOT NULL CHECK (byte_offset >= 0),
            byte_count          INTEGER NOT NULL CHECK (byte_count >= 0),
            PRIMARY KEY (redaction_record_id, chunk_index)
        );
        """;

    // ── v15: typed review participation evidence with exact source links ─────────────────────────────
    private const string V15Sql = """
        CREATE UNIQUE INDEX ux_audit_source_record_id_hash
            ON audit_source_record (id, content_sha256);

        CREATE TABLE review_action (
            engagement_round_id  INTEGER NOT NULL REFERENCES engagement_round (id),
            action_id             TEXT NOT NULL,
            kind                  TEXT NOT NULL CHECK (kind IN (
                'CreateRootSummary', 'AppendSummaryDelta', 'SubmitInlineFindings',
                'PostClarificationQuestion', 'ReplyToDiscussion', 'FinalizeRound')),
            status                TEXT NOT NULL CHECK (status IN (
                'Planned', 'CollectedOnly', 'Sending', 'Accepted', 'Rejected')),
            payload_sha256        TEXT NOT NULL,
            provider_target_json  TEXT NULL,
            provider_receipt_json TEXT NULL,
            rejection_json        TEXT NULL,
            created_at            TEXT NOT NULL,
            updated_at            TEXT NOT NULL,
            PRIMARY KEY (engagement_round_id, action_id),
            CHECK (status <> 'Accepted' OR provider_receipt_json IS NOT NULL),
            CHECK (status <> 'Rejected' OR rejection_json IS NOT NULL)
        );

        CREATE TABLE review_action_source (
            engagement_round_id  INTEGER NOT NULL,
            action_id             TEXT NOT NULL,
            source_record_id      TEXT NOT NULL,
            source_content_sha256 TEXT NOT NULL,
            PRIMARY KEY (engagement_round_id, action_id, source_record_id),
            FOREIGN KEY (engagement_round_id, action_id)
                REFERENCES review_action (engagement_round_id, action_id),
            FOREIGN KEY (source_record_id, source_content_sha256)
                REFERENCES audit_source_record (id, content_sha256)
        );

        CREATE TABLE clarification_question (
            id                    TEXT PRIMARY KEY,
            engagement_round_id   INTEGER NOT NULL REFERENCES engagement_round (id),
            wording               TEXT NOT NULL,
            provider_target_json  TEXT NULL,
            file_path             TEXT NULL,
            line                  INTEGER NULL CHECK (line IS NULL OR line > 0),
            evidence_summary      TEXT NOT NULL,
            withheld_conclusion   TEXT NOT NULL,
            action_id             TEXT NULL,
            provider_receipt_json TEXT NULL,
            asked_at              TEXT NULL,
            state                 TEXT NOT NULL CHECK (state IN (
                'Open', 'Answered', 'Contested', 'Superseded', 'UnansweredAtMerge')),
            created_at            TEXT NOT NULL,
            updated_at            TEXT NOT NULL,
            FOREIGN KEY (engagement_round_id, action_id)
                REFERENCES review_action (engagement_round_id, action_id),
            CHECK (
                (asked_at IS NULL AND provider_receipt_json IS NULL)
                OR (asked_at IS NOT NULL AND provider_receipt_json IS NOT NULL))
        );

        CREATE TABLE clarification_question_source (
            question_id           TEXT NOT NULL REFERENCES clarification_question (id),
            source_record_id      TEXT NOT NULL,
            source_content_sha256 TEXT NOT NULL,
            PRIMARY KEY (question_id, source_record_id),
            FOREIGN KEY (source_record_id, source_content_sha256)
                REFERENCES audit_source_record (id, content_sha256)
        );

        CREATE TABLE clarification_candidate_answer (
            id                     TEXT PRIMARY KEY,
            question_id            TEXT NOT NULL REFERENCES clarification_question (id),
            observed_in_round_id   INTEGER NOT NULL REFERENCES engagement_round (id),
            provider_reference_json TEXT NOT NULL,
            interpretation_summary TEXT NOT NULL,
            observed_at             TEXT NOT NULL
        );

        CREATE TABLE clarification_candidate_answer_source (
            candidate_answer_id    TEXT NOT NULL REFERENCES clarification_candidate_answer (id),
            source_record_id       TEXT NOT NULL,
            source_content_sha256  TEXT NOT NULL,
            PRIMARY KEY (candidate_answer_id, source_record_id),
            FOREIGN KEY (source_record_id, source_content_sha256)
                REFERENCES audit_source_record (id, content_sha256)
        );

        CREATE TABLE round_observation (
            id                  TEXT PRIMARY KEY,
            engagement_round_id INTEGER NOT NULL REFERENCES engagement_round (id),
            sequence            INTEGER NOT NULL CHECK (sequence >= 0),
            kind                TEXT NOT NULL CHECK (kind IN (
                'ContextClaim', 'Finding', 'Question', 'Answer', 'Correction',
                'DiscussionContribution', 'DeliberateNoAction', 'JudgeResult', 'Gap')),
            summary             TEXT NOT NULL,
            observed_at         TEXT NOT NULL,
            UNIQUE (engagement_round_id, sequence)
        );

        CREATE TABLE round_observation_source (
            observation_id       TEXT NOT NULL REFERENCES round_observation (id),
            source_record_id     TEXT NOT NULL,
            source_content_sha256 TEXT NOT NULL,
            PRIMARY KEY (observation_id, source_record_id),
            FOREIGN KEY (source_record_id, source_content_sha256)
                REFERENCES audit_source_record (id, content_sha256)
        );

        CREATE TRIGGER round_observation_no_update
        BEFORE UPDATE ON round_observation
        BEGIN
            SELECT RAISE(ABORT, 'round observations are append-only');
        END;

        CREATE TRIGGER round_observation_no_delete
        BEFORE DELETE ON round_observation
        BEGIN
            SELECT RAISE(ABORT, 'round observations are append-only');
        END;

        CREATE TRIGGER round_observation_source_no_update
        BEFORE UPDATE ON round_observation_source
        BEGIN
            SELECT RAISE(ABORT, 'round observation source links are append-only');
        END;

        CREATE TRIGGER round_observation_source_no_delete
        BEFORE DELETE ON round_observation_source
        BEGIN
            SELECT RAISE(ABORT, 'round observation source links are append-only');
        END;
        """;

    // ── v16: merged-close outcome items and their promotion results ─────────────────────────────────
    // close_outcome_item is the persisted denominator: one row per deterministic candidate, carrying
    // both what the analyst proposed and what the verifier independently confirmed. Reports recompute
    // their aggregates from these rows, so a rendered count can never drift from the evidence.
    //
    // The evidence link repeats the (id, content_sha256) pair so a mutated source orphans the link
    // exactly as it does for questions, actions, and observations.
    //
    // promotion_outcome is keyed by source observation ID and destination, which is what makes an
    // identical promotion replay a no-op rather than a duplicate contribution.
    private const string V16Sql = """
        CREATE TABLE close_outcome_item (
            engagement_round_id      INTEGER NOT NULL REFERENCES engagement_round (id),
            candidate_id             TEXT NOT NULL,
            kind                     TEXT NOT NULL CHECK (kind IN (
                'Question', 'Finding', 'SubstantiveContribution', 'ProviderAction')),
            summary                  TEXT NOT NULL,
            proposed_label           TEXT NULL,
            verified_label           TEXT NOT NULL CHECK (verified_label IN (
                'Indeterminate', 'Confirmed', 'Addressed', 'NotAddressed', 'Novel', 'IndependentlyRaised')),
            verification_reason_code TEXT NOT NULL,
            observed_at              TEXT NOT NULL,
            PRIMARY KEY (engagement_round_id, candidate_id),
            CHECK (verified_label <> 'Indeterminate' OR verification_reason_code <> '')
        );

        CREATE TABLE close_outcome_item_source (
            engagement_round_id   INTEGER NOT NULL,
            candidate_id          TEXT NOT NULL,
            source_record_id      TEXT NOT NULL,
            source_content_sha256 TEXT NOT NULL,
            PRIMARY KEY (engagement_round_id, candidate_id, source_record_id),
            FOREIGN KEY (engagement_round_id, candidate_id)
                REFERENCES close_outcome_item (engagement_round_id, candidate_id) ON DELETE CASCADE,
            FOREIGN KEY (source_record_id, source_content_sha256)
                REFERENCES audit_source_record (id, content_sha256)
        );

        CREATE TABLE promotion_outcome (
            engagement_round_id     INTEGER NOT NULL REFERENCES engagement_round (id),
            source_observation_id   TEXT NOT NULL,
            destination_kind        TEXT NOT NULL CHECK (destination_kind IN (
                'KnowledgeBase', 'DeveloperLearnings')),
            disposition             TEXT NOT NULL CHECK (disposition IN ('Written', 'Declined', 'Failed')),
            destination_path        TEXT NULL,
            destination_content_sha256 TEXT NULL,
            reason_code             TEXT NULL,
            created_at              TEXT NOT NULL,
            updated_at              TEXT NOT NULL,
            PRIMARY KEY (engagement_round_id, source_observation_id, destination_kind),
            CHECK (disposition <> 'Written' OR (destination_path IS NOT NULL AND destination_content_sha256 IS NOT NULL)),
            CHECK (disposition = 'Written' OR reason_code IS NOT NULL)
        );
        """;
}
