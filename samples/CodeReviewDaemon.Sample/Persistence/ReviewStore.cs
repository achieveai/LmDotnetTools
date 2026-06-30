using CodeReviewDaemon.Sample.Persistence.Migrations;
using CodeReviewDaemon.Sample.Persistence.Models;
using Microsoft.Data.Sqlite;

namespace CodeReviewDaemon.Sample.Persistence;

/// <summary>
/// The daemon's orchestration source of truth. Wraps a single migrated SQLite connection and exposes
/// only the operations the orchestrator / poller / poster consume: repo identity normalization (§7),
/// idempotent <c>review_run</c> creation + resume-state updates (§6), opaque poll cursors (§12), the
/// crash-safe outbox (§11), and append-compatible artifacts (§14). It is intentionally not a generic
/// repository — every method has a current consumer.
/// </summary>
internal sealed class ReviewStore : IDisposable
{
    private readonly SqliteConnection _connection;

    public ReviewStore(string connectionString)
    {
        _connection = SqliteConnectionFactory.Open(connectionString);
        MigrationRunner.Migrate(_connection);
    }

    private static string UtcNow() => DateTimeOffset.UtcNow.ToString("O");

    // ── Repo identity (§7) ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the id of the <c>repo</c> row for <paramref name="identity"/>, inserting it on first
    /// sight. Lookup is by the case-folded <see cref="RepoIdentity.NormalizedKey"/>, so observations
    /// that differ only by casing collapse to one row while the first-seen display name is preserved.
    /// </summary>
    public long EnsureRepo(RepoIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

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

    // ── review_run (§6) ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Inserts <paramref name="run"/>, or returns the existing row when its identity tuple already
    /// exists. Idempotent: the same (repo, pr, head/base, watermark, kind, variant, mode) tuple never
    /// creates a second run.
    /// </summary>
    public ReviewRun CreateOrGetReviewRun(ReviewRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var now = UtcNow();
        using var insert = _connection.CreateCommand();
        insert.CommandText = """
            INSERT OR IGNORE INTO review_run (
                repo_id, pr_id, head_sha, base_sha, trigger_watermark, review_kind, variant_id, mode,
                merge_sha, model_provider, model_id, prompt_template_hash, policy_bundle_version,
                feature_flag_snapshot, stage, workflow_status, pr_lifecycle_state, created_at, updated_at)
            VALUES (
                $repoId, $prId, $head, $base, $watermark, $kind, $variant, $mode,
                $merge, $modelProvider, $modelId, $promptHash, $policyVersion,
                $flags, $stage, $workflow, $prState, $now, $now);
            """;
        _ = insert.Parameters.AddWithValue("$repoId", run.RepoId);
        _ = insert.Parameters.AddWithValue("$prId", run.PrId);
        _ = insert.Parameters.AddWithValue("$head", run.HeadSha);
        _ = insert.Parameters.AddWithValue("$base", run.BaseSha);
        _ = insert.Parameters.AddWithValue("$watermark", run.TriggerWatermark);
        _ = insert.Parameters.AddWithValue("$kind", run.ReviewKind);
        _ = insert.Parameters.AddWithValue("$variant", run.VariantId);
        _ = insert.Parameters.AddWithValue("$mode", run.Mode);
        _ = insert.Parameters.AddWithValue("$merge", (object?)run.MergeSha ?? DBNull.Value);
        _ = insert.Parameters.AddWithValue("$modelProvider", (object?)run.ModelProvider ?? DBNull.Value);
        _ = insert.Parameters.AddWithValue("$modelId", (object?)run.ModelId ?? DBNull.Value);
        _ = insert.Parameters.AddWithValue("$promptHash", (object?)run.PromptTemplateHash ?? DBNull.Value);
        _ = insert.Parameters.AddWithValue("$policyVersion", (object?)run.PolicyBundleVersion ?? DBNull.Value);
        _ = insert.Parameters.AddWithValue("$flags", (object?)run.FeatureFlagSnapshot ?? DBNull.Value);
        _ = insert.Parameters.AddWithValue("$stage", run.Stage.ToString());
        _ = insert.Parameters.AddWithValue("$workflow", run.WorkflowStatus.ToString());
        _ = insert.Parameters.AddWithValue("$prState", run.PrLifecycleState.ToString());
        _ = insert.Parameters.AddWithValue("$now", now);
        _ = insert.ExecuteNonQuery();

        using var select = _connection.CreateCommand();
        select.CommandText = """
            SELECT * FROM review_run
            WHERE repo_id = $repoId AND pr_id = $prId AND head_sha = $head AND base_sha = $base
              AND trigger_watermark = $watermark AND review_kind = $kind AND variant_id = $variant
              AND mode = $mode;
            """;
        _ = select.Parameters.AddWithValue("$repoId", run.RepoId);
        _ = select.Parameters.AddWithValue("$prId", run.PrId);
        _ = select.Parameters.AddWithValue("$head", run.HeadSha);
        _ = select.Parameters.AddWithValue("$base", run.BaseSha);
        _ = select.Parameters.AddWithValue("$watermark", run.TriggerWatermark);
        _ = select.Parameters.AddWithValue("$kind", run.ReviewKind);
        _ = select.Parameters.AddWithValue("$variant", run.VariantId);
        _ = select.Parameters.AddWithValue("$mode", run.Mode);
        using var reader = select.ExecuteReader();
        _ = reader.Read();
        return MapReviewRun(reader);
    }

    public ReviewRun? GetReviewRun(long id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT * FROM review_run WHERE id = $id;";
        _ = command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapReviewRun(reader) : null;
    }

    /// <summary>Advances the three resume axes for a run (orchestrator step completion).</summary>
    public void UpdateReviewRunState(long id, ReviewStage stage, WorkflowStatus workflowStatus, PrLifecycleState prLifecycleState)
    {
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

    // ── poll_cursor (§12) ────────────────────────────────────────────────────────────────────────

    /// <summary>Upserts a cursor keyed by (provider, scope).</summary>
    public void SaveCursor(OpaqueCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);

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

        return CursorReadResult.Usable(new OpaqueCursor
        {
            Provider = reader.GetString(reader.GetOrdinal("provider")),
            Scope = reader.GetString(reader.GetOrdinal("scope")),
            CursorVersion = version,
            CursorPayload = payload,
            HighWaterMark = GetNullableString(reader, "high_water_mark"),
            Etag = GetNullableString(reader, "etag"),
            Continuation = GetNullableString(reader, "continuation"),
            SinceTimestamp = GetNullableString(reader, "since_timestamp"),
        });
    }

    // ── review_outbox (§11) ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Enqueues an outbox entry, or returns the existing one when its idempotency key already exists.
    /// Idempotent enqueue is the first half of exactly-once posting.
    /// </summary>
    public OutboxEntry EnqueueOutbox(OutboxEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

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
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT * FROM review_outbox WHERE id = $id;";
        _ = command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapOutbox(reader) : null;
    }

    private OutboxEntry? GetOutboxByKey(string idempotencyKey)
    {
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
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT * FROM review_artifact WHERE review_run_id = $runId ORDER BY id;";
        _ = command.Parameters.AddWithValue("$runId", reviewRunId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new ReviewArtifact
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                ReviewRunId = reader.GetInt64(reader.GetOrdinal("review_run_id")),
                ArtifactSchemaVersion = reader.GetInt32(reader.GetOrdinal("artifact_schema_version")),
                ArtifactKind = reader.GetString(reader.GetOrdinal("artifact_kind")),
                Provider = reader.GetString(reader.GetOrdinal("provider")),
                Payload = reader.GetString(reader.GetOrdinal("payload")),
            });
        }

        return results;
    }

    // ── mapping helpers ──────────────────────────────────────────────────────────────────────────

    private static ReviewRun MapReviewRun(SqliteDataReader reader) => new()
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
        MergeSha = GetNullableString(reader, "merge_sha"),
        ModelProvider = GetNullableString(reader, "model_provider"),
        ModelId = GetNullableString(reader, "model_id"),
        PromptTemplateHash = GetNullableString(reader, "prompt_template_hash"),
        PolicyBundleVersion = GetNullableString(reader, "policy_bundle_version"),
        FeatureFlagSnapshot = GetNullableString(reader, "feature_flag_snapshot"),
        Stage = Enum.Parse<ReviewStage>(reader.GetString(reader.GetOrdinal("stage"))),
        WorkflowStatus = Enum.Parse<WorkflowStatus>(reader.GetString(reader.GetOrdinal("workflow_status"))),
        PrLifecycleState = Enum.Parse<PrLifecycleState>(reader.GetString(reader.GetOrdinal("pr_lifecycle_state"))),
    };

    private static OutboxEntry MapOutbox(SqliteDataReader reader) => new()
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

    public void Dispose() => _connection.Dispose();
}
