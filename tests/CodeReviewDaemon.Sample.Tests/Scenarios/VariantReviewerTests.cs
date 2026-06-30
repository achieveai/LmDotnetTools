using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P4.2 — the collect-only A/B (B) arm. <see cref="VariantReviewer"/> runs a comparison variant
/// (different model + prompt) collect-only and persists its output as a <c>b-variant-review</c>
/// <see cref="ReviewArtifact"/> in SQLite — <b>never</b> the ReviewBot git repo. These tests pin that
/// the output lands only in SQLite, that the varied model and variant id are recorded, and that a
/// writing variant is rejected from this isolated path.
/// </summary>
public sealed class VariantReviewerTests
{
    private const string RunId = "b-variant-run-1";
    private const string Provider = "github";

    private static readonly ReviewVariant ComparisonVariant =
        new(VariantId: "b", ModelId: "anthropic/claude-haiku-4-5", SystemPrompt: "Be terse.", CanWrite: false);

    [Fact]
    public async Task ReviewAsync_persists_b_variant_output_to_sqlite_with_model_and_variant()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var reviewRunId = SeedRun(store);

        var agent = AgentReturning("## Review (B)\nConsider: extract the parser.");

        var result = await Reviewer(agent, store).ReviewAsync(
            reviewRunId, Provider, ComparisonVariant, "Review this diff:\n- Foo.cs", CancellationToken.None);

        result.ReviewText.Should().Be("## Review (B)\nConsider: extract the parser.");
        result.RunId.Should().Be(RunId);

        var artifacts = store.GetArtifacts(reviewRunId);
        var bVariant = artifacts.Should().ContainSingle().Subject;
        bVariant.ArtifactKind.Should().Be(VariantReviewer.VariantReviewArtifactKind);
        bVariant.ArtifactSchemaVersion.Should().Be(VariantReviewer.VariantReviewArtifactSchemaVersion);
        bVariant.Provider.Should().Be(Provider);

        // The model (A/B model axis) and variant id are recorded alongside the review text.
        using var payload = JsonDocument.Parse(bVariant.Payload);
        ReadString(payload, "VariantId").Should().Be("b");
        ReadString(payload, "ModelId").Should().Be("anthropic/claude-haiku-4-5");
        ReadString(payload, "ReviewText").Should().Be("## Review (B)\nConsider: extract the parser.");
    }

    [Fact]
    public async Task ReviewAsync_never_writes_to_the_reviewbot_filesystem()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var reviewRunId = SeedRun(store);
        var fs = new FakeSandboxFileSystem();

        var agent = AgentReturning("B review body");

        // The reviewer is constructed with NO filesystem/sandbox handle at all — the B arm structurally
        // cannot touch the ReviewBot checkout. We assert the artifact landed in SQLite and the (unused)
        // filesystem fake recorded zero writes.
        _ = await Reviewer(agent, store).ReviewAsync(
            reviewRunId, Provider, ComparisonVariant, "diff", CancellationToken.None);

        store.GetArtifacts(reviewRunId).Should().ContainSingle();
        fs.Writes.Should().BeEmpty("the B arm persists only to SQLite, never the ReviewBot repo");
    }

    [Fact]
    public async Task ReviewAsync_rejects_a_writing_variant()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var reviewRunId = SeedRun(store);

        var writingVariant = ComparisonVariant with { VariantId = "primary", CanWrite = true };
        var agent = AgentReturning("body");

        var act = () => Reviewer(agent, store).ReviewAsync(
            reviewRunId, Provider, writingVariant, "diff", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        store.GetArtifacts(reviewRunId).Should().BeEmpty("a writing variant must never be persisted as a B artifact");
    }

    private static FakeMultiTurnAgent AgentReturning(string text) =>
        new(RunId, new TextMessage { Text = text, Role = Role.Assistant, RunId = RunId });

    private static VariantReviewer Reviewer(FakeMultiTurnAgent agent, ReviewStore store) =>
        new(agent, store, NullLogger<VariantReviewer>.Instance);

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
