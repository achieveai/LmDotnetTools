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

        var first = store.EnsureRepo(
            new RepoIdentity
            {
                Provider = "github",
                OrgOrOwner = "achieveai",
                RepoName = "LmDotNetTools",
            }
        );
        var second = store.EnsureRepo(
            new RepoIdentity
            {
                Provider = "GitHub",
                OrgOrOwner = "AchieveAI",
                RepoName = "lmdotnettools",
            }
        );

        second.Should().Be(first, "casing-only differences must normalize to the same repo row");
    }

    [Fact]
    public void Distinct_repositories_get_distinct_rows()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);

        var github = store.EnsureRepo(
            new RepoIdentity
            {
                Provider = "github",
                OrgOrOwner = "achieveai",
                RepoName = "repo-a",
            }
        );
        var ado = store.EnsureRepo(
            new RepoIdentity
            {
                Provider = "azure-devops",
                OrgOrOwner = "achieveai",
                Project = "proj",
                RepoName = "repo-a",
            }
        );

        ado.Should().NotBe(github, "provider/project differences are distinct identities");
    }

    [Fact]
    public void Normalized_key_is_lowercased_while_display_name_keeps_original_casing()
    {
        var identity = new RepoIdentity
        {
            Provider = "GitHub",
            OrgOrOwner = "AchieveAI",
            RepoName = "LmDotNetTools",
        };

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
    public void A_new_trigger_watermark_reuses_the_same_review_run()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        var first = store.CreateOrGetReviewRun(SampleRun(repoId) with { TriggerWatermark = "wm-1" });
        var second = store.CreateOrGetReviewRun(SampleRun(repoId) with { TriggerWatermark = "wm-2" });

        second
            .Id.Should()
            .Be(
                first.Id,
                "trigger_watermark (the PR updated_at) is NOT part of the identity — posting a review comment "
                    + "mutates updated_at, so keying on it would spawn a duplicate run + review on the next poll"
            );
    }

    [Fact]
    public void Toggling_posting_mode_reuses_the_same_review_run()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        // A PR first seen while collect-only, then re-observed after posting is enabled (the poller seeds
        // mode="post"). Must resolve to the SAME run — otherwise enabling posting re-reviews every open PR.
        var collectOnly = store.CreateOrGetReviewRun(SampleRun(repoId) with { Mode = "collect-only" });
        var afterPostingEnabled = store.CreateOrGetReviewRun(SampleRun(repoId) with { Mode = "post" });

        afterPostingEnabled
            .Id.Should()
            .Be(
                collectOnly.Id,
                "mode (post vs collect-only) is an authorization decision, NOT part of a review's identity — "
                    + "keying on it would re-review every open PR the moment posting is toggled"
            );
        afterPostingEnabled.Mode.Should().Be("collect-only", "the existing run is returned as-is, not re-seeded");
    }

    [Fact]
    public void A_new_head_sha_produces_a_distinct_review_run()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        var first = store.CreateOrGetReviewRun(SampleRun(repoId) with { HeadSha = "sha-1" });
        var second = store.CreateOrGetReviewRun(SampleRun(repoId) with { HeadSha = "sha-2" });

        second.Id.Should().NotBe(first.Id, "a new commit (head_sha) is what legitimately re-reviews");
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

        var created = store.CreateOrGetReviewRun(
            SampleRun(repoId) with
            {
                ModelProvider = "anthropic",
                ModelId = "claude-opus-4-8",
                PromptTemplateHash = "sha256:abc",
                PolicyBundleVersion = "policy-v3",
                FeatureFlagSnapshot = "{\"collectOnly\":true}",
                MergeSha = "merge-sha",
            }
        );

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

    // ── prior-review summary (re-review context) ──────────────────────────────────────────────────

    [Fact]
    public void GetPriorReviewSummary_returns_the_newest_completed_head_and_the_completed_count()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        _ = store.CreateOrGetReviewRun(SampleRun(repoId) with { HeadSha = "sha-old", Stage = ReviewStage.Reviewed });
        _ = store.CreateOrGetReviewRun(SampleRun(repoId) with { HeadSha = "sha-new", Stage = ReviewStage.Posted });
        // A different PR's completed run must not be counted.
        _ = store.CreateOrGetReviewRun(
            SampleRun(repoId) with
            {
                PrId = "200",
                HeadSha = "sha-other",
                Stage = ReviewStage.Reviewed,
            }
        );
        var current = store.CreateOrGetReviewRun(SampleRun(repoId) with { HeadSha = "sha-current" });

        var summary = store.GetPriorReviewSummary(repoId, "118", current.Id);

        summary.PrevHeadSha.Should().Be("sha-new", "the most recently created completed run is the last review");
        summary.PriorReviewCount.Should().Be(2, "two prior runs for this PR reached a completed review stage");
    }

    [Fact]
    public void GetPriorReviewSummary_does_not_count_runs_that_never_completed_a_review()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        _ = store.CreateOrGetReviewRun(
            SampleRun(repoId) with
            {
                HeadSha = "sha-ctx",
                Stage = ReviewStage.ContextReady,
            }
        );
        var current = store.CreateOrGetReviewRun(SampleRun(repoId) with { HeadSha = "sha-current" });

        var summary = store.GetPriorReviewSummary(repoId, "118", current.Id);

        summary.PrevHeadSha.Should().BeNull("a run stuck at ContextReady never produced review output");
        summary.PriorReviewCount.Should().Be(0);
    }

    [Fact]
    public void GetPriorReviewSummary_ignores_the_b_variant_so_ab_runs_do_not_double_count()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        _ = store.CreateOrGetReviewRun(
            SampleRun(repoId) with
            {
                HeadSha = "sha-b",
                VariantId = "b",
                Stage = ReviewStage.Reviewed,
            }
        );
        var current = store.CreateOrGetReviewRun(SampleRun(repoId) with { HeadSha = "sha-current" });

        var summary = store.GetPriorReviewSummary(repoId, "118", current.Id);

        summary.PrevHeadSha.Should().BeNull("only the primary variant counts toward the re-review history");
        summary.PriorReviewCount.Should().Be(0);
    }

    // ── §6 confidentiality trust signals (Task 17) ────────────────────────────────────────────────

    [Theory]
    [InlineData(true, false)] // task's example: fork PR against a private target
    [InlineData(false, true)] // same-org PR against a public target — both differ from the fail-closed defaults
    [InlineData(false, false)]
    public void The_confidentiality_trust_signals_round_trip_the_exact_stored_values(
        bool isForkPr,
        bool isTargetRepoPublic
    )
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        var created = store.CreateOrGetReviewRun(
            SampleRun(repoId) with
            {
                IsForkPr = isForkPr,
                IsTargetRepoPublic = isTargetRepoPublic,
            }
        );

        var reloaded = store.GetReviewRun(created.Id);
        reloaded.Should().NotBeNull();
        reloaded!
            .IsForkPr.Should()
            .Be(
                isForkPr,
                "the persisted fork signal must survive reload rather than fall back to the fail-closed default (true)"
            );
        reloaded
            .IsTargetRepoPublic.Should()
            .Be(
                isTargetRepoPublic,
                "the persisted target-visibility signal must survive reload rather than fall back to the fail-closed default (true)"
            );
    }

    // ── ListReviewedPrsAsync (PR-lifecycle sweeper) ───────────────────────────────────────────────

    [Fact]
    public async Task ListReviewedPrsAsync_returns_each_reviewed_pr_once_across_multiple_runs()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        // Two distinct runs for PR 118 (different head shas) must collapse to a single reviewed-PR row;
        // PR 200 is a second reviewed PR.
        _ = store.CreateOrGetReviewRun(SampleRun(repoId) with { PrId = "118", HeadSha = "sha-1" });
        _ = store.CreateOrGetReviewRun(SampleRun(repoId) with { PrId = "118", HeadSha = "sha-2" });
        _ = store.CreateOrGetReviewRun(SampleRun(repoId) with { PrId = "200", HeadSha = "sha-3" });

        var reviewed = await store.ListReviewedPrsAsync(CancellationToken.None);

        reviewed.Should().HaveCount(2, "the two runs for PR 118 collapse to one reviewed-PR row");
        reviewed.Select(r => r.PrId).Should().BeEquivalentTo(["118", "200"]);
        reviewed.Should().OnlyContain(r => r.Provider == "github" && r.Repo.RepoName == "LmDotnetTools");
    }

    [Fact]
    public async Task ListReviewedPrsAsync_is_empty_when_nothing_has_been_reviewed()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);

        var reviewed = await store.ListReviewedPrsAsync(CancellationToken.None);

        reviewed.Should().BeEmpty();
    }

    // ── pr_author (per-developer review feedback) ─────────────────────────────────────────────────

    [Fact]
    public void The_pr_author_round_trips_and_stays_null_when_unknown()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        var withAuthor = store.CreateOrGetReviewRun(SampleRun(repoId) with { PrAuthor = "octocat" });
        var withoutAuthor = store.CreateOrGetReviewRun(
            SampleRun(repoId) with
            {
                PrId = "200",
                HeadSha = "sha-x",
                PrAuthor = null,
            }
        );

        store.GetReviewRun(withAuthor.Id)!.PrAuthor.Should().Be("octocat");

        // Unknown must reload as null, not as "" or a placeholder: null is the single value every
        // consumer reads as "no feedback record is addressable for this PR".
        store.GetReviewRun(withoutAuthor.Id)!.PrAuthor.Should().BeNull();
    }

    // ── pr_title / pr_description (the prose half of the knowledge-retrieval key) ─────────────────

    /// <summary>
    /// The PR's own prose is captured at POLL time and read at the Reviewed stage, which on a resumed run
    /// is a different process entirely — so the store round trip is the whole mechanism. A column that
    /// writes and reads back null would leave ranking permanently keyed on changed paths alone while every
    /// unit test of the ranker still passed.
    /// </summary>
    [Fact]
    public void The_pr_prose_round_trips_and_stays_null_when_the_provider_gave_none()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        var described = store.CreateOrGetReviewRun(
            SampleRun(repoId) with
            {
                PrTitle = "Remove the stale featureflag entry",
                PrDescription = "The ECS task definition still carries the flag.",
            }
        );
        var bare = store.CreateOrGetReviewRun(
            SampleRun(repoId) with
            {
                PrId = "300",
                HeadSha = "sha-y",
                PrTitle = null,
                PrDescription = null,
            }
        );

        var reloaded = store.GetReviewRun(described.Id)!;
        reloaded.PrTitle.Should().Be("Remove the stale featureflag entry");
        reloaded.PrDescription.Should().Be("The ECS task definition still carries the flag.");

        // Null must reload as null rather than "": the ranker tokenizes whatever it is handed, and an
        // empty string is the value a pre-migration row carries.
        store.GetReviewRun(bare.Id)!.PrTitle.Should().BeNull();
        store.GetReviewRun(bare.Id)!.PrDescription.Should().BeNull();
    }

    [Fact]
    public async Task ListReviewedPrsAsync_keeps_one_row_per_pr_and_prefers_the_known_author()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        // The realistic shape after this feature ships: PR 118 was first reviewed before the author was
        // recorded, then reviewed again after. A DISTINCT over pr_author would emit PR 118 TWICE — the
        // sweeper would sweep it twice, and one of those rows would have erased the author.
        _ = store.CreateOrGetReviewRun(SampleRun(repoId) with { PrId = "118", HeadSha = "sha-1", PrAuthor = null });
        _ = store.CreateOrGetReviewRun(
            SampleRun(repoId) with
            {
                PrId = "118",
                HeadSha = "sha-2",
                PrAuthor = "octocat",
            }
        );
        _ = store.CreateOrGetReviewRun(SampleRun(repoId) with { PrId = "200", HeadSha = "sha-3", PrAuthor = null });

        var reviewed = await store.ListReviewedPrsAsync(CancellationToken.None);

        reviewed.Should().HaveCount(2, "runs for the same PR collapse to one row even when only some carry an author");
        reviewed
            .Single(r => r.PrId == "118")
            .Author.Should()
            .Be("octocat", "a recorded author must win over a run that predates the column");
        reviewed
            .Single(r => r.PrId == "200")
            .Author.Should()
            .BeNull("a PR no run recorded an author for stays unknown rather than borrowing another PR's");
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
        store.SaveCursor(
            SampleCursor(CurrentCursorVersion) with
            {
                CursorPayload = "{\"page\":5}",
                HighWaterMark = "later",
            }
        );

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
        store
            .TryTransitionOutbox(
                entry.Id,
                OutboxStatus.Sending,
                OutboxStatus.Posted,
                providerResponseId: "gh-comment-42"
            )
            .Should()
            .BeTrue();

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

        _ = store.AddArtifact(
            new ReviewArtifact
            {
                ReviewRunId = runId,
                ArtifactSchemaVersion = 1,
                ArtifactKind = "b-variant-review",
                Provider = "github",
                Payload = "{\"score\":7}",
            }
        );
        _ = store.AddArtifact(
            new ReviewArtifact
            {
                ReviewRunId = runId,
                ArtifactSchemaVersion = 1,
                ArtifactKind = "judge",
                Provider = "github",
                Payload = "{\"rationale\":\"ok\"}",
            }
        );

        var artifacts = store.GetArtifacts(runId);
        artifacts.Should().HaveCount(2);
        artifacts.Select(a => a.ArtifactKind).Should().ContainInOrder("b-variant-review", "judge");
        artifacts[0].ArtifactSchemaVersion.Should().Be(1);
    }

    [Fact]
    public void TryGetLatestArtifact_returns_the_newest_of_a_kind_without_collapsing_the_history()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var runId = SeedRun(store);
        // Two checkpoints of the SAME kind, as a retried review lifecycle writes them, plus an unrelated kind
        // in between — a lookup that read the last row overall, or the first of the kind, would pick wrong.
        _ = store.AddArtifact(SampleArtifact(runId, "review-provisional", "{\"n\":1}"));
        _ = store.AddArtifact(SampleArtifact(runId, "context", "{\"n\":0}"));
        _ = store.AddArtifact(SampleArtifact(runId, "review-provisional", "{\"n\":2}"));

        store.TryGetLatestArtifact(runId, "review-provisional")!.Payload.Should().Be("{\"n\":2}");
        store
            .GetArtifacts(runId)
            .Should()
            .HaveCount(3, "the lookup reads the newest row, it never replaces or prunes the append-only history");
    }

    [Fact]
    public void TryGetLatestArtifact_returns_null_when_the_run_has_no_artifact_of_that_kind()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var runId = SeedRun(store);
        _ = store.AddArtifact(SampleArtifact(runId, "context", "{}"));

        // A missing checkpoint is the ordinary "fresh review" case, not an error: it must read as absent.
        store.TryGetLatestArtifact(runId, "review-provisional").Should().BeNull();
    }

    /// <summary>
    /// The kind-filtered listing #453 asks for. <c>GetArtifacts</c> materialises every payload a run
    /// holds, and the largest of them by far is <c>review-context</c> — the whole diff, as a .NET
    /// string. The eval sweep wants judge rows and discards the rest, so it was paying for a diff per
    /// run to find a grade; <c>TryGetLatestArtifact</c> could not serve it because the sweep needs
    /// every judge row of a run, not the newest one.
    /// </summary>
    [Fact]
    public void ListArtifacts_returns_only_the_kinds_asked_for_in_append_order()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var runId = SeedRun(store);

        _ = store.AddArtifact(SampleArtifact(runId, "review-context", "{\"diff\":\"a very large diff\"}"));
        _ = store.AddArtifact(SampleArtifact(runId, "judge", "{\"n\":1}"));
        _ = store.AddArtifact(SampleArtifact(runId, "review", "{\"text\":\"a review\"}"));
        _ = store.AddArtifact(SampleArtifact(runId, "judge", "{\"n\":2}"));

        var judged = store.ListArtifacts(runId, "judge");

        judged.Select(a => a.ArtifactKind).Should().AllBe("judge");
        judged.Select(a => a.Payload).Should().ContainInOrder("{\"n\":1}", "{\"n\":2}");
        judged.Should().HaveCount(2, "every judge row of the run, not just the newest");
    }

    [Fact]
    public void ListArtifacts_takes_more_than_one_kind()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var runId = SeedRun(store);

        _ = store.AddArtifact(SampleArtifact(runId, "review-context", "{\"diff\":\"big\"}"));
        _ = store.AddArtifact(SampleArtifact(runId, "judge", "{\"n\":1}"));
        _ = store.AddArtifact(SampleArtifact(runId, "b-variant-review", "{\"text\":\"b\"}"));

        store
            .ListArtifacts(runId, "judge", "b-variant-review")
            .Select(a => a.ArtifactKind)
            .Should()
            .ContainInOrder("judge", "b-variant-review");
    }

    /// <summary>
    /// The unfiltered listing is untouched by #453 — every other reader of this table still walks the
    /// whole run, and the corpus reader genuinely wants the diff.
    /// </summary>
    [Fact]
    public void ListArtifacts_does_not_change_what_the_unfiltered_listing_returns()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var runId = SeedRun(store);

        _ = store.AddArtifact(SampleArtifact(runId, "review-context", "{\"diff\":\"big\"}"));
        _ = store.AddArtifact(SampleArtifact(runId, "judge", "{\"n\":1}"));

        _ = store.ListArtifacts(runId, "judge");

        store.GetArtifacts(runId).Should().HaveCount(2);
    }

    /// <summary>
    /// No kinds is refused rather than answered. An empty <c>IN ()</c> matches nothing, so the query
    /// would return an empty list — indistinguishable from "this run recorded nothing", which for the
    /// sweep means every candidate of the run silently counts as ungraded.
    /// </summary>
    [Fact]
    public void ListArtifacts_refuses_an_empty_kind_list_rather_than_matching_nothing()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var runId = SeedRun(store);
        _ = store.AddArtifact(SampleArtifact(runId, "judge", "{\"n\":1}"));

        var list = () => store.ListArtifacts(runId);

        list.Should().Throw<ArgumentException>().WithMessage("*at least one artifact kind*");
    }

    [Fact]
    public void ListArtifacts_refuses_a_blank_kind()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var runId = SeedRun(store);

        var list = () => store.ListArtifacts(runId, "judge", "   ");

        list.Should().Throw<ArgumentException>();
    }

    private static ReviewArtifact SampleArtifact(long runId, string kind, string payload) =>
        new()
        {
            ReviewRunId = runId,
            ArtifactSchemaVersion = 1,
            ArtifactKind = kind,
            Provider = "github",
            Payload = payload,
        };

    // ── §7 GetRepo rehydration ──────────────────────────────────────────────────────────────────

    [Fact]
    public void GetRepo_rehydrates_the_identity_for_a_stored_repo()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var adoIdentity = new RepoIdentity
        {
            Provider = "azure-devops",
            OrgOrOwner = "contoso",
            Project = "Platform",
            RepoName = "widgets",
            RepoStableId = "repo-guid-1",
        };
        var id = store.EnsureRepo(adoIdentity);

        var loaded = store.GetRepo(id);

        loaded.Should().NotBeNull();
        loaded!.Provider.Should().Be("azure-devops");
        loaded.OrgOrOwner.Should().Be("contoso");
        loaded.Project.Should().Be("Platform");
        loaded.RepoName.Should().Be("widgets");
        loaded.RepoStableId.Should().Be("repo-guid-1");
    }

    [Fact]
    public void GetRepo_returns_null_for_an_unknown_id()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);

        store.GetRepo(9999).Should().BeNull();
    }

    // ── concurrency ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Concurrent_readers_and_writers_neither_throw_nor_lose_rows()
    {
        // The store wraps ONE SqliteConnection, which is not thread-safe; every operation now runs under
        // the store's gate, held across command-plus-reader.
        //
        // Read this as a SMOKE test, not a race detector. The unguarded failure is real but rare: probed
        // at 24 threads × 400 iterations it threw ArgumentOutOfRangeException from Microsoft.Data.Sqlite's
        // internal per-connection command list exactly ONCE in 9,600 operations (with the gate: zero).
        // Reproducing that here would mean a ~5-minute probabilistic test — flaky by construction — so
        // this pins the cheap, deterministic half instead: concurrent readers and writers complete and
        // every row lands. The rare-throw evidence lives in the ReviewStore <remarks>. The poller is still
        // serial; this pins that the STORE is no longer the reason it has to be.
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);

        var runId = SeedRun(store);
        const int Workers = 8;
        const int PerWorker = 25;

        // An async gate rather than a Barrier: workers are released simultaneously without first parking
        // eight thread-pool threads, so the contention is real even on a small pool.
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = Enumerable
            .Range(0, Workers)
            .Select(worker =>
                Task.Run(async () =>
                {
                    await start.Task;
                    for (var i = 0; i < PerWorker; i++)
                    {
                        _ = store.AddArtifact(
                            new ReviewArtifact
                            {
                                ReviewRunId = runId,
                                ArtifactSchemaVersion = 1,
                                ArtifactKind = "review",
                                Provider = "github",
                                Payload = $"{{\"worker\":{worker},\"i\":{i}}}",
                            }
                        );

                        // Readers that STREAM while the other workers write — the case a per-command lock
                        // would still get wrong.
                        _ = store.GetArtifacts(runId);
                        _ = store.GetReviewRun(runId);
                        _ = await store.ListReviewedPrsAsync(CancellationToken.None);
                    }
                })
            )
            .ToList();

        start.SetResult();
        var drain = async () => await Task.WhenAll(work);

        _ = await drain.Should().NotThrowAsync("every operation serializes on the store's gate");
        store
            .GetArtifacts(runId)
            .Should()
            .HaveCount(Workers * PerWorker, "no write was lost, duplicated, or rolled back by a racing command");
    }

    // ── the delta-review cutoff and the dedup-context signal (#225 items 1 + 2) ───────────────────

    [Fact]
    public void A_pr_that_has_never_carried_a_posted_review_reports_no_cutoff()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());
        _ = store.CreateOrGetReviewRun(SampleRun(repoId));

        store
            .GetLastPostedReviewAt(repoId, "118")
            .Should()
            .BeNull(
                "null is a real answer — it is what lets the fetch-failure path tell a first review, which is safe "
                    + "to post blind, from a re-review, which is not"
            );
    }

    [Fact]
    public void The_posted_review_cutoff_returns_what_delivery_stamped()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());
        var runId = store.CreateOrGetReviewRun(SampleRun(repoId)).Id;
        var postedAt = DateTimeOffset.Parse("2026-07-20T10:00:00Z");

        store.MarkReviewPosted(runId, postedAt);

        store.GetLastPostedReviewAt(repoId, "118").Should().Be(postedAt);
    }

    [Fact]
    public void The_posted_review_cutoff_survives_a_new_head_because_it_spans_runs()
    {
        // The property the whole design turns on. A run's identity includes head_sha, so every push opens a NEW
        // review_run row — but the bot's last word on a PR is its last word whichever head it was reviewing.
        // Read per-row instead of per-PR and the cutoff resets on every push, re-classifying the entire
        // conversation as "past" and burying the questions the next review is required to answer.
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());
        var first = store.CreateOrGetReviewRun(SampleRun(repoId)).Id;
        var second = store.CreateOrGetReviewRun(SampleRun(repoId) with { HeadSha = "head-sha-2" }).Id;
        second.Should().NotBe(first, "a new head is a new run, which is exactly why the cutoff must span them");
        var firstPostedAt = DateTimeOffset.Parse("2026-07-20T10:00:00Z");

        store.MarkReviewPosted(first, firstPostedAt);

        store
            .GetLastPostedReviewAt(repoId, "118")
            .Should()
            .Be(firstPostedAt, "the newer run has posted nothing yet, so the older run's delivery is still the cutoff");
    }

    [Fact]
    public void The_posted_review_cutoff_is_the_latest_delivery_not_the_last_one_written()
    {
        // MAX, not last-write-wins. Runs do not settle in id order under retries and the stranded-run
        // reconciler, so an older run stamping after a newer one must not drag the cutoff backwards.
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());
        var first = store.CreateOrGetReviewRun(SampleRun(repoId)).Id;
        var second = store.CreateOrGetReviewRun(SampleRun(repoId) with { HeadSha = "head-sha-2" }).Id;
        var later = DateTimeOffset.Parse("2026-07-25T10:00:00Z");

        store.MarkReviewPosted(second, later);
        store.MarkReviewPosted(first, DateTimeOffset.Parse("2026-07-20T10:00:00Z"));

        store.GetLastPostedReviewAt(repoId, "118").Should().Be(later);
    }

    [Fact]
    public void The_posted_review_cutoff_does_not_leak_between_prs()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());
        var reviewed = store.CreateOrGetReviewRun(SampleRun(repoId)).Id;
        _ = store.CreateOrGetReviewRun(SampleRun(repoId) with { PrId = "119" });

        store.MarkReviewPosted(reviewed, DateTimeOffset.Parse("2026-07-20T10:00:00Z"));

        store
            .GetLastPostedReviewAt(repoId, "119")
            .Should()
            .BeNull("a cutoff that leaked across PRs would silence a never-reviewed PR's whole conversation");
    }

    [Fact]
    public void A_stored_cutoff_reads_back_as_the_same_instant_from_another_offset()
    {
        // The column is text. A stamp written from a non-UTC offset must normalize, or MAX stops being
        // chronological the first time two rows are written from different offsets.
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());
        var runId = store.CreateOrGetReviewRun(SampleRun(repoId)).Id;
        var offsetInstant = new DateTimeOffset(2026, 7, 20, 15, 30, 0, TimeSpan.FromHours(5));

        store.MarkReviewPosted(runId, offsetInstant);

        store
            .GetLastPostedReviewAt(repoId, "118")
            .Should()
            .Be(offsetInstant, "DateTimeOffset equality is by instant, and the stored form must preserve it");
        store
            .GetLastPostedReviewAt(repoId, "118")!
            .Value.Offset.Should()
            .Be(TimeSpan.Zero, "everything is normalized to UTC so lexicographic MAX stays chronological");
    }

    [Fact]
    public void A_run_starts_with_its_dedup_context_intact()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var runId = SeedRun(store);

        store
            .WasDedupContextLost(runId)
            .Should()
            .BeFalse(
                "the default has to be 'not lost', or every row predating the column would stop posting on upgrade"
            );
    }

    [Fact]
    public void A_lost_dedup_context_latches_and_is_readable_after_a_reopen()
    {
        // Durability is the point: the flag is written at the Reviewed stage and read at the Posted stage, which
        // after a restart is a different process. An in-memory flag would post blind on exactly that retry.
        using var db = new TempSqliteDatabase();
        long runId;
        using (var store = new ReviewStore(db.ConnectionString))
        {
            runId = SeedRun(store);
            store.MarkDedupContextLost(runId);
            store.MarkDedupContextLost(runId);
        }

        using var reopened = new ReviewStore(db.ConnectionString);
        reopened.WasDedupContextLost(runId).Should().BeTrue();
    }

    // ── shared fixtures ───────────────────────────────────────────────────────────────────────────

    private static RepoIdentity SampleRepo() =>
        new()
        {
            Provider = "github",
            OrgOrOwner = "achieveai",
            RepoName = "LmDotnetTools",
            RepoStableId = "R_node_123",
        };

    private static ReviewRun SampleRun(long repoId) =>
        new()
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

    private static OpaqueCursor SampleCursor(int version) =>
        new()
        {
            Provider = "github",
            Scope = "achieveai/repo:open-prs",
            CursorVersion = version,
            CursorPayload = "{\"page\":2}",
            HighWaterMark = "2026-06-01T00:00:00Z",
        };

    private static OutboxEntry SampleOutbox(long runId) =>
        new()
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
