using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P2.1 — the orchestration store contracts: repo identity normalization (§7), <c>review_run</c>
/// identity/idempotency + resume-state (§6), opaque-cursor resync tolerance (§12), crash-safe outbox
/// transitions (§11), and append-compatible artifacts (§14).
/// </summary>
public sealed class ReviewStoreTests
{
    // ── §7 repo identity normalization ────────────────────────────────────────────────────────────

    [Fact]
    public void Repo_identity_collapses_casing_drift_to_one_row_but_preserves_display_name()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);

        var first = store.EnsureRepo(new RepoIdentity
        {
            Provider = "github",
            OrgOrOwner = "achieveai",
            RepoName = "LmDotNetTools",
        });
        var second = store.EnsureRepo(new RepoIdentity
        {
            Provider = "GitHub",
            OrgOrOwner = "AchieveAI",
            RepoName = "lmdotnettools",
        });

        second.Should().Be(first, "casing-only differences must normalize to the same repo row");
    }

    [Fact]
    public void Distinct_repositories_get_distinct_rows()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);

        var github = store.EnsureRepo(new RepoIdentity { Provider = "github", OrgOrOwner = "achieveai", RepoName = "repo-a" });
        var ado = store.EnsureRepo(new RepoIdentity { Provider = "azure-devops", OrgOrOwner = "achieveai", Project = "proj", RepoName = "repo-a" });

        ado.Should().NotBe(github, "provider/project differences are distinct identities");
    }

    [Fact]
    public void Normalized_key_is_lowercased_while_display_name_keeps_original_casing()
    {
        var identity = new RepoIdentity { Provider = "GitHub", OrgOrOwner = "AchieveAI", RepoName = "LmDotNetTools" };

        identity.NormalizedKey.Should().Be("github/achieveai/lmdotnettools");
        identity.DisplayName.Should().Be("AchieveAI/LmDotNetTools");
    }

    // ── §6 review_run identity + idempotency ──────────────────────────────────────────────────────

    [Fact]
    public void Creating_the_same_review_run_identity_twice_returns_the_same_row()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        var first = store.CreateOrGetReviewRun(SampleRun(repoId));
        var second = store.CreateOrGetReviewRun(SampleRun(repoId));

        first.Id.Should().BeGreaterThan(0);
        second.Id.Should().Be(first.Id, "the §6 identity tuple is unique");
    }

    [Fact]
    public void A_new_trigger_watermark_produces_a_distinct_review_run()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        var first = store.CreateOrGetReviewRun(SampleRun(repoId) with { TriggerWatermark = "wm-1" });
        var second = store.CreateOrGetReviewRun(SampleRun(repoId) with { TriggerWatermark = "wm-2" });

        second.Id.Should().NotBe(first.Id, "same SHA but a new trigger re-reviews — hence the watermark");
    }

    [Fact]
    public void The_b_variant_is_a_distinct_run_from_the_primary()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        var primary = store.CreateOrGetReviewRun(SampleRun(repoId) with { VariantId = "primary" });
        var bVariant = store.CreateOrGetReviewRun(SampleRun(repoId) with { VariantId = "b" });

        bVariant.Id.Should().NotBe(primary.Id);
    }

    [Fact]
    public void Reproducibility_inputs_round_trip()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        var created = store.CreateOrGetReviewRun(SampleRun(repoId) with
        {
            ModelProvider = "anthropic",
            ModelId = "claude-opus-4-8",
            PromptTemplateHash = "sha256:abc",
            PolicyBundleVersion = "policy-v3",
            FeatureFlagSnapshot = "{\"collectOnly\":true}",
            MergeSha = "merge-sha",
        });

        var reloaded = store.GetReviewRun(created.Id);
        reloaded.Should().NotBeNull();
        reloaded!.ModelProvider.Should().Be("anthropic");
        reloaded.ModelId.Should().Be("claude-opus-4-8");
        reloaded.PromptTemplateHash.Should().Be("sha256:abc");
        reloaded.PolicyBundleVersion.Should().Be("policy-v3");
        reloaded.FeatureFlagSnapshot.Should().Be("{\"collectOnly\":true}");
        reloaded.MergeSha.Should().Be("merge-sha");
    }

    [Fact]
    public void Updating_the_three_axes_persists()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());
        var run = store.CreateOrGetReviewRun(SampleRun(repoId));

        store.UpdateReviewRunState(run.Id, ReviewStage.Reviewed, WorkflowStatus.Running, PrLifecycleState.Open);

        var reloaded = store.GetReviewRun(run.Id);
        reloaded!.Stage.Should().Be(ReviewStage.Reviewed);
        reloaded.WorkflowStatus.Should().Be(WorkflowStatus.Running);
        reloaded.PrLifecycleState.Should().Be(PrLifecycleState.Open);
    }

    // ── §12 opaque cursor resync tolerance ────────────────────────────────────────────────────────

    private const int CurrentCursorVersion = 1;

    [Fact]
    public void A_missing_cursor_signals_resync()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);

        var result = store.ReadCursor("github", "achieveai/repo:open-prs", CurrentCursorVersion);

        result.ShouldResync.Should().BeTrue();
        result.Cursor.Should().BeNull();
    }

    [Fact]
    public void A_matching_version_cursor_is_usable()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        store.SaveCursor(SampleCursor(CurrentCursorVersion));

        var result = store.ReadCursor("github", "achieveai/repo:open-prs", CurrentCursorVersion);

        result.ShouldResync.Should().BeFalse();
        result.Cursor!.CursorPayload.Should().Be("{\"page\":2}");
        result.Cursor.HighWaterMark.Should().Be("2026-06-01T00:00:00Z");
    }

    [Theory]
    [InlineData(0)] // older than the reader understands
    [InlineData(99)] // produced by a newer build
    public void An_old_or_future_cursor_version_signals_resync(int storedVersion)
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        store.SaveCursor(SampleCursor(storedVersion));

        var result = store.ReadCursor("github", "achieveai/repo:open-prs", CurrentCursorVersion);

        result.ShouldResync.Should().BeTrue();
        result.Cursor.Should().BeNull();
    }

    [Fact]
    public void An_empty_payload_signals_resync()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        store.SaveCursor(SampleCursor(CurrentCursorVersion) with { CursorPayload = "   " });

        var result = store.ReadCursor("github", "achieveai/repo:open-prs", CurrentCursorVersion);

        result.ShouldResync.Should().BeTrue();
    }

    [Fact]
    public void Saving_a_cursor_twice_upserts_in_place()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);

        store.SaveCursor(SampleCursor(CurrentCursorVersion));
        store.SaveCursor(SampleCursor(CurrentCursorVersion) with { CursorPayload = "{\"page\":5}", HighWaterMark = "later" });

        var result = store.ReadCursor("github", "achieveai/repo:open-prs", CurrentCursorVersion);
        result.Cursor!.CursorPayload.Should().Be("{\"page\":5}");
        result.Cursor.HighWaterMark.Should().Be("later");
    }

    // ── §11 outbox idempotency + crash-safe transitions ───────────────────────────────────────────

    [Fact]
    public void Enqueuing_the_same_idempotency_key_twice_returns_the_same_entry()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var runId = SeedRun(store);

        var first = store.EnqueueOutbox(SampleOutbox(runId));
        var second = store.EnqueueOutbox(SampleOutbox(runId));

        first.Id.Should().BeGreaterThan(0);
        second.Id.Should().Be(first.Id, "the versioned idempotency key is unique");
        second.Status.Should().Be(OutboxStatus.Pending);
    }

    [Fact]
    public void Outbox_advances_through_the_pending_sending_posted_sequence()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var runId = SeedRun(store);
        var entry = store.EnqueueOutbox(SampleOutbox(runId));

        store.TryTransitionOutbox(entry.Id, OutboxStatus.Pending, OutboxStatus.Sending).Should().BeTrue();
        store.TryTransitionOutbox(entry.Id, OutboxStatus.Sending, OutboxStatus.Posted, providerResponseId: "gh-comment-42").Should().BeTrue();

        var reloaded = store.GetOutbox(entry.Id);
        reloaded!.Status.Should().Be(OutboxStatus.Posted);
        reloaded.ProviderResponseId.Should().Be("gh-comment-42");
    }

    [Fact]
    public void A_transition_from_the_wrong_state_is_rejected()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var runId = SeedRun(store);
        var entry = store.EnqueueOutbox(SampleOutbox(runId));

        // The row is Pending, so a Sending->Posted transition must not apply (crash-replay safety:
        // a second worker cannot double-post).
        store.TryTransitionOutbox(entry.Id, OutboxStatus.Sending, OutboxStatus.Posted).Should().BeFalse();
        store.GetOutbox(entry.Id)!.Status.Should().Be(OutboxStatus.Pending);
    }

    [Fact]
    public void Body_hash_is_stored_separately_from_the_idempotency_key()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var runId = SeedRun(store);

        var entry = store.EnqueueOutbox(SampleOutbox(runId) with { BodyHash = "sha256:body" });

        store.GetOutbox(entry.Id)!.BodyHash.Should().Be("sha256:body");
        entry.IdempotencyKey.Should().NotContain("sha256:body");
    }

    // ── §14 append-compatible artifacts ───────────────────────────────────────────────────────────

    [Fact]
    public void Artifacts_are_appended_and_round_trip_with_their_schema_version()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var runId = SeedRun(store);

        _ = store.AddArtifact(new ReviewArtifact
        {
            ReviewRunId = runId,
            ArtifactSchemaVersion = 1,
            ArtifactKind = "b-variant-review",
            Provider = "github",
            Payload = "{\"score\":7}",
        });
        _ = store.AddArtifact(new ReviewArtifact
        {
            ReviewRunId = runId,
            ArtifactSchemaVersion = 1,
            ArtifactKind = "judge",
            Provider = "github",
            Payload = "{\"rationale\":\"ok\"}",
        });

        var artifacts = store.GetArtifacts(runId);
        artifacts.Should().HaveCount(2);
        artifacts.Select(a => a.ArtifactKind).Should().ContainInOrder("b-variant-review", "judge");
        artifacts[0].ArtifactSchemaVersion.Should().Be(1);
    }

    // ── shared fixtures ───────────────────────────────────────────────────────────────────────────

    private static RepoIdentity SampleRepo() => new()
    {
        Provider = "github",
        OrgOrOwner = "achieveai",
        RepoName = "LmDotnetTools",
        RepoStableId = "R_node_123",
    };

    private static ReviewRun SampleRun(long repoId) => new()
    {
        RepoId = repoId,
        PrId = "118",
        HeadSha = "head-sha",
        BaseSha = "base-sha",
        TriggerWatermark = "wm-1",
        ReviewKind = "full",
        VariantId = "primary",
        Mode = "collect-only",
        Stage = ReviewStage.Discovered,
        WorkflowStatus = WorkflowStatus.Pending,
        PrLifecycleState = PrLifecycleState.Open,
    };

    private static OpaqueCursor SampleCursor(int version) => new()
    {
        Provider = "github",
        Scope = "achieveai/repo:open-prs",
        CursorVersion = version,
        CursorPayload = "{\"page\":2}",
        HighWaterMark = "2026-06-01T00:00:00Z",
    };

    private static OutboxEntry SampleOutbox(long runId) => new()
    {
        IdempotencyKey = "v1:github:achieveai::R_node_123:118:PostReviewComment:summary:body:wm-1:primary",
        Provider = "github",
        ReviewRunId = runId,
        Operation = "PostReviewComment",
        ArtifactKind = "summary",
        Status = OutboxStatus.Pending,
    };

    private static long SeedRun(ReviewStore store)
    {
        var repoId = store.EnsureRepo(SampleRepo());
        return store.CreateOrGetReviewRun(SampleRun(repoId)).Id;
    }
}
