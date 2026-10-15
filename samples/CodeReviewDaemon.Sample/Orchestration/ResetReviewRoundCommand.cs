using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeReviewDaemon.Sample.Hosting;
using CodeReviewDaemon.Sample.Workspace;
using Microsoft.Data.Sqlite;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>Accepts an independently re-read slot reset without reconstructing the old write outcome.</summary>
internal static class ResetReviewRoundCommand
{
    internal const string AcceptanceKind = "workflow-round-reset-acceptance";
    private const string ReceiptKind = "workflow-round-reset-receipt";
    private const string ParkReason =
        "Operator accepted exact round slot reset; original remote write outcome remains unknown.";

    public static JsonObject Inspect(long roundId, string databasePath, JsonObject? receipt)
    {
        using var connection = Open(databasePath, write: false);
        return InspectCore(connection, roundId, receipt);
    }

    /// <summary>The caller must re-read the remote receipt from the recorded session, not trust the supplied file.</summary>
    public static async Task<JsonObject> AcceptAsync(
        long roundId,
        string databasePath,
        JsonObject suppliedReceipt,
        Func<JsonObject, CancellationToken, Task<JsonObject>> rereadRemote,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(suppliedReceipt);
        ArgumentNullException.ThrowIfNull(rereadRemote);
        using var lease = RunPrCoordinatorLease.TryAcquire(databasePath);
        if (!lease.IsAcquired)
            throw new InvalidOperationException("Stop the workflow coordinator before accepting a round reset.");
        using var connection = Open(databasePath, write: true);
        var inspection = InspectCore(connection, roundId, suppliedReceipt);
        RequireReady(inspection);
        var identity = (JsonObject)inspection["expectedIdentity"]!.DeepClone();
        var remoteReceipt = await rereadRemote(identity, ct).ConfigureAwait(false);
        if (!JsonNode.DeepEquals(remoteReceipt, suppliedReceipt))
            throw new InvalidDataException("Supplied receipt differs from the independently re-read remote receipt.");
        // The remote helper's receipt branch verifies the fresh slot under its selected-slot lock.
        RequireReceipt(remoteReceipt, identity);
        ct.ThrowIfCancellationRequested();
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = InspectCore(connection, roundId, suppliedReceipt, transaction);
        RequireReady(current);
        if (
            !JsonNode.DeepEquals(current["expectedIdentity"], identity)
            || current["assignmentArtifactId"]?.GetValue<long>() != inspection["assignmentArtifactId"]?.GetValue<long>()
            || current["preparationArtifactId"]?.GetValue<long>()
                != inspection["preparationArtifactId"]?.GetValue<long>()
            || current["diagnosticArtifactId"]?.GetValue<long>() != inspection["diagnosticArtifactId"]?.GetValue<long>()
        )
            throw new InvalidOperationException(
                "Round or its latest preparation evidence changed during remote receipt verification."
            );
        var runId = current["candidateRunId"]!.GetValue<long>();
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var receiptHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(remoteReceipt.ToJsonString())));
        var acceptance = new JsonObject
        {
            ["schema"] = 1,
            ["roundId"] = roundId,
            ["runId"] = runId,
            ["kind"] = current["roundKind"]!.DeepClone(),
            ["eventKey"] = current["eventKey"]!.DeepClone(),
            ["assignmentArtifactId"] = current["assignmentArtifactId"]!.DeepClone(),
            ["preparationArtifactId"] = current["preparationArtifactId"]!.DeepClone(),
            ["assignmentId"] = identity["assignmentId"]!.DeepClone(),
            ["identity"] = identity.DeepClone(),
            ["receiptSha256"] = receiptHash,
            ["originalOutcomeUnknown"] = true,
            ["disposition"] = "OperatorReset",
        };
        InsertArtifact(connection, transaction, runId, ReceiptKind, remoteReceipt.ToJsonString(), now);
        InsertArtifact(connection, transaction, runId, AcceptanceKind, acceptance.ToJsonString(), now);
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE workflow_round SET outcome = 'OperatorReset', completed_at = $now, updated_at = $now
                WHERE id = $round AND completed_at IS NULL AND outcome IN ('Pending', 'Running', 'Failed', 'Unknown')
                  AND kind = $kind AND event_key = $event
                  AND (workflow_instance_id IS NULL OR workflow_instance_id = $instance);
                """;
            update.Parameters.AddWithValue("$round", roundId);
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$kind", current["roundKind"]!.GetValue<string>());
            update.Parameters.AddWithValue("$event", current["eventKey"]!.GetValue<string>());
            update.Parameters.AddWithValue("$instance", identity["workflowInstanceId"]!.GetValue<string>());
            if (update.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Round changed before operator acceptance.");
        }
        using (var park = connection.CreateCommand())
        {
            park.Transaction = transaction;
            park.CommandText = """
                UPDATE review_run SET parked_at = $now, park_reason = $reason, workflow_status = 'Failed'
                WHERE id = $run AND workflow_status <> 'Completed' AND parked_at IS NULL;
                """;
            park.Parameters.AddWithValue("$run", runId);
            park.Parameters.AddWithValue("$now", now);
            park.Parameters.AddWithValue("$reason", ParkReason);
            park.ExecuteNonQuery();
        }
        transaction.Commit();
        return new JsonObject
        {
            ["status"] = "accepted",
            ["roundId"] = roundId,
            ["runId"] = runId,
            ["receiptSha256"] = receiptHash,
        };
    }

    private static void RequireReady(JsonObject inspection)
    {
        if (inspection["status"]?.GetValue<string>() != "ready")
            throw new InvalidOperationException(
                string.Join(" ", inspection["blockers"]!.AsArray().Select(x => x!.GetValue<string>()))
            );
    }

    private static SqliteConnection Open(string path, bool write)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = write ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString()
        );
        connection.Open();
        return connection;
    }

    private static JsonObject InspectCore(
        SqliteConnection connection,
        long roundId,
        JsonObject? receipt,
        SqliteTransaction? tx = null
    )
    {
        long version;
        using (var pragma = connection.CreateCommand())
        {
            pragma.Transaction = tx;
            pragma.CommandText = "PRAGMA user_version;";
            version = Convert.ToInt64(pragma.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        if (version is < 13 or > 15)
            throw new InvalidOperationException(
                "Reset acceptance supports only schema v13–v15; no migration will run."
            );
        using var roundQuery = connection.CreateCommand();
        roundQuery.Transaction = tx;
        roundQuery.CommandText =
            version >= 14
                ? "SELECT repo_id, pr_id, head_sha, kind, event_key, outcome, completed_at, workflow_instance_id, review_run_id FROM workflow_round WHERE id = $id;"
                : "SELECT repo_id, pr_id, head_sha, kind, event_key, outcome, completed_at, workflow_instance_id FROM workflow_round WHERE id = $id;";
        roundQuery.Parameters.AddWithValue("$id", roundId);
        long repoId;
        string prId,
            headSha,
            kind,
            eventKey,
            outcome;
        long? ownerId;
        bool completed;
        string? workflowInstance;
        using (var reader = roundQuery.ExecuteReader())
        {
            if (!reader.Read())
                throw new InvalidOperationException("Workflow round is missing.");
            repoId = reader.GetInt64(0);
            prId = reader.GetString(1);
            headSha = reader.GetString(2);
            kind = reader.GetString(3);
            eventKey = reader.GetString(4);
            outcome = reader.GetString(5);
            completed = !reader.IsDBNull(6);
            workflowInstance = reader.IsDBNull(7) ? null : reader.GetString(7);
            ownerId = version >= 14 && !reader.IsDBNull(8) ? reader.GetInt64(8) : null;
        }
        var blockers = new JsonArray();
        if (completed || outcome == "OperatorReset")
            blockers.Add("Round already has a terminal disposition; acceptance cannot rewrite it.");
        if (kind != "Merged")
            blockers.Add("Only the exact merged round is supported by this acceptance.");
        if (workflowInstance is not null && workflowInstance != $"review-round-{roundId}")
            blockers.Add("Workflow instance does not match the round.");
        using var candidates = connection.CreateCommand();
        candidates.Transaction = tx;
        candidates.CommandText = """
            SELECT id, trigger_watermark, mode, base_sha, workflow_status, parked_at FROM review_run
            WHERE repo_id = $repo AND pr_id = $pr AND head_sha = $head
              AND ($owner IS NULL OR id = $owner) ORDER BY id;
            """;
        candidates.Parameters.AddWithValue("$repo", repoId);
        candidates.Parameters.AddWithValue("$pr", prId);
        candidates.Parameters.AddWithValue("$head", headSha);
        candidates.Parameters.AddWithValue("$owner", (object?)ownerId ?? DBNull.Value);
        var runs = new List<(long Id, string Trigger, string Mode, string Base, string Status, bool Parked)>();
        using (var reader = candidates.ExecuteReader())
            while (reader.Read())
                runs.Add(
                    (
                        reader.GetInt64(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        !reader.IsDBNull(5)
                    )
                );
        if (runs.Count != 1)
            blockers.Add("Round has no unique matching run identity.");
        var result = new JsonObject
        {
            ["status"] = "blocked",
            ["roundId"] = roundId,
            ["roundKind"] = kind,
            ["roundOutcome"] = outcome,
            ["eventKey"] = eventKey,
            ["ownerRunId"] = ownerId,
            ["candidateRunId"] = runs.Count == 1 ? runs[0].Id : null,
            ["blockers"] = blockers,
        };
        if (runs.Count == 1)
        {
            var run = runs[0];
            if (run.Mode != "collect-only" || run.Trigger != eventKey || (run.Parked && run.Status != "Completed"))
                blockers.Add("Candidate run is not the exact unparked collect-only merged event.");
            var assignment = LatestArtifact(connection, tx, run.Id, WorkflowWorkspace.AssignmentArtifactKind);
            if (assignment is null)
                blockers.Add("Owning run has no workspace assignment.");
            else
            {
                var parsed = JsonSerializer.Deserialize<WorkflowWorkspaceAssignment>(assignment.Value.Payload);
                if (
                    parsed is null
                    || parsed.WorkflowInstanceId != $"review-round-{roundId}"
                    || string.IsNullOrWhiteSpace(parsed.AssignmentId)
                )
                    blockers.Add("Assignment does not belong to this workflow round.");
                else
                {
                    parsed.Slot.Validate();
                    result["assignmentId"] = parsed.AssignmentId;
                    result["assignmentArtifactId"] = assignment.Value.Id;
                    result["slot"] = parsed.Slot.Name;
                    result["sourcePath"] = parsed.Slot.SourceRelativePath;
                    result["assignmentActive"] = parsed.Active;
                    if (parsed.Active)
                        blockers.Add("Assignment is active; an owner transition is not proven.");
                    using var conflict = connection.CreateCommand();
                    conflict.Transaction = tx;
                    conflict.CommandText = """
                        SELECT COUNT(*) FROM active_workflow_workspace active
                        JOIN review_artifact artifact ON artifact.id = active.artifact_id
                        WHERE active.review_run_id <> $run
                          AND json_extract(artifact.payload, '$.Slot.RepositoryName') = $repository
                          AND json_extract(artifact.payload, '$.Slot.Index') = $slot;
                        """;
                    conflict.Parameters.AddWithValue("$run", run.Id);
                    conflict.Parameters.AddWithValue("$repository", parsed.Slot.RepositoryName);
                    conflict.Parameters.AddWithValue("$slot", parsed.Slot.Index);
                    if (Convert.ToInt64(conflict.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                        blockers.Add("Another active assignment occupies the same slot.");
                    var checkout = LatestArtifact(connection, tx, run.Id, WorkflowWorkspace.CheckoutArtifactKind);
                    if (checkout is not null)
                        blockers.Add(
                            "Prepared checkout belongs to a different reset mode; unprepared baseline required."
                        );
                    if (receipt?["identity"] is JsonObject submitted)
                    {
                        var rootHead = submitted["rootHead"]?.GetValue<string>();
                        var sourceBaseline = submitted["sourceBaseline"]?.GetValue<string>();
                        if (!IsCanonicalSha(rootHead) || !IsCanonicalSha(sourceBaseline))
                            blockers.Add("Unprepared reset requires exact canonical baseline Git identities.");
                        else
                        {
                            result["expectedIdentity"] = new JsonObject
                            {
                                ["runId"] = run.Id,
                                ["assignmentId"] = parsed.AssignmentId,
                                ["workflowInstanceId"] = parsed.WorkflowInstanceId,
                                ["repository"] = parsed.Slot.RepositoryName,
                                ["slot"] = parsed.Slot.Index,
                                ["pr"] = prId,
                                ["sourcePath"] = parsed.Slot.SourceRelativePath,
                                ["mode"] = "unprepared-baseline",
                                ["rootHead"] = rootHead,
                                ["sourceBaseline"] = sourceBaseline,
                            };
                        }
                    }
                }
            }
            var diagnostic = LatestArtifact(
                connection,
                tx,
                run.Id,
                WorkflowWorkspace.PreparationDiagnosticArtifactKind
            );
            if (diagnostic is null)
                blockers.Add("Recorded remote session diagnostic is missing.");
            else
            {
                result["diagnosticArtifactId"] = diagnostic.Value.Id;
                var recorded = JsonNode.Parse(diagnostic.Value.Payload);
                result["recordedSessionId"] = recorded?["SessionId"]?.GetValue<string>();
                if (
                    string.IsNullOrWhiteSpace(result["recordedSessionId"]?.GetValue<string>())
                    || recorded?["AssignmentId"]?.GetValue<string>() != result["assignmentId"]?.GetValue<string>()
                    || recorded?["WriteUnacknowledged"]?.GetValue<bool>() != true
                )
                    blockers.Add("Remote session diagnostic does not bind the unknown assignment.");
            }
            var remote = LatestArtifact(connection, tx, run.Id, WorkflowWorkspace.RemotePreparationArtifactKind);
            if (remote is null)
                blockers.Add("Remote preparation evidence is missing.");
            else
            {
                result["preparationArtifactId"] = remote.Value.Id;
                var state = JsonNode.Parse(remote.Value.Payload);
                result["remotePreparationSettled"] = state?["Settled"]?.GetValue<bool>() == true;
                result["remotePreparationOutcomeUnknown"] = state?["OutcomeUnknown"]?.GetValue<bool>() == true;
                if (state?["OutcomeUnknown"]?.GetValue<bool>() != true || state["Settled"]?.GetValue<bool>() == true)
                    blockers.Add("Only the preserved unknown-write preparation is accepted by this path.");
                if (state?["AssignmentId"]?.GetValue<string>() != result["assignmentId"]?.GetValue<string>())
                    blockers.Add("Remote preparation belongs to another assignment.");
            }
            using var outbox = connection.CreateCommand();
            outbox.Transaction = tx;
            outbox.CommandText = "SELECT COUNT(*) FROM review_outbox WHERE review_run_id = $run;";
            outbox.Parameters.AddWithValue("$run", run.Id);
            if (Convert.ToInt64(outbox.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                blockers.Add("Publication outbox is not empty; effects require separate reconciliation.");
            if (LatestArtifact(connection, tx, run.Id, AcceptanceKind) is not null)
                blockers.Add("This run already has an operator reset acceptance.");
        }
        if (receipt is null)
            blockers.Add("Exact remote receipt and independent reinspection are required before confirmation.");
        else if (result["expectedIdentity"] is JsonObject expected)
        {
            try
            {
                RequireReceipt(receipt, expected);
            }
            catch (Exception error) when (error is InvalidDataException or JsonException or InvalidOperationException)
            {
                blockers.Add(error.Message);
            }
        }
        result["status"] = blockers.Count == 0 ? "ready" : "blocked";
        return result;
    }

    private static bool IsCanonicalSha(string? value) =>
        value is { Length: 40 } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static void RequireReceipt(JsonObject receipt, JsonObject identity)
    {
        if (receipt.ToJsonString().Length > 32 * 1024 * 1024)
            throw new InvalidDataException("Reset receipt exceeds the operator verification bound.");
        if (
            identity["mode"]?.GetValue<string>() != "unprepared-baseline"
            || identity["rootHead"]?.GetValue<string>() != receipt["rootHead"]?.GetValue<string>()
            || identity["sourceBaseline"]?.GetValue<string>() != receipt["sourceBaseline"]?.GetValue<string>()
            || receipt["recoveryPath"]?.GetValue<string>()
                != ".git/review-setup/daemon-resets/" + identity["assignmentId"]?.GetValue<string>()
            || receipt["schema"]?.GetValue<int>() != 1
            || receipt["cleanupVersion"]?.GetValue<int>() != 2
            || receipt["status"]?.GetValue<string>() != "reset"
            || !JsonNode.DeepEquals(receipt["identity"], identity)
            || string.IsNullOrWhiteSpace(receipt["rootHead"]?.GetValue<string>())
            || string.IsNullOrWhiteSpace(receipt["sourceBaseline"]?.GetValue<string>())
            || string.IsNullOrWhiteSpace(receipt["recoveryPath"]?.GetValue<string>())
        )
            throw new InvalidDataException(
                "Reset receipt does not confirm the exact run, round, event, assignment and fresh slot."
            );
    }

    private static (long Id, string Payload)? LatestArtifact(
        SqliteConnection connection,
        SqliteTransaction? tx,
        long runId,
        string kind
    )
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            "SELECT id, payload FROM review_artifact WHERE review_run_id = $run AND artifact_kind = $kind ORDER BY id DESC LIMIT 1;";
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$kind", kind);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetInt64(0), reader.GetString(1)) : null;
    }

    private static void InsertArtifact(
        SqliteConnection connection,
        SqliteTransaction tx,
        long runId,
        string kind,
        string payload,
        string now
    )
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            "INSERT INTO review_artifact (review_run_id, artifact_schema_version, artifact_kind, provider, payload, created_at) VALUES ($run, 1, $kind, 'operator', $payload, $now);";
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
    }
}
