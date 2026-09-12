using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>Stored provenance survives readback and identity reuse independently of the retired prompt factory.</summary>
public sealed class RunProvenanceTests
{
    [Fact]
    public void Null_provenance_does_not_erase_a_recorded_digest()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var run = SeedRun(store, null);
        store.RecordRunProvenance(run.Id, "original-digest");
        store.RecordRunProvenance(run.Id, null);
        store.GetReviewRun(run.Id)!.PromptTemplateHash.Should().Be("original-digest");
    }

    [Fact]
    public void Repeated_admission_preserves_provenance_until_explicitly_updated()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var run = SeedRun(store, null);
        store.RecordRunProvenance(run.Id, "old-digest");
        store.CreateOrGetReviewRun(SeedFor(run.RepoId, "new-digest")).PromptTemplateHash.Should().Be("old-digest");
        store.RecordRunProvenance(run.Id, "new-digest");
        store.GetReviewRun(run.Id)!.PromptTemplateHash.Should().Be("new-digest");
    }

    private static ReviewRun SeedRun(ReviewStore store, string? promptTemplateHash)
    {
        var repoId = store.EnsureRepo(
            new RepoIdentity
            {
                Provider = "github",
                OrgOrOwner = "achieveai",
                RepoName = "LmDotnetTools",
                RepoStableId = "repo-stable-1",
            }
        );
        return store.CreateOrGetReviewRun(SeedFor(repoId, promptTemplateHash));
    }

    private static ReviewRun SeedFor(long repoId, string? promptTemplateHash) =>
        new()
        {
            RepoId = repoId,
            PrId = "118",
            HeadSha = "head-sha",
            BaseSha = "base-sha",
            TriggerWatermark = "2026-06-29T12:34:56Z",
            ReviewKind = "full",
            VariantId = "primary",
            Mode = "collect-only",
            Stage = ReviewStage.Discovered,
            WorkflowStatus = WorkflowStatus.Running,
            PrLifecycleState = PrLifecycleState.Open,
            PromptTemplateHash = promptTemplateHash,
        };
}
