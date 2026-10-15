using System.Text.Json;
using System.Text.Json.Nodes;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Migrations;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using Microsoft.Data.Sqlite;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

public sealed class ResetReviewRoundCommandTests
{
    [Fact]
    public async Task Verified_v13_reset_retires_only_exact_round_and_preserves_unknown_write()
    {
        using var fixture = new RoundResetFixture();
        var inspection = ResetReviewRoundCommand.Inspect(fixture.RoundId, fixture.Database.Path, fixture.Receipt);
        inspection["status"]!.GetValue<string>().Should().Be("ready");
        fixture.Query("PRAGMA user_version;").Should().Be("13");
        var calls = 0;
        var result = await ResetReviewRoundCommand.AcceptAsync(
            fixture.RoundId,
            fixture.Database.Path,
            fixture.Receipt,
            (identity, _) =>
            {
                calls++;
                JsonNode.DeepEquals(identity, fixture.Receipt["identity"]).Should().BeTrue();
                return Task.FromResult(fixture.Receipt);
            },
            CancellationToken.None
        );
        result["status"]!.GetValue<string>().Should().Be("accepted");
        calls.Should().Be(1);
        fixture.Query("PRAGMA user_version;").Should().Be("13");
        fixture.Query("SELECT outcome FROM workflow_round WHERE id=15;").Should().Be("OperatorReset");
        fixture.Query("SELECT completed_at IS NOT NULL FROM workflow_round WHERE id=15;").Should().Be("1");
        fixture.Query("SELECT workflow_status FROM review_run WHERE id=1;").Should().Be("Failed");
        fixture.Query("SELECT parked_at IS NOT NULL FROM review_run WHERE id=1;").Should().Be("1");
        fixture
            .Query(
                "SELECT payload FROM review_artifact WHERE artifact_kind='workflow-workspace-remote-preparation' ORDER BY id LIMIT 1;"
            )
            .Should()
            .Be(fixture.UnknownPayload);
        fixture
            .Query("SELECT COUNT(*) FROM review_artifact WHERE artifact_kind='workflow-round-reset-acceptance';")
            .Should()
            .Be("1");

        using var migratedCopy = new ReviewStore(fixture.Database.ConnectionString);
        migratedCopy.GetPendingWorkflowRounds().Should().BeEmpty();
        migratedCopy.GetWorkflowRound(fixture.RoundId)!.Outcome.Should().Be(WorkflowRoundOutcome.OperatorReset);
        migratedCopy.GetAmbiguousWorkflowRoundId(migratedCopy.GetReviewRun(1)!).Should().Be(fixture.RoundId);
        migratedCopy.GetAmbiguousWorkflowRoundId(migratedCopy.GetReviewRun(2)!).Should().BeNull();
        var original = migratedCopy.TryGetLatestArtifact(1, WorkflowWorkspace.RemotePreparationArtifactKind)!;
        migratedCopy.HasAcceptedRoundPreparation(1, original.Id, fixture.AssignmentId).Should().BeTrue();
        WorkflowWorkspace.HasAnyUnsettledPreparation(migratedCopy).Should().BeFalse();
        migratedCopy.AddArtifact(
            new ReviewArtifact
            {
                ReviewRunId = 1,
                ArtifactKind = WorkflowWorkspace.RemotePreparationArtifactKind,
                ArtifactSchemaVersion = 1,
                Provider = "operator",
                Payload = fixture.UnknownPayload,
            }
        );
        var next = migratedCopy.TryGetLatestArtifact(1, WorkflowWorkspace.RemotePreparationArtifactKind)!;
        migratedCopy.HasAcceptedRoundPreparation(1, next.Id, fixture.AssignmentId).Should().BeFalse();
        WorkflowWorkspace.HasAnyUnsettledPreparation(migratedCopy).Should().BeTrue();
        Func<Task> repeat = () =>
            ResetReviewRoundCommand.AcceptAsync(
                fixture.RoundId,
                fixture.Database.Path,
                fixture.Receipt,
                (_, _) => throw new Exception("Remote should not be re-read for a terminal round"),
                CancellationToken.None
            );
        await repeat.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Mismatch_conflict_and_changed_preparation_refuse_without_acceptance()
    {
        using var fixture = new RoundResetFixture();
        var originalBytes = fixture.Query(
            "SELECT payload FROM review_artifact WHERE artifact_kind='workflow-workspace-remote-preparation' LIMIT 1;"
        );
        var wrong = (JsonObject)fixture.Receipt.DeepClone();
        wrong["identity"]!["slot"] = 3;
        ResetReviewRoundCommand.Inspect(fixture.RoundId, fixture.Database.Path, wrong)["status"]!
            .GetValue<string>()
            .Should()
            .Be("blocked");
        Func<Task> wrongRemote = () =>
            ResetReviewRoundCommand.AcceptAsync(
                fixture.RoundId,
                fixture.Database.Path,
                fixture.Receipt,
                (_, _) => Task.FromResult(wrong),
                CancellationToken.None
            );
        await wrongRemote.Should().ThrowAsync<InvalidDataException>();
        fixture.Query("SELECT outcome FROM workflow_round WHERE id=15;").Should().Be("Unknown");

        fixture.Artifact(
            2,
            WorkflowWorkspace.AssignmentArtifactKind,
            JsonSerializer.Serialize(
                new WorkflowWorkspaceAssignment(new ReviewSlot("Astra", 4), true, "foreign", "review-run-2")
            )
        );
        ResetReviewRoundCommand.Inspect(fixture.RoundId, fixture.Database.Path, fixture.Receipt)["status"]!
            .GetValue<string>()
            .Should()
            .Be("blocked");
        fixture.Sql("DELETE FROM active_workflow_workspace WHERE review_run_id=2;");
        fixture.Sql("DELETE FROM review_artifact WHERE review_run_id=2;");
        Func<Task> changedPrep = () =>
            ResetReviewRoundCommand.AcceptAsync(
                fixture.RoundId,
                fixture.Database.Path,
                fixture.Receipt,
                (_, _) =>
                {
                    fixture.Artifact(1, WorkflowWorkspace.RemotePreparationArtifactKind, fixture.UnknownPayload);
                    return Task.FromResult(fixture.Receipt);
                },
                CancellationToken.None
            );
        await changedPrep
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*preparation evidence changed*");
        fixture.Query("SELECT outcome FROM workflow_round WHERE id=15;").Should().Be("Unknown");
        fixture
            .Query("SELECT COUNT(*) FROM review_artifact WHERE artifact_kind='workflow-round-reset-acceptance';")
            .Should()
            .Be("0");
        fixture
            .Query(
                "SELECT payload FROM review_artifact WHERE artifact_kind='workflow-workspace-remote-preparation' ORDER BY id LIMIT 1;"
            )
            .Should()
            .Be(originalBytes);
    }

    private sealed class RoundResetFixture : IDisposable
    {
        public readonly TempSqliteDatabase Database = new();
        public long RoundId => 15;
        public string AssignmentId => "0123456789abcdef0123456789abcdef";
        public string UnknownPayload =>
            "{\"AssignmentId\":\"0123456789abcdef0123456789abcdef\",\"Settled\":false,\"OutcomeUnknown\":true}";
        public JsonObject Receipt { get; }

        public RoundResetFixture()
        {
            using var connection = Open();
            foreach (var migration in SchemaMigrations.All.Where(item => item.Version <= 13))
            {
                using var command = connection.CreateCommand();
                command.CommandText = migration.Sql + $"\nPRAGMA user_version={migration.Version};";
                command.ExecuteNonQuery();
            }
            var slot = new ReviewSlot("Astra", 4);
            var head = new string('a', 40);
            var baseline = new string('b', 40);
            Receipt = new JsonObject
            {
                ["schema"] = 1,
                ["cleanupVersion"] = 2,
                ["status"] = "reset",
                ["identity"] = new JsonObject
                {
                    ["runId"] = 1,
                    ["assignmentId"] = AssignmentId,
                    ["workflowInstanceId"] = "review-round-15",
                    ["repository"] = "Astra",
                    ["slot"] = 4,
                    ["pr"] = "5331591",
                    ["sourcePath"] = slot.SourceRelativePath,
                    ["mode"] = "unprepared-baseline",
                    ["rootHead"] = head,
                    ["sourceBaseline"] = baseline,
                },
                ["rootHead"] = head,
                ["sourceBaseline"] = baseline,
                ["recoveryPath"] = ".git/review-setup/daemon-resets/" + AssignmentId,
                ["inventory"] = new JsonArray(),
            };
            Sql(
                """
                INSERT INTO repo (id,provider,normalized_key,display_name,org_or_owner,project,repo_name,created_at)
                VALUES (1,'ado','fixture/astra','Astra','fixture','fixture','Astra','2026-01-01');
                INSERT INTO review_run (id,repo_id,pr_id,head_sha,base_sha,trigger_watermark,review_kind,variant_id,mode,stage,workflow_status,pr_lifecycle_state,created_at,updated_at)
                VALUES (1,1,'5331591','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb','merged:fixture-event','full','primary','collect-only','Discovered','Pending','Merged','2026-01-01','2026-01-01');
                INSERT INTO review_run (id,repo_id,pr_id,head_sha,base_sha,trigger_watermark,review_kind,variant_id,mode,stage,workflow_status,pr_lifecycle_state,created_at,updated_at)
                VALUES (2,1,'independent-pr','dddddddddddddddddddddddddddddddddddddddd','bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb','independent','full','primary','collect-only','Discovered','RetryPending','Open','2026-01-01','2026-01-01');
                INSERT INTO workflow_round (id,repo_id,pr_id,head_sha,kind,event_key,frozen_input_json,workflow_instance_id,outcome,created_at,updated_at)
                VALUES (15,1,'5331591','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','Merged','merged:fixture-event','{}','review-round-15','Unknown','2026-01-01','2026-01-01');
                """
            );
            Artifact(
                1,
                WorkflowWorkspace.AssignmentArtifactKind,
                JsonSerializer.Serialize(new WorkflowWorkspaceAssignment(slot, false, AssignmentId, "review-round-15"))
            );
            Artifact(1, WorkflowWorkspace.RemotePreparationArtifactKind, UnknownPayload);
            Artifact(
                1,
                WorkflowWorkspace.PreparationDiagnosticArtifactKind,
                "{\"AssignmentId\":\"0123456789abcdef0123456789abcdef\",\"SessionId\":\"recorded-session\",\"WriteUnacknowledged\":true}"
            );
        }

        public void Sql(string sql)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public string Query(string sql)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar()?.ToString() ?? "<null>";
        }

        public void Artifact(int runId, string kind, string payload)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO review_artifact (review_run_id,artifact_schema_version,artifact_kind,provider,payload,created_at) VALUES ($run,1,$kind,'operator',$payload,'2026-01-01');";
            command.Parameters.AddWithValue("$run", runId);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$payload", payload);
            command.ExecuteNonQuery();
        }

        private SqliteConnection Open()
        {
            var connection = new SqliteConnection(Database.ConnectionString);
            connection.Open();
            return connection;
        }

        public void Dispose() => Database.Dispose();
    }
}
