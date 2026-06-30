using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P4.1 — the judge grades a review and <b>persists only</b> a <c>judge</c> artifact carrying exactly
/// <c>{score, rationale, variant_id}</c> (AC#7). These tests pin that contract against a real
/// <see cref="ReviewStore"/>: the verdict round-trips, the payload has no extra fields, and a malformed
/// verdict is still recorded rather than thrown — the judge never auto-routes or rewrites anything.
/// </summary>
public sealed class JudgeAgentTests
{
    private const string RunId = "judge-run-1";
    private const string Provider = "github";

    [Fact]
    public async Task JudgeAsync_persists_only_a_judge_artifact_with_score_rationale_and_variant()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var reviewRunId = SeedRun(store);

        var agent = new FakeMultiTurnAgent(
            RunId,
            new TextMessage
            {
                Text = "{\"score\": 8, \"rationale\": \"Thorough; caught the null deref.\"}",
                Role = Role.Assistant,
                RunId = RunId,
            }
        );

        var verdict = await Judge(agent, store).JudgeAsync(
            new JudgeRequest(reviewRunId, Provider, "b", "Grade this review:\n## Review\n..."),
            CancellationToken.None
        );

        verdict.Score.Should().Be(8);
        verdict.Rationale.Should().Be("Thorough; caught the null deref.");
        verdict.VariantId.Should().Be("b");

        var artifacts = store.GetArtifacts(reviewRunId);
        var judge = artifacts.Should().ContainSingle().Subject;
        judge.ArtifactKind.Should().Be(JudgeAgent.JudgeArtifactKind);
        judge.Provider.Should().Be(Provider);
        judge.ArtifactSchemaVersion.Should().Be(JudgeAgent.JudgeArtifactSchemaVersion);

        // AC#7: the payload carries EXACTLY score, rationale, variant_id — nothing more.
        using var payload = JsonDocument.Parse(judge.Payload);
        var properties = payload.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        properties.Should().HaveCount(3);
        ReadInt(payload, "Score").Should().Be(8);
        ReadString(payload, "Rationale").Should().Be("Thorough; caught the null deref.");
        ReadString(payload, "VariantId").Should().Be("b");
    }

    [Fact]
    public async Task JudgeAsync_unwraps_a_fenced_json_verdict()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var reviewRunId = SeedRun(store);

        var agent = new FakeMultiTurnAgent(
            RunId,
            new TextMessage
            {
                Text = "Here is my verdict:\n```json\n{\"score\": 5, \"rationale\": \"Adequate.\"}\n```",
                Role = Role.Assistant,
                RunId = RunId,
            }
        );

        var verdict = await Judge(agent, store).JudgeAsync(
            new JudgeRequest(reviewRunId, Provider, "primary", "grade"),
            CancellationToken.None
        );

        verdict.Score.Should().Be(5);
        verdict.Rationale.Should().Be("Adequate.");
    }

    [Fact]
    public async Task JudgeAsync_records_a_malformed_verdict_instead_of_throwing()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var reviewRunId = SeedRun(store);

        var agent = new FakeMultiTurnAgent(
            RunId,
            new TextMessage
            {
                Text = "I could not produce a structured verdict.",
                Role = Role.Assistant,
                RunId = RunId,
            }
        );

        var verdict = await Judge(agent, store).JudgeAsync(
            new JudgeRequest(reviewRunId, Provider, "b", "grade"),
            CancellationToken.None
        );

        // A malformed verdict defaults to score 0 with the raw text as the rationale — still persisted.
        verdict.Score.Should().Be(0);
        verdict.Rationale.Should().Be("I could not produce a structured verdict.");
        store.GetArtifacts(reviewRunId).Should().ContainSingle();
    }

    [Fact]
    public async Task JudgeAsync_records_the_variant_id_verbatim()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var reviewRunId = SeedRun(store);

        var agent = new FakeMultiTurnAgent(
            RunId,
            new TextMessage
            {
                Text = "{\"score\": 9, \"rationale\": \"Strong.\"}",
                Role = Role.Assistant,
                RunId = RunId,
            }
        );

        _ = await Judge(agent, store).JudgeAsync(
            new JudgeRequest(reviewRunId, Provider, "primary", "grade"),
            CancellationToken.None
        );

        using var payload = JsonDocument.Parse(store.GetArtifacts(reviewRunId)[0].Payload);
        ReadString(payload, "VariantId").Should().Be("primary");
    }

    private static JudgeAgent Judge(FakeMultiTurnAgent agent, ReviewStore store) =>
        new(agent, store, NullLogger<JudgeAgent>.Instance);

    private static int ReadInt(JsonDocument document, string property) =>
        document.RootElement.GetProperty(property).GetInt32();

    private static string? ReadString(JsonDocument document, string property) =>
        document.RootElement.GetProperty(property).GetString();

    private static long SeedRun(ReviewStore store)
    {
        var repoId = store.EnsureRepo(new RepoIdentity
        {
            Provider = "github",
            OrgOrOwner = "achieveai",
            RepoName = "LmDotnetTools",
            RepoStableId = "R_node_123",
        });
        return store.CreateOrGetReviewRun(new ReviewRun
        {
            RepoId = repoId,
            PrId = "118",
            HeadSha = "head-sha",
            BaseSha = "base-sha",
            TriggerWatermark = "wm-1",
            ReviewKind = "full",
            VariantId = "primary",
            Mode = "collect-only",
            Stage = ReviewStage.Reviewed,
            WorkflowStatus = WorkflowStatus.Running,
            PrLifecycleState = PrLifecycleState.Open,
        }).Id;
    }
}
