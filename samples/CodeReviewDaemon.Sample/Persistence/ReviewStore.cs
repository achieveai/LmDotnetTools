using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmMultiTurn.Audit;
using CodeReviewDaemon.Sample.Persistence.Migrations;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace;
using Microsoft.Data.Sqlite;

namespace CodeReviewDaemon.Sample.Persistence;

/// <summary>
/// The daemon's orchestration source of truth. Wraps a single migrated SQLite connection and exposes
/// only the operations the orchestrator / poller / poster consume: repo identity normalization (§7),
/// idempotent <c>review_run</c> creation + resume-state updates (§6), opaque poll cursors (§12), the
/// crash-safe outbox (§11), and append-compatible artifacts (§14). It is intentionally not a generic
/// repository — every method has a current consumer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Thread safety.</b> A single <see cref="SqliteConnection"/> is NOT thread-safe. SQLite's own handle
/// is serialized internally, so the damage is not corrupt rows — it is the connection's <i>managed</i>
/// per-connection command list, which every <see cref="SqliteCommand"/> mutates as it is created and
/// disposed. Measured unguarded on this store (24 threads × 400 write+read+read+list iterations): rows
/// all landed, but one iteration threw <c>ArgumentOutOfRangeException ("Index was out of range")</c> from
/// inside that list. Rare, non-deterministic, and it would surface as a single inexplicably failed review.
/// Every public operation therefore runs under <see cref="_gate"/>, held for the whole command-plus-reader
/// sequence rather than just the execute call — a streaming <see cref="SqliteDataReader"/> keeps its
/// command alive until drained, so releasing at the execute call would leave the same window open. The
/// same measurement with the gate: zero errors, and ~4× faster (contention on SQLite's internal mutex
/// costs more than serializing up front). That makes the store safe to share across concurrent reviews;
/// it does not make reviews concurrent — the poller is still serial — it only removes this class as the
/// reason they cannot be.
/// </para>
/// <para>
/// The gate is reentrant (<see cref="Lock"/> has Monitor semantics), so a public method may call a
/// private helper that takes it again. It is deliberately a process-local lock, not a SQLite-level one:
/// the daemon is the single writer of its own database file.
/// </para>
/// </remarks>
internal sealed class ReviewStore : IMultiTurnAuditSink, IDisposable
{
    internal const int AuditChunkBytes = 64 * 1024;

    private readonly SqliteConnection _connection;
    private readonly Lock _gate = new();
    private readonly TimeProvider _timeProvider;

    public ReviewStore(string connectionString, TimeProvider? timeProvider = null)
    {
        _connection = SqliteConnectionFactory.Open(connectionString);
        _timeProvider = timeProvider ?? TimeProvider.System;
        MigrationRunner.Migrate(_connection);
    }

    private string UtcNow() => _timeProvider.GetUtcNow().ToString("O");

    /// <summary>
    /// Renders an instant the way <see cref="UtcNow"/> does: normalized to UTC first, so every stored
    /// timestamp has the same fixed-width shape and offset and lexicographic ordering stays chronological.
    /// </summary>
    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("O");

    /// <summary>The inverse of <see cref="Utc"/> for a column this store wrote — round-trip ("O") text.</summary>
    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    // ── Repo identity (§7) ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the id of the <c>repo</c> row for <paramref name="identity"/>, inserting it on first
    /// sight. Lookup is by the case-folded <see cref="RepoIdentity.NormalizedKey"/>, so observations
    /// that differ only by casing collapse to one row while the first-seen display name is preserved.
    /// </summary>
    public long EnsureRepo(RepoIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        using var gate = _gate.EnterScope();
        using var find = _connection.CreateCommand();
        find.CommandText = "SELECT id FROM repo WHERE normalized_key = $key;";
        _ = find.Parameters.AddWithValue("$key", identity.NormalizedKey);
        var existing = find.ExecuteScalar();
        if (existing is not null)
        {
            return Convert.ToInt64(existing);
        }

        using var insert = _connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO repo (provider, normalized_key, display_name, org_or_owner, project, repo_name, repo_stable_id, created_at)
            VALUES ($provider, $key, $display, $org, $project, $name, $stableId, $now)
            RETURNING id;
            """;
        _ = insert.Parameters.AddWithValue("$provider", identity.Provider);
        _ = insert.Parameters.AddWithValue("$key", identity.NormalizedKey);
        _ = insert.Parameters.AddWithValue("$display", identity.DisplayName);
        _ = insert.Parameters.AddWithValue("$org", identity.OrgOrOwner);
        _ = insert.Parameters.AddWithValue("$project", (object?)identity.Project ?? DBNull.Value);
        _ = insert.Parameters.AddWithValue("$name", identity.RepoName);
        _ = insert.Parameters.AddWithValue("$stableId", (object?)identity.RepoStableId ?? DBNull.Value);
        _ = insert.Parameters.AddWithValue("$now", UtcNow());
        return Convert.ToInt64(insert.ExecuteScalar());
    }

    /// <summary>
    /// Rehydrates the <see cref="RepoIdentity"/> for a <c>repo</c> row id, or <c>null</c> when no such
    /// row exists. Consumed by the poster/executor, which carry only the run's <c>repo_id</c> and need
    /// the provider/owner/project/name to address the PR for posting.
    /// </summary>
    public RepoIdentity? GetRepo(long id)
    {
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT provider, org_or_owner, project, repo_name, repo_stable_id FROM repo WHERE id = $id;";
        _ = command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new RepoIdentity
        {
            Provider = reader.GetString(reader.GetOrdinal("provider")),
            OrgOrOwner = reader.GetString(reader.GetOrdinal("org_or_owner")),
            Project = GetNullableString(reader, "project"),
            RepoName = reader.GetString(reader.GetOrdinal("repo_name")),
            RepoStableId = GetNullableString(reader, "repo_stable_id"),
        };
    }

    // ── review_run (§6) ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Inserts <paramref name="run"/>, or returns the existing row when this reviewed commit already has
    /// one. A run's identity is the COMMIT: <c>(repo, pr, head_sha, base_sha, review_kind, variant_id)</c>.
    /// It deliberately excludes <c>mode</c> and <c>trigger_watermark</c>: <c>mode</c> (post vs collect-only)
    /// is an authorization decision made at post time (<c>EnableCommentPosting</c>), not part of what the
    /// review IS — keying identity on it would spawn a fresh run (and a redundant re-review of every open
    /// PR) the moment posting is toggled; <c>trigger_watermark</c> (the PR's <c>updated_at</c>) is mutated
    /// by the act of posting a comment, so keying on it would spawn a duplicate run on the very next poll.
    /// The lookup is therefore mode/watermark-agnostic, preferring the furthest-progressed row so a
    /// completed review short-circuits rather than re-running. A new <c>head_sha</c> is what legitimately
    /// starts a new run. The check-then-insert runs under the store's gate, so concurrent callers with the
    /// same identity serialize and the second one sees the first one's row.
    /// </summary>
    public ReviewRun CreateOrGetReviewRun(ReviewRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        // Held across the find-then-insert pair, not just each command: two callers racing the same
        // identity would otherwise both miss and both insert.
        using var gate = _gate.EnterScope();
        var existing = FindReviewRunByIdentity(run);
        if (existing is not null)
        {
            if (run.EngagementRoundId is { } existingRoundId)
            {
                AttachExistingRunToRound(existing, existingRoundId);
            }

            return existing;
        }

        if (run.EngagementRoundId is { } roundId)
        {
            EnsureCodeReviewRound(roundId);
        }

        var now = UtcNow();
        using var insert = _connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO review_run (
                repo_id, pr_id, head_sha, base_sha, trigger_watermark, review_kind, variant_id, mode,
                engagement_round_id, merge_sha, model_provider, model_id, prompt_template_hash,
                policy_bundle_version, feature_flag_snapshot, stage, workflow_status, pr_lifecycle_state,
                is_fork_pr, is_target_repo_public, pr_author,
                pr_title, pr_description, created_at, updated_at)
            VALUES (
                $repoId, $prId, $head, $base, $watermark, $kind, $variant, $mode,
                $roundId, $merge, $modelProvider, $modelId, $promptHash, $policyVersion,
                $flags, $stage, $workflow, $prState,
                $isForkPr, $isTargetRepoPublic, $prAuthor,
                $prTitle, $prDescription, $now, $now);
            """;
        _ = insert.Parameters.AddWithValue("$repoId", run.RepoId);
        _ = insert.Parameters.AddWithValue("$prId", run.PrId);
        _ = insert.Parameters.AddWithValue("$head", run.HeadSha);
        _ = insert.Parameters.AddWithValue("$base", run.BaseSha);
        _ = insert.Parameters.AddWithValue("$watermark", run.TriggerWatermark);
        _ = insert.Parameters.AddWithValue("$kind", run.ReviewKind);
        _ = insert.Parameters.AddWithValue("$variant", run.VariantId);
        _ = insert.Parameters.AddWithValue("$mode", run.Mode);
        _ = insert.Parameters.AddWithValue("$roundId", (object?)run.EngagementRoundId ?? DBNull.Value);
        _ = insert.Parameters.AddWithValue("$merge", (object?)run.MergeSha ?? DBNull.Value);
        _ = insert.Parameters.AddWithValue("$modelProvider", (object?)run.ModelProvider ?? DBNull.Value);
        _ = insert.Parameters.AddWithValue("$modelId", (object?)run.ModelId ?? DBNull.Value);
        _ = insert.Parameters.AddWithValue("$promptHash", (object?)run.PromptTemplateHash ?? DBNull.Value);
        _ = insert.Parameters.AddWithValue("$policyVersion", (object?)run.PolicyBundleVersion ?? DBNull.Value);
        _ = insert.Parameters.AddWithValue("$flags", (object?)run.FeatureFlagSnapshot ?? DBNull.Value);
        _ = insert.Parameters.AddWithValue("$stage", run.Stage.ToString());
        _ = insert.Parameters.AddWithValue("$workflow", run.WorkflowStatus.ToString());
        _ = insert.Parameters.AddWithValue("$prState", run.PrLifecycleState.ToString());
        _ = insert.Parameters.AddWithValue("$isForkPr", run.IsForkPr);
        _ = insert.Parameters.AddWithValue("$isTargetRepoPublic", run.IsTargetRepoPublic);
        _ = insert.Parameters.AddWithValue("$prAuthor", (object?)run.PrAuthor ?? DBNull.Value);
        _ = insert.Parameters.AddWithValue("$prTitle", (object?)run.PrTitle ?? DBNull.Value);
        _ = insert.Parameters.AddWithValue("$prDescription", (object?)run.PrDescription ?? DBNull.Value);
        _ = insert.Parameters.AddWithValue("$now", now);
        _ = insert.ExecuteNonQuery();

        var created = FindReviewRunByIdentity(run)!;
        if (run.EngagementRoundId is { } createdRoundId)
        {
            LinkRoundToReviewRun(createdRoundId, created.Id);
        }

        return created;
    }

    private void AttachExistingRunToRound(ReviewRun run, long roundId)
    {
        EnsureCodeReviewRound(roundId);
        if (run.EngagementRoundId is { } linkedRoundId && linkedRoundId != roundId)
        {
            throw new InvalidOperationException(
                $"Review run {run.Id} already belongs to engagement round {linkedRoundId}; it cannot be reassigned to {roundId}."
            );
        }

        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE review_run
            SET engagement_round_id = $roundId, updated_at = $now
            WHERE id = $runId AND engagement_round_id IS NULL;
            """;
        _ = command.Parameters.AddWithValue("$roundId", roundId);
        _ = command.Parameters.AddWithValue("$now", UtcNow());
        _ = command.Parameters.AddWithValue("$runId", run.Id);
        _ = command.ExecuteNonQuery();
        LinkRoundToReviewRun(roundId, run.Id);
    }

    private void EnsureCodeReviewRound(long roundId)
    {
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT intent FROM engagement_round WHERE id = $roundId;";
        _ = command.Parameters.AddWithValue("$roundId", roundId);
        if (command.ExecuteScalar() is not string intent)
        {
            throw new ArgumentOutOfRangeException(nameof(roundId), $"Engagement round {roundId} does not exist.");
        }

        if (!StringComparer.Ordinal.Equals(intent, EngagementRoundIntent.CodeReview.ToString()))
        {
            throw new InvalidOperationException(
                $"Only a {EngagementRoundIntent.CodeReview} engagement round may own a review run."
            );
        }
    }

    private void LinkRoundToReviewRun(long roundId, long reviewRunId)
    {
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE engagement_round
            SET review_run_id = $runId, updated_at = $now
            WHERE id = $roundId
              AND intent = 'CodeReview'
              AND (review_run_id IS NULL OR review_run_id = $runId);
            """;
        _ = command.Parameters.AddWithValue("$runId", reviewRunId);
        _ = command.Parameters.AddWithValue("$now", UtcNow());
        _ = command.Parameters.AddWithValue("$roundId", roundId);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException(
                $"Engagement round {roundId} cannot be linked to review run {reviewRunId}."
            );
        }
    }

    // ── PR engagement coordinator (migration v9) ─────────────────────────────────────────────────

    public PrEngagement CreateOrGetEngagement(PrEngagement engagement)
    {
        ArgumentNullException.ThrowIfNull(engagement);
        ValidateWatermark(engagement.LatestActivity, engagement.Provider);
        if (engagement.ConsumedActivity is { } consumed)
        {
            ValidateWatermark(consumed, engagement.Provider);
        }

        using var gate = _gate.EnterScope();
        using var insert = _connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO pr_engagement (
                repo_id, provider, pr_id, lifecycle, latest_head_sha, latest_base_sha,
                last_reviewed_head_sha, latest_activity_json, consumed_activity_json,
                last_completed_at, next_eligible_at, root_summary_receipt_json, created_at, updated_at)
            VALUES (
                $repoId, $provider, $prId, $lifecycle, $head, $base,
                $lastReviewedHead, $latestActivity, $consumedActivity,
                $lastCompletedAt, $nextEligibleAt, $rootReceipt, $now, $updatedAt)
            ON CONFLICT (provider, repo_id, pr_id) DO NOTHING;
            """;
        _ = insert.Parameters.AddWithValue("$repoId", engagement.RepoId);
        _ = insert.Parameters.AddWithValue("$provider", engagement.Provider);
        _ = insert.Parameters.AddWithValue("$prId", engagement.PrId);
        _ = insert.Parameters.AddWithValue("$lifecycle", engagement.Lifecycle.ToString());
        _ = insert.Parameters.AddWithValue("$head", engagement.LatestHeadSha);
        _ = insert.Parameters.AddWithValue("$base", engagement.LatestBaseSha);
        _ = insert.Parameters.AddWithValue(
            "$lastReviewedHead",
            (object?)engagement.LastReviewedHeadSha ?? DBNull.Value
        );
        _ = insert.Parameters.AddWithValue("$latestActivity", SerializeWatermark(engagement.LatestActivity));
        _ = insert.Parameters.AddWithValue(
            "$consumedActivity",
            engagement.ConsumedActivity is { } activity ? SerializeWatermark(activity) : DBNull.Value
        );
        _ = insert.Parameters.AddWithValue(
            "$lastCompletedAt",
            engagement.LastCompletedAt is { } completed ? Utc(completed) : DBNull.Value
        );
        _ = insert.Parameters.AddWithValue(
            "$nextEligibleAt",
            engagement.NextEligibleAt is { } eligible ? Utc(eligible) : DBNull.Value
        );
        _ = insert.Parameters.AddWithValue("$rootReceipt", (object?)engagement.RootSummaryReceiptJson ?? DBNull.Value);
        _ = insert.Parameters.AddWithValue("$now", UtcNow());
        _ = insert.Parameters.AddWithValue("$updatedAt", Utc(engagement.UpdatedAt));
        _ = insert.ExecuteNonQuery();
        return GetEngagementByIdentity(engagement.Provider, engagement.RepoId, engagement.PrId)
            ?? throw new InvalidOperationException(
                $"The engagement for provider '{engagement.Provider}', repository {engagement.RepoId}, "
                    + $"PR '{engagement.PrId}' was not persisted."
            );
    }

    /// <summary>
    /// Coalesces provider truth into the stable PR coordinator. Head/base/lifecycle always follow the newest
    /// observation, while the activity watermark advances monotonically so an out-of-order provider page cannot
    /// erase pending discussion demand.
    /// </summary>
    public void UpdateEngagementObservation(
        long engagementId,
        PrLifecycleState lifecycle,
        string latestHeadSha,
        string latestBaseSha,
        ProviderActivityWatermark latestActivity,
        DateTimeOffset observedAt
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(latestHeadSha);
        ArgumentException.ThrowIfNullOrWhiteSpace(latestBaseSha);
        ArgumentException.ThrowIfNullOrWhiteSpace(latestActivity.Provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(latestActivity.StableObjectId);

        using var gate = _gate.EnterScope();
        var current =
            GetEngagement(engagementId)
            ?? throw new ArgumentOutOfRangeException(nameof(engagementId), "The engagement does not exist.");
        ValidateWatermark(latestActivity, current.Provider);
        var activity = latestActivity.CompareTo(current.LatestActivity) > 0 ? latestActivity : current.LatestActivity;

        using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE pr_engagement
            SET lifecycle = CASE WHEN updated_at <= $at THEN $lifecycle ELSE lifecycle END,
                latest_head_sha = CASE WHEN updated_at <= $at THEN $head ELSE latest_head_sha END,
                latest_base_sha = CASE WHEN updated_at <= $at THEN $base ELSE latest_base_sha END,
                latest_activity_json = $activity,
                updated_at = CASE WHEN updated_at <= $at THEN $at ELSE updated_at END
            WHERE id = $id;
            """;
        _ = command.Parameters.AddWithValue("$lifecycle", lifecycle.ToString());
        _ = command.Parameters.AddWithValue("$head", latestHeadSha);
        _ = command.Parameters.AddWithValue("$base", latestBaseSha);
        _ = command.Parameters.AddWithValue("$activity", SerializeWatermark(activity));
        _ = command.Parameters.AddWithValue("$at", Utc(observedAt));
        _ = command.Parameters.AddWithValue("$id", engagementId);
        _ = command.ExecuteNonQuery();
    }

    public PrEngagement? GetEngagement(long engagementId)
    {
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT * FROM pr_engagement WHERE id = $id;";
        _ = command.Parameters.AddWithValue("$id", engagementId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapEngagement(reader) : null;
    }

    public PrEngagement? GetEngagement(string provider, long repoId, string prId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(prId);
        return GetEngagementByIdentity(provider, repoId, prId);
    }

    /// <summary>
    /// Completes historical coordinator fields without replacing newer observed provider state.
    /// Non-null evidence fills only fields that cutover has not already established.
    /// </summary>
    public void AdoptHistoricalEngagementEvidence(
        long engagementId,
        string? lastReviewedHeadSha,
        DateTimeOffset? lastCompletedAt,
        string? rootSummaryReceiptJson
    )
    {
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE pr_engagement
            SET last_reviewed_head_sha = COALESCE(last_reviewed_head_sha, $lastReviewedHead),
                last_completed_at = COALESCE(last_completed_at, $lastCompletedAt),
                next_eligible_at = COALESCE(next_eligible_at, $nextEligibleAt),
                root_summary_receipt_json = COALESCE(root_summary_receipt_json, $rootReceipt),
                updated_at = $now
            WHERE id = $id;
            """;
        _ = command.Parameters.AddWithValue("$lastReviewedHead", (object?)lastReviewedHeadSha ?? DBNull.Value);
        _ = command.Parameters.AddWithValue(
            "$lastCompletedAt",
            lastCompletedAt is { } completed ? Utc(completed) : DBNull.Value
        );
        _ = command.Parameters.AddWithValue(
            "$nextEligibleAt",
            lastCompletedAt is { } at ? Utc(at.AddHours(1)) : DBNull.Value
        );
        _ = command.Parameters.AddWithValue("$rootReceipt", (object?)rootSummaryReceiptJson ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$now", UtcNow());
        _ = command.Parameters.AddWithValue("$id", engagementId);
        _ = command.ExecuteNonQuery();
    }

    private PrEngagement? GetEngagementByIdentity(string provider, long repoId, string prId)
    {
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM pr_engagement
            WHERE provider = $provider AND repo_id = $repoId AND pr_id = $prId;
            """;
        _ = command.Parameters.AddWithValue("$provider", provider);
        _ = command.Parameters.AddWithValue("$repoId", repoId);
        _ = command.Parameters.AddWithValue("$prId", prId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapEngagement(reader) : null;
    }

    /// <summary>
    /// Atomically creates one pending round and attaches the engagement's lease to it. A null result means
    /// this engagement already has admitted work; the partial unique index is the cross-caller authority.
    /// </summary>
    public EngagementRound? TryAdmitRound(EngagementRound round)
    {
        ArgumentNullException.ThrowIfNull(round);
        if (round.Status != EngagementRoundStatus.Pending)
        {
            throw new ArgumentException("A newly admitted engagement round must be Pending.", nameof(round));
        }

        if (
            round.ActivityLowerBound is { } lower
            && round.ActivityUpperBound is { } upper
            && lower.CompareTo(upper) > 0
        )
        {
            throw new ArgumentException("An activity lower bound cannot follow its upper bound.", nameof(round));
        }

        using var gate = _gate.EnterScope();
        using var transaction = _connection.BeginTransaction();
        long roundId;
        try
        {
            using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO engagement_round (
                    pr_engagement_id, intent, status, head_sha, base_sha,
                    activity_lower_bound_json, activity_upper_bound_json, prior_observation_boundary,
                    review_run_id, governed_failure_count, started_at, completed_at, superseded_at,
                    parked_at, park_reason, created_at, updated_at)
                VALUES (
                    $engagementId, $intent, $status, $head, $base,
                    $lower, $upper, $boundary,
                    $reviewRunId, $failureCount, $startedAt, $completedAt, $supersededAt,
                    $parkedAt, $parkReason, $now, $now)
                RETURNING id;
                """;
            AddRoundParameters(insert, round);
            _ = insert.Parameters.AddWithValue("$now", UtcNow());
            roundId = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == 2067)
        {
            transaction.Rollback();
            return null;
        }

        using (var claim = _connection.CreateCommand())
        {
            claim.Transaction = transaction;
            claim.CommandText = """
                UPDATE pr_engagement
                SET active_round_id = $roundId, latest_round_id = $roundId, updated_at = $now
                WHERE id = $engagementId AND active_round_id IS NULL;
                """;
            _ = claim.Parameters.AddWithValue("$roundId", roundId);
            _ = claim.Parameters.AddWithValue("$now", UtcNow());
            _ = claim.Parameters.AddWithValue("$engagementId", round.PrEngagementId);
            if (claim.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return null;
            }
        }

        transaction.Commit();
        return GetEngagementRound(roundId);
    }

    public EngagementRound? GetEngagementRound(long roundId)
    {
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT * FROM engagement_round WHERE id = $id;";
        _ = command.Parameters.AddWithValue("$id", roundId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapEngagementRound(reader) : null;
    }

    public IReadOnlyList<EngagementRound> ListEngagementRounds(long engagementId)
    {
        var result = new List<EngagementRound>();
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT * FROM engagement_round WHERE pr_engagement_id = $id ORDER BY id;";
        _ = command.Parameters.AddWithValue("$id", engagementId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(MapEngagementRound(reader));
        }

        return result;
    }

    /// <summary>
    /// Idempotently adopts one completed pre-v9 code-review run into its PR engagement. This records only
    /// historical identity; it does not advance cooldown or consume provider activity.
    /// </summary>
    public EngagementRound SeedHistoricalCompletedCodeReviewRound(
        long engagementId,
        long reviewRunId,
        DateTimeOffset? completedAt
    )
    {
        using var gate = _gate.EnterScope();
        using var transaction = _connection.BeginTransaction();

        using (var existing = _connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT engagement_round_id FROM review_run WHERE id = $runId;";
            _ = existing.Parameters.AddWithValue("$runId", reviewRunId);
            var existingRoundId = existing.ExecuteScalar();
            if (existingRoundId is long roundId)
            {
                transaction.Rollback();
                return GetEngagementRound(roundId)
                    ?? throw new InvalidOperationException($"Engagement round {roundId} does not exist.");
            }
        }

        using (var validate = _connection.CreateCommand())
        {
            validate.Transaction = transaction;
            validate.CommandText = """
                SELECT COUNT(*)
                FROM review_run r
                JOIN pr_engagement e
                  ON e.id = $engagementId
                 AND e.repo_id = r.repo_id
                 AND e.pr_id = r.pr_id
                WHERE r.id = $runId
                  AND r.variant_id = $variant
                  AND r.stage IN ('Reviewed', 'Judged', 'Posted');
                """;
            _ = validate.Parameters.AddWithValue("$engagementId", engagementId);
            _ = validate.Parameters.AddWithValue("$runId", reviewRunId);
            _ = validate.Parameters.AddWithValue("$variant", PrimaryVariantId);
            if (Convert.ToInt64(validate.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
            {
                throw new InvalidOperationException(
                    $"Review run {reviewRunId} cannot be adopted by engagement {engagementId}."
                );
            }
        }

        long createdRoundId;
        using (var insert = _connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO engagement_round (
                    pr_engagement_id, intent, status, head_sha, base_sha,
                    activity_lower_bound_json, activity_upper_bound_json, prior_observation_boundary,
                    review_run_id, governed_failure_count, started_at, completed_at, superseded_at,
                    parked_at, park_reason, created_at, updated_at)
                SELECT e.id, 'CodeReview', 'Completed', r.head_sha, r.base_sha,
                       NULL, NULL, 0,
                       r.id, 0, NULL, $completedAt, NULL,
                       NULL, NULL, $now, $now
                FROM review_run r
                JOIN pr_engagement e ON e.id = $engagementId
                WHERE r.id = $runId
                RETURNING id;
                """;
            _ = insert.Parameters.AddWithValue("$engagementId", engagementId);
            _ = insert.Parameters.AddWithValue("$runId", reviewRunId);
            _ = insert.Parameters.AddWithValue(
                "$completedAt",
                completedAt is { } completed ? Utc(completed) : DBNull.Value
            );
            _ = insert.Parameters.AddWithValue("$now", UtcNow());
            createdRoundId = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        using (var linkRun = _connection.CreateCommand())
        {
            linkRun.Transaction = transaction;
            linkRun.CommandText = """
                UPDATE review_run
                SET engagement_round_id = $roundId, updated_at = $now
                WHERE id = $runId AND engagement_round_id IS NULL;
                """;
            _ = linkRun.Parameters.AddWithValue("$roundId", createdRoundId);
            _ = linkRun.Parameters.AddWithValue("$now", UtcNow());
            _ = linkRun.Parameters.AddWithValue("$runId", reviewRunId);
            if (linkRun.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException($"Review run {reviewRunId} was adopted concurrently.");
            }
        }

        using (var linkEngagement = _connection.CreateCommand())
        {
            linkEngagement.Transaction = transaction;
            linkEngagement.CommandText = """
                UPDATE pr_engagement
                SET latest_round_id = $roundId, updated_at = $now
                WHERE id = $engagementId;
                """;
            _ = linkEngagement.Parameters.AddWithValue("$roundId", createdRoundId);
            _ = linkEngagement.Parameters.AddWithValue("$now", UtcNow());
            _ = linkEngagement.Parameters.AddWithValue("$engagementId", engagementId);
            _ = linkEngagement.ExecuteNonQuery();
        }

        transaction.Commit();
        return GetEngagementRound(createdRoundId)!;
    }

    /// <summary>
    /// Applies a legal compare-and-swap transition. Terminal rows cannot re-enter execution. Completion also
    /// releases the lease and advances consumed activity/cooldown in the same transaction.
    /// </summary>
    public bool TryTransitionEngagementRound(
        long roundId,
        EngagementRoundStatus expected,
        EngagementRoundStatus next,
        DateTimeOffset transitionAt
    )
    {
        if (!IsLegalRoundTransition(expected, next))
        {
            return false;
        }

        using var gate = _gate.EnterScope();
        using var transaction = _connection.BeginTransaction();
        EngagementRound? current;
        using (var read = _connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT * FROM engagement_round WHERE id = $id AND status = $expected;";
            _ = read.Parameters.AddWithValue("$id", roundId);
            _ = read.Parameters.AddWithValue("$expected", expected.ToString());
            using var reader = read.ExecuteReader();
            current = reader.Read() ? MapEngagementRound(reader) : null;
        }

        if (current is null)
        {
            transaction.Rollback();
            return false;
        }

        using (var update = _connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE engagement_round
                SET status = $next,
                    started_at = CASE WHEN $next = 'Running' THEN COALESCE(started_at, $at) ELSE started_at END,
                    completed_at = CASE WHEN $next = 'Completed' THEN $at ELSE completed_at END,
                    superseded_at = CASE WHEN $next = 'Superseded' THEN $at ELSE superseded_at END,
                    parked_at = CASE WHEN $next = 'Parked' THEN $at ELSE parked_at END,
                    updated_at = $at
                WHERE id = $id AND status = $expected;
                """;
            _ = update.Parameters.AddWithValue("$next", next.ToString());
            _ = update.Parameters.AddWithValue("$at", Utc(transitionAt));
            _ = update.Parameters.AddWithValue("$id", roundId);
            _ = update.Parameters.AddWithValue("$expected", expected.ToString());
            if (update.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return false;
            }
        }

        if (next is EngagementRoundStatus.Completed or EngagementRoundStatus.Superseded or EngagementRoundStatus.Parked)
        {
            UpdateEngagementAfterTerminalRound(current, next, transitionAt, transaction);
        }

        transaction.Commit();
        return true;
    }

    /// <summary>Atomically charges one failure to an active round and returns its durable count.</summary>
    public int IncrementEngagementRoundFailureCount(long roundId)
    {
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE engagement_round
            SET governed_failure_count = governed_failure_count + 1,
                updated_at = $at
            WHERE id = $id AND status IN ('Running', 'RetryPending')
            RETURNING governed_failure_count;
            """;
        _ = command.Parameters.AddWithValue("$at", UtcNow());
        _ = command.Parameters.AddWithValue("$id", roundId);
        var value = command.ExecuteScalar();
        return value is null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    /// <summary>Parks one active round with a stable, daemon-authored reason and releases its PR lease.</summary>
    public bool TryParkEngagementRound(
        long roundId,
        EngagementRoundStatus expected,
        DateTimeOffset parkedAt,
        string reason
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (expected is not (EngagementRoundStatus.Running or EngagementRoundStatus.RetryPending))
        {
            return false;
        }

        using var gate = _gate.EnterScope();
        using var transaction = _connection.BeginTransaction();
        EngagementRound? current;
        using (var read = _connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT * FROM engagement_round WHERE id = $id AND status = $expected;";
            _ = read.Parameters.AddWithValue("$id", roundId);
            _ = read.Parameters.AddWithValue("$expected", expected.ToString());
            using var reader = read.ExecuteReader();
            current = reader.Read() ? MapEngagementRound(reader) : null;
        }

        if (current is null)
        {
            transaction.Rollback();
            return false;
        }

        using (var update = _connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE engagement_round
                SET status = 'Parked', parked_at = $at, park_reason = $reason, updated_at = $at
                WHERE id = $id AND status = $expected;
                """;
            _ = update.Parameters.AddWithValue("$at", Utc(parkedAt));
            _ = update.Parameters.AddWithValue("$reason", reason);
            _ = update.Parameters.AddWithValue("$id", roundId);
            _ = update.Parameters.AddWithValue("$expected", expected.ToString());
            if (update.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return false;
            }
        }

        UpdateEngagementAfterTerminalRound(current, EngagementRoundStatus.Parked, parkedAt, transaction);
        transaction.Commit();
        return true;
    }

    private void UpdateEngagementAfterTerminalRound(
        EngagementRound round,
        EngagementRoundStatus next,
        DateTimeOffset at,
        SqliteTransaction transaction
    )
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE pr_engagement
            SET active_round_id = CASE WHEN active_round_id = $roundId THEN NULL ELSE active_round_id END,
                last_reviewed_head_sha = CASE
                    WHEN $completed = 1 AND $intent = 'CodeReview' THEN $head
                    ELSE last_reviewed_head_sha
                END,
                consumed_activity_json = CASE
                    WHEN $completed = 1 THEN COALESCE($upper, consumed_activity_json)
                    ELSE consumed_activity_json
                END,
                last_completed_at = CASE WHEN $completed = 1 THEN $at ELSE last_completed_at END,
                next_eligible_at = CASE
                    WHEN $completed = 1 AND $intent IN ('CodeReview', 'DiscussionFollowUp') THEN $eligible
                    WHEN $completed = 1 AND $intent = 'MergedClose' THEN NULL
                    ELSE next_eligible_at
                END,
                updated_at = $at
            WHERE id = $engagementId;
            """;
        var completed = next == EngagementRoundStatus.Completed;
        _ = command.Parameters.AddWithValue("$roundId", round.Id);
        _ = command.Parameters.AddWithValue("$completed", completed);
        _ = command.Parameters.AddWithValue("$intent", round.Intent.ToString());
        _ = command.Parameters.AddWithValue("$head", round.HeadSha);
        _ = command.Parameters.AddWithValue(
            "$upper",
            round.ActivityUpperBound is { } upper ? SerializeWatermark(upper) : DBNull.Value
        );
        _ = command.Parameters.AddWithValue("$at", Utc(at));
        _ = command.Parameters.AddWithValue("$eligible", Utc(at.AddHours(1)));
        _ = command.Parameters.AddWithValue("$engagementId", round.PrEngagementId);
        _ = command.ExecuteNonQuery();
    }

    private static bool IsLegalRoundTransition(EngagementRoundStatus expected, EngagementRoundStatus next) =>
        (expected, next) switch
        {
            (EngagementRoundStatus.Pending, EngagementRoundStatus.Running) => true,
            (EngagementRoundStatus.Pending, EngagementRoundStatus.Superseded) => true,
            (EngagementRoundStatus.Running, EngagementRoundStatus.Completed) => true,
            (EngagementRoundStatus.Running, EngagementRoundStatus.RetryPending) => true,
            (EngagementRoundStatus.Running, EngagementRoundStatus.Superseded) => true,
            (EngagementRoundStatus.Running, EngagementRoundStatus.Parked) => true,
            (EngagementRoundStatus.RetryPending, EngagementRoundStatus.Running) => true,
            (EngagementRoundStatus.RetryPending, EngagementRoundStatus.Superseded) => true,
            (EngagementRoundStatus.RetryPending, EngagementRoundStatus.Parked) => true,
            _ => false,
        };

    private static void AddRoundParameters(SqliteCommand command, EngagementRound round)
    {
        _ = command.Parameters.AddWithValue("$engagementId", round.PrEngagementId);
        _ = command.Parameters.AddWithValue("$intent", round.Intent.ToString());
        _ = command.Parameters.AddWithValue("$status", round.Status.ToString());
        _ = command.Parameters.AddWithValue("$head", round.HeadSha);
        _ = command.Parameters.AddWithValue("$base", round.BaseSha);
        _ = command.Parameters.AddWithValue(
            "$lower",
            round.ActivityLowerBound is { } lower ? SerializeWatermark(lower) : DBNull.Value
        );
        _ = command.Parameters.AddWithValue(
            "$upper",
            round.ActivityUpperBound is { } upper ? SerializeWatermark(upper) : DBNull.Value
        );
        _ = command.Parameters.AddWithValue("$boundary", round.PriorObservationBoundary);
        _ = command.Parameters.AddWithValue("$reviewRunId", (object?)round.ReviewRunId ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$failureCount", round.GovernedFailureCount);
        _ = command.Parameters.AddWithValue("$startedAt", round.StartedAt is { } started ? Utc(started) : DBNull.Value);
        _ = command.Parameters.AddWithValue(
            "$completedAt",
            round.CompletedAt is { } completed ? Utc(completed) : DBNull.Value
        );
        _ = command.Parameters.AddWithValue(
            "$supersededAt",
            round.SupersededAt is { } superseded ? Utc(superseded) : DBNull.Value
        );
        _ = command.Parameters.AddWithValue("$parkedAt", round.ParkedAt is { } parked ? Utc(parked) : DBNull.Value);
        _ = command.Parameters.AddWithValue("$parkReason", (object?)round.ParkReason ?? DBNull.Value);
    }

    private static string SerializeWatermark(ProviderActivityWatermark watermark) =>
        JsonSerializer.Serialize(watermark);

    private static ProviderActivityWatermark DeserializeWatermark(string json) =>
        JsonSerializer.Deserialize<ProviderActivityWatermark>(json)
        ?? throw new InvalidOperationException("A persisted activity watermark was null.");

    private static void ValidateWatermark(ProviderActivityWatermark watermark, string provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(watermark.Provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(watermark.StableObjectId);
        if (!StringComparer.OrdinalIgnoreCase.Equals(watermark.Provider, provider))
        {
            throw new ArgumentException(
                "An activity watermark must belong to its engagement provider.",
                nameof(watermark)
            );
        }
    }

    // ── Complete model-turn audit source (migration v10) ───────────────────────────────────────────

    public ValueTask RecordAsync(ModelTurnAuditRecord record, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = StoreAuditRecord(record);
        return ValueTask.CompletedTask;
    }

    public AuditSourceRecord StoreAuditRecord(ModelTurnAuditRecord source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var (engagementId, roundId) = ParseAuditScope(source);
        var content = source.Content.ToArray();
        if (
            content.LongLength != source.ByteCount
            || !StringComparer.Ordinal.Equals(Sha256(content), source.ContentSha256)
        )
        {
            throw new ArgumentException(
                "The audit record content does not match its declared hash and length.",
                nameof(source)
            );
        }

        var record = ToAuditSourceRecord(source, roundId);
        using var gate = _gate.EnterScope();
        using var transaction = _connection.BeginTransaction();
        ValidateAuditScope(engagementId, roundId, transaction);
        InsertAuditRecord(record, transaction);
        var persisted = GetAuditRecord(record.Id, transaction);
        if (!AuditRecordMetadataMatches(persisted, record))
        {
            throw new InvalidOperationException(
                $"Audit source record '{record.Id}' conflicts with persisted metadata."
            );
        }

        if (source.Outcome == AuditCaptureOutcome.Complete)
        {
            if (persisted.CompletedAtUtc is null)
            {
                StoreAuditChunks(record.Id, content, transaction);
                CompleteAuditRecord(record, source.CapturedAtUtc, transaction);
                persisted = GetAuditRecord(record.Id, transaction);
            }
            else
            {
                var existing = ReadAuditBytes(record.Id, "audit_source_chunk", "source_record_id", transaction);
                if (!existing.AsSpan().SequenceEqual(content))
                {
                    throw new InvalidOperationException(
                        $"Audit source record '{record.Id}' conflicts with persisted bytes."
                    );
                }
            }
        }

        transaction.Commit();
        return persisted;
    }

    public AuditSourceRecord BeginAuditRecord(AuditSourceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ValidateAuditRecord(record);

        using var gate = _gate.EnterScope();
        using var transaction = _connection.BeginTransaction();
        InsertAuditRecord(record, transaction);

        var persisted = GetAuditRecord(record.Id, transaction);
        if (!AuditRecordMetadataMatches(persisted, record))
        {
            throw new InvalidOperationException(
                $"Audit source record '{record.Id}' conflicts with persisted metadata."
            );
        }

        transaction.Commit();
        return persisted;
    }

    public void AppendAuditChunk(string sourceRecordId, int chunkIndex, long byteOffset, ReadOnlySpan<byte> content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRecordId);
        ArgumentOutOfRangeException.ThrowIfNegative(chunkIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(byteOffset);
        if (content.Length > AuditChunkBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(content),
                $"An audit chunk may contain at most {AuditChunkBytes} bytes."
            );
        }

        var bytes = content.ToArray();
        var sha256 = Sha256(bytes);
        using var gate = _gate.EnterScope();
        using var transaction = _connection.BeginTransaction();
        var record = GetAuditRecord(sourceRecordId, transaction);
        if (record.CaptureOutcome != AuditSourceCaptureOutcome.Complete || record.CompletedAtUtc is not null)
        {
            throw new InvalidOperationException(
                $"Audit source record '{sourceRecordId}' does not accept additional chunks."
            );
        }

        InsertAuditBlob(sha256, bytes, transaction);
        InsertSourceChunk(sourceRecordId, chunkIndex, byteOffset, sha256, bytes.LongLength, transaction);
        transaction.Commit();
    }

    public AuditSourceRecord CompleteAuditRecord(
        string sourceRecordId,
        string contentSha256,
        long byteCount,
        DateTimeOffset completedAtUtc
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRecordId);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentSha256);
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);

        using var gate = _gate.EnterScope();
        using var transaction = _connection.BeginTransaction();
        var record = GetAuditRecord(sourceRecordId, transaction);
        if (record.CaptureOutcome != AuditSourceCaptureOutcome.Complete)
        {
            throw new InvalidOperationException("Only a Complete audit source record accepts content chunks.");
        }

        if (byteCount != record.ByteCount || !StringComparer.Ordinal.Equals(contentSha256, record.ContentSha256))
        {
            throw new InvalidOperationException(
                $"Audit source record '{sourceRecordId}' completion metadata conflicts with its declaration."
            );
        }

        if (record.CompletedAtUtc is null)
        {
            CompleteAuditRecord(record, completedAtUtc, transaction);
        }
        else if (record.CompletedAtUtc != completedAtUtc)
        {
            throw new InvalidOperationException(
                $"Audit source record '{sourceRecordId}' was completed with conflicting metadata."
            );
        }

        var completed = GetAuditRecord(sourceRecordId, transaction);
        transaction.Commit();
        return completed;
    }

    public AuditSourceRecord? GetAuditRecord(string sourceRecordId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRecordId);
        using var gate = _gate.EnterScope();
        return TryGetAuditRecord(sourceRecordId, transaction: null);
    }

    public byte[] ReadAuditContent(string sourceRecordId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRecordId);
        using var gate = _gate.EnterScope();
        var record = GetAuditRecord(sourceRecordId, transaction: null);
        if (record.CaptureOutcome != AuditSourceCaptureOutcome.Complete)
        {
            return [];
        }

        if (record.CompletedAtUtc is null)
        {
            throw new InvalidOperationException($"Audit source record '{sourceRecordId}' is not complete.");
        }

        var content = ReadAuditBytes(sourceRecordId, "audit_source_chunk", "source_record_id", transaction: null);
        if (
            content.LongLength != record.ByteCount
            || !StringComparer.Ordinal.Equals(Sha256(content), record.ContentSha256)
        )
        {
            throw new InvalidOperationException($"Audit source record '{sourceRecordId}' failed content verification.");
        }

        return content;
    }

    public IReadOnlyList<AuditSourceRecord> ListAuditRecordsForRound(long engagementRoundId)
    {
        var records = new List<AuditSourceRecord>();
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM audit_source_record
            WHERE engagement_round_id = $roundId
            ORDER BY thread_id ASC, run_id ASC, generation_id ASC, sequence ASC,
                     captured_at ASC, record_type ASC, id ASC;
            """;
        _ = command.Parameters.AddWithValue("$roundId", engagementRoundId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            records.Add(MapAuditSourceRecord(reader));
        }

        return records;
    }

    public AuditRedactionRecord CreateAuditRedaction(AuditRedactionRecord redaction, ReadOnlySpan<byte> content)
    {
        ArgumentNullException.ThrowIfNull(redaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(redaction.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(redaction.SourceRecordId);
        ArgumentException.ThrowIfNullOrWhiteSpace(redaction.AffectedRangesJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(redaction.ReasonCode);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(redaction.Version);
        var bytes = content.ToArray();
        if (
            bytes.LongLength != redaction.RedactedByteCount
            || !StringComparer.Ordinal.Equals(Sha256(bytes), redaction.RedactedContentSha256)
        )
        {
            throw new ArgumentException(
                "Redacted content does not match its declared hash and length.",
                nameof(content)
            );
        }

        using var gate = _gate.EnterScope();
        using var transaction = _connection.BeginTransaction();
        using (var insert = _connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO audit_redaction_record (
                    id, source_record_id, version, redacted_content_sha256, redacted_byte_count,
                    affected_ranges_json, reason_code, created_at)
                VALUES ($id, $sourceId, $version, $sha256, $byteCount, $ranges, $reason, $createdAt)
                ON CONFLICT (id) DO NOTHING;
                """;
            _ = insert.Parameters.AddWithValue("$id", redaction.Id);
            _ = insert.Parameters.AddWithValue("$sourceId", redaction.SourceRecordId);
            _ = insert.Parameters.AddWithValue("$version", redaction.Version);
            _ = insert.Parameters.AddWithValue("$sha256", redaction.RedactedContentSha256);
            _ = insert.Parameters.AddWithValue("$byteCount", redaction.RedactedByteCount);
            _ = insert.Parameters.AddWithValue("$ranges", redaction.AffectedRangesJson);
            _ = insert.Parameters.AddWithValue("$reason", redaction.ReasonCode);
            _ = insert.Parameters.AddWithValue("$createdAt", Utc(redaction.CreatedAtUtc));
            _ = insert.ExecuteNonQuery();
        }

        var persisted = GetAuditRedaction(redaction.Id, transaction);
        if (persisted != redaction)
        {
            throw new InvalidOperationException($"Audit redaction '{redaction.Id}' conflicts with persisted metadata.");
        }

        for (var offset = 0; offset < bytes.Length; offset += AuditChunkBytes)
        {
            var count = Math.Min(AuditChunkBytes, bytes.Length - offset);
            var chunk = bytes.AsSpan(offset, count).ToArray();
            var sha256 = Sha256(chunk);
            InsertAuditBlob(sha256, chunk, transaction);
            InsertRedactionChunk(redaction.Id, offset / AuditChunkBytes, offset, sha256, chunk.LongLength, transaction);
        }

        transaction.Commit();
        return persisted;
    }

    public byte[] ReadAuditRedactionContent(string redactionRecordId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(redactionRecordId);
        using var gate = _gate.EnterScope();
        var record = GetAuditRedaction(redactionRecordId, transaction: null);
        var content = ReadAuditBytes(
            redactionRecordId,
            "audit_redaction_chunk",
            "redaction_record_id",
            transaction: null
        );
        if (
            content.LongLength != record.RedactedByteCount
            || !StringComparer.Ordinal.Equals(Sha256(content), record.RedactedContentSha256)
        )
        {
            throw new InvalidOperationException($"Audit redaction '{redactionRecordId}' failed content verification.");
        }

        return content;
    }

    public AuditSourceRecord RecordLegacyAuditGap(
        long engagementRoundId,
        string sourceRecordId,
        string gapReasonCode,
        DateTimeOffset capturedAtUtc
    ) =>
        BeginAuditRecord(
            new AuditSourceRecord(
                sourceRecordId,
                engagementRoundId,
                "legacy",
                "legacy",
                sourceRecordId,
                null,
                0,
                "legacy_gap",
                null,
                null,
                null,
                Sha256([]),
                0,
                Sha256([]),
                0,
                AuditSourceCaptureOutcome.Gap,
                gapReasonCode,
                capturedAtUtc,
                capturedAtUtc
            )
        );

    internal long CountAuditBlobs()
    {
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM audit_blob;";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    // ── Typed engagement evidence (migration v11) ──────────────────────────────────────────────────

    public ReviewAction AddOrGetReviewAction(ReviewAction action, IReadOnlyList<AuditSourceReference> sources)
    {
        ArgumentNullException.ThrowIfNull(action);
        ValidateReviewAction(action);
        using var gate = _gate.EnterScope();
        using var transaction = _connection.BeginTransaction();
        ValidateSourceReferences(action.EngagementRoundId, sources, transaction);

        using (var insert = _connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO review_action (
                    engagement_round_id, action_id, kind, status, payload_sha256,
                    provider_target_json, provider_receipt_json, rejection_json, created_at, updated_at)
                VALUES (
                    $roundId, $actionId, $kind, $status, $payloadSha256,
                    $target, $receipt, $rejection, $createdAt, $updatedAt)
                ON CONFLICT (engagement_round_id, action_id) DO NOTHING;
                """;
            AddReviewActionParameters(insert, action);
            _ = insert.ExecuteNonQuery();
        }

        var persisted = GetReviewAction(action.EngagementRoundId, action.ActionId, transaction);
        if (!ReviewActionIntentMatches(persisted, action))
        {
            throw new InvalidOperationException(
                $"Review action '{action.ActionId}' conflicts with persisted metadata."
            );
        }

        InsertSourceReferences(
            "review_action_source",
            ["engagement_round_id", "action_id"],
            [action.EngagementRoundId, action.ActionId],
            sources,
            transaction
        );
        EnsureSourceReferencesMatch(
            "review_action_source",
            "engagement_round_id = $owner0 AND action_id = $owner1",
            [action.EngagementRoundId, action.ActionId],
            sources,
            transaction
        );
        transaction.Commit();
        return persisted;
    }

    public ReviewAction? GetReviewAction(long engagementRoundId, string actionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        using var gate = _gate.EnterScope();
        return TryGetReviewAction(engagementRoundId, actionId, transaction: null);
    }

    public IReadOnlyList<ReviewAction> ListReviewActions(long engagementRoundId)
    {
        var actions = new List<ReviewAction>();
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM review_action
            WHERE engagement_round_id = $roundId
            ORDER BY created_at ASC, action_id ASC;
            """;
        _ = command.Parameters.AddWithValue("$roundId", engagementRoundId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            actions.Add(MapReviewAction(reader));
        }

        return actions;
    }

    public bool TrySetRootSummaryReceipt(long engagementId, string providerReceiptJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerReceiptJson);
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE pr_engagement
            SET root_summary_receipt_json = $receipt,
                updated_at = $at
            WHERE id = $id
              AND (
                  root_summary_receipt_json IS NULL
                  OR root_summary_receipt_json = $receipt
              );
            """;
        _ = command.Parameters.AddWithValue("$receipt", providerReceiptJson);
        _ = command.Parameters.AddWithValue("$at", UtcNow());
        _ = command.Parameters.AddWithValue("$id", engagementId);
        return command.ExecuteNonQuery() == 1;
    }

    public bool TryReopenCollectedReviewAction(long engagementRoundId, string actionId, DateTimeOffset reopenedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE review_action
            SET status = 'Planned', updated_at = $at
            WHERE engagement_round_id = $roundId
              AND action_id = $actionId
              AND status = 'CollectedOnly';
            """;
        _ = command.Parameters.AddWithValue("$at", Utc(reopenedAtUtc));
        _ = command.Parameters.AddWithValue("$roundId", engagementRoundId);
        _ = command.Parameters.AddWithValue("$actionId", actionId);
        return command.ExecuteNonQuery() == 1;
    }

    public bool TryTransitionReviewAction(
        long engagementRoundId,
        string actionId,
        ReviewActionStatus expected,
        ReviewActionStatus next,
        string? providerReceiptJson,
        string? rejectionJson,
        DateTimeOffset transitionedAtUtc
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        if (!IsLegalReviewActionTransition(expected, next))
        {
            return false;
        }

        if (next == ReviewActionStatus.Accepted && string.IsNullOrWhiteSpace(providerReceiptJson))
        {
            throw new ArgumentException(
                "An accepted review action requires a provider receipt.",
                nameof(providerReceiptJson)
            );
        }

        if (next == ReviewActionStatus.Rejected && string.IsNullOrWhiteSpace(rejectionJson))
        {
            throw new ArgumentException("A rejected review action requires rejection details.", nameof(rejectionJson));
        }

        using var gate = _gate.EnterScope();
        using var transaction = _connection.BeginTransaction();
        using (var update = _connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE review_action
                SET status = $next,
                    provider_receipt_json = $receipt,
                    rejection_json = $rejection,
                    updated_at = $at
                WHERE engagement_round_id = $roundId AND action_id = $actionId AND status = $expected;
                """;
            _ = update.Parameters.AddWithValue("$next", next.ToString());
            _ = update.Parameters.AddWithValue("$receipt", (object?)providerReceiptJson ?? DBNull.Value);
            _ = update.Parameters.AddWithValue("$rejection", (object?)rejectionJson ?? DBNull.Value);
            _ = update.Parameters.AddWithValue("$at", Utc(transitionedAtUtc));
            _ = update.Parameters.AddWithValue("$roundId", engagementRoundId);
            _ = update.Parameters.AddWithValue("$actionId", actionId);
            _ = update.Parameters.AddWithValue("$expected", expected.ToString());
            if (update.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return false;
            }
        }

        if (next == ReviewActionStatus.Accepted)
        {
            using var markAsked = _connection.CreateCommand();
            markAsked.Transaction = transaction;
            markAsked.CommandText = """
                UPDATE clarification_question
                SET provider_receipt_json = $receipt, asked_at = $at, updated_at = $at
                WHERE engagement_round_id = $roundId AND action_id = $actionId AND asked_at IS NULL;
                """;
            _ = markAsked.Parameters.AddWithValue("$receipt", providerReceiptJson);
            _ = markAsked.Parameters.AddWithValue("$at", Utc(transitionedAtUtc));
            _ = markAsked.Parameters.AddWithValue("$roundId", engagementRoundId);
            _ = markAsked.Parameters.AddWithValue("$actionId", actionId);
            _ = markAsked.ExecuteNonQuery();
        }

        transaction.Commit();
        return true;
    }

    public ClarificationQuestion AddClarificationQuestion(
        ClarificationQuestion question,
        IReadOnlyList<AuditSourceReference> sources
    )
    {
        ArgumentNullException.ThrowIfNull(question);
        ValidateClarificationQuestion(question);
        using var gate = _gate.EnterScope();
        using var transaction = _connection.BeginTransaction();
        ValidateSourceReferences(question.EngagementRoundId, sources, transaction);

        using (var insert = _connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO clarification_question (
                    id, engagement_round_id, wording, provider_target_json, file_path, line,
                    evidence_summary, withheld_conclusion, action_id, provider_receipt_json,
                    asked_at, state, created_at, updated_at)
                VALUES (
                    $id, $roundId, $wording, $target, $file, $line,
                    $evidence, $withheld, $actionId, $receipt,
                    $askedAt, $state, $createdAt, $updatedAt)
                ON CONFLICT (id) DO NOTHING;
                """;
            AddClarificationQuestionParameters(insert, question);
            _ = insert.ExecuteNonQuery();
        }

        var persisted =
            GetClarificationQuestion(question.Id, transaction)
            ?? throw new InvalidOperationException($"Clarification question '{question.Id}' was not persisted.");
        ValidateAskedQuestionAction(persisted, transaction);
        if (persisted != question)
        {
            throw new InvalidOperationException(
                $"Clarification question '{question.Id}' conflicts with persisted metadata."
            );
        }

        InsertSourceReferences("clarification_question_source", ["question_id"], [question.Id], sources, transaction);
        EnsureSourceReferencesMatch(
            "clarification_question_source",
            "question_id = $owner0",
            [question.Id],
            sources,
            transaction
        );
        transaction.Commit();
        return persisted;
    }

    public ClarificationQuestion? GetClarificationQuestion(string questionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(questionId);
        using var gate = _gate.EnterScope();
        return GetClarificationQuestion(questionId, transaction: null);
    }

    public IReadOnlyList<ClarificationQuestion> ListAskedClarificationQuestions(long engagementRoundId)
    {
        var questions = new List<ClarificationQuestion>();
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT q.* FROM clarification_question q
            INNER JOIN review_action a
                ON a.engagement_round_id = q.engagement_round_id AND a.action_id = q.action_id
            WHERE q.engagement_round_id = $roundId
              AND a.status = 'Accepted'
              AND q.provider_receipt_json IS NOT NULL
              AND q.asked_at IS NOT NULL
            ORDER BY q.created_at, q.id;
            """;
        _ = command.Parameters.AddWithValue("$roundId", engagementRoundId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            questions.Add(MapClarificationQuestion(reader));
        }

        return questions;
    }

    /// <summary>Lists the candidate answers observed for one question, oldest first.</summary>
    public IReadOnlyList<ClarificationCandidateAnswer> ListClarificationCandidateAnswers(string questionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(questionId);
        var answers = new List<ClarificationCandidateAnswer>();
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM clarification_candidate_answer
            WHERE question_id = $questionId
            ORDER BY observed_at, id;
            """;
        _ = command.Parameters.AddWithValue("$questionId", questionId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            answers.Add(
                new ClarificationCandidateAnswer(
                    reader.GetString(reader.GetOrdinal("id")),
                    reader.GetString(reader.GetOrdinal("question_id")),
                    reader.GetInt64(reader.GetOrdinal("observed_in_round_id")),
                    reader.GetString(reader.GetOrdinal("provider_reference_json")),
                    reader.GetString(reader.GetOrdinal("interpretation_summary")),
                    GetRequiredTimestamp(reader, "observed_at")
                )
            );
        }

        return answers;
    }

    public IReadOnlyList<ClarificationQuestion> ListOpenAskedClarificationQuestionsForEngagement(long engagementId)
    {
        var questions = new List<ClarificationQuestion>();
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT q.* FROM clarification_question q
            INNER JOIN engagement_round r ON r.id = q.engagement_round_id
            INNER JOIN review_action a
                ON a.engagement_round_id = q.engagement_round_id AND a.action_id = q.action_id
            WHERE r.pr_engagement_id = $engagementId
              AND q.state IN ('Open', 'Contested')
              AND a.status = 'Accepted'
              AND q.provider_receipt_json IS NOT NULL
              AND q.asked_at IS NOT NULL
            ORDER BY q.created_at, q.id;
            """;
        _ = command.Parameters.AddWithValue("$engagementId", engagementId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            questions.Add(MapClarificationQuestion(reader));
        }

        return questions;
    }

    public bool TryTransitionClarificationQuestion(
        string questionId,
        ClarificationQuestionState expected,
        ClarificationQuestionState next,
        DateTimeOffset transitionedAtUtc
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(questionId);
        if (!IsLegalQuestionTransition(expected, next))
        {
            return false;
        }

        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE clarification_question
            SET state = $next, updated_at = $at
            WHERE id = $id AND state = $expected;
            """;
        _ = command.Parameters.AddWithValue("$next", next.ToString());
        _ = command.Parameters.AddWithValue("$at", Utc(transitionedAtUtc));
        _ = command.Parameters.AddWithValue("$id", questionId);
        _ = command.Parameters.AddWithValue("$expected", expected.ToString());
        return command.ExecuteNonQuery() == 1;
    }

    public ClarificationCandidateAnswer AddClarificationCandidateAnswer(
        ClarificationCandidateAnswer candidate,
        IReadOnlyList<AuditSourceReference> sources
    )
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.QuestionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.ProviderReferenceJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.InterpretationSummary);

        using var gate = _gate.EnterScope();
        using var transaction = _connection.BeginTransaction();
        var question =
            GetClarificationQuestion(candidate.QuestionId, transaction)
            ?? throw new ArgumentException($"Clarification question '{candidate.QuestionId}' does not exist.");
        ValidateRoundBelongsToSameEngagement(candidate.ObservedInRoundId, question.EngagementRoundId, transaction);
        ValidateSourceReferences(candidate.ObservedInRoundId, sources, transaction);
        using (var insert = _connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO clarification_candidate_answer (
                    id, question_id, observed_in_round_id, provider_reference_json,
                    interpretation_summary, observed_at)
                VALUES ($id, $questionId, $observedInRoundId, $providerRef, $interpretation, $observedAt)
                ON CONFLICT (id) DO NOTHING;
                """;
            _ = insert.Parameters.AddWithValue("$id", candidate.Id);
            _ = insert.Parameters.AddWithValue("$questionId", candidate.QuestionId);
            _ = insert.Parameters.AddWithValue("$observedInRoundId", candidate.ObservedInRoundId);
            _ = insert.Parameters.AddWithValue("$providerRef", candidate.ProviderReferenceJson);
            _ = insert.Parameters.AddWithValue("$interpretation", candidate.InterpretationSummary);
            _ = insert.Parameters.AddWithValue("$observedAt", Utc(candidate.ObservedAtUtc));
            _ = insert.ExecuteNonQuery();
        }

        var persisted = GetClarificationCandidateAnswer(candidate.Id, transaction);
        if (persisted != candidate)
        {
            throw new InvalidOperationException(
                $"Clarification candidate answer '{candidate.Id}' conflicts with persisted metadata."
            );
        }

        InsertSourceReferences(
            "clarification_candidate_answer_source",
            ["candidate_answer_id"],
            [candidate.Id],
            sources,
            transaction
        );
        EnsureSourceReferencesMatch(
            "clarification_candidate_answer_source",
            "candidate_answer_id = $owner0",
            [candidate.Id],
            sources,
            transaction
        );
        transaction.Commit();
        return persisted;
    }

    public RoundObservation AppendRoundObservationOnce(
        string observationId,
        long engagementRoundId,
        RoundObservationDraft draft,
        DateTimeOffset observedAtUtc
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(observationId);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.Summary);

        using var gate = _gate.EnterScope();
        using var transaction = _connection.BeginTransaction();
        ValidateSourceReferences(engagementRoundId, draft.Evidence, transaction);
        using (var insert = _connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO round_observation (
                    id, engagement_round_id, sequence, kind, summary, observed_at)
                SELECT $id, $roundId, COALESCE(MAX(sequence), 0) + 1, $kind, $summary, $observedAt
                FROM round_observation
                WHERE engagement_round_id = $roundId
                ON CONFLICT (id) DO NOTHING;
                """;
            _ = insert.Parameters.AddWithValue("$id", observationId);
            _ = insert.Parameters.AddWithValue("$roundId", engagementRoundId);
            _ = insert.Parameters.AddWithValue("$kind", draft.Kind.ToString());
            _ = insert.Parameters.AddWithValue("$summary", draft.Summary);
            _ = insert.Parameters.AddWithValue("$observedAt", Utc(observedAtUtc));
            _ = insert.ExecuteNonQuery();
        }

        var persisted = GetRoundObservation(observationId, transaction);
        if (
            persisted.EngagementRoundId != engagementRoundId
            || persisted.Kind != draft.Kind
            || !string.Equals(persisted.Summary, draft.Summary, StringComparison.Ordinal)
            || persisted.ObservedAtUtc != observedAtUtc
        )
        {
            throw new InvalidOperationException(
                $"Round observation '{observationId}' conflicts with persisted metadata."
            );
        }

        InsertSourceReferences(
            "round_observation_source",
            ["observation_id"],
            [observationId],
            draft.Evidence,
            transaction
        );
        EnsureSourceReferencesMatch(
            "round_observation_source",
            "observation_id = $owner0",
            [observationId],
            draft.Evidence,
            transaction
        );
        transaction.Commit();
        return persisted;
    }

    public RoundObservation AppendRoundObservation(
        RoundObservation observation,
        IReadOnlyList<AuditSourceReference> sources
    )
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentException.ThrowIfNullOrWhiteSpace(observation.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(observation.Summary);
        ArgumentOutOfRangeException.ThrowIfNegative(observation.Sequence);

        using var gate = _gate.EnterScope();
        using var transaction = _connection.BeginTransaction();
        if (sources.Count > 0)
        {
            ValidateSourceReferences(observation.EngagementRoundId, sources, transaction);
        }

        using (var insert = _connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO round_observation (
                    id, engagement_round_id, sequence, kind, summary, observed_at)
                VALUES ($id, $roundId, $sequence, $kind, $summary, $observedAt)
                ON CONFLICT (id) DO NOTHING;
                """;
            _ = insert.Parameters.AddWithValue("$id", observation.Id);
            _ = insert.Parameters.AddWithValue("$roundId", observation.EngagementRoundId);
            _ = insert.Parameters.AddWithValue("$sequence", observation.Sequence);
            _ = insert.Parameters.AddWithValue("$kind", observation.Kind.ToString());
            _ = insert.Parameters.AddWithValue("$summary", observation.Summary);
            _ = insert.Parameters.AddWithValue("$observedAt", Utc(observation.ObservedAtUtc));
            _ = insert.ExecuteNonQuery();
        }

        var persisted = GetRoundObservation(observation.Id, transaction);
        if (persisted != observation)
        {
            throw new InvalidOperationException(
                $"Round observation '{observation.Id}' conflicts with persisted metadata."
            );
        }

        InsertSourceReferences("round_observation_source", ["observation_id"], [observation.Id], sources, transaction);
        EnsureSourceReferencesMatch(
            "round_observation_source",
            "observation_id = $owner0",
            [observation.Id],
            sources,
            transaction
        );
        transaction.Commit();
        return persisted;
    }

    /// <summary>Lists a round's append-only observations in their recorded order.</summary>
    public IReadOnlyList<RoundObservation> ListRoundObservations(long engagementRoundId)
    {
        var observations = new List<RoundObservation>();
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM round_observation
            WHERE engagement_round_id = $roundId
            ORDER BY sequence, id;
            """;
        _ = command.Parameters.AddWithValue("$roundId", engagementRoundId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            observations.Add(MapRoundObservation(reader));
        }

        return observations;
    }

    /// <summary>Reads the exact evidence an observation was recorded against.</summary>
    public IReadOnlyList<AuditSourceReference> ListRoundObservationSources(string observationId) =>
        ListSourceReferences("round_observation_source", "observation_id = $owner0", [observationId]);

    /// <summary>Reads the exact evidence a clarification question was recorded against.</summary>
    public IReadOnlyList<AuditSourceReference> ListClarificationQuestionSources(string questionId) =>
        ListSourceReferences("clarification_question_source", "question_id = $owner0", [questionId]);

    /// <summary>Reads the exact evidence a typed review action was recorded against.</summary>
    public IReadOnlyList<AuditSourceReference> ListReviewActionSources(long engagementRoundId, string actionId) =>
        ListSourceReferences(
            "review_action_source",
            "engagement_round_id = $owner0 AND action_id = $owner1",
            [engagementRoundId, actionId]
        );

    private IReadOnlyList<AuditSourceReference> ListSourceReferences(
        string table,
        string ownerPredicate,
        IReadOnlyList<object> ownerValues
    )
    {
        var references = new List<AuditSourceReference>();
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            SELECT source_record_id, source_content_sha256 FROM {table}
            WHERE {ownerPredicate}
            ORDER BY source_record_id;
            """;
        for (var index = 0; index < ownerValues.Count; index++)
        {
            _ = command.Parameters.AddWithValue($"$owner{index}", ownerValues[index]);
        }

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            references.Add(new AuditSourceReference(reader.GetString(0), reader.GetString(1)));
        }

        return references;
    }

    /// <summary>
    /// True when the exact bytes a citation names still exist in this engagement. A source whose
    /// content changed, or that was removed, no longer matches, which is what turns a definitive
    /// merged-close label back into <c>Indeterminate</c>.
    /// </summary>
    public bool AuditSourceMatchesEngagement(long engagementId, string sourceRecordId, string contentSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRecordId);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentSha256);
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM audit_source_record r
            INNER JOIN engagement_round e ON e.id = r.engagement_round_id
            WHERE r.id = $id AND r.content_sha256 = $sha256 AND e.pr_engagement_id = $engagementId;
            """;
        _ = command.Parameters.AddWithValue("$id", sourceRecordId);
        _ = command.Parameters.AddWithValue("$sha256", contentSha256);
        _ = command.Parameters.AddWithValue("$engagementId", engagementId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    /// <summary>
    /// Replaces the merged-close outcome rows for a round. Re-verification is expected to change
    /// labels, so the write is a full replace of that round's items and keeps the report a pure
    /// projection of the latest verification rather than an accumulation of stale rows.
    /// </summary>
    public void SaveCloseOutcomeItems(long engagementRoundId, IReadOnlyList<CloseOutcomeItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        using var gate = _gate.EnterScope();
        using var transaction = _connection.BeginTransaction();

        foreach (var table in (string[])["close_outcome_item_source", "close_outcome_item"])
        {
            using var delete = _connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM {table} WHERE engagement_round_id = $roundId;";
            _ = delete.Parameters.AddWithValue("$roundId", engagementRoundId);
            _ = delete.ExecuteNonQuery();
        }

        foreach (var item in items)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(item.CandidateId);
            ValidateSourceReferencesInEngagement(engagementRoundId, item.Sources, transaction);
            using (var insert = _connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO close_outcome_item (
                        engagement_round_id, candidate_id, kind, summary, proposed_label,
                        verified_label, verification_reason_code, observed_at)
                    VALUES (
                        $roundId, $candidateId, $kind, $summary, $proposed,
                        $verified, $reason, $observedAt);
                    """;
                _ = insert.Parameters.AddWithValue("$roundId", engagementRoundId);
                _ = insert.Parameters.AddWithValue("$candidateId", item.CandidateId);
                _ = insert.Parameters.AddWithValue("$kind", item.Kind.ToString());
                _ = insert.Parameters.AddWithValue("$summary", item.Summary);
                _ = insert.Parameters.AddWithValue("$proposed", (object?)item.ProposedLabel ?? DBNull.Value);
                _ = insert.Parameters.AddWithValue("$verified", item.VerifiedLabel.ToString());
                _ = insert.Parameters.AddWithValue("$reason", item.VerificationReasonCode);
                _ = insert.Parameters.AddWithValue("$observedAt", Utc(item.ObservedAtUtc));
                _ = insert.ExecuteNonQuery();
            }

            InsertSourceReferences(
                "close_outcome_item_source",
                ["engagement_round_id", "candidate_id"],
                [engagementRoundId, item.CandidateId],
                item.Sources,
                transaction
            );
        }

        transaction.Commit();
    }

    /// <summary>Reads a round's merged-close outcome rows in ordinal candidate order.</summary>
    public IReadOnlyList<CloseOutcomeItem> ListCloseOutcomeItems(long engagementRoundId)
    {
        var items = new List<(CloseOutcomeItem Item, string CandidateId)>();
        using (var gate = _gate.EnterScope())
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT * FROM close_outcome_item
                WHERE engagement_round_id = $roundId
                ORDER BY candidate_id;
                """;
            _ = command.Parameters.AddWithValue("$roundId", engagementRoundId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var candidateId = reader.GetString(reader.GetOrdinal("candidate_id"));
                items.Add(
                    (
                        new CloseOutcomeItem(
                            engagementRoundId,
                            candidateId,
                            Enum.Parse<CloseCandidateKind>(reader.GetString(reader.GetOrdinal("kind"))),
                            reader.GetString(reader.GetOrdinal("summary")),
                            reader.IsDBNull(reader.GetOrdinal("proposed_label"))
                                ? null
                                : reader.GetString(reader.GetOrdinal("proposed_label")),
                            Enum.Parse<CloseOutcomeLabel>(reader.GetString(reader.GetOrdinal("verified_label"))),
                            reader.GetString(reader.GetOrdinal("verification_reason_code")),
                            GetRequiredTimestamp(reader, "observed_at"),
                            []
                        ),
                        candidateId
                    )
                );
            }
        }

        return
        [
            .. items.Select(entry =>
                entry.Item with
                {
                    Sources = ListSourceReferences(
                        "close_outcome_item_source",
                        "engagement_round_id = $owner0 AND candidate_id = $owner1",
                        [engagementRoundId, entry.CandidateId]
                    ),
                }
            ),
        ];
    }

    /// <summary>
    /// Records the outcome of each promotion pass. The primary key is the source observation plus its
    /// destination, so replaying an identical promotion updates one row instead of contributing twice.
    /// </summary>
    public void SavePromotionOutcomes(long engagementRoundId, IReadOnlyList<PromotionOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        using var gate = _gate.EnterScope();
        using var transaction = _connection.BeginTransaction();
        var now = Utc(_timeProvider.GetUtcNow());
        foreach (var outcome in outcomes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(outcome.SourceObservationId);
            using var upsert = _connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO promotion_outcome (
                    engagement_round_id, source_observation_id, destination_kind, disposition,
                    destination_path, destination_content_sha256, reason_code, created_at, updated_at)
                VALUES (
                    $roundId, $sourceId, $destination, $disposition,
                    $path, $hash, $reason, $now, $now)
                ON CONFLICT (engagement_round_id, source_observation_id, destination_kind) DO UPDATE SET
                    disposition = excluded.disposition,
                    destination_path = excluded.destination_path,
                    destination_content_sha256 = excluded.destination_content_sha256,
                    reason_code = excluded.reason_code,
                    updated_at = excluded.updated_at;
                """;
            _ = upsert.Parameters.AddWithValue("$roundId", engagementRoundId);
            _ = upsert.Parameters.AddWithValue("$sourceId", outcome.SourceObservationId);
            _ = upsert.Parameters.AddWithValue("$destination", outcome.DestinationKind);
            _ = upsert.Parameters.AddWithValue("$disposition", outcome.Disposition.ToString());
            _ = upsert.Parameters.AddWithValue("$path", (object?)outcome.DestinationPath ?? DBNull.Value);
            _ = upsert.Parameters.AddWithValue("$hash", (object?)outcome.DestinationContentHash ?? DBNull.Value);
            _ = upsert.Parameters.AddWithValue("$reason", (object?)outcome.ReasonCode ?? DBNull.Value);
            _ = upsert.Parameters.AddWithValue("$now", now);
            _ = upsert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Reads a round's promotion outcomes, knowledge base before developer learnings.</summary>
    public IReadOnlyList<PromotionOutcome> ListPromotionOutcomes(long engagementRoundId)
    {
        var outcomes = new List<PromotionOutcome>();
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM promotion_outcome
            WHERE engagement_round_id = $roundId
            ORDER BY destination_kind DESC, source_observation_id;
            """;
        _ = command.Parameters.AddWithValue("$roundId", engagementRoundId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            outcomes.Add(
                new PromotionOutcome(
                    reader.GetString(reader.GetOrdinal("source_observation_id")),
                    reader.GetString(reader.GetOrdinal("destination_kind")),
                    Enum.Parse<PromotionDisposition>(reader.GetString(reader.GetOrdinal("disposition"))),
                    reader.IsDBNull(reader.GetOrdinal("destination_path"))
                        ? null
                        : reader.GetString(reader.GetOrdinal("destination_path")),
                    reader.IsDBNull(reader.GetOrdinal("destination_content_sha256"))
                        ? null
                        : reader.GetString(reader.GetOrdinal("destination_content_sha256")),
                    reader.IsDBNull(reader.GetOrdinal("reason_code"))
                        ? null
                        : reader.GetString(reader.GetOrdinal("reason_code"))
                )
            );
        }

        return outcomes;
    }

    /// <summary>
    /// Validates evidence for a close item. Close reasons over the whole engagement, so a citation may
    /// legitimately name a source captured in an earlier round of the same pull request.
    /// </summary>
    private void ValidateSourceReferencesInEngagement(
        long engagementRoundId,
        IReadOnlyList<AuditSourceReference> sources,
        SqliteTransaction transaction
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        foreach (var source in sources)
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT COUNT(*)
                FROM audit_source_record r
                INNER JOIN engagement_round e ON e.id = r.engagement_round_id
                WHERE r.id = $id
                  AND r.content_sha256 = $sha256
                  AND e.pr_engagement_id = (
                      SELECT pr_engagement_id FROM engagement_round WHERE id = $roundId);
                """;
            _ = command.Parameters.AddWithValue("$id", source.SourceRecordId);
            _ = command.Parameters.AddWithValue("$sha256", source.ContentSha256);
            _ = command.Parameters.AddWithValue("$roundId", engagementRoundId);
            if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
            {
                throw new ArgumentException(
                    $"Audit source '{source.SourceRecordId}' with the exact hash does not exist in this engagement.",
                    nameof(sources)
                );
            }
        }
    }

    private static bool ReviewActionIntentMatches(ReviewAction persisted, ReviewAction requested) =>
        persisted.EngagementRoundId == requested.EngagementRoundId
        && StringComparer.Ordinal.Equals(persisted.ActionId, requested.ActionId)
        && persisted.Kind == requested.Kind
        && StringComparer.Ordinal.Equals(persisted.PayloadSha256, requested.PayloadSha256)
        && StringComparer.Ordinal.Equals(persisted.ProviderTargetJson, requested.ProviderTargetJson);

    private static bool IsLegalReviewActionTransition(ReviewActionStatus expected, ReviewActionStatus next) =>
        (expected, next) switch
        {
            (ReviewActionStatus.Planned, ReviewActionStatus.CollectedOnly) => true,
            (ReviewActionStatus.Planned, ReviewActionStatus.Sending) => true,
            (ReviewActionStatus.Planned, ReviewActionStatus.Accepted) => true,
            (ReviewActionStatus.Planned, ReviewActionStatus.Rejected) => true,
            (ReviewActionStatus.Sending, ReviewActionStatus.Accepted) => true,
            (ReviewActionStatus.Sending, ReviewActionStatus.Rejected) => true,
            _ => false,
        };

    private static bool IsLegalQuestionTransition(
        ClarificationQuestionState expected,
        ClarificationQuestionState next
    ) =>
        (expected, next) switch
        {
            (ClarificationQuestionState.Open, ClarificationQuestionState.Answered) => true,
            (ClarificationQuestionState.Open, ClarificationQuestionState.Contested) => true,
            (ClarificationQuestionState.Open, ClarificationQuestionState.Superseded) => true,
            (ClarificationQuestionState.Open, ClarificationQuestionState.UnansweredAtMerge) => true,
            (ClarificationQuestionState.Answered, ClarificationQuestionState.Contested) => true,
            (ClarificationQuestionState.Answered, ClarificationQuestionState.Superseded) => true,
            (ClarificationQuestionState.Contested, ClarificationQuestionState.Answered) => true,
            (ClarificationQuestionState.Contested, ClarificationQuestionState.Superseded) => true,
            (ClarificationQuestionState.Contested, ClarificationQuestionState.UnansweredAtMerge) => true,
            _ => false,
        };

    private static void ValidateReviewAction(ReviewAction action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action.ActionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(action.PayloadSha256);
        if (action.Status == ReviewActionStatus.Accepted && string.IsNullOrWhiteSpace(action.ProviderReceiptJson))
        {
            throw new ArgumentException("An accepted review action requires a provider receipt.", nameof(action));
        }

        if (action.Status == ReviewActionStatus.Rejected && string.IsNullOrWhiteSpace(action.RejectionJson))
        {
            throw new ArgumentException("A rejected review action requires rejection details.", nameof(action));
        }
    }

    private static void ValidateClarificationQuestion(ClarificationQuestion question)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(question.Wording);
        ArgumentException.ThrowIfNullOrWhiteSpace(question.EvidenceSummary);
        ArgumentException.ThrowIfNullOrWhiteSpace(question.WithheldConclusion);
        if (question.Line is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(question), "A question line must be positive.");
        }

        if ((question.AskedAtUtc is null) != (question.ProviderReceiptJson is null))
        {
            throw new ArgumentException(
                "An asked question requires both its receipt and asked time.",
                nameof(question)
            );
        }
    }

    private void ValidateAskedQuestionAction(ClarificationQuestion question, SqliteTransaction transaction)
    {
        if (question.AskedAtUtc is null)
        {
            return;
        }

        var action = question.ActionId is { } actionId
            ? TryGetReviewAction(question.EngagementRoundId, actionId, transaction)
            : null;
        if (action?.Status != ReviewActionStatus.Accepted)
        {
            throw new ArgumentException(
                "An asked question requires a linked accepted review action.",
                nameof(question)
            );
        }
    }

    private void ValidateRoundBelongsToSameEngagement(
        long observedRoundId,
        long questionRoundId,
        SqliteTransaction transaction
    )
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM engagement_round observed
            INNER JOIN engagement_round question
                ON question.pr_engagement_id = observed.pr_engagement_id
            WHERE observed.id = $observedRoundId AND question.id = $questionRoundId;
            """;
        _ = command.Parameters.AddWithValue("$observedRoundId", observedRoundId);
        _ = command.Parameters.AddWithValue("$questionRoundId", questionRoundId);
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
        {
            throw new ArgumentException(
                $"Observed round {observedRoundId} and question round {questionRoundId} do not belong to the same engagement."
            );
        }
    }

    private void ValidateSourceReferences(
        long engagementRoundId,
        IReadOnlyList<AuditSourceReference> sources,
        SqliteTransaction transaction
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0)
        {
            throw new ArgumentException(
                "Structured evidence requires at least one exact source reference.",
                nameof(sources)
            );
        }

        if (sources.Select(source => source.SourceRecordId).Distinct(StringComparer.Ordinal).Count() != sources.Count)
        {
            throw new ArgumentException(
                "Structured evidence cannot repeat a source record reference.",
                nameof(sources)
            );
        }

        foreach (var source in sources)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(source.SourceRecordId);
            ArgumentException.ThrowIfNullOrWhiteSpace(source.ContentSha256);
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT COUNT(*) FROM audit_source_record
                WHERE id = $id AND content_sha256 = $sha256 AND engagement_round_id = $roundId;
                """;
            _ = command.Parameters.AddWithValue("$id", source.SourceRecordId);
            _ = command.Parameters.AddWithValue("$sha256", source.ContentSha256);
            _ = command.Parameters.AddWithValue("$roundId", engagementRoundId);
            if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
            {
                throw new ArgumentException(
                    $"Audit source '{source.SourceRecordId}' with the exact hash does not exist in round {engagementRoundId}.",
                    nameof(sources)
                );
            }
        }
    }

    private void InsertSourceReferences(
        string table,
        IReadOnlyList<string> ownerColumns,
        IReadOnlyList<object> ownerValues,
        IReadOnlyList<AuditSourceReference> sources,
        SqliteTransaction transaction
    )
    {
        var ownerNames = string.Join(", ", ownerColumns);
        var ownerParameters = string.Join(", ", ownerColumns.Select((_, index) => $"$owner{index}"));
        foreach (var source in sources)
        {
            using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = $"""
                INSERT INTO {table} (
                    {ownerNames}, source_record_id, source_content_sha256)
                VALUES (
                    {ownerParameters}, $sourceId, $sourceSha256)
                ON CONFLICT DO NOTHING;
                """;
            for (var index = 0; index < ownerValues.Count; index++)
            {
                _ = insert.Parameters.AddWithValue($"$owner{index}", ownerValues[index]);
            }

            _ = insert.Parameters.AddWithValue("$sourceId", source.SourceRecordId);
            _ = insert.Parameters.AddWithValue("$sourceSha256", source.ContentSha256);
            _ = insert.ExecuteNonQuery();
        }
    }

    private void EnsureSourceReferencesMatch(
        string table,
        string ownerPredicate,
        IReadOnlyList<object> ownerValues,
        IReadOnlyList<AuditSourceReference> requested,
        SqliteTransaction transaction
    )
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT source_record_id, source_content_sha256 FROM {table}
            WHERE {ownerPredicate}
            ORDER BY source_record_id;
            """;
        for (var index = 0; index < ownerValues.Count; index++)
        {
            _ = command.Parameters.AddWithValue($"$owner{index}", ownerValues[index]);
        }

        var persisted = new List<AuditSourceReference>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            persisted.Add(new AuditSourceReference(reader.GetString(0), reader.GetString(1)));
        }

        if (!persisted.SequenceEqual(requested.OrderBy(source => source.SourceRecordId, StringComparer.Ordinal)))
        {
            throw new InvalidOperationException("Structured evidence conflicts with its persisted source references.");
        }
    }

    private ReviewAction GetReviewAction(long roundId, string actionId, SqliteTransaction transaction) =>
        TryGetReviewAction(roundId, actionId, transaction)
        ?? throw new InvalidOperationException($"Review action '{actionId}' was not persisted.");

    private ReviewAction? TryGetReviewAction(long roundId, string actionId, SqliteTransaction? transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT * FROM review_action WHERE engagement_round_id = $roundId AND action_id = $actionId;
            """;
        _ = command.Parameters.AddWithValue("$roundId", roundId);
        _ = command.Parameters.AddWithValue("$actionId", actionId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapReviewAction(reader) : null;
    }

    private ClarificationQuestion? GetClarificationQuestion(string questionId, SqliteTransaction? transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM clarification_question WHERE id = $id;";
        _ = command.Parameters.AddWithValue("$id", questionId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapClarificationQuestion(reader) : null;
    }

    private ClarificationCandidateAnswer GetClarificationCandidateAnswer(
        string candidateId,
        SqliteTransaction transaction
    )
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM clarification_candidate_answer WHERE id = $id;";
        _ = command.Parameters.AddWithValue("$id", candidateId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new ClarificationCandidateAnswer(
                reader.GetString(reader.GetOrdinal("id")),
                reader.GetString(reader.GetOrdinal("question_id")),
                reader.GetInt64(reader.GetOrdinal("observed_in_round_id")),
                reader.GetString(reader.GetOrdinal("provider_reference_json")),
                reader.GetString(reader.GetOrdinal("interpretation_summary")),
                GetRequiredTimestamp(reader, "observed_at")
            )
            : throw new InvalidOperationException($"Clarification candidate answer '{candidateId}' was not persisted.");
    }

    private RoundObservation GetRoundObservation(string observationId, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM round_observation WHERE id = $id;";
        _ = command.Parameters.AddWithValue("$id", observationId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? MapRoundObservation(reader)
            : throw new InvalidOperationException($"Round observation '{observationId}' was not persisted.");
    }

    private static RoundObservation MapRoundObservation(SqliteDataReader reader) =>
        new(
            reader.GetString(reader.GetOrdinal("id")),
            reader.GetInt64(reader.GetOrdinal("engagement_round_id")),
            reader.GetInt64(reader.GetOrdinal("sequence")),
            Enum.Parse<ObservationKind>(reader.GetString(reader.GetOrdinal("kind"))),
            reader.GetString(reader.GetOrdinal("summary")),
            GetRequiredTimestamp(reader, "observed_at")
        );

    private static void AddReviewActionParameters(SqliteCommand command, ReviewAction action)
    {
        _ = command.Parameters.AddWithValue("$roundId", action.EngagementRoundId);
        _ = command.Parameters.AddWithValue("$actionId", action.ActionId);
        _ = command.Parameters.AddWithValue("$kind", action.Kind.ToString());
        _ = command.Parameters.AddWithValue("$status", action.Status.ToString());
        _ = command.Parameters.AddWithValue("$payloadSha256", action.PayloadSha256);
        _ = command.Parameters.AddWithValue("$target", (object?)action.ProviderTargetJson ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$receipt", (object?)action.ProviderReceiptJson ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$rejection", (object?)action.RejectionJson ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$createdAt", Utc(action.CreatedAtUtc));
        _ = command.Parameters.AddWithValue("$updatedAt", Utc(action.UpdatedAtUtc));
    }

    private static void AddClarificationQuestionParameters(SqliteCommand command, ClarificationQuestion question)
    {
        _ = command.Parameters.AddWithValue("$id", question.Id);
        _ = command.Parameters.AddWithValue("$roundId", question.EngagementRoundId);
        _ = command.Parameters.AddWithValue("$wording", question.Wording);
        _ = command.Parameters.AddWithValue("$target", (object?)question.ProviderTargetJson ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$file", (object?)question.FilePath ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$line", (object?)question.Line ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$evidence", question.EvidenceSummary);
        _ = command.Parameters.AddWithValue("$withheld", question.WithheldConclusion);
        _ = command.Parameters.AddWithValue("$actionId", (object?)question.ActionId ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$receipt", (object?)question.ProviderReceiptJson ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$askedAt", question.AskedAtUtc is { } asked ? Utc(asked) : DBNull.Value);
        _ = command.Parameters.AddWithValue("$state", question.State.ToString());
        _ = command.Parameters.AddWithValue("$createdAt", Utc(question.CreatedAtUtc));
        _ = command.Parameters.AddWithValue("$updatedAt", Utc(question.UpdatedAtUtc));
    }

    private static ReviewAction MapReviewAction(SqliteDataReader reader) =>
        new(
            reader.GetInt64(reader.GetOrdinal("engagement_round_id")),
            reader.GetString(reader.GetOrdinal("action_id")),
            Enum.Parse<ReviewActionKind>(reader.GetString(reader.GetOrdinal("kind"))),
            Enum.Parse<ReviewActionStatus>(reader.GetString(reader.GetOrdinal("status"))),
            reader.GetString(reader.GetOrdinal("payload_sha256")),
            GetNullableString(reader, "provider_target_json"),
            GetNullableString(reader, "provider_receipt_json"),
            GetNullableString(reader, "rejection_json"),
            GetRequiredTimestamp(reader, "created_at"),
            GetRequiredTimestamp(reader, "updated_at")
        );

    private static ClarificationQuestion MapClarificationQuestion(SqliteDataReader reader) =>
        new(
            reader.GetString(reader.GetOrdinal("id")),
            reader.GetInt64(reader.GetOrdinal("engagement_round_id")),
            reader.GetString(reader.GetOrdinal("wording")),
            GetNullableString(reader, "provider_target_json"),
            GetNullableString(reader, "file_path"),
            GetNullableInt32(reader, "line"),
            reader.GetString(reader.GetOrdinal("evidence_summary")),
            reader.GetString(reader.GetOrdinal("withheld_conclusion")),
            GetNullableString(reader, "action_id"),
            GetNullableString(reader, "provider_receipt_json"),
            GetNullableTimestamp(reader, "asked_at"),
            Enum.Parse<ClarificationQuestionState>(reader.GetString(reader.GetOrdinal("state"))),
            GetRequiredTimestamp(reader, "created_at"),
            GetRequiredTimestamp(reader, "updated_at")
        );

    private static void ValidateAuditRecord(AuditSourceRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(record.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.ThreadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.GenerationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.RecordType);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.ContentSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.SourceContentSha256);
        ArgumentOutOfRangeException.ThrowIfNegative(record.Sequence);
        ArgumentOutOfRangeException.ThrowIfNegative(record.ByteCount);
        ArgumentOutOfRangeException.ThrowIfNegative(record.SourceByteCount);
        if (record.CaptureOutcome == AuditSourceCaptureOutcome.Complete)
        {
            if (record.GapReasonCode is not null || record.CompletedAtUtc is not null)
            {
                throw new ArgumentException(
                    "A Complete audit source record starts incomplete and has no gap reason.",
                    nameof(record)
                );
            }
        }
        else if (
            record.ByteCount != 0
            || !StringComparer.Ordinal.Equals(record.ContentSha256, Sha256([]))
            || string.IsNullOrWhiteSpace(record.GapReasonCode)
            || record.CompletedAtUtc is null
        )
        {
            throw new ArgumentException(
                "A Gap or Redacted audit source record addresses empty content and has a reason code.",
                nameof(record)
            );
        }
    }

    private static bool AuditRecordMetadataMatches(AuditSourceRecord persisted, AuditSourceRecord requested) =>
        requested.CompletedAtUtc is null
            ? persisted with { CompletedAtUtc = null } == requested
            : persisted == requested;

    private static (long EngagementId, long RoundId) ParseAuditScope(ModelTurnAuditRecord source)
    {
        if (
            !long.TryParse(
                source.Scope.EngagementId,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var engagementId
            )
            || engagementId <= 0
            || !long.TryParse(source.Scope.RoundId, NumberStyles.None, CultureInfo.InvariantCulture, out var roundId)
            || roundId <= 0
        )
        {
            throw new ArgumentException(
                "The audit record engagement and round scope must identify a persisted engagement round.",
                nameof(source)
            );
        }

        return (engagementId, roundId);
    }

    private static AuditSourceRecord ToAuditSourceRecord(ModelTurnAuditRecord source, long roundId) =>
        new(
            source.RecordId,
            roundId,
            source.ThreadId,
            source.RunId,
            source.GenerationId,
            source.ParentTurnId,
            source.Sequence,
            source.RecordType,
            source.Role,
            source.ModelId,
            source.ProviderId,
            source.ContentSha256,
            source.ByteCount,
            source.ContentSha256,
            source.ByteCount,
            source.Outcome switch
            {
                AuditCaptureOutcome.Complete => AuditSourceCaptureOutcome.Complete,
                AuditCaptureOutcome.Gap => AuditSourceCaptureOutcome.Gap,
                AuditCaptureOutcome.Redacted => AuditSourceCaptureOutcome.Redacted,
                _ => throw new ArgumentOutOfRangeException(nameof(source), "Unknown audit capture outcome."),
            },
            source.GapReason,
            source.CapturedAtUtc,
            source.Outcome == AuditCaptureOutcome.Complete ? null : source.CapturedAtUtc
        );

    private void ValidateAuditScope(long engagementId, long roundId, SqliteTransaction transaction)
    {
        using var scope = _connection.CreateCommand();
        scope.Transaction = transaction;
        scope.CommandText = """
            SELECT COUNT(*) FROM engagement_round
            WHERE id = $roundId AND pr_engagement_id = $engagementId;
            """;
        _ = scope.Parameters.AddWithValue("$roundId", roundId);
        _ = scope.Parameters.AddWithValue("$engagementId", engagementId);
        if (Convert.ToInt64(scope.ExecuteScalar()) != 1)
        {
            throw new ArgumentException("The audit record engagement and round scope do not match.");
        }
    }

    private void InsertAuditRecord(AuditSourceRecord record, SqliteTransaction transaction)
    {
        ValidateAuditRecord(record);
        using var insert = _connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO audit_source_record (
                id, engagement_round_id, thread_id, run_id, generation_id, parent_turn_id,
                sequence, record_type, role, model_id, provider_id, content_sha256, byte_count,
                source_content_sha256, source_byte_count, capture_outcome, gap_reason_code, captured_at, completed_at)
            VALUES (
                $id, $roundId, $threadId, $runId, $generationId, $parentTurnId,
                $sequence, $recordType, $role, $modelId, $providerId, $contentSha256, $byteCount,
                $sourceContentSha256, $sourceByteCount, $outcome, $gapReason, $capturedAt, $completedAt)
            ON CONFLICT (id) DO NOTHING;
            """;
        AddAuditRecordParameters(insert, record);
        _ = insert.ExecuteNonQuery();
    }

    private void StoreAuditChunks(string recordId, byte[] content, SqliteTransaction transaction)
    {
        for (var offset = 0; offset < content.Length; offset += AuditChunkBytes)
        {
            var count = Math.Min(AuditChunkBytes, content.Length - offset);
            var chunk = content.AsSpan(offset, count).ToArray();
            var sha256 = Sha256(chunk);
            InsertAuditBlob(sha256, chunk, transaction);
            InsertSourceChunk(recordId, offset / AuditChunkBytes, offset, sha256, chunk.LongLength, transaction);
        }
    }

    private void CompleteAuditRecord(
        AuditSourceRecord record,
        DateTimeOffset completedAtUtc,
        SqliteTransaction transaction
    )
    {
        var content = ReadAuditBytes(record.Id, "audit_source_chunk", "source_record_id", transaction);
        if (
            content.LongLength != record.ByteCount
            || !StringComparer.Ordinal.Equals(Sha256(content), record.ContentSha256)
        )
        {
            throw new InvalidOperationException(
                $"Audit source record '{record.Id}' content does not match its declared hash and length."
            );
        }

        using var update = _connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE audit_source_record
            SET completed_at = $completedAt
            WHERE id = $id AND completed_at IS NULL;
            """;
        _ = update.Parameters.AddWithValue("$completedAt", Utc(completedAtUtc));
        _ = update.Parameters.AddWithValue("$id", record.Id);
        if (update.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException($"Audit source record '{record.Id}' could not be completed.");
        }
    }

    private static void AddAuditRecordParameters(SqliteCommand command, AuditSourceRecord record)
    {
        _ = command.Parameters.AddWithValue("$id", record.Id);
        _ = command.Parameters.AddWithValue("$roundId", record.EngagementRoundId);
        _ = command.Parameters.AddWithValue("$threadId", record.ThreadId);
        _ = command.Parameters.AddWithValue("$runId", record.RunId);
        _ = command.Parameters.AddWithValue("$generationId", record.GenerationId);
        _ = command.Parameters.AddWithValue("$parentTurnId", (object?)record.ParentTurnId ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$sequence", record.Sequence);
        _ = command.Parameters.AddWithValue("$recordType", record.RecordType);
        _ = command.Parameters.AddWithValue("$role", (object?)record.Role ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$modelId", (object?)record.ModelId ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$providerId", (object?)record.ProviderId ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$contentSha256", record.ContentSha256);
        _ = command.Parameters.AddWithValue("$byteCount", record.ByteCount);
        _ = command.Parameters.AddWithValue("$sourceContentSha256", record.SourceContentSha256);
        _ = command.Parameters.AddWithValue("$sourceByteCount", record.SourceByteCount);
        _ = command.Parameters.AddWithValue("$outcome", record.CaptureOutcome.ToString());
        _ = command.Parameters.AddWithValue("$gapReason", (object?)record.GapReasonCode ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$capturedAt", Utc(record.CapturedAtUtc));
        _ = command.Parameters.AddWithValue(
            "$completedAt",
            record.CompletedAtUtc is { } completed ? Utc(completed) : DBNull.Value
        );
    }

    private AuditSourceRecord GetAuditRecord(string sourceRecordId, SqliteTransaction? transaction) =>
        TryGetAuditRecord(sourceRecordId, transaction)
        ?? throw new ArgumentOutOfRangeException(
            nameof(sourceRecordId),
            $"Audit source record '{sourceRecordId}' does not exist."
        );

    private AuditSourceRecord? TryGetAuditRecord(string sourceRecordId, SqliteTransaction? transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM audit_source_record WHERE id = $id;";
        _ = command.Parameters.AddWithValue("$id", sourceRecordId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapAuditSourceRecord(reader) : null;
    }

    private AuditRedactionRecord GetAuditRedaction(string redactionRecordId, SqliteTransaction? transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM audit_redaction_record WHERE id = $id;";
        _ = command.Parameters.AddWithValue("$id", redactionRecordId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new ArgumentOutOfRangeException(
                nameof(redactionRecordId),
                $"Audit redaction '{redactionRecordId}' does not exist."
            );
        }

        return MapAuditRedactionRecord(reader);
    }

    private void InsertAuditBlob(string sha256, byte[] content, SqliteTransaction transaction)
    {
        using var insert = _connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO audit_blob (sha256, byte_count, content)
            VALUES ($sha256, $byteCount, $content)
            ON CONFLICT (sha256) DO NOTHING;
            """;
        _ = insert.Parameters.AddWithValue("$sha256", sha256);
        _ = insert.Parameters.AddWithValue("$byteCount", content.LongLength);
        _ = insert.Parameters.AddWithValue("$content", content);
        _ = insert.ExecuteNonQuery();

        using var verify = _connection.CreateCommand();
        verify.Transaction = transaction;
        verify.CommandText = "SELECT byte_count, content FROM audit_blob WHERE sha256 = $sha256;";
        _ = verify.Parameters.AddWithValue("$sha256", sha256);
        using var reader = verify.ExecuteReader();
        if (
            !reader.Read()
            || reader.GetInt64(0) != content.LongLength
            || !reader.GetFieldValue<byte[]>(1).AsSpan().SequenceEqual(content)
        )
        {
            throw new InvalidOperationException($"Audit blob '{sha256}' conflicts with persisted bytes.");
        }
    }

    private void InsertSourceChunk(
        string sourceRecordId,
        int chunkIndex,
        long byteOffset,
        string sha256,
        long byteCount,
        SqliteTransaction transaction
    )
    {
        using (var insert = _connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO audit_source_chunk (source_record_id, chunk_index, blob_sha256, byte_offset, byte_count)
                VALUES ($recordId, $chunkIndex, $sha256, $byteOffset, $byteCount)
                ON CONFLICT (source_record_id, chunk_index) DO NOTHING;
                """;
            _ = insert.Parameters.AddWithValue("$recordId", sourceRecordId);
            _ = insert.Parameters.AddWithValue("$chunkIndex", chunkIndex);
            _ = insert.Parameters.AddWithValue("$sha256", sha256);
            _ = insert.Parameters.AddWithValue("$byteOffset", byteOffset);
            _ = insert.Parameters.AddWithValue("$byteCount", byteCount);
            _ = insert.ExecuteNonQuery();
        }

        VerifyAuditChunk(
            "audit_source_chunk",
            "source_record_id",
            sourceRecordId,
            chunkIndex,
            byteOffset,
            sha256,
            byteCount,
            transaction
        );
    }

    private void InsertRedactionChunk(
        string redactionId,
        int chunkIndex,
        long byteOffset,
        string sha256,
        long byteCount,
        SqliteTransaction transaction
    )
    {
        using (var insert = _connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO audit_redaction_chunk (
                    redaction_record_id, chunk_index, blob_sha256, byte_offset, byte_count)
                VALUES ($recordId, $chunkIndex, $sha256, $byteOffset, $byteCount)
                ON CONFLICT (redaction_record_id, chunk_index) DO NOTHING;
                """;
            _ = insert.Parameters.AddWithValue("$recordId", redactionId);
            _ = insert.Parameters.AddWithValue("$chunkIndex", chunkIndex);
            _ = insert.Parameters.AddWithValue("$sha256", sha256);
            _ = insert.Parameters.AddWithValue("$byteOffset", byteOffset);
            _ = insert.Parameters.AddWithValue("$byteCount", byteCount);
            _ = insert.ExecuteNonQuery();
        }

        VerifyAuditChunk(
            "audit_redaction_chunk",
            "redaction_record_id",
            redactionId,
            chunkIndex,
            byteOffset,
            sha256,
            byteCount,
            transaction
        );
    }

    private void VerifyAuditChunk(
        string chunkTable,
        string recordColumn,
        string recordId,
        int chunkIndex,
        long byteOffset,
        string sha256,
        long byteCount,
        SqliteTransaction transaction
    )
    {
        using var verify = _connection.CreateCommand();
        verify.Transaction = transaction;
        verify.CommandText = $"""
            SELECT blob_sha256, byte_offset, byte_count
            FROM {chunkTable}
            WHERE {recordColumn} = $recordId AND chunk_index = $chunkIndex;
            """;
        _ = verify.Parameters.AddWithValue("$recordId", recordId);
        _ = verify.Parameters.AddWithValue("$chunkIndex", chunkIndex);
        using var reader = verify.ExecuteReader();
        if (
            !reader.Read()
            || !StringComparer.Ordinal.Equals(reader.GetString(0), sha256)
            || reader.GetInt64(1) != byteOffset
            || reader.GetInt64(2) != byteCount
        )
        {
            throw new InvalidOperationException(
                $"Audit chunk {chunkIndex} for record '{recordId}' conflicts with persisted bytes."
            );
        }
    }

    private byte[] ReadAuditBytes(
        string recordId,
        string chunkTable,
        string recordColumn,
        SqliteTransaction? transaction
    )
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT c.chunk_index, c.byte_offset, c.byte_count, b.content
            FROM {chunkTable} c
            JOIN audit_blob b ON b.sha256 = c.blob_sha256
            WHERE c.{recordColumn} = $recordId
            ORDER BY c.chunk_index ASC;
            """;
        _ = command.Parameters.AddWithValue("$recordId", recordId);
        using var reader = command.ExecuteReader();
        using var result = new MemoryStream();
        var expectedIndex = 0;
        long expectedOffset = 0;
        while (reader.Read())
        {
            var index = reader.GetInt32(0);
            var offset = reader.GetInt64(1);
            var byteCount = reader.GetInt64(2);
            var content = reader.GetFieldValue<byte[]>(3);
            if (index != expectedIndex || offset != expectedOffset || content.LongLength != byteCount)
            {
                throw new InvalidOperationException(
                    $"Audit content '{recordId}' has a missing, overlapping, or malformed chunk."
                );
            }

            result.Write(content);
            expectedIndex++;
            expectedOffset += byteCount;
        }

        return result.ToArray();
    }

    private static string Sha256(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    /// <summary>
    /// Finds the <c>review_run</c> for a reviewed commit's identity — <c>(repo, pr, head, base, kind,
    /// variant)</c>, mode/watermark-agnostic (see <see cref="CreateOrGetReviewRun"/>). When more than one
    /// row exists for the identity, the furthest-progressed one wins so a completed review is not rerun.
    /// </summary>
    private ReviewRun? FindReviewRunByIdentity(ReviewRun run)
    {
        using var gate = _gate.EnterScope();
        using var select = _connection.CreateCommand();
        select.CommandText = """
            SELECT * FROM review_run
            WHERE repo_id = $repoId AND pr_id = $prId AND head_sha = $head AND base_sha = $base
              AND review_kind = $kind AND variant_id = $variant
            ORDER BY CASE stage
                       WHEN 'Posted' THEN 4
                       WHEN 'Judged' THEN 3
                       WHEN 'Reviewed' THEN 2
                       WHEN 'ContextReady' THEN 1
                       ELSE 0
                     END DESC,
                     id DESC
            LIMIT 1;
            """;
        _ = select.Parameters.AddWithValue("$repoId", run.RepoId);
        _ = select.Parameters.AddWithValue("$prId", run.PrId);
        _ = select.Parameters.AddWithValue("$head", run.HeadSha);
        _ = select.Parameters.AddWithValue("$base", run.BaseSha);
        _ = select.Parameters.AddWithValue("$kind", run.ReviewKind);
        _ = select.Parameters.AddWithValue("$variant", run.VariantId);
        using var reader = select.ExecuteReader();
        return reader.Read() ? MapReviewRun(reader) : null;
    }

    /// <summary>
    /// Returns completed code-review rounds for one engagement through the caller's frozen prior-round
    /// boundary, newest first. The head filter keeps discussion context tied to the exact unchanged commit.
    /// </summary>
    public IReadOnlyList<ReviewRun> ListPriorCompletedCodeReviewRuns(
        long engagementId,
        string headSha,
        long priorRoundBoundary
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headSha);
        ArgumentOutOfRangeException.ThrowIfNegative(priorRoundBoundary);

        var results = new List<ReviewRun>();
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT r.*
            FROM engagement_round er
            JOIN review_run r ON r.id = er.review_run_id
            WHERE er.pr_engagement_id = $engagementId
              AND er.intent = 'CodeReview'
              AND er.status = 'Completed'
              AND er.id <= $boundary
              AND er.head_sha = $head
              AND r.engagement_round_id = er.id
            ORDER BY er.id DESC;
            """;
        _ = command.Parameters.AddWithValue("$engagementId", engagementId);
        _ = command.Parameters.AddWithValue("$boundary", priorRoundBoundary);
        _ = command.Parameters.AddWithValue("$head", headSha);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(MapReviewRun(reader));
        }

        return results;
    }

    public ReviewRun? GetReviewRun(long id)
    {
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT * FROM review_run WHERE id = $id;";
        _ = command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapReviewRun(reader) : null;
    }

    /// <summary>Returns the newest primary run that produced review output for one PR.</summary>
    public ReviewRun? GetLatestReviewedRun(long repoId, string prId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prId);
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM review_run
            WHERE repo_id = $repoId AND pr_id = $prId AND variant_id = $variant
              AND stage IN ('Reviewed', 'Judged', 'Posted')
            ORDER BY id DESC LIMIT 1;
            """;
        _ = command.Parameters.AddWithValue("$repoId", repoId);
        _ = command.Parameters.AddWithValue("$prId", prId);
        _ = command.Parameters.AddWithValue("$variant", PrimaryVariantId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapReviewRun(reader) : null;
    }

    /// <summary>
    /// Lists review runs that reached at least <paramref name="minimumStage"/>, oldest first, for
    /// corpus assembly by the eval runner.
    /// <para>
    /// Oldest first and id-ordered rather than newest first: a corpus snapshot is identified by a
    /// content hash over its items, and a listing whose order flipped as new reviews landed would
    /// make the same set of rows read as a different corpus on every call. <c>id</c> is the only
    /// monotone key on the table — <c>created_at</c> is text and two runs can share a timestamp.
    /// </para>
    /// </summary>
    /// <param name="minimumStage">
    /// Least stage a run must have reached. A run that has not reached
    /// <see cref="ReviewStage.Reviewed"/> has no review text to pair with its input, so including
    /// it would put an item in the corpus that no candidate can be built from.
    /// </param>
    /// <param name="limit">Most rows to return.</param>
    /// <param name="afterId">
    /// Exclusive lower bound on <c>id</c>. Zero (the default) starts at the beginning of the table.
    /// <para>
    /// It exists because <c>ORDER BY id LIMIT n</c> takes the <b>oldest</b> n rows, so once the
    /// store holds more than the limit every later call returns the same set forever and no review
    /// recorded after that point can enter a corpus. Flipping to <c>DESC</c> has the opposite
    /// failure — the set drifts on every call and nothing is ever comparable — so the caller states
    /// its window instead, and the reader above says out loud when the limit cut that window short.
    /// </para>
    /// </param>
    public IReadOnlyList<ReviewRun> ListReviewRuns(ReviewStage minimumStage, int limit, long afterId = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        ArgumentOutOfRangeException.ThrowIfNegative(afterId);

        // Stage is persisted as its enum NAME, so SQL cannot order it. The ordinal comparison is
        // done here, where the enum's own ordering is available, rather than by hardcoding a name
        // list into the query that a new stage would silently fall out of.
        var eligible = Enum.GetValues<ReviewStage>().Where(s => s >= minimumStage).Select(s => s.ToString()).ToList();

        var placeholders = string.Join(',', eligible.Select((_, i) => $"$stage{i}"));

        var results = new List<ReviewRun>();
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText =
            $"SELECT * FROM review_run WHERE stage IN ({placeholders}) AND id > $afterId "
            + "ORDER BY id LIMIT $limit;";
        _ = command.Parameters.AddWithValue("$afterId", afterId);
        for (var i = 0; i < eligible.Count; i++)
        {
            _ = command.Parameters.AddWithValue($"$stage{i}", eligible[i]);
        }

        _ = command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(MapReviewRun(reader));
        }

        return results;
    }

    /// <summary>
    /// Records the prompt-template digest the review was actually DISPATCHED under.
    /// <c>prompt_template_hash</c> has existed since v1 of the schema and, until this method, nothing wrote
    /// it — every row carried NULL, so no prompt change the daemon shipped could be attributed to the reviews
    /// it altered, and the eval corpus that already ships the column (<c>DaemonCorpusReader</c>) shipped an
    /// empty string for every candidate.
    /// <para>
    /// Written at dispatch rather than at creation, because creation is the wrong moment for it. The INSERT
    /// in <see cref="CreateOrGetReviewRun"/> runs in the POLLER, at discovery, before any prompt is rendered;
    /// and on an identity match that method returns the existing row untouched, so a run discovered under one
    /// prompt and dispatched under another after a deploy — the ordinary fate of everything left in
    /// <see cref="WorkflowStatus.RetryPending"/> — would keep the first build's hash and be filed under a
    /// prompt it never ran. Last dispatch wins, because the last dispatch is what produced the review of
    /// record.
    /// </para>
    /// <para>
    /// A null argument leaves the column alone (COALESCE on the parameter), so a caller that cannot establish
    /// the hash says so by passing null rather than by erasing what a previous dispatch knew.
    /// </para>
    /// </summary>
    public void RecordRunProvenance(long id, string? promptTemplateHash)
    {
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE review_run
            SET prompt_template_hash = COALESCE($promptHash, prompt_template_hash),
                updated_at = $now
            WHERE id = $id;
            """;
        _ = command.Parameters.AddWithValue("$promptHash", (object?)promptTemplateHash ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$now", UtcNow());
        _ = command.Parameters.AddWithValue("$id", id);
        _ = command.ExecuteNonQuery();
    }

    /// <summary>Advances the three resume axes for a run (orchestrator step completion).</summary>
    public void UpdateReviewRunState(
        long id,
        ReviewStage stage,
        WorkflowStatus workflowStatus,
        PrLifecycleState prLifecycleState
    )
    {
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE review_run
            SET stage = $stage, workflow_status = $workflow, pr_lifecycle_state = $prState, updated_at = $now
            WHERE id = $id;
            """;
        _ = command.Parameters.AddWithValue("$stage", stage.ToString());
        _ = command.Parameters.AddWithValue("$workflow", workflowStatus.ToString());
        _ = command.Parameters.AddWithValue("$prState", prLifecycleState.ToString());
        _ = command.Parameters.AddWithValue("$now", UtcNow());
        _ = command.Parameters.AddWithValue("$id", id);
        _ = command.ExecuteNonQuery();
    }

    /// <summary>
    /// Charges one STUCK failure (<c>PrOrchestrator.IsGovernedFailure</c>) against the run's durable retry
    /// budget and returns the new total.
    /// </summary>
    /// <remarks>
    /// The budget is durable because the in-memory one could not be: the stranded-run reconciler resumes a
    /// stuck run every ~45 minutes through <see cref="Orchestration.PrOrchestrator.ReconcileAsync"/>, which
    /// resets <see cref="Orchestration.RetryGovernor"/> for that run, so its count never reached the bound.
    /// Nothing on the resume path may touch this column — only
    /// <see cref="ClearGovernedFailureCount"/>, and only on a governed stage actually succeeding.
    /// <para>
    /// It deliberately does NOT stamp <c>updated_at</c>. That column is the liveness signal the stranded-run
    /// listings read as "somebody is working on this", and it is owned by
    /// <see cref="UpdateReviewRunState"/>, which the orchestrator's stage catch has already called by the
    /// time this runs. Advancing it a second time here would say nothing new and would couple the budget to
    /// the staleness windows.
    /// </para>
    /// <para>
    /// The read-back rides on the UPDATE's own <c>RETURNING</c> clause, so the increment and the value it
    /// produced are one statement — a separate SELECT could observe a different total if a concurrent
    /// review charged the same run between the two.
    /// </para>
    /// </remarks>
    public int IncrementGovernedFailureCount(long runId)
    {
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE review_run SET governed_failure_count = governed_failure_count + 1
            WHERE id = $id
            RETURNING governed_failure_count;
            """;
        _ = command.Parameters.AddWithValue("$id", runId);
        var value = command.ExecuteScalar();
        return value is null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Un-charges the run's durable budget after a governed stage succeeded.
    /// </summary>
    /// <remarks>
    /// The mirror of <see cref="IncrementGovernedFailureCount"/>, and it has to exist for the same reason
    /// the in-memory governor clears on success: a run that failed persistently and then RECOVERED would
    /// otherwise carry those failures toward a park it no longer deserves — one bad afternoon followed by a
    /// healthy week would still end in a permanent park.
    /// </remarks>
    public void ClearGovernedFailureCount(long runId)
    {
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE review_run SET governed_failure_count = 0 WHERE id = $id;";
        _ = command.Parameters.AddWithValue("$id", runId);
        _ = command.ExecuteNonQuery();
    }

    /// <summary>
    /// Parks <paramref name="runId"/> permanently: nothing will attempt it again until a new commit opens a
    /// new run. Returns whether THIS call is the one that parked it.
    /// </summary>
    /// <remarks>
    /// <c>WHERE ... AND parked_at IS NULL</c> makes parking once-only, and the return value is how a caller
    /// tells "I parked it" from "it was already parked". That distinction is load bearing: the park notice
    /// posted to the pull request hangs off it, so a second park attempt cannot post a second comment.
    /// <para>
    /// This is the first and only writer of <see cref="WorkflowStatus.Failed"/> in the daemon. The status is
    /// for operators reading the row; the RE-PICK exclusion is <c>parked_at</c> alone, because
    /// <see cref="ListStrandedRuns"/> selects <c>workflow_status &lt;&gt; 'Completed'</c> and a
    /// <c>Failed</c> row still satisfies that.
    /// </para>
    /// </remarks>
    public bool TryMarkReviewRunParked(long runId, DateTimeOffset parkedAt, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE review_run
            SET parked_at = $at, park_reason = $reason, workflow_status = $status
            WHERE id = $id AND parked_at IS NULL;
            """;
        _ = command.Parameters.AddWithValue("$at", Utc(parkedAt));
        _ = command.Parameters.AddWithValue("$reason", reason);
        _ = command.Parameters.AddWithValue("$status", WorkflowStatus.Failed.ToString());
        _ = command.Parameters.AddWithValue("$id", runId);
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>
    /// Records that <paramref name="runId"/>'s review is proven to be on the PR, at
    /// <paramref name="postedAtUtc"/>. This is the durable half of the delta-review cutoff (#225 item 1).
    /// </summary>
    /// <remarks>
    /// Call this ONLY on proven delivery. The value's whole worth is that a later review can trust "anything
    /// after this is new", and a stamp written on an attempt would move the cutoff past comments that arrived
    /// while the post was failing — hiding exactly the discussion the next review is required to answer. It is
    /// deliberately not part of <see cref="UpdateReviewRunState"/>: that method drives the resume axes and
    /// refreshes <c>updated_at</c>, which the stranded-run reconciler reads as "somebody is working on this".
    /// The instant is a parameter rather than read from the clock here so the caller that knows delivery
    /// happened also decides when, and so a test can seed a cutoff without waiting for one.
    /// <para>
    /// FIRST WRITE WINS — hence <c>AND last_posted_review_at IS NULL</c>. Proven delivery is not a
    /// once-only observation: the Posted stage is retried after any terminal failure, and on that retry
    /// <c>ReviewPoster</c> replays the outbox row, which still proves a comment is on the PR. So the
    /// caller can truthfully report proven delivery many times over for ONE posted comment, each time holding a
    /// fresh clock reading. Taking the latest would walk the cutoff forward across the whole outage and bury
    /// every comment left during it. A run posts at most once — its identity includes <c>head_sha</c>, so a new
    /// push opens a new row, and within a row the outbox guarantees exactly-once — therefore any write after
    /// the first is necessarily a re-observation of the same comment, and the first reading is the closest one
    /// to when it was actually published.
    /// </para>
    /// <para>
    /// The stamp is the daemon's clock, the comments it is later compared against carry the provider's, and
    /// the two are not the same clock. The asymmetry is deliberate rather than merely tolerated: a daemon
    /// running BEHIND the provider is harmless — the cutoff falls early and at worst re-reads a comment it has
    /// already answered — while a daemon running AHEAD can bury a comment published within the skew. That is
    /// the direction that loses discussion, so if this is ever tightened, tighten it by moving the stamp
    /// earlier (the provider's own published-at for the posted comment), never later.
    /// </para>
    /// </remarks>
    public void MarkReviewPosted(long runId, DateTimeOffset postedAtUtc)
    {
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE review_run SET last_posted_review_at = $at WHERE id = $id AND last_posted_review_at IS NULL;
            """;
        _ = command.Parameters.AddWithValue("$at", Utc(postedAtUtc));
        _ = command.Parameters.AddWithValue("$id", runId);
        _ = command.ExecuteNonQuery();
    }

    /// <summary>
    /// The latest instant at which ANY run for this PR was proven to have posted its review, or null when no
    /// run ever has. This is the delta-review cutoff: comments after it are new since the bot last spoke.
    /// </summary>
    /// <remarks>
    /// Deliberately spans head shas. A run's identity includes <c>head_sha</c>, so every new head opens a new
    /// row — but the bot's last word on a PR is its last word whichever head it was reviewing, and scoping the
    /// cutoff to one row would reset it on every push and re-classify the whole conversation as past.
    /// <c>MAX</c> over the stored text is chronological because <see cref="Utc"/> writes fixed-width UTC.
    /// Null is a real answer meaning "this PR has never carried a posted review", and callers use it as such —
    /// see the fetch-failure path in <c>DaemonReviewStageExecutor.PrependExistingCommentsAsync</c>, where it
    /// is the difference between a first review that is safe to post blind and a re-review that is not.
    /// </remarks>
    public DateTimeOffset? GetLastPostedReviewAt(long repoId, string prId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prId);

        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT MAX(last_posted_review_at) FROM review_run WHERE repo_id = $repoId AND pr_id = $prId;
            """;
        _ = command.Parameters.AddWithValue("$repoId", repoId);
        _ = command.Parameters.AddWithValue("$prId", prId);
        var value = command.ExecuteScalar();
        return value is string text && text.Length > 0
            ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            : null;
    }

    /// <summary>Returns the latest exact provider receipt for a posted root review comment.</summary>
    public OutboxEntry? GetLatestPostedReviewOutbox(long repoId, string prId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prId);
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT o.*
            FROM review_outbox o
            JOIN review_run r ON r.id = o.review_run_id
            WHERE r.repo_id = $repoId
              AND r.pr_id = $prId
              AND o.operation = $operation
              AND o.artifact_kind = $artifactKind
              AND o.status = 'Posted'
              AND o.provider_response_id IS NOT NULL
            ORDER BY o.id DESC
            LIMIT 1;
            """;
        _ = command.Parameters.AddWithValue("$repoId", repoId);
        _ = command.Parameters.AddWithValue("$prId", prId);
        _ = command.Parameters.AddWithValue("$operation", "post-review-comment");
        _ = command.Parameters.AddWithValue("$artifactKind", "review");
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapOutbox(reader) : null;
    }

    /// <summary>Returns exact provider object IDs accepted for this PR by posted outbox rows and actions.</summary>
    public IReadOnlySet<string> GetPostedProviderReceiptIds(long repoId, string prId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prId);
        var result = new HashSet<string>(StringComparer.Ordinal);
        using var gate = _gate.EnterScope();
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = """
                SELECT o.provider_response_id
                FROM review_outbox o
                JOIN review_run r ON r.id = o.review_run_id
                WHERE r.repo_id = $repoId
                  AND r.pr_id = $prId
                  AND o.status = 'Posted'
                  AND o.provider_response_id IS NOT NULL;
                """;
            _ = command.Parameters.AddWithValue("$repoId", repoId);
            _ = command.Parameters.AddWithValue("$prId", prId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                _ = result.Add(reader.GetString(0));
            }
        }

        using (var command = _connection.CreateCommand())
        {
            command.CommandText = """
                SELECT a.provider_receipt_json
                FROM review_action a
                JOIN engagement_round er ON er.id = a.engagement_round_id
                JOIN pr_engagement e ON e.id = er.pr_engagement_id
                WHERE e.repo_id = $repoId
                  AND e.pr_id = $prId
                  AND a.status = 'Accepted'
                  AND a.provider_receipt_json IS NOT NULL;
                """;
            _ = command.Parameters.AddWithValue("$repoId", repoId);
            _ = command.Parameters.AddWithValue("$prId", prId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (TryReadProviderCommentId(reader.GetString(0), out var providerCommentId))
                {
                    _ = result.Add(providerCommentId);
                }
            }
        }

        return result;
    }

    private static bool TryReadProviderCommentId(string receiptJson, out string providerCommentId)
    {
        try
        {
            using var document = JsonDocument.Parse(receiptJson);
            if (
                document.RootElement.TryGetProperty("providerCommentId", out var commentId)
                && commentId.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(commentId.GetString())
            )
            {
                providerCommentId = commentId.GetString()!;
                return true;
            }
        }
        catch (JsonException)
        {
            // Historical receipt payloads are opaque and may not use the current publication schema.
        }

        providerCommentId = string.Empty;
        return false;
    }

    /// <summary>
    /// Records that <paramref name="runId"/> built its review WITHOUT the list of comments already on the PR
    /// (the provider listing failed), so the delivery boundary can decline to post it blind (#225 item 2).
    /// </summary>
    /// <remarks>
    /// One-way and idempotent: nothing clears it, because nothing later in the run's life re-acquires the
    /// context the review was already written without. A retry that wants to post must be a NEW run, which
    /// gets its own fetch and its own flag.
    /// </remarks>
    public void MarkDedupContextLost(long runId)
    {
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE review_run SET dedup_context_lost = 1 WHERE id = $id;
            """;
        _ = command.Parameters.AddWithValue("$id", runId);
        _ = command.ExecuteNonQuery();
    }

    /// <summary>
    /// Whether <paramref name="runId"/> was flagged by <see cref="MarkDedupContextLost"/>. Read at the
    /// delivery boundary rather than carried on the in-memory run, so a Posted stage reached after a restart —
    /// the retry that most needs the answer — still gets it.
    /// </summary>
    public bool WasDedupContextLost(long runId)
    {
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT dedup_context_lost FROM review_run WHERE id = $id;";
        _ = command.Parameters.AddWithValue("$id", runId);
        return command.ExecuteScalar() is long flag && flag != 0;
    }

    /// <summary>
    /// Looks back over prior <c>review_run</c> rows for the same (repoId, prId), excluding
    /// <paramref name="excludeRunId"/> (the run currently being processed), to give a re-review its
    /// context: the head sha it was last reviewed at, and how many rounds have completed so far. Only
    /// the PRIMARY variant counts — the B arm of an A/B comparison is diff-only and shares no notes
    /// dir, so it must not inflate the round count or be mistaken for "the" prior review. A run only
    /// counts once it reached a stage that actually produced review output (<see cref="ReviewStage.Reviewed"/>,
    /// <see cref="ReviewStage.Judged"/>, or <see cref="ReviewStage.Posted"/>); a run stuck at
    /// <see cref="ReviewStage.Discovered"/> or <see cref="ReviewStage.ContextReady"/> never reviewed
    /// anything and must not count.
    /// </summary>
    public PriorReviewSummary GetPriorReviewSummary(long repoId, string prId, long excludeRunId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prId);

        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT head_sha
            FROM review_run
            WHERE repo_id = $repoId AND pr_id = $prId AND variant_id = $variant
              AND id != $excludeRunId
              AND stage IN ('Reviewed', 'Judged', 'Posted')
            ORDER BY id DESC;
            """;
        _ = command.Parameters.AddWithValue("$repoId", repoId);
        _ = command.Parameters.AddWithValue("$prId", prId);
        _ = command.Parameters.AddWithValue("$variant", PrimaryVariantId);
        _ = command.Parameters.AddWithValue("$excludeRunId", excludeRunId);
        using var reader = command.ExecuteReader();

        string? prevHeadSha = null;
        var priorReviewCount = 0;
        while (reader.Read())
        {
            prevHeadSha ??= reader.GetString(reader.GetOrdinal("head_sha"));
            priorReviewCount++;
        }

        return new PriorReviewSummary(prevHeadSha, priorReviewCount);
    }

    /// <summary>Stable id of the primary (non-comparison) review variant — see <see cref="GetPriorReviewSummary"/>.</summary>
    private const string PrimaryVariantId = "primary";

    /// <summary>
    /// The persisted review payload of every EARLIER primary round on this PR, oldest first, so a caller can
    /// answer the only question that authorizes the "no new findings since the last review" exit: did an
    /// earlier round actually produce a review to have had findings since?
    /// </summary>
    /// <remarks>
    /// Deliberately NOT "does a prior RUN exist". A run that was discovered and then died before reviewing
    /// anything leaves a <c>review_run</c> row and no review, and that is not a hypothetical shape: of the 57
    /// live runs that emitted the sentinel with no earlier review, 6 had one or two prior runs, all of them
    /// parked at Discovered or ContextReady. Counting those rows as a prior review authorizes the precise
    /// claim this query exists to refuse.
    /// <para>
    /// The stage filter is the one <see cref="GetPriorReviewSummary"/> applies, for the same reasons, but it is
    /// NOT sufficient alone — a run can reach a review-producing stage and still persist no body, or persist
    /// the sentinel — so this returns the payloads and lets the caller test what is inside them. The
    /// persistence layer does not know what a sentinel is and must not learn.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> GetPriorReviewPayloads(
        long repoId,
        string prId,
        long excludeRunId,
        string artifactKind
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prId);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactKind);

        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        // The run's LATEST artifact of the kind — the same "latest wins" rule TryGetLatestArtifact defines —
        // because a run that retried its review stage holds more than one.
        command.CommandText = """
            SELECT ra.payload AS payload
            FROM review_run rr
            JOIN review_artifact ra ON ra.id = (
                SELECT id FROM review_artifact
                WHERE review_run_id = rr.id AND artifact_kind = $kind
                ORDER BY id DESC LIMIT 1)
            WHERE rr.repo_id = $repoId AND rr.pr_id = $prId AND rr.variant_id = $variant
              AND rr.id != $excludeRunId
              AND rr.stage IN ('Reviewed', 'Judged', 'Posted')
            ORDER BY rr.id;
            """;
        _ = command.Parameters.AddWithValue("$repoId", repoId);
        _ = command.Parameters.AddWithValue("$prId", prId);
        _ = command.Parameters.AddWithValue("$variant", PrimaryVariantId);
        _ = command.Parameters.AddWithValue("$excludeRunId", excludeRunId);
        _ = command.Parameters.AddWithValue("$kind", artifactKind);

        var payloads = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            payloads.Add(reader.GetString(reader.GetOrdinal("payload")));
        }

        return payloads;
    }

    /// <summary>
    /// The FIRST review payload of every PR whose first review landed at or after <paramref name="since"/>,
    /// so a caller can measure how often a first-ever review answered that nothing had changed.
    /// </summary>
    /// <remarks>
    /// This is the population-level counterpart to the per-run guard in <c>DaemonReviewStageExecutor</c>. The
    /// guard makes one false no-change claim impossible; this measures whether the fleet as a whole is still
    /// healthy, which the guard cannot tell you.
    /// <para>
    /// Before the 2026-08-07T16:12:17Z rebuild, 57 of 115 first reviews (49.6%) came back as the
    /// no-new-findings sentinel — a claim about a previous review that never happened — and 0 of 104 did
    /// afterwards. <b>The cause of that recovery is not in the record</b>: the daemon runs from an uncommitted
    /// tree, and the one committed fix that would be credited for it post-dates the drop by two days. A defect
    /// whose repair is unexplained can return without anything announcing it, so the rate is measured
    /// continuously rather than assumed to stay at zero.
    /// </para>
    /// <para>
    /// "First" is by artifact id within (repo, PR) on the primary variant, so a legitimate later-round sentinel
    /// cannot inflate the rate. The caller counts sentinels; the persistence layer does not know what one is.
    /// </para>
    /// <para>
    /// <paramref name="since"/> is an INSTANT, not a pre-formatted string, so the one "O"-shaped rendering that
    /// makes <c>created_at</c> comparable lexicographically lives in <see cref="Utc"/> beside the writer —
    /// exactly as <see cref="ListStrandedRuns"/> and <see cref="ListRetryPendingRuns"/> take theirs. A caller
    /// formatting its own cutoff would be a second copy of that contract, agreeing today and pinned by nothing.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> GetFirstReviewPayloadsSince(DateTimeOffset since, string artifactKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactKind);

        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT ra.payload AS payload
            FROM review_artifact ra
            JOIN review_run rr ON rr.id = ra.review_run_id
            WHERE ra.artifact_kind = $kind AND rr.variant_id = $variant
              AND ra.created_at >= $since
              AND ra.id IN (
                SELECT MIN(ra2.id)
                FROM review_artifact ra2
                JOIN review_run rr2 ON rr2.id = ra2.review_run_id
                WHERE ra2.artifact_kind = $kind AND rr2.variant_id = $variant
                GROUP BY rr2.repo_id, rr2.pr_id)
            ORDER BY ra.id;
            """;
        _ = command.Parameters.AddWithValue("$kind", artifactKind);
        _ = command.Parameters.AddWithValue("$variant", PrimaryVariantId);
        _ = command.Parameters.AddWithValue("$since", Utc(since));

        var payloads = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            payloads.Add(reader.GetString(reader.GetOrdinal("payload")));
        }

        return payloads;
    }

    /// <summary>
    /// Returns one row per PR that has at least one <c>review_run</c> (grouped over the reviewed
    /// (repo, pr) pairs — multiple runs, variants, or head shas for the same PR collapse to a single
    /// row). Consumed by the PR-lifecycle sweeper to enumerate the PRs it must re-poll for close/merge
    /// transitions; the caller derives the per-PR branch name itself.
    /// <para>
    /// Grouped rather than <c>DISTINCT</c> because of <c>pr_author</c>: a PR reviewed both before and
    /// after the author column existed has runs with and without it, and a DISTINCT over the author
    /// would emit that PR twice — sweeping it twice and, worse, once with the author erased.
    /// <c>MAX</c> ignores NULLs, so a known author always beats an unknown one.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Keeps its <c>Async</c> signature (the sweeper awaits it) but runs synchronously under the store's
    /// gate: <c>Microsoft.Data.Sqlite</c>'s <c>*Async</c> members are synchronous wrappers — SQLite has no
    /// async I/O — and the gate is a ref-struct scope that cannot span an <c>await</c>. Draining the reader
    /// under the gate is the point: the connection is unusable by anyone else until the rows are read.
    /// </remarks>
    public Task<IReadOnlyList<ReviewedPrRow>> ListReviewedPrsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var results = new List<ReviewedPrRow>();
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT r.provider, r.org_or_owner, r.project, r.repo_name, r.repo_stable_id, rr.pr_id,
                   MAX(rr.pr_author) AS pr_author
            FROM review_run rr
            JOIN repo r ON r.id = rr.repo_id
            GROUP BY r.provider, r.org_or_owner, r.project, r.repo_name, r.repo_stable_id, rr.pr_id;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var repo = new RepoIdentity
            {
                Provider = reader.GetString(reader.GetOrdinal("provider")),
                OrgOrOwner = reader.GetString(reader.GetOrdinal("org_or_owner")),
                Project = GetNullableString(reader, "project"),
                RepoName = reader.GetString(reader.GetOrdinal("repo_name")),
                RepoStableId = GetNullableString(reader, "repo_stable_id"),
            };
            results.Add(
                new ReviewedPrRow(
                    repo,
                    repo.Provider,
                    reader.GetString(reader.GetOrdinal("pr_id")),
                    GetNullableString(reader, "pr_author")
                )
            );
        }

        return Task.FromResult<IReadOnlyList<ReviewedPrRow>>(results);
    }

    /// <summary>
    /// Returns the provider-recorded author for one reviewed PR. A known identity wins over pre-migration
    /// runs that carry null; null means no run for this exact repository/PR recorded an author.
    /// </summary>
    public string? GetPrAuthor(long repoId, string prId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prId);

        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT MAX(pr_author)
            FROM review_run
            WHERE repo_id = $repoId AND pr_id = $prId;
            """;
        _ = command.Parameters.AddWithValue("$repoId", repoId);
        _ = command.Parameters.AddWithValue("$prId", prId);
        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// Returns the non-terminal runs that nothing else will ever advance — <c>workflow_status</c> is not
    /// <see cref="WorkflowStatus.Completed"/> and <c>updated_at</c> is older than
    /// <paramref name="staleBefore"/> — oldest first, capped at <paramref name="limit"/>.
    /// <para>
    /// A run only ever advances when a poll enumerates its PR, and the poll lists the OPEN pull requests
    /// inside the operator's recency window. So a run whose PR has since closed, or whose PR has been quiet
    /// for longer than that window, has no route back into the daemon at all: no retry ever arrives, and the
    /// self-healing built into the retry path (a re-leased slot clearing its own stale locks, say) never gets
    /// to run. Nothing else in the daemon reads <c>review_run</c> again. This query is that route.
    /// </para>
    /// <para>
    /// <paramref name="staleBefore"/> is what keeps the query off runs the poll is still working: a healthy
    /// run stamps <c>updated_at</c> at every stage boundary, so any grace period comfortably larger than a
    /// stage deadline excludes everything currently in flight. Comparing the stored text against
    /// <see cref="Utc"/> is sound because every timestamp is written round-trip (<c>"O"</c>) and therefore
    /// orders lexicographically the same way it orders chronologically.
    /// </para>
    /// <para>
    /// <see cref="StrandedRunRow.Superseded"/> reports whether a later run has already re-reviewed what this
    /// one owes: same PR, same <c>review_kind</c> and <c>variant_id</c>, at a DIFFERENT COMMIT PAIR — either
    /// <c>head_sha</c> or <c>base_sha</c> moved. What a review is ABOUT is the diff, and the diff is both
    /// endpoints: a target-branch rebase moves <c>base_sha</c> under an unchanged head, and the later run then
    /// reviewed the PR as it now stands while this one still owes findings about changes that have since landed
    /// in the target branch. Keying on the head alone would resume it and, on a posting daemon, publish them.
    /// The pair is also exactly the commit component of the identity tuple this store keys runs on, so the two
    /// rows are legitimately distinct runs and the later one is the current one.
    /// That distinction is the caller's safety rail — resuming a superseded run would review, and on a posting
    /// daemon publish, a diff that a newer commit pair has already replaced. Each half of the predicate is load
    /// bearing in the other direction too, because the caller's response to the flag is to retire the run
    /// permanently, and this listing is the run's only remaining route back. Merely "a later run for the same
    /// PR" also matches a sibling review that never produced this run's output at all — a second variant, or
    /// another kind — and matches a duplicate row at the same base AND head, where nothing went stale. Either
    /// one would silently drop a review that was still owed. <c>mode</c> is deliberately absent: it is an
    /// authorization decision made at post time, not part of what the review IS (see
    /// <see cref="CreateOrGetReviewRun"/>), so a newer head reviewed after posting was toggled still supersedes.
    /// </para>
    /// </summary>
    /// <remarks>
    /// One capped query, no offset paging: the rows are ordered by <c>id</c> but selected on
    /// <c>workflow_status</c>, which the caller mutates as it works. A second page taken by offset over a
    /// predicate that has shifted under it would skip rows.
    /// </remarks>
    public IReadOnlyList<StrandedRunRow> ListStrandedRuns(DateTimeOffset staleBefore, int limit) =>
        ListStrandedRunsWhere("<>", WorkflowStatus.Completed, staleBefore, limit);

    /// <summary>
    /// The same listing narrowed to <see cref="WorkflowStatus.RetryPending"/> — the runs whose last write was a
    /// deliberate "try this again", so the caller can react to them on a staleness of its own rather than on the
    /// one that decides a run has been abandoned (#429).
    /// <para>
    /// Everything <see cref="ListStrandedRuns"/>'s summary says still applies, most importantly
    /// <see cref="StrandedRunRow.Superseded"/>: this listing shares that subquery rather than restating it,
    /// because a fast path that resumed a run whose commit pair has since been re-reviewed would publish a
    /// stale review FASTER, which is worse than the delay it was added to remove. The only difference is the
    /// status predicate.
    /// </para>
    /// <para>
    /// Why <c>RetryPending</c> is the one status that earns a shorter window: it is written by
    /// <see cref="Orchestration.PrOrchestrator"/>'s stage catch, so it means a stage ran and failed and the run
    /// is owed another attempt. <c>Pending</c> and <c>Running</c> mean the opposite — nobody has said anything
    /// about this run, so its age is the only evidence available and the abandonment window is the right one.
    /// </para>
    /// </summary>
    public IReadOnlyList<StrandedRunRow> ListRetryPendingRuns(DateTimeOffset staleBefore, int limit) =>
        ListStrandedRunsWhere("=", WorkflowStatus.RetryPending, staleBefore, limit);

    /// <summary>
    /// Shared body of the two stranded-run listings. Only the comparison OPERATOR and the status value differ,
    /// and the operator comes from this file's own two call sites rather than from a caller, so no user input
    /// reaches the SQL. Shared rather than copied because the half that would matter if the two drifted is the
    /// <c>superseded</c> subquery: a listing that lost it would resume — and on a posting daemon publish — a
    /// review of a commit pair that a later run has already replaced.
    /// <para>
    /// <c>parked_at IS NULL</c> is the one authoritative exclusion for a run that spent its durable retry
    /// budget, and it lives here so both listings inherit it. It is deliberately NOT paired with a
    /// <c>workflow_status &lt;&gt; 'Failed'</c> conjunct saying the same thing twice: this listing is a
    /// parked run's last route back, and two independently-sufficient predicates would let either one be
    /// deleted without any test noticing. It is also not redundant with the status test above it — the
    /// abandonment listing selects <c>&lt;&gt; 'Completed'</c>, which a parked (<c>Failed</c>) row satisfies.
    /// </para>
    /// </summary>
    private IReadOnlyList<StrandedRunRow> ListStrandedRunsWhere(
        string statusOperator,
        WorkflowStatus status,
        DateTimeOffset staleBefore,
        int limit
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var results = new List<StrandedRunRow>();
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            SELECT rr.*, r.provider, r.org_or_owner, r.project, r.repo_name, r.repo_stable_id,
                   EXISTS (SELECT 1 FROM review_run n
                            WHERE n.repo_id = rr.repo_id AND n.pr_id = rr.pr_id AND n.id > rr.id
                              AND n.review_kind = rr.review_kind AND n.variant_id = rr.variant_id
                              AND (n.head_sha <> rr.head_sha OR n.base_sha <> rr.base_sha)) AS superseded
            FROM review_run rr
            JOIN repo r ON r.id = rr.repo_id
            WHERE rr.workflow_status {statusOperator} $status AND rr.updated_at < $staleBefore
              AND rr.parked_at IS NULL
            ORDER BY rr.id
            LIMIT $limit;
            """;
        _ = command.Parameters.AddWithValue("$status", status.ToString());
        _ = command.Parameters.AddWithValue("$staleBefore", Utc(staleBefore));
        _ = command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var repo = new RepoIdentity
            {
                Provider = reader.GetString(reader.GetOrdinal("provider")),
                OrgOrOwner = reader.GetString(reader.GetOrdinal("org_or_owner")),
                Project = GetNullableString(reader, "project"),
                RepoName = reader.GetString(reader.GetOrdinal("repo_name")),
                RepoStableId = GetNullableString(reader, "repo_stable_id"),
            };
            results.Add(
                new StrandedRunRow(MapReviewRun(reader), repo, reader.GetInt64(reader.GetOrdinal("superseded")) != 0)
            );
        }

        return results;
    }

    // ── poll_cursor (§12) ────────────────────────────────────────────────────────────────────────

    /// <summary>Upserts a cursor keyed by (provider, scope).</summary>
    public void SaveCursor(OpaqueCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO poll_cursor (provider, scope, cursor_version, cursor_payload, high_water_mark, etag, continuation, since_timestamp, updated_at)
            VALUES ($provider, $scope, $version, $payload, $hwm, $etag, $continuation, $since, $now)
            ON CONFLICT (provider, scope) DO UPDATE SET
                cursor_version = excluded.cursor_version,
                cursor_payload = excluded.cursor_payload,
                high_water_mark = excluded.high_water_mark,
                etag = excluded.etag,
                continuation = excluded.continuation,
                since_timestamp = excluded.since_timestamp,
                updated_at = excluded.updated_at;
            """;
        _ = command.Parameters.AddWithValue("$provider", cursor.Provider);
        _ = command.Parameters.AddWithValue("$scope", cursor.Scope);
        _ = command.Parameters.AddWithValue("$version", cursor.CursorVersion);
        _ = command.Parameters.AddWithValue("$payload", cursor.CursorPayload);
        _ = command.Parameters.AddWithValue("$hwm", (object?)cursor.HighWaterMark ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$etag", (object?)cursor.Etag ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$continuation", (object?)cursor.Continuation ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$since", (object?)cursor.SinceTimestamp ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$now", UtcNow());
        _ = command.ExecuteNonQuery();
    }

    /// <summary>
    /// Reads the cursor for (provider, scope) and decides whether the caller must resync. Per §12 the
    /// reader tolerates missing, empty/invalid, and version-mismatched (older or newer) cursors by
    /// signalling a resync rather than handing back an unusable cursor.
    /// </summary>
    public CursorReadResult ReadCursor(string provider, string scope, int supportedVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT * FROM poll_cursor WHERE provider = $provider AND scope = $scope;";
        _ = command.Parameters.AddWithValue("$provider", provider);
        _ = command.Parameters.AddWithValue("$scope", scope);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return CursorReadResult.Resync();
        }

        var version = reader.GetInt32(reader.GetOrdinal("cursor_version"));
        var payload = reader.GetString(reader.GetOrdinal("cursor_payload"));
        if (version != supportedVersion || string.IsNullOrWhiteSpace(payload))
        {
            return CursorReadResult.Resync();
        }

        return CursorReadResult.Usable(
            new OpaqueCursor
            {
                Provider = reader.GetString(reader.GetOrdinal("provider")),
                Scope = reader.GetString(reader.GetOrdinal("scope")),
                CursorVersion = version,
                CursorPayload = payload,
                HighWaterMark = GetNullableString(reader, "high_water_mark"),
                Etag = GetNullableString(reader, "etag"),
                Continuation = GetNullableString(reader, "continuation"),
                SinceTimestamp = GetNullableString(reader, "since_timestamp"),
            }
        );
    }

    // ── review_outbox (§11) ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Enqueues an outbox entry, or returns the existing one when its idempotency key already exists.
    /// Idempotent enqueue is the first half of exactly-once posting.
    /// </summary>
    public OutboxEntry EnqueueOutbox(OutboxEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Insert-then-read-back is one logical operation: the gate spans both so the row this returns is
        // the one the INSERT settled on (its own, or the pre-existing one IGNORE kept).
        using var gate = _gate.EnterScope();
        var now = UtcNow();
        using var insert = _connection.CreateCommand();
        insert.CommandText = """
            INSERT OR IGNORE INTO review_outbox (
                idempotency_key, provider, review_run_id, operation, artifact_kind, status, body_hash, provider_response_id, created_at, updated_at)
            VALUES ($key, $provider, $runId, $operation, $kind, $status, $bodyHash, $responseId, $now, $now);
            """;
        _ = insert.Parameters.AddWithValue("$key", entry.IdempotencyKey);
        _ = insert.Parameters.AddWithValue("$provider", entry.Provider);
        _ = insert.Parameters.AddWithValue("$runId", entry.ReviewRunId);
        _ = insert.Parameters.AddWithValue("$operation", entry.Operation);
        _ = insert.Parameters.AddWithValue("$kind", entry.ArtifactKind);
        _ = insert.Parameters.AddWithValue("$status", entry.Status.ToString());
        _ = insert.Parameters.AddWithValue("$bodyHash", (object?)entry.BodyHash ?? DBNull.Value);
        _ = insert.Parameters.AddWithValue("$responseId", (object?)entry.ProviderResponseId ?? DBNull.Value);
        _ = insert.Parameters.AddWithValue("$now", now);
        _ = insert.ExecuteNonQuery();

        return GetOutboxByKey(entry.IdempotencyKey)!;
    }

    /// <summary>
    /// Atomically moves an outbox row from <paramref name="from"/> to <paramref name="to"/>, optionally
    /// recording the provider's response id. Returns <c>false</c> when the row is not in the expected
    /// state (already advanced by another worker, or never enqueued) — the conditional UPDATE is what
    /// makes crash-replay safe.
    /// </summary>
    public bool TryTransitionOutbox(long id, OutboxStatus from, OutboxStatus to, string? providerResponseId = null)
    {
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE review_outbox
            SET status = $to,
                provider_response_id = COALESCE($responseId, provider_response_id),
                updated_at = $now
            WHERE id = $id AND status = $from;
            """;
        _ = command.Parameters.AddWithValue("$to", to.ToString());
        _ = command.Parameters.AddWithValue("$responseId", (object?)providerResponseId ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$now", UtcNow());
        _ = command.Parameters.AddWithValue("$id", id);
        _ = command.Parameters.AddWithValue("$from", from.ToString());
        return command.ExecuteNonQuery() == 1;
    }

    public OutboxEntry? GetOutbox(long id)
    {
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT * FROM review_outbox WHERE id = $id;";
        _ = command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapOutbox(reader) : null;
    }

    /// <summary>
    /// Returns every outbox row for a run, in id order. Consumed by the reconcile path (and tests) to
    /// inspect the recorded side effects of a run — e.g. the <c>push-reviewbot</c> retention outcome.
    /// </summary>
    public IReadOnlyList<OutboxEntry> GetOutboxForRun(long reviewRunId)
    {
        var results = new List<OutboxEntry>();
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT * FROM review_outbox WHERE review_run_id = $runId ORDER BY id;";
        _ = command.Parameters.AddWithValue("$runId", reviewRunId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(MapOutbox(reader));
        }

        return results;
    }

    private OutboxEntry? GetOutboxByKey(string idempotencyKey)
    {
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT * FROM review_outbox WHERE idempotency_key = $key;";
        _ = command.Parameters.AddWithValue("$key", idempotencyKey);
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapOutbox(reader) : null;
    }

    // ── review_artifact (§14) ────────────────────────────────────────────────────────────────────

    /// <summary>Appends a schema-versioned artifact (Review/Judge/Knowledge or B-variant output).</summary>
    public ReviewArtifact AddArtifact(ReviewArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO review_artifact (review_run_id, artifact_schema_version, artifact_kind, provider, payload, created_at)
            VALUES ($runId, $schemaVersion, $kind, $provider, $payload, $now)
            RETURNING id;
            """;
        _ = command.Parameters.AddWithValue("$runId", artifact.ReviewRunId);
        _ = command.Parameters.AddWithValue("$schemaVersion", artifact.ArtifactSchemaVersion);
        _ = command.Parameters.AddWithValue("$kind", artifact.ArtifactKind);
        _ = command.Parameters.AddWithValue("$provider", artifact.Provider);
        _ = command.Parameters.AddWithValue("$payload", artifact.Payload);
        _ = command.Parameters.AddWithValue("$now", UtcNow());
        var id = Convert.ToInt64(command.ExecuteScalar());
        return artifact with { Id = id };
    }

    public IReadOnlyList<ReviewArtifact> GetArtifacts(long reviewRunId)
    {
        var results = new List<ReviewArtifact>();
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT * FROM review_artifact WHERE review_run_id = $runId ORDER BY id;";
        _ = command.Parameters.AddWithValue("$runId", reviewRunId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(MapArtifact(reader));
        }

        return results;
    }

    /// <summary>
    /// Every artifact of a run whose kind is one of <paramref name="artifactKinds"/>, in append order.
    /// <para>
    /// The middle of the three reads this table offers, and the one that was missing (#453).
    /// <see cref="GetArtifacts"/> materialises every payload the run holds — the largest by far being
    /// <c>review-context</c>, which carries the whole diff as a .NET string — and
    /// <see cref="TryGetLatestArtifact"/> returns a single row. A caller that wants <i>all</i> rows of
    /// <i>one</i> kind, as the eval corpus sweep does when it matches a judge row per review variant,
    /// had to take the first and throw away the diffs. Filtering in SQL is the difference between
    /// reading a grade and reading every diff in the window to find one.
    /// </para>
    /// <para>
    /// The append-only history is untouched, exactly as with the two reads beside it: this is a
    /// projection of the same rows, and <see cref="GetArtifacts"/> still returns all of them.
    /// </para>
    /// </summary>
    /// <param name="reviewRunId">The run whose artifacts to list.</param>
    /// <param name="artifactKinds">
    /// The kinds to return. At least one, and none blank — an empty list would compile to an
    /// <c>IN ()</c> that matches nothing, and an empty result is indistinguishable from a run that
    /// recorded nothing. For the sweep that difference is every candidate of the run silently
    /// counting as ungraded, so it is refused rather than answered.
    /// </param>
    /// <exception cref="ArgumentException">No kind was given, or one of them is blank.</exception>
    public IReadOnlyList<ReviewArtifact> ListArtifacts(long reviewRunId, params string[] artifactKinds)
    {
        ArgumentNullException.ThrowIfNull(artifactKinds);

        if (artifactKinds.Length == 0)
        {
            throw new ArgumentException(
                "A kind-filtered artifact listing needs at least one artifact kind; an empty filter "
                    + "matches no row, which reads as a run that recorded nothing.",
                nameof(artifactKinds)
            );
        }

        foreach (var kind in artifactKinds)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(kind, nameof(artifactKinds));
        }

        var results = new List<ReviewArtifact>();
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();

        // Parameterised per kind rather than interpolated: the kinds are constants today, and the
        // day one arrives from configuration is the day an interpolated IN list becomes an injection.
        var placeholders = string.Join(", ", artifactKinds.Select((_, i) => $"$kind{i}"));
        command.CommandText =
            $"SELECT * FROM review_artifact WHERE review_run_id = $runId AND artifact_kind IN ({placeholders}) ORDER BY id;";
        _ = command.Parameters.AddWithValue("$runId", reviewRunId);

        for (var i = 0; i < artifactKinds.Length; i++)
        {
            _ = command.Parameters.AddWithValue($"$kind{i}", artifactKinds[i]);
        }

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(MapArtifact(reader));
        }

        return results;
    }

    /// <summary>
    /// The most recently appended artifact of <paramref name="artifactKind"/> for a run, or <c>null</c> when the
    /// run has none. This is the single lookup every "what did this run last record?" read goes through —
    /// including the checkpoint reads that resume an interrupted review — so the "latest wins" rule is defined
    /// in ONE place rather than re-derived by each caller filtering a full artifact list.
    /// <para>
    /// The append-only history is untouched: this SELECTs the highest id of the kind instead of collapsing or
    /// replacing rows, so every earlier artifact stays readable through <see cref="GetArtifacts"/>.
    /// </para>
    /// </summary>
    public ReviewArtifact? TryGetLatestArtifact(long reviewRunId, string artifactKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactKind);

        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM review_artifact
            WHERE review_run_id = $runId AND artifact_kind = $kind
            ORDER BY id DESC LIMIT 1;
            """;
        _ = command.Parameters.AddWithValue("$runId", reviewRunId);
        _ = command.Parameters.AddWithValue("$kind", artifactKind);
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapArtifact(reader) : null;
    }

    private static ReviewArtifact MapArtifact(SqliteDataReader reader) =>
        new()
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            ReviewRunId = reader.GetInt64(reader.GetOrdinal("review_run_id")),
            ArtifactSchemaVersion = reader.GetInt32(reader.GetOrdinal("artifact_schema_version")),
            ArtifactKind = reader.GetString(reader.GetOrdinal("artifact_kind")),
            Provider = reader.GetString(reader.GetOrdinal("provider")),
            Payload = reader.GetString(reader.GetOrdinal("payload")),
        };

    // ── deep_link_conversation (deep-link retention ledger) ──────────────────────────────────────

    /// <summary>
    /// Records a hosted S2S conversation as reachable behind a posted deep-link, starting its retention
    /// clock. Called at the single mint choke point (<c>S2SReviewAgent.EnsureProvisionedAsync</c>) so the
    /// judge and A/B arms — whose thread ids never reach an artifact — are covered too.
    /// <para>
    /// <c>INSERT OR IGNORE</c>: a re-record for a thread already in the ledger must not restart the clock,
    /// so the FIRST mint wins. A thread that has already been discarded has no row and is not resurrected
    /// here either — <see cref="RemoveDeepLinkConversation"/> is the end of its life, not a pause.
    /// </para>
    /// </summary>
    public void RecordDeepLinkConversation(string threadId, string? title, DateTimeOffset? mintedAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO deep_link_conversation (thread_id, title, minted_at)
            VALUES ($threadId, $title, $mintedAt);
            """;
        _ = command.Parameters.AddWithValue("$threadId", threadId);
        _ = command.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$mintedAt", mintedAtUtc is { } minted ? Utc(minted) : UtcNow());
        _ = command.ExecuteNonQuery();

        // Backfill the title onto a row another writer created first. TrackRunHostedConversation runs at the
        // same mint and inserts with no title (the executor knows the run, not the host-facing conversation
        // name), so without this the INSERT OR IGNORE above would silently discard the only human-readable
        // label the retention sweep has to report. Guarded on the existing title being NULL, which keeps the
        // FIRST-mint-wins rule intact: this fills an absence, it never overwrites a recorded name.
        if (!string.IsNullOrWhiteSpace(title))
        {
            using var backfill = _connection.CreateCommand();
            backfill.CommandText = """
                UPDATE deep_link_conversation
                SET title = $title
                WHERE thread_id = $threadId AND title IS NULL;
                """;
            _ = backfill.Parameters.AddWithValue("$title", title);
            _ = backfill.Parameters.AddWithValue("$threadId", threadId);
            _ = backfill.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Lists the conversations minted strictly before <paramref name="cutoffUtc"/> — i.e. those whose
    /// deep-link has outlived the retention window and may be discarded. Comparison is lexicographic over
    /// the fixed-width UTC round-trip ("O") timestamps this store writes, which is chronological because
    /// every value is normalized to UTC on the way in.
    /// </summary>
    public IReadOnlyList<DeepLinkConversationRow> ListDeepLinkConversationsMintedBefore(DateTimeOffset cutoffUtc)
    {
        var results = new List<DeepLinkConversationRow>();
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT d.thread_id, d.title, d.minted_at
            FROM deep_link_conversation d
            WHERE d.minted_at < $cutoff
            ORDER BY d.minted_at;
            """;
        _ = command.Parameters.AddWithValue("$cutoff", Utc(cutoffUtc));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(
                new DeepLinkConversationRow(
                    reader.GetString(reader.GetOrdinal("thread_id")),
                    GetNullableString(reader, "title"),
                    DateTimeOffset.Parse(
                        reader.GetString(reader.GetOrdinal("minted_at")),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind
                    )
                )
            );
        }

        return results;
    }

    /// <summary>
    /// Drops a conversation from the ledger once it has been discarded (or was already gone server-side).
    /// Idempotent — a missing row is not an error, so a retried sweep cannot fail on its own success.
    /// </summary>
    public void RemoveDeepLinkConversation(string threadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM deep_link_conversation WHERE thread_id = $threadId;";
        _ = command.Parameters.AddWithValue("$threadId", threadId);
        _ = command.ExecuteNonQuery();
    }

    /// <summary>
    /// Records that <paramref name="reviewRunId"/> owns the hosted conversation <paramref name="threadId"/>,
    /// creating the ledger row if the retention recorder has not already. This is the durable half of the
    /// pooled-slot release contract: an S2S conversation is a live container with the run's leased slot
    /// mounted into it, and the terminal stage — which may be a DIFFERENT PROCESS after a restart — has only
    /// a run id to find those mounts by.
    /// <para>
    /// Two statements rather than an upsert, because the two facts have different owners. The
    /// <c>INSERT OR IGNORE</c> preserves <see cref="RecordDeepLinkConversation"/>'s rule that the FIRST mint
    /// wins the retention clock — re-recording must never restart it, and a conversation already discarded
    /// (no row) must not be resurrected by an ownership claim. The <c>UPDATE</c> then attributes the row,
    /// and only while it is UNATTRIBUTED: a thread id is minted by exactly one run, so a second claim is
    /// either this run retrying or a bug, and in both cases the first writer is the one to trust.
    /// </para>
    /// <para>
    /// Note this writes a ledger row even when deep-link retention is switched off (no recorder wired). That
    /// is deliberate: the row's PRIMARY job here is release bookkeeping, and a run whose conversations are
    /// invisible cannot release them. Where no sweeper is registered nothing ages the rows out, which is the
    /// same "keep them forever" the operator already asked for.
    /// </para>
    /// </summary>
    public void TrackRunHostedConversation(
        long reviewRunId,
        string threadId,
        string? title,
        DateTimeOffset? mintedAtUtc = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        using var gate = _gate.EnterScope();
        using var insert = _connection.CreateCommand();
        insert.CommandText = """
            INSERT OR IGNORE INTO deep_link_conversation (thread_id, title, minted_at)
            VALUES ($threadId, $title, $mintedAt);
            """;
        _ = insert.Parameters.AddWithValue("$threadId", threadId);
        _ = insert.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        _ = insert.Parameters.AddWithValue("$mintedAt", mintedAtUtc is { } minted ? Utc(minted) : UtcNow());
        _ = insert.ExecuteNonQuery();

        using var attribute = _connection.CreateCommand();
        attribute.CommandText = """
            UPDATE deep_link_conversation
            SET review_run_id = $runId
            WHERE thread_id = $threadId AND review_run_id IS NULL;
            """;
        _ = attribute.Parameters.AddWithValue("$runId", reviewRunId);
        _ = attribute.Parameters.AddWithValue("$threadId", threadId);
        _ = attribute.ExecuteNonQuery();
    }

    /// <summary>
    /// Every hosted conversation <paramref name="reviewRunId"/> minted — the review, the judge, each A/B
    /// variant — oldest first, each with whether its workspace release has been CONFIRMED. The terminal
    /// stage releases all of them before it lets host git near the slot or gives the slot back.
    /// <para>
    /// A conversation already discarded by the retention sweep has no row and is therefore absent here,
    /// which is correct: there is nothing left to release.
    /// </para>
    /// </summary>
    public IReadOnlyList<RunHostedConversationRow> ListRunHostedConversations(long reviewRunId)
    {
        var results = new List<RunHostedConversationRow>();
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT thread_id, title, minted_at, released_at, conversation_released_at
            FROM deep_link_conversation
            WHERE review_run_id = $runId
            ORDER BY minted_at, thread_id;
            """;
        _ = command.Parameters.AddWithValue("$runId", reviewRunId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(
                new RunHostedConversationRow(
                    reader.GetString(reader.GetOrdinal("thread_id")),
                    GetNullableString(reader, "title"),
                    ParseUtc(reader.GetString(reader.GetOrdinal("minted_at"))),
                    GetNullableString(reader, "released_at") is { } released ? ParseUtc(released) : null,
                    GetNullableString(reader, "conversation_released_at") is { } conversationReleased
                        ? ParseUtc(conversationReleased)
                        : null
                )
            );
        }

        return results;
    }

    /// <summary>
    /// Stamps a hosted conversation as durably DISABLED — it accepts no further send and can never remount.
    /// This is deliberately separate from backend release: a host may prove disablement while its gateway
    /// cannot observe mount teardown, in which case the old slot address remains permanently withheld.
    /// Idempotent and first-write-wins so retries preserve the instant at which disablement was first observed.
    /// </summary>
    public void MarkHostedConversationReleased(string threadId, DateTimeOffset releasedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE deep_link_conversation
            SET conversation_released_at = $releasedAt
            WHERE thread_id = $threadId AND conversation_released_at IS NULL;
            """;
        _ = command.Parameters.AddWithValue("$releasedAt", Utc(releasedAtUtc));
        _ = command.Parameters.AddWithValue("$threadId", threadId);
        _ = command.ExecuteNonQuery();
    }

    /// <summary>
    /// Stamps a conversation's hosted workspace as RELEASED — the host has positively confirmed the backend
    /// mount is gone. Idempotent and first-write-wins (<c>released_at IS NULL</c>), so a repeated release is
    /// a no-op rather than a second instant overwriting the one that actually happened.
    /// <para>
    /// This is NOT a delete and must never become one: the row keeps its retention clock so the posted
    /// comment's deep-link goes on resolving, and retention expiry stays the only path that discards a
    /// conversation.
    /// </para>
    /// </summary>
    public void MarkHostedWorkspaceReleased(string threadId, DateTimeOffset releasedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE deep_link_conversation
            SET released_at = COALESCE(released_at, $releasedAt),
                conversation_released_at = COALESCE(conversation_released_at, $releasedAt)
            WHERE thread_id = $threadId;
            """;
        _ = command.Parameters.AddWithValue("$releasedAt", Utc(releasedAtUtc));
        _ = command.Parameters.AddWithValue("$threadId", threadId);
        _ = command.ExecuteNonQuery();
    }

    // ── restart-safe pooled-slot ownership ───────────────────────────────────────────────────────

    public long AppendReviewSlotClaim(long reviewRunId, string slotHostPath, DateTimeOffset? claimedAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slotHostPath);
        slotHostPath = Path.GetFullPath(slotHostPath);

        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO review_slot_claim (review_run_id, slot_host_path, claimed_at)
            VALUES ($runId, $slotHostPath, $claimedAt)
            RETURNING id;
            """;
        _ = command.Parameters.AddWithValue("$runId", reviewRunId);
        _ = command.Parameters.AddWithValue("$slotHostPath", slotHostPath);
        _ = command.Parameters.AddWithValue("$claimedAt", claimedAtUtc is { } claimed ? Utc(claimed) : UtcNow());
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public long AppendReviewProvisionIntent(
        long slotClaimId,
        long reviewRunId,
        DateTimeOffset? provisioningBeganAtUtc = null
    )
    {
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO review_provision_intent (slot_claim_id, review_run_id, provisioning_began_at)
            SELECT id, $runId, $beganAt
            FROM review_slot_claim
            WHERE id = $claimId AND review_run_id = $runId AND resolved_at IS NULL
            RETURNING id;
            """;
        _ = command.Parameters.AddWithValue("$claimId", slotClaimId);
        _ = command.Parameters.AddWithValue("$runId", reviewRunId);
        _ = command.Parameters.AddWithValue("$beganAt", provisioningBeganAtUtc is { } began ? Utc(began) : UtcNow());
        return command.ExecuteScalar() is { } id
            ? Convert.ToInt64(id)
            : throw new InvalidOperationException(
                $"Review slot claim {slotClaimId} is missing, resolved, or does not belong to run {reviewRunId}."
            );
    }

    public void AssociateReviewProvisionIntent(
        long provisionIntentId,
        string threadId,
        DateTimeOffset? associatedAtUtc = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE review_provision_intent
            SET thread_id = $threadId, associated_at = $associatedAt
            WHERE id = $intentId AND thread_id IS NULL AND retracted_at IS NULL;
            """;
        _ = command.Parameters.AddWithValue("$threadId", threadId);
        _ = command.Parameters.AddWithValue("$associatedAt", associatedAtUtc is { } at ? Utc(at) : UtcNow());
        _ = command.Parameters.AddWithValue("$intentId", provisionIntentId);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException(
                $"Review provision intent {provisionIntentId} is missing or already settled."
            );
        }
    }

    public void RetractReviewProvisionIntent(long provisionIntentId, DateTimeOffset? retractedAtUtc = null)
    {
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE review_provision_intent
            SET retracted_at = $retractedAt
            WHERE id = $intentId AND thread_id IS NULL AND retracted_at IS NULL;
            """;
        _ = command.Parameters.AddWithValue("$retractedAt", retractedAtUtc is { } at ? Utc(at) : UtcNow());
        _ = command.Parameters.AddWithValue("$intentId", provisionIntentId);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException(
                $"Review provision intent {provisionIntentId} is missing or already settled."
            );
        }
    }

    public IReadOnlyList<ReviewSlotClaimRow> ListUnresolvedReviewSlotClaims()
    {
        using var gate = _gate.EnterScope();
        var claims = new List<ReviewSlotClaimRow>();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT c.id, c.review_run_id, c.slot_host_path, c.claimed_at,
                   i.id AS intent_id, i.provisioning_began_at, i.thread_id, i.associated_at,
                   i.retracted_at
            FROM review_slot_claim c
            LEFT JOIN review_provision_intent i ON i.slot_claim_id = c.id
            WHERE c.resolved_at IS NULL
            ORDER BY c.id, i.id;
            """;
        using var reader = command.ExecuteReader();
        ReviewSlotClaimRow? current = null;
        var intents = new List<ReviewProvisionIntentRow>();
        while (reader.Read())
        {
            var claimId = reader.GetInt64(reader.GetOrdinal("id"));
            if (current is null || current.Id != claimId)
            {
                if (current is not null)
                {
                    claims.Add(current with { Intents = [.. intents] });
                    intents.Clear();
                }

                current = new ReviewSlotClaimRow(
                    claimId,
                    reader.GetInt64(reader.GetOrdinal("review_run_id")),
                    reader.GetString(reader.GetOrdinal("slot_host_path")),
                    ParseUtc(reader.GetString(reader.GetOrdinal("claimed_at"))),
                    []
                );
            }

            if (!reader.IsDBNull(reader.GetOrdinal("intent_id")))
            {
                intents.Add(
                    new ReviewProvisionIntentRow(
                        reader.GetInt64(reader.GetOrdinal("intent_id")),
                        ParseUtc(reader.GetString(reader.GetOrdinal("provisioning_began_at"))),
                        GetNullableString(reader, "thread_id"),
                        GetNullableString(reader, "associated_at") is { } associated ? ParseUtc(associated) : null,
                        GetNullableString(reader, "retracted_at") is { } retracted ? ParseUtc(retracted) : null
                    )
                );
            }
        }

        if (current is not null)
        {
            claims.Add(current with { Intents = [.. intents] });
        }

        return claims;
    }

    public void ResolveReviewSlotClaim(long claimId, DateTimeOffset resolvedAtUtc)
    {
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE review_slot_claim
            SET resolved_at = $resolvedAt
            WHERE id = $claimId AND resolved_at IS NULL;
            """;
        _ = command.Parameters.AddWithValue("$resolvedAt", Utc(resolvedAtUtc));
        _ = command.Parameters.AddWithValue("$claimId", claimId);
        _ = command.ExecuteNonQuery();
    }

    // ── policy refusals (the collect-only capability gates, #536) ────────────────────────────────

    /// <summary>
    /// Appends one refusal to the ledger. Append-only and never deduplicated: a gate that fired twice
    /// refused twice, and collapsing repeats would erase the rate — which is the only thing that
    /// distinguishes a one-off from a reviewer that retries a write every run.
    /// </summary>
    public void RecordPolicyRefusal(PolicyRefusalRecord refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);

        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO policy_refusal (at_utc, kind, provider, subject, method, target, reason)
            VALUES ($atUtc, $kind, $provider, $subject, $method, $target, $reason);
            """;
        _ = command.Parameters.AddWithValue("$atUtc", Utc(refusal.AtUtc));
        _ = command.Parameters.AddWithValue("$kind", refusal.Kind.ToString());
        _ = command.Parameters.AddWithValue("$provider", refusal.Provider);
        _ = command.Parameters.AddWithValue("$subject", refusal.Subject);
        _ = command.Parameters.AddWithValue("$method", refusal.Method);
        _ = command.Parameters.AddWithValue("$target", refusal.Target);
        _ = command.Parameters.AddWithValue("$reason", refusal.Reason);
        _ = command.ExecuteNonQuery();
    }

    /// <summary>
    /// Every recorded refusal, oldest first. Ordered by <c>id</c> rather than <c>at_utc</c> so two refusals
    /// written inside the same timestamp tick still read back in the order they happened.
    /// </summary>
    public IReadOnlyList<PolicyRefusalRecord> ListPolicyRefusals()
    {
        var results = new List<PolicyRefusalRecord>();
        using var gate = _gate.EnterScope();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT at_utc, kind, provider, subject, method, target, reason
            FROM policy_refusal
            ORDER BY id;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(
                new PolicyRefusalRecord(
                    DateTimeOffset.Parse(
                        reader.GetString(reader.GetOrdinal("at_utc")),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind
                    ),
                    Enum.Parse<PolicyRefusalKind>(reader.GetString(reader.GetOrdinal("kind"))),
                    reader.GetString(reader.GetOrdinal("provider")),
                    reader.GetString(reader.GetOrdinal("subject")),
                    reader.GetString(reader.GetOrdinal("method")),
                    reader.GetString(reader.GetOrdinal("target")),
                    reader.GetString(reader.GetOrdinal("reason"))
                )
            );
        }

        return results;
    }

    // ── mapping helpers ──────────────────────────────────────────────────────────────────────────

    private static AuditSourceRecord MapAuditSourceRecord(SqliteDataReader reader) =>
        new(
            Id: reader.GetString(reader.GetOrdinal("id")),
            EngagementRoundId: reader.GetInt64(reader.GetOrdinal("engagement_round_id")),
            ThreadId: reader.GetString(reader.GetOrdinal("thread_id")),
            RunId: reader.GetString(reader.GetOrdinal("run_id")),
            GenerationId: reader.GetString(reader.GetOrdinal("generation_id")),
            ParentTurnId: GetNullableString(reader, "parent_turn_id"),
            Sequence: reader.GetInt64(reader.GetOrdinal("sequence")),
            RecordType: reader.GetString(reader.GetOrdinal("record_type")),
            Role: GetNullableString(reader, "role"),
            ModelId: GetNullableString(reader, "model_id"),
            ProviderId: GetNullableString(reader, "provider_id"),
            ContentSha256: reader.GetString(reader.GetOrdinal("content_sha256")),
            ByteCount: reader.GetInt64(reader.GetOrdinal("byte_count")),
            SourceContentSha256: reader.GetString(reader.GetOrdinal("source_content_sha256")),
            SourceByteCount: reader.GetInt64(reader.GetOrdinal("source_byte_count")),
            CaptureOutcome: Enum.Parse<AuditSourceCaptureOutcome>(
                reader.GetString(reader.GetOrdinal("capture_outcome"))
            ),
            GapReasonCode: GetNullableString(reader, "gap_reason_code"),
            CapturedAtUtc: GetRequiredTimestamp(reader, "captured_at"),
            CompletedAtUtc: GetNullableTimestamp(reader, "completed_at")
        );

    private static AuditRedactionRecord MapAuditRedactionRecord(SqliteDataReader reader) =>
        new(
            Id: reader.GetString(reader.GetOrdinal("id")),
            SourceRecordId: reader.GetString(reader.GetOrdinal("source_record_id")),
            Version: reader.GetInt32(reader.GetOrdinal("version")),
            RedactedContentSha256: reader.GetString(reader.GetOrdinal("redacted_content_sha256")),
            RedactedByteCount: reader.GetInt64(reader.GetOrdinal("redacted_byte_count")),
            AffectedRangesJson: reader.GetString(reader.GetOrdinal("affected_ranges_json")),
            ReasonCode: reader.GetString(reader.GetOrdinal("reason_code")),
            CreatedAtUtc: GetRequiredTimestamp(reader, "created_at")
        );

    private static PrEngagement MapEngagement(SqliteDataReader reader) =>
        new(
            Id: reader.GetInt64(reader.GetOrdinal("id")),
            RepoId: reader.GetInt64(reader.GetOrdinal("repo_id")),
            Provider: reader.GetString(reader.GetOrdinal("provider")),
            PrId: reader.GetString(reader.GetOrdinal("pr_id")),
            Lifecycle: Enum.Parse<PrLifecycleState>(reader.GetString(reader.GetOrdinal("lifecycle"))),
            LatestHeadSha: reader.GetString(reader.GetOrdinal("latest_head_sha")),
            LatestBaseSha: reader.GetString(reader.GetOrdinal("latest_base_sha")),
            LastReviewedHeadSha: GetNullableString(reader, "last_reviewed_head_sha"),
            LatestActivity: DeserializeWatermark(reader.GetString(reader.GetOrdinal("latest_activity_json"))),
            ConsumedActivity: GetNullableString(reader, "consumed_activity_json") is { } consumed
                ? DeserializeWatermark(consumed)
                : null,
            LastCompletedAt: GetNullableTimestamp(reader, "last_completed_at"),
            NextEligibleAt: GetNullableTimestamp(reader, "next_eligible_at"),
            ActiveRoundId: GetNullableInt64(reader, "active_round_id"),
            LatestRoundId: GetNullableInt64(reader, "latest_round_id"),
            RootSummaryReceiptJson: GetNullableString(reader, "root_summary_receipt_json"),
            UpdatedAt: GetRequiredTimestamp(reader, "updated_at")
        );

    private static EngagementRound MapEngagementRound(SqliteDataReader reader) =>
        new(
            Id: reader.GetInt64(reader.GetOrdinal("id")),
            PrEngagementId: reader.GetInt64(reader.GetOrdinal("pr_engagement_id")),
            Intent: Enum.Parse<EngagementRoundIntent>(reader.GetString(reader.GetOrdinal("intent"))),
            Status: Enum.Parse<EngagementRoundStatus>(reader.GetString(reader.GetOrdinal("status"))),
            HeadSha: reader.GetString(reader.GetOrdinal("head_sha")),
            BaseSha: reader.GetString(reader.GetOrdinal("base_sha")),
            ActivityLowerBound: GetNullableString(reader, "activity_lower_bound_json") is { } lower
                ? DeserializeWatermark(lower)
                : null,
            ActivityUpperBound: GetNullableString(reader, "activity_upper_bound_json") is { } upper
                ? DeserializeWatermark(upper)
                : null,
            PriorObservationBoundary: reader.GetInt64(reader.GetOrdinal("prior_observation_boundary")),
            ReviewRunId: GetNullableInt64(reader, "review_run_id"),
            GovernedFailureCount: reader.GetInt32(reader.GetOrdinal("governed_failure_count")),
            StartedAt: GetNullableTimestamp(reader, "started_at"),
            CompletedAt: GetNullableTimestamp(reader, "completed_at"),
            SupersededAt: GetNullableTimestamp(reader, "superseded_at"),
            ParkedAt: GetNullableTimestamp(reader, "parked_at"),
            ParkReason: GetNullableString(reader, "park_reason")
        );

    private static ReviewRun MapReviewRun(SqliteDataReader reader) =>
        new()
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            RepoId = reader.GetInt64(reader.GetOrdinal("repo_id")),
            PrId = reader.GetString(reader.GetOrdinal("pr_id")),
            HeadSha = reader.GetString(reader.GetOrdinal("head_sha")),
            BaseSha = reader.GetString(reader.GetOrdinal("base_sha")),
            TriggerWatermark = reader.GetString(reader.GetOrdinal("trigger_watermark")),
            ReviewKind = reader.GetString(reader.GetOrdinal("review_kind")),
            VariantId = reader.GetString(reader.GetOrdinal("variant_id")),
            Mode = reader.GetString(reader.GetOrdinal("mode")),
            EngagementRoundId = GetNullableInt64(reader, "engagement_round_id"),
            MergeSha = GetNullableString(reader, "merge_sha"),
            ModelProvider = GetNullableString(reader, "model_provider"),
            ModelId = GetNullableString(reader, "model_id"),
            PromptTemplateHash = GetNullableString(reader, "prompt_template_hash"),
            PolicyBundleVersion = GetNullableString(reader, "policy_bundle_version"),
            FeatureFlagSnapshot = GetNullableString(reader, "feature_flag_snapshot"),
            Stage = Enum.Parse<ReviewStage>(reader.GetString(reader.GetOrdinal("stage"))),
            WorkflowStatus = Enum.Parse<WorkflowStatus>(reader.GetString(reader.GetOrdinal("workflow_status"))),
            PrLifecycleState = Enum.Parse<PrLifecycleState>(reader.GetString(reader.GetOrdinal("pr_lifecycle_state"))),
            IsForkPr = reader.GetBoolean(reader.GetOrdinal("is_fork_pr")),
            IsTargetRepoPublic = reader.GetBoolean(reader.GetOrdinal("is_target_repo_public")),
            PrAuthor = GetNullableString(reader, "pr_author"),
            PrTitle = GetNullableString(reader, "pr_title"),
            PrDescription = GetNullableString(reader, "pr_description"),
            GovernedFailureCount = reader.GetInt32(reader.GetOrdinal("governed_failure_count")),
            ParkedAt = GetNullableTimestamp(reader, "parked_at"),
            ParkReason = GetNullableString(reader, "park_reason"),
        };

    private static OutboxEntry MapOutbox(SqliteDataReader reader) =>
        new()
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            IdempotencyKey = reader.GetString(reader.GetOrdinal("idempotency_key")),
            Provider = reader.GetString(reader.GetOrdinal("provider")),
            ReviewRunId = reader.GetInt64(reader.GetOrdinal("review_run_id")),
            Operation = reader.GetString(reader.GetOrdinal("operation")),
            ArtifactKind = reader.GetString(reader.GetOrdinal("artifact_kind")),
            Status = Enum.Parse<OutboxStatus>(reader.GetString(reader.GetOrdinal("status"))),
            BodyHash = GetNullableString(reader, "body_hash"),
            ProviderResponseId = GetNullableString(reader, "provider_response_id"),
        };

    private static string? GetNullableString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static long? GetNullableInt64(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    private static int? GetNullableInt32(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private static DateTimeOffset GetRequiredTimestamp(SqliteDataReader reader, string column) =>
        DateTimeOffset.Parse(
            reader.GetString(reader.GetOrdinal(column)),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind
        );

    /// <summary>
    /// Reads a nullable timestamp column written by <see cref="Utc"/> — round-trip ("O") UTC text, parsed
    /// with <see cref="DateTimeStyles.RoundtripKind"/> so the offset survives rather than being reinterpreted
    /// as local time.
    /// </summary>
    private static DateTimeOffset? GetNullableTimestamp(SqliteDataReader reader, string column) =>
        GetNullableString(reader, column) is { Length: > 0 } text
            ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            : null;

    /// <summary>
    /// Disposes the connection under the gate, so it is never torn down while an in-flight operation is
    /// still reading from it.
    /// </summary>
    public void Dispose()
    {
        using var gate = _gate.EnterScope();
        _connection.Dispose();
    }
}

/// <summary>
/// A PR that has been reviewed at least once, as enumerated by <see cref="ReviewStore.ListReviewedPrsAsync"/>
/// for the PR-lifecycle sweeper. Carries the repo <paramref name="Repo"/> and its <paramref name="Provider"/>
/// (the provider to poll) plus the external <paramref name="PrId"/>; the caller derives any branch name.
/// <paramref name="Author"/> is the identity that opened the PR, or null when no run recorded one —
/// the at-close feedback extraction writes nothing rather than guessing.
/// </summary>
internal sealed record ReviewedPrRow(RepoIdentity Repo, string Provider, string PrId, string? Author = null);

/// <summary>
/// One non-terminal run that the poll can no longer reach, as enumerated by
/// <see cref="ReviewStore.ListStrandedRuns"/>. <paramref name="Repo"/> is joined in because
/// <see cref="ReviewRun"/> carries only the local <c>repo_id</c> and the caller must ask the PR provider what
/// became of the PR. <paramref name="Superseded"/> is true when a later run exists for the same (repo, PR) —
/// this run's head has been reviewed again since, so it must be retired rather than resumed.
/// </summary>
internal sealed record StrandedRunRow(ReviewRun Run, RepoIdentity Repo, bool Superseded);

/// <summary>
/// The re-review context for a PR, as computed by <see cref="ReviewStore.GetPriorReviewSummary"/>:
/// the head sha of the most recently completed PRIMARY-variant review (<c>null</c> when this PR has
/// never completed a review), and how many rounds have completed so far.
/// </summary>
internal sealed record PriorReviewSummary(string? PrevHeadSha, int PriorReviewCount);

/// <summary>
/// One hosted conversation still reachable behind a posted deep-link, as enumerated by
/// <see cref="ReviewStore.ListDeepLinkConversationsMintedBefore"/>. <paramref name="MintedAt"/> is when the
/// conversation was provisioned — the start of its retention window, not when the review finished.
/// </summary>
internal sealed record DeepLinkConversationRow(string ThreadId, string? Title, DateTimeOffset MintedAt);

/// <summary>
/// One hosted conversation a REVIEW RUN minted, as enumerated by
/// <see cref="ReviewStore.ListRunHostedConversations"/>. <paramref name="ConversationReleasedAt"/> proves
/// the conversation accepts no send and cannot remount. <paramref name="ReleasedAt"/> is the stronger fact:
/// the review host positively confirmed backend mount quiescence, so the old slot address may be reused.
/// </summary>
internal sealed record RunHostedConversationRow(
    string ThreadId,
    string? Title,
    DateTimeOffset MintedAt,
    DateTimeOffset? ReleasedAt,
    DateTimeOffset? ConversationReleasedAt
);

internal sealed record ReviewSlotClaimRow(
    long Id,
    long ReviewRunId,
    string SlotHostPath,
    DateTimeOffset ClaimedAt,
    IReadOnlyList<ReviewProvisionIntentRow> Intents
);

internal sealed record ReviewProvisionIntentRow(
    long Id,
    DateTimeOffset ProvisioningBeganAt,
    string? ThreadId,
    DateTimeOffset? AssociatedAt,
    DateTimeOffset? RetractedAt
);
