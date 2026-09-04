using System.Security.Cryptography;
using System.Text;
using AchieveAi.LmDotnetTools.LmMultiTurn.Audit;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PersistedReviewAction = CodeReviewDaemon.Sample.Persistence.Models.ReviewAction;
using PersistedReviewActionKind = CodeReviewDaemon.Sample.Persistence.Models.ReviewActionKind;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

public sealed class ReviewPublicationCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Collect_only_root_records_the_exact_action_without_contacting_the_provider()
    {
        using var fixture = new Fixture();
        var request = fixture.Root("root-1", "## Review\nOne supported finding.");

        var first = await fixture.Coordinator.CreateRootSummaryAsync(request, CancellationToken.None);
        var replay = await fixture.Coordinator.CreateRootSummaryAsync(request, CancellationToken.None);

        first.Status.Should().Be(ReviewActionStatus.CollectedOnly);
        replay.Should().Be(first);
        fixture.Publisher.FindCallCount.Should().Be(0);
        fixture.Publisher.PostCount.Should().Be(0);
        fixture.Store.GetReviewAction(fixture.RoundId, "root-1")!.Status.Should().Be(ReviewActionStatus.CollectedOnly);
        fixture.Store.GetEngagement(fixture.EngagementId)!.RootSummaryReceiptJson.Should().BeNull();
    }

    [Fact]
    public async Task Reused_action_id_with_changed_payload_is_rejected_without_a_second_effect()
    {
        using var fixture = new Fixture();
        var first = fixture.Root("root-1", "first body");
        var changed = fixture.Root("root-1", "changed body");
        _ = await fixture.Coordinator.CreateRootSummaryAsync(first, CancellationToken.None);

        var conflict = await fixture.Coordinator.CreateRootSummaryAsync(changed, CancellationToken.None);

        conflict.Status.Should().Be(ReviewActionStatus.Rejected);
        conflict.RejectionCode.Should().Be("action_id_conflict");
        fixture.Publisher.PostCount.Should().Be(0);
        fixture.Store.GetReviewAction(fixture.RoundId, "root-1")!.PayloadSha256.Should().NotBe(Sha256("changed body"));
    }

    [Theory]
    [InlineData("wrong-provider", 1, "118", "head-1", "scope_provider_mismatch")]
    [InlineData("github", 424242, "118", "head-1", "scope_repository_mismatch")]
    [InlineData("github", 1, "119", "head-1", "scope_pr_mismatch")]
    [InlineData("github", 1, "118", "head-2", "stale_head")]
    public async Task Scope_mismatch_is_rejected_before_any_provider_effect(
        string provider,
        long repoId,
        string prId,
        string head,
        string rejectionCode
    )
    {
        using var fixture = new Fixture();
        repoId = repoId == 1 ? fixture.RepoId : repoId;
        var request = fixture.Root("root-1", "body") with
        {
            Scope = fixture.Scope("root-1") with
            {
                Provider = provider,
                RepoId = repoId,
                PrId = prId,
                ExpectedHeadSha = head,
            },
        };

        var outcome = await fixture.Coordinator.CreateRootSummaryAsync(request, CancellationToken.None);

        outcome.Status.Should().Be(ReviewActionStatus.Rejected);
        outcome.RejectionCode.Should().Be(rejectionCode);
        fixture.Publisher.FindCallCount.Should().Be(0);
        fixture.Publisher.PostCount.Should().Be(0);
    }

    [Fact]
    public async Task Live_post_revalidates_provider_lifecycle_and_head_before_sending()
    {
        using var fixture = new Fixture();
        fixture.Provider.PrState = PrLifecycle.Merged;
        var request = fixture.Root("root-1", "body", live: true);

        var outcome = await fixture.Coordinator.CreateRootSummaryAsync(request, CancellationToken.None);

        outcome.Status.Should().Be(ReviewActionStatus.Rejected);
        outcome.RejectionCode.Should().Be("pr_not_open");
        fixture.Provider.PrStateCalls.Should().Be(1);
        fixture.Provider.HeadShaCalls.Should().Be(0);
        fixture.Publisher.PostCount.Should().Be(0);
        fixture.Store.GetReviewAction(fixture.RoundId, "root-1")!.Status.Should().Be(ReviewActionStatus.Rejected);
    }

    [Fact]
    public async Task Discussion_round_posts_once_without_fabricating_a_review_run()
    {
        using var fixture = new Fixture(EngagementRoundIntent.DiscussionFollowUp, createReviewRun: false);
        var request = new ClarificationQuestionRequest(
            fixture.Scope("question-1", live: true),
            "Could you clarify the intended fallback?",
            "review-comment:41"
        );

        var first = await fixture.Coordinator.PostClarificationQuestionAsync(request, CancellationToken.None);
        var replay = await fixture.Coordinator.PostClarificationQuestionAsync(request, CancellationToken.None);

        first.Status.Should().Be(ReviewActionStatus.Accepted);
        first.RejectionCode.Should().BeNull();
        replay.Should().Be(first);
        fixture.Store.GetEngagementRound(fixture.RoundId)!.ReviewRunId.Should().BeNull();
        fixture.Store.GetReviewAction(fixture.RoundId, "question-1")!.Status.Should().Be(ReviewActionStatus.Accepted);
        fixture.Publisher.FindCallCount.Should().Be(1);
        fixture.Publisher.PostCount.Should().Be(1);
    }

    [Fact]
    public async Task Discussion_retry_adopts_a_provider_comment_left_by_a_crashed_sending_attempt()
    {
        using var fixture = new Fixture(EngagementRoundIntent.DiscussionFollowUp, createReviewRun: false);
        var request = new ClarificationQuestionRequest(
            fixture.Scope("question-1", live: true),
            "Could you clarify the intended fallback?",
            "review-comment:41"
        );
        fixture.Store.AddOrGetReviewAction(
            new PersistedReviewAction(
                fixture.RoundId,
                request.Scope.ActionId,
                PersistedReviewActionKind.PostClarificationQuestion,
                ReviewActionStatus.Planned,
                PayloadSha(new { body = request.Body, providerTargetId = request.ProviderTargetId }),
                System.Text.Json.JsonSerializer.Serialize(
                    new { providerTargetId = request.ProviderTargetId },
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
                ),
                null,
                null,
                Now,
                Now
            ),
            request.Scope.Sources
        );
        fixture
            .Store.TryTransitionReviewAction(
                fixture.RoundId,
                request.Scope.ActionId,
                ReviewActionStatus.Planned,
                ReviewActionStatus.Sending,
                null,
                null,
                Now
            )
            .Should()
            .BeTrue();
        fixture.Publisher.SeedExistingComment(
            fixture.PublicationKey(request.Scope.ActionId, PersistedReviewActionKind.PostClarificationQuestion),
            "review-comment:42"
        );

        var recovered = await fixture.Coordinator.PostClarificationQuestionAsync(request, CancellationToken.None);

        recovered.Status.Should().Be(ReviewActionStatus.Accepted);
        recovered.ProviderCommentId.Should().Be("review-comment:42");
        fixture.Publisher.FindCallCount.Should().Be(1);
        fixture.Publisher.PostCount.Should().Be(0);
    }

    [Fact]
    public async Task Concurrent_discussion_publication_has_one_provider_effect_and_one_receipt()
    {
        using var fixture = new Fixture(EngagementRoundIntent.DiscussionFollowUp, createReviewRun: false);
        var request = new ClarificationQuestionRequest(
            fixture.Scope("question-1", live: true),
            "Could you clarify the intended fallback?",
            "review-comment:41"
        );
        fixture.Publisher.BlockFinds();

        var first = fixture.Coordinator.PostClarificationQuestionAsync(request, CancellationToken.None);
        await fixture.Publisher.WaitForFindsAsync(1);
        var second = fixture.Coordinator.PostClarificationQuestionAsync(request, CancellationToken.None);
        fixture.Publisher.ReleaseFinds();

        var outcomes = await Task.WhenAll(first, second);

        outcomes.Should().OnlyContain(outcome => outcome.Status == ReviewActionStatus.Accepted);
        outcomes.Select(outcome => outcome.ProviderCommentId).Should().OnlyContain(id => id == "resp-1");
        fixture.Publisher.FindCallCount.Should().Be(1);
        fixture.Publisher.PostCount.Should().Be(1);
    }

    [Fact]
    public async Task Authorized_retry_adopts_the_provider_backstop_and_records_one_root_receipt()
    {
        using var fixture = new Fixture();
        var collected = fixture.Root("root-1", "body");
        _ = await fixture.Coordinator.CreateRootSummaryAsync(collected, CancellationToken.None);
        var key = fixture.PublicationKey("root-1", PersistedReviewActionKind.CreateRootSummary);
        fixture.Publisher.SeedExistingComment(key, "issue-comment:41");

        var accepted = await fixture.Coordinator.CreateRootSummaryAsync(
            collected with
            {
                Scope = collected.Scope with { LivePostingAuthorized = true },
            },
            CancellationToken.None
        );
        var replay = await fixture.Coordinator.CreateRootSummaryAsync(
            collected with
            {
                Scope = collected.Scope with { LivePostingAuthorized = true },
            },
            CancellationToken.None
        );

        accepted.Status.Should().Be(ReviewActionStatus.Accepted);
        accepted.ProviderCommentId.Should().Be("issue-comment:41");
        replay.Should().Be(accepted);
        fixture.Publisher.FindCallCount.Should().Be(1);
        fixture.Publisher.PostCount.Should().Be(0);
        fixture.Store.GetEngagement(fixture.EngagementId)!.RootSummaryReceiptJson.Should().Contain("issue-comment:41");
    }

    [Fact]
    public async Task Authorized_retry_recovers_an_existing_planned_action_through_the_provider_backstop()
    {
        using var fixture = new Fixture();
        var request = fixture.Root("root-1", "body", live: true);
        fixture.Store.AddOrGetReviewAction(
            new PersistedReviewAction(
                fixture.RoundId,
                request.Scope.ActionId,
                PersistedReviewActionKind.CreateRootSummary,
                ReviewActionStatus.Planned,
                PayloadSha(new { body = "body", providerTargetId = (string?)null }),
                null,
                null,
                null,
                Now,
                Now
            ),
            request.Scope.Sources
        );
        fixture.Publisher.SeedExistingComment(
            fixture.PublicationKey("root-1", PersistedReviewActionKind.CreateRootSummary),
            "issue-comment:41"
        );

        var recovered = await fixture.Coordinator.CreateRootSummaryAsync(request, CancellationToken.None);

        recovered.Status.Should().Be(ReviewActionStatus.Accepted);
        recovered.ProviderCommentId.Should().Be("issue-comment:41");
        fixture.Publisher.FindCallCount.Should().Be(1);
        fixture.Publisher.PostCount.Should().Be(0);
    }

    [Fact]
    public async Task Concurrent_planned_root_recovery_returns_the_same_accepted_receipt_to_every_caller()
    {
        using var fixture = new Fixture();
        var request = fixture.Root("root-1", "body", live: true);
        fixture.Store.AddOrGetReviewAction(
            new PersistedReviewAction(
                fixture.RoundId,
                request.Scope.ActionId,
                PersistedReviewActionKind.CreateRootSummary,
                ReviewActionStatus.Planned,
                PayloadSha(new { body = "body", providerTargetId = (string?)null }),
                null,
                null,
                null,
                Now,
                Now
            ),
            request.Scope.Sources
        );
        fixture.Publisher.SeedExistingComment(
            fixture.PublicationKey("root-1", PersistedReviewActionKind.CreateRootSummary),
            "issue-comment:41"
        );
        fixture.Publisher.BlockFinds();

        var first = fixture.Coordinator.CreateRootSummaryAsync(request, CancellationToken.None);
        await fixture.Publisher.WaitForFindsAsync(1);
        fixture.AdvanceTime(TimeSpan.FromMinutes(1));
        var second = fixture.Coordinator.CreateRootSummaryAsync(request, CancellationToken.None);
        await fixture.Publisher.WaitForFindsAsync(2);
        fixture.Publisher.ReleaseFinds();
        await WaitForStatusAsync(
            () => fixture.Store.GetReviewAction(fixture.RoundId, "root-1")!.Status,
            ReviewActionStatus.Accepted
        );
        fixture.Publisher.ReleaseFinds();

        var outcomes = await Task.WhenAll(first, second);

        outcomes.Should().OnlyContain(outcome => outcome.Status == ReviewActionStatus.Accepted);
        outcomes.Select(outcome => outcome.ProviderCommentId).Should().OnlyContain(id => id == "issue-comment:41");
        fixture.Publisher.FindCallCount.Should().Be(2);
        fixture.Publisher.PostCount.Should().Be(0);
        fixture.Store.GetEngagement(fixture.EngagementId)!.RootSummaryReceiptJson.Should().NotBeNull();
    }

    [Fact]
    public async Task Accepted_root_replay_repairs_a_missing_engagement_root_receipt()
    {
        using var fixture = new Fixture();
        var request = fixture.Root("root-1", "body");
        fixture.Store.AddOrGetReviewAction(
            new PersistedReviewAction(
                fixture.RoundId,
                request.Scope.ActionId,
                PersistedReviewActionKind.CreateRootSummary,
                ReviewActionStatus.Planned,
                PayloadSha(new { body = "body", providerTargetId = (string?)null }),
                null,
                null,
                null,
                Now,
                Now
            ),
            request.Scope.Sources
        );
        const string receipt =
            "{\"provider\":\"github\",\"providerCommentId\":\"issue-comment:41\",\"outboxId\":7,\"acceptedAt\":\"2026-09-02T18:00:00+00:00\"}";
        fixture
            .Store.TryTransitionReviewAction(
                fixture.RoundId,
                request.Scope.ActionId,
                ReviewActionStatus.Planned,
                ReviewActionStatus.Accepted,
                receipt,
                null,
                Now
            )
            .Should()
            .BeTrue();
        fixture.Store.GetEngagement(fixture.EngagementId)!.RootSummaryReceiptJson.Should().BeNull();

        var replay = await fixture.Coordinator.CreateRootSummaryAsync(request, CancellationToken.None);

        replay.Status.Should().Be(ReviewActionStatus.Accepted);
        fixture.Store.GetEngagement(fixture.EngagementId)!.RootSummaryReceiptJson.Should().Be(receipt);
        fixture.Publisher.FindCallCount.Should().Be(0);
        fixture.Publisher.PostCount.Should().Be(0);
    }

    [Fact]
    public async Task Finalize_retry_completes_an_existing_planned_finalize_action()
    {
        using var fixture = new Fixture();
        var request = new FinalizeRoundRequest(fixture.Scope("final-1"), NoOp: true);
        fixture.Store.AddOrGetReviewAction(
            new PersistedReviewAction(
                fixture.RoundId,
                request.Scope.ActionId,
                PersistedReviewActionKind.FinalizeRound,
                ReviewActionStatus.Planned,
                PayloadSha(new { request.NoOp }),
                null,
                null,
                null,
                Now,
                Now
            ),
            request.Scope.Sources
        );

        var recovered = await fixture.Coordinator.FinalizeRoundAsync(request, CancellationToken.None);

        recovered.Status.Should().Be(ReviewActionStatus.Accepted);
        fixture.Publisher.FindCallCount.Should().Be(0);
        fixture.Publisher.PostCount.Should().Be(0);
    }

    [Fact]
    public async Task Github_inline_findings_are_submitted_as_one_atomic_exact_head_review()
    {
        using var fixture = new Fixture(EngagementRoundIntent.DiscussionFollowUp, createReviewRun: false);
        fixture.Publisher.RichReceipt = new PostedComment(
            "review:900",
            ReviewId: "900",
            Permalink: "https://example.test/reviews/900",
            Status: "COMMENTED"
        );
        fixture.Provider.InlineAnchorSnapshot = new ProviderInlineAnchorSnapshot(
            "head-1",
            [
                new ProviderInlineAnchorFile("src/Retry.cs", [new LineRange(11, 2)], []),
                new ProviderInlineAnchorFile("tests/RetryTests.cs", [new LineRange(44, 1)], []),
            ]
        );
        var request = new InlineFindingsRequest(
            fixture.Scope("inline-github", live: true),
            [
                new InlineFindingRequest("src/Retry.cs", "RIGHT", 11, 12, "Retry state is lost."),
                new InlineFindingRequest("tests/RetryTests.cs", "RIGHT", null, 44, "This assertion is vacuous."),
            ]
        );

        var outcome = await fixture.Coordinator.SubmitInlineFindingsAsync(request, CancellationToken.None);

        outcome.Status.Should().Be(ReviewActionStatus.Accepted);
        outcome.ProviderReviewId.Should().Be("900");
        outcome.ProviderPermalink.Should().Be("https://example.test/reviews/900");
        var inlineReview = fixture.Publisher.InlineReviews.Should().ContainSingle().Subject;
        inlineReview.CommitId.Should().Be("head-1");
        inlineReview
            .Comments.Should()
            .BeEquivalentTo(
                [
                    new GitHubInlineReviewComment(
                        new ProviderCommentSpan("src/Retry.cs", "RIGHT", 11, 12),
                        "Retry state is lost."
                    ),
                    new GitHubInlineReviewComment(
                        new ProviderCommentSpan("tests/RetryTests.cs", "RIGHT", null, 44),
                        "This assertion is vacuous."
                    ),
                ],
                options => options.WithStrictOrdering()
            );
        fixture.Publisher.FindCallCount.Should().Be(1);
        fixture.Publisher.PostCount.Should().Be(1);
    }

    [Fact]
    public async Task Github_inline_batch_rejects_a_syntactically_valid_anchor_absent_from_the_current_diff()
    {
        using var fixture = new Fixture(EngagementRoundIntent.DiscussionFollowUp, createReviewRun: false);
        fixture.Provider.InlineAnchorSnapshot = new ProviderInlineAnchorSnapshot(
            "head-1",
            [new ProviderInlineAnchorFile("src/Retry.cs", [new LineRange(11, 2)], [])]
        );
        var request = new InlineFindingsRequest(
            fixture.Scope("inline-absent", live: true),
            [new InlineFindingRequest("src/Retry.cs", "RIGHT", null, 30, "This line is not in the current diff.")]
        );

        var outcome = await fixture.Coordinator.SubmitInlineFindingsAsync(request, CancellationToken.None);

        outcome.Status.Should().Be(ReviewActionStatus.Rejected);
        outcome.RejectionCode.Should().Be("invalid_anchor");
        fixture.Provider.InlineAnchorSnapshotCalls.Should().Be(1);
        fixture.Publisher.FindCallCount.Should().Be(0);
        fixture.Publisher.PostCount.Should().Be(0);
        fixture.Publisher.InlineReviews.Should().BeEmpty();
    }

    [Fact]
    public async Task Invalid_github_inline_batch_is_rejected_before_any_provider_effect()
    {
        using var fixture = new Fixture(EngagementRoundIntent.DiscussionFollowUp, createReviewRun: false);
        var request = new InlineFindingsRequest(
            fixture.Scope("inline-invalid", live: true),
            [
                new InlineFindingRequest("src/Retry.cs", "RIGHT", 11, 12, "valid"),
                new InlineFindingRequest("src/Retry.cs", "RIGHT", 18, 17, "invalid range"),
            ]
        );

        var outcome = await fixture.Coordinator.SubmitInlineFindingsAsync(request, CancellationToken.None);

        outcome.Status.Should().Be(ReviewActionStatus.Rejected);
        outcome.RejectionCode.Should().Be("invalid_anchor");
        fixture.Provider.PrStateCalls.Should().Be(0);
        fixture.Publisher.FindCallCount.Should().Be(0);
        fixture.Publisher.PostCount.Should().Be(0);
        fixture.Publisher.InlineReviews.Should().BeEmpty();
    }

    [Fact]
    public async Task Github_inline_review_replay_adopts_the_complete_native_receipt_without_posting_again()
    {
        using var fixture = new Fixture(EngagementRoundIntent.DiscussionFollowUp, createReviewRun: false);
        fixture.Provider.InlineAnchorSnapshot = new ProviderInlineAnchorSnapshot(
            "head-1",
            [new ProviderInlineAnchorFile("src/Retry.cs", [new LineRange(12, 1)], [])]
        );
        var receipt = new PostedComment(
            "review:901",
            ReviewId: "901",
            Permalink: "https://example.test/reviews/901",
            Status: "COMMENTED"
        );
        fixture.Publisher.SeedExistingComment(
            fixture.PublicationKey("inline-recover", PersistedReviewActionKind.SubmitInlineFindings),
            receipt
        );
        var request = new InlineFindingsRequest(
            fixture.Scope("inline-recover", live: true),
            [new InlineFindingRequest("src/Retry.cs", "RIGHT", null, 12, "Recovered finding.")]
        );

        var outcome = await fixture.Coordinator.SubmitInlineFindingsAsync(request, CancellationToken.None);

        outcome.Status.Should().Be(ReviewActionStatus.Accepted);
        outcome.ProviderReviewId.Should().Be("901");
        outcome.ProviderPermalink.Should().Be("https://example.test/reviews/901");
        fixture.Publisher.FindCallCount.Should().Be(1);
        fixture.Publisher.PostCount.Should().Be(0);
        fixture.Publisher.InlineReviews.Should().BeEmpty();
    }

    [Fact]
    public async Task Concurrent_github_inline_review_retries_publish_exactly_once()
    {
        using var fixture = new Fixture(EngagementRoundIntent.DiscussionFollowUp, createReviewRun: false);
        fixture.Provider.InlineAnchorSnapshot = new ProviderInlineAnchorSnapshot(
            "head-1",
            [new ProviderInlineAnchorFile("src/Retry.cs", [new LineRange(12, 1)], [])]
        );
        fixture.Publisher.BlockFinds();
        var request = new InlineFindingsRequest(
            fixture.Scope("inline-concurrent", live: true),
            [new InlineFindingRequest("src/Retry.cs", "RIGHT", null, 12, "Concurrent finding.")]
        );

        var first = fixture.Coordinator.SubmitInlineFindingsAsync(request, CancellationToken.None);
        var second = fixture.Coordinator.SubmitInlineFindingsAsync(request, CancellationToken.None);
        await fixture.Publisher.WaitForFindsAsync(1);
        fixture.Publisher.ReleaseFinds();
        var outcomes = await Task.WhenAll(first, second);

        outcomes.Should().OnlyContain(outcome => outcome.Status == ReviewActionStatus.Accepted);
        fixture.Publisher.FindCallCount.Should().Be(1);
        fixture.Publisher.PostCount.Should().Be(1);
        fixture.Publisher.InlineReviews.Should().ContainSingle();
    }

    [Fact]
    public async Task Github_inline_review_revalidates_the_head_after_idempotency_lookup_before_sending()
    {
        using var fixture = new Fixture(EngagementRoundIntent.DiscussionFollowUp, createReviewRun: false);
        fixture.Provider.InlineAnchorSnapshot = new ProviderInlineAnchorSnapshot(
            "head-1",
            [new ProviderInlineAnchorFile("src/Retry.cs", [new LineRange(12, 1)], [])]
        );
        fixture.Publisher.BlockFinds();
        var request = new InlineFindingsRequest(
            fixture.Scope("inline-stale-before-send", live: true),
            [new InlineFindingRequest("src/Retry.cs", "RIGHT", null, 12, "Do not publish this on a newer head.")]
        );

        var publication = fixture.Coordinator.SubmitInlineFindingsAsync(request, CancellationToken.None);
        await fixture.Publisher.WaitForFindsAsync(1);
        fixture.Provider.CurrentHeadSha = "head-2";
        fixture.Publisher.ReleaseFinds();
        var outcome = await publication;

        outcome.Status.Should().Be(ReviewActionStatus.Rejected);
        outcome.RejectionCode.Should().Be("stale_head");
        fixture.Provider.HeadShaCalls.Should().BeGreaterThan(1);
        fixture.Publisher.PostCount.Should().Be(0);
        fixture.Publisher.InlineReviews.Should().BeEmpty();
    }

    [Fact]
    public async Task Collect_only_reply_records_the_exact_action_without_resolving_or_contacting_the_provider()
    {
        using var fixture = new Fixture(EngagementRoundIntent.DiscussionFollowUp, createReviewRun: false);
        var request = new DiscussionReplyRequest(
            fixture.Scope("reply-1"),
            "The retry applies per run.",
            "review-comment:41"
        );

        var first = await fixture.Coordinator.ReplyToDiscussionAsync(request, CancellationToken.None);
        var replay = await fixture.Coordinator.ReplyToDiscussionAsync(request, CancellationToken.None);

        first.Status.Should().Be(ReviewActionStatus.CollectedOnly);
        replay.Should().Be(first);
        fixture.Provider.EngagementSnapshotCalls.Should().Be(0);
        fixture.Publisher.FindCallCount.Should().Be(0);
        fixture.Publisher.PostCount.Should().Be(0);
    }

    [Fact]
    public async Task Github_inline_reply_resolves_the_exact_host_supplied_target_and_preserves_native_receipt()
    {
        using var fixture = new Fixture(EngagementRoundIntent.DiscussionFollowUp, createReviewRun: false);
        const string providerTargetId = "review-comment:42";
        var target = fixture.ProviderComment(
            threadId: "41",
            commentId: "42",
            providerObjectId: providerTargetId,
            parentCommentId: "41",
            permalink: "https://example.test/review-comments/42"
        );
        fixture.AuthorizeProviderTarget(target);
        fixture.SetEngagementSnapshot(target);
        fixture.Publisher.RichReceipt = new PostedComment(
            "review:900:thread:41:comment:43",
            ReviewId: "900",
            ThreadId: "41",
            CommentId: "43",
            ParentCommentId: "41",
            Permalink: "https://example.test/review-comments/43"
        );
        var request = new DiscussionReplyRequest(
            fixture.Scope("reply-1", live: true),
            "The retry applies per run.",
            providerTargetId
        );

        var first = await fixture.Coordinator.ReplyToDiscussionAsync(request, CancellationToken.None);
        var replay = await fixture.Coordinator.ReplyToDiscussionAsync(request, CancellationToken.None);

        first
            .Should()
            .Be(
                new PublicationOutcome(
                    ReviewActionStatus.Accepted,
                    "reply-1",
                    "900",
                    "41",
                    "43",
                    "https://example.test/review-comments/43",
                    false,
                    Now,
                    null
                )
            );
        replay.Should().Be(first);
        fixture
            .Publisher.InlineReplies.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                new GitHubInlineReplyRequest(
                    "41",
                    "42",
                    "https://example.test/review-comments/42",
                    "The retry applies per run."
                )
            );
        fixture.Publisher.PostCount.Should().Be(1);
        fixture.Provider.EngagementSnapshotCalls.Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_native_replies_have_one_provider_effect_and_one_complete_receipt()
    {
        using var fixture = new Fixture(EngagementRoundIntent.DiscussionFollowUp, createReviewRun: false);
        const string providerTargetId = "review-comment:42";
        var target = fixture.ProviderComment(threadId: "41", commentId: "42", providerObjectId: providerTargetId);
        fixture.AuthorizeProviderTarget(target);
        fixture.SetEngagementSnapshot(target);
        fixture.Publisher.RichReceipt = new PostedComment(
            "review:900:thread:41:comment:43",
            ReviewId: "900",
            ThreadId: "41",
            CommentId: "43"
        );
        var request = new DiscussionReplyRequest(
            fixture.Scope("reply-concurrent", live: true),
            "reply",
            providerTargetId
        );
        fixture.Publisher.BlockFinds();

        var first = fixture.Coordinator.ReplyToDiscussionAsync(request, CancellationToken.None);
        await fixture.Publisher.WaitForFindsAsync(1);
        var second = fixture.Coordinator.ReplyToDiscussionAsync(request, CancellationToken.None);
        fixture.Publisher.ReleaseFinds();

        var outcomes = await Task.WhenAll(first, second);

        outcomes.Should().OnlyContain(outcome => outcome.Status == ReviewActionStatus.Accepted);
        outcomes.Select(outcome => outcome.ProviderReviewId).Should().OnlyContain(id => id == "900");
        outcomes.Select(outcome => outcome.ProviderCommentId).Should().OnlyContain(id => id == "43");
        fixture.Publisher.FindCallCount.Should().Be(1);
        fixture.Publisher.PostCount.Should().Be(1);
        fixture.Publisher.InlineReplies.Should().ContainSingle();
    }

    [Fact]
    public async Task Github_issue_comment_reply_is_explicitly_degraded_and_keeps_the_target_permalink()
    {
        using var fixture = new Fixture(EngagementRoundIntent.DiscussionFollowUp, createReviewRun: false);
        const string providerTargetId = "issue-comment:51";
        var target = fixture.ProviderComment(
            threadId: "51",
            commentId: "51",
            providerObjectId: providerTargetId,
            permalink: "https://example.test/issues/comments/51"
        );
        fixture.AuthorizeProviderTarget(target);
        fixture.SetEngagementSnapshot(target);
        fixture.Publisher.RichReceipt = new PostedComment(
            "issue-comment:52",
            CommentId: "52",
            Permalink: "https://example.test/issues/comments/52",
            RelationshipDegraded: true
        );
        var request = new DiscussionReplyRequest(
            fixture.Scope("reply-flat", live: true),
            "Thanks, that resolves the question.",
            providerTargetId
        );

        var outcome = await fixture.Coordinator.ReplyToDiscussionAsync(request, CancellationToken.None);

        outcome.RelationshipDegraded.Should().BeTrue();
        outcome.ProviderCommentId.Should().Be("52");
        fixture
            .Publisher.FlatConversationComments.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                new GitHubFlatConversationComment(
                    "Thanks, that resolves the question.",
                    "https://example.test/issues/comments/51"
                )
            );
        fixture.Publisher.PostCount.Should().Be(1);
    }

    [Fact]
    public async Task Ado_reply_uses_the_provider_qualified_target_when_comment_ids_repeat_across_threads()
    {
        using var fixture = new Fixture(
            EngagementRoundIntent.DiscussionFollowUp,
            createReviewRun: false,
            providerName: "ado"
        );
        var other = fixture.ProviderComment(
            threadId: "800",
            commentId: "2",
            providerObjectId: "thread:800:comment:2",
            status: "active"
        );
        var target = fixture.ProviderComment(
            threadId: "900",
            commentId: "2",
            providerObjectId: "thread:900:comment:2",
            parentCommentId: "1",
            path: "/src/Retry.cs",
            side: "RIGHT",
            startLine: 11,
            endLine: 12,
            status: "active",
            iterationContext: "{\"changeTrackingId\":7,\"iterationContext\":{\"firstComparingIteration\":3,\"secondComparingIteration\":4}}"
        );
        fixture.AuthorizeProviderTarget(target);
        fixture.SetEngagementSnapshot(other, target);
        fixture.Publisher.RichReceipt = new PostedComment(
            "thread:900:comment:3",
            ThreadId: "900",
            CommentId: "3",
            ParentCommentId: "2",
            Status: "active",
            Span: new ProviderCommentSpan("/src/Retry.cs", "RIGHT", 11, 12),
            IterationContext: new AdoPullRequestThreadContext(7, new AdoIterationContext(3, 4))
        );
        var request = new DiscussionReplyRequest(
            fixture.Scope("reply-ado", live: true),
            "The retry applies per run.",
            target.ProviderObjectId
        );

        var outcome = await fixture.Coordinator.ReplyToDiscussionAsync(request, CancellationToken.None);

        outcome.Status.Should().Be(ReviewActionStatus.Accepted);
        outcome.ProviderThreadId.Should().Be("900");
        outcome.ProviderCommentId.Should().Be("3");
        fixture
            .Publisher.ThreadReplies.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                new AdoThreadReplyRequest(
                    "900",
                    "2",
                    "active",
                    new ProviderCommentSpan("/src/Retry.cs", "RIGHT", 11, 12),
                    new AdoPullRequestThreadContext(7, new AdoIterationContext(3, 4)),
                    "The retry applies per run."
                )
            );
        fixture.Publisher.PostCount.Should().Be(1);
    }

    [Theory]
    [InlineData("thread:800:comment:2", "target_not_authorized")]
    [InlineData("thread:999:comment:2", "target_not_found")]
    public async Task Ado_reply_rejects_a_guessed_or_missing_provider_target_without_posting(
        string providerTargetId,
        string rejectionCode
    )
    {
        using var fixture = new Fixture(
            EngagementRoundIntent.DiscussionFollowUp,
            createReviewRun: false,
            providerName: "ado"
        );
        var authorized = fixture.ProviderComment(
            threadId: "900",
            commentId: "2",
            providerObjectId: "thread:900:comment:2"
        );
        var guessed = fixture.ProviderComment(
            threadId: "800",
            commentId: "2",
            providerObjectId: "thread:800:comment:2"
        );
        fixture.AuthorizeProviderTarget(authorized);
        fixture.SetEngagementSnapshot(
            providerTargetId == "thread:999:comment:2" ? [authorized] : [authorized, guessed]
        );
        var request = new DiscussionReplyRequest(
            fixture.Scope("reply-rejected", live: true),
            "reply",
            providerTargetId
        );

        var outcome = await fixture.Coordinator.ReplyToDiscussionAsync(request, CancellationToken.None);

        outcome.RejectionCode.Should().Be(rejectionCode);
        fixture.Publisher.PostCount.Should().Be(0);
        fixture.Publisher.ThreadReplies.Should().BeEmpty();
    }

    [Fact]
    public async Task Ado_reply_rejects_malformed_present_iteration_context_without_posting()
    {
        using var fixture = new Fixture(
            EngagementRoundIntent.DiscussionFollowUp,
            createReviewRun: false,
            providerName: "ado"
        );
        var target = fixture.ProviderComment(
            threadId: "900",
            commentId: "2",
            providerObjectId: "thread:900:comment:2",
            iterationContext: "{not-json"
        );
        fixture.AuthorizeProviderTarget(target);
        fixture.SetEngagementSnapshot(target);
        var request = new DiscussionReplyRequest(
            fixture.Scope("reply-malformed-context", live: true),
            "reply",
            target.ProviderObjectId
        );

        var outcome = await fixture.Coordinator.ReplyToDiscussionAsync(request, CancellationToken.None);

        outcome.Status.Should().Be(ReviewActionStatus.Rejected);
        outcome.RejectionCode.Should().Be("target_not_replyable");
        fixture.Publisher.PostCount.Should().Be(0);
        fixture.Publisher.ThreadReplies.Should().BeEmpty();
    }

    [Fact]
    public async Task Reply_retry_adopts_the_complete_provider_receipt_after_a_crash_without_reposting()
    {
        using var fixture = new Fixture(EngagementRoundIntent.DiscussionFollowUp, createReviewRun: false);
        const string providerTargetId = "review-comment:41";
        var target = fixture.ProviderComment("41", "41", providerTargetId);
        fixture.AuthorizeProviderTarget(target);
        fixture.SetEngagementSnapshot(target);
        var request = new DiscussionReplyRequest(fixture.Scope("reply-recover", live: true), "reply", providerTargetId);
        fixture.Store.AddOrGetReviewAction(
            new PersistedReviewAction(
                fixture.RoundId,
                request.Scope.ActionId,
                PersistedReviewActionKind.ReplyToDiscussion,
                ReviewActionStatus.Planned,
                PayloadSha(new { request.Body, request.ProviderTargetId }),
                System.Text.Json.JsonSerializer.Serialize(
                    new { request.ProviderTargetId },
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
                ),
                null,
                null,
                Now,
                Now
            ),
            request.Scope.Sources
        );
        fixture.Store.TryTransitionReviewAction(
            fixture.RoundId,
            request.Scope.ActionId,
            ReviewActionStatus.Planned,
            ReviewActionStatus.Sending,
            null,
            null,
            Now
        );
        fixture.Publisher.SeedExistingComment(
            fixture.PublicationKey(request.Scope.ActionId, PersistedReviewActionKind.ReplyToDiscussion),
            new PostedComment(
                "review:900:thread:41:comment:42",
                ReviewId: "900",
                ThreadId: "41",
                CommentId: "42",
                ParentCommentId: "41",
                Permalink: "https://example.test/review-comments/42"
            )
        );

        var recovered = await fixture.Coordinator.ReplyToDiscussionAsync(request, CancellationToken.None);

        recovered.ProviderReviewId.Should().Be("900");
        recovered.ProviderThreadId.Should().Be("41");
        recovered.ProviderCommentId.Should().Be("42");
        recovered.ProviderPermalink.Should().Be("https://example.test/review-comments/42");
        fixture.Publisher.PostCount.Should().Be(0);
        fixture.Publisher.InlineReplies.Should().BeEmpty();
    }

    [Fact]
    public async Task Reply_retry_rejects_an_unauthorized_sending_action_before_adopting_a_provider_marker()
    {
        using var fixture = new Fixture(EngagementRoundIntent.DiscussionFollowUp, createReviewRun: false);
        const string providerTargetId = "review-comment:41";
        var request = new DiscussionReplyRequest(
            fixture.Scope("reply-untrusted", live: true),
            "reply",
            providerTargetId
        );
        fixture.Store.AddOrGetReviewAction(
            new PersistedReviewAction(
                fixture.RoundId,
                request.Scope.ActionId,
                PersistedReviewActionKind.ReplyToDiscussion,
                ReviewActionStatus.Planned,
                PayloadSha(new { request.Body, request.ProviderTargetId }),
                System.Text.Json.JsonSerializer.Serialize(
                    new { request.ProviderTargetId },
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
                ),
                null,
                null,
                Now,
                Now
            ),
            request.Scope.Sources
        );
        fixture.Store.TryTransitionReviewAction(
            fixture.RoundId,
            request.Scope.ActionId,
            ReviewActionStatus.Planned,
            ReviewActionStatus.Sending,
            null,
            null,
            Now
        );
        fixture.Publisher.SeedExistingComment(
            fixture.PublicationKey(request.Scope.ActionId, PersistedReviewActionKind.ReplyToDiscussion),
            new PostedComment("review:900:thread:41:comment:42", ReviewId: "900", ThreadId: "41", CommentId: "42")
        );

        var outcome = await fixture.Coordinator.ReplyToDiscussionAsync(request, CancellationToken.None);

        outcome.Status.Should().Be(ReviewActionStatus.Rejected);
        outcome.RejectionCode.Should().Be("target_not_authorized");
        outcome.ProviderReviewId.Should().BeNull();
        fixture.Provider.EngagementSnapshotCalls.Should().Be(0);
        fixture.Publisher.PostCount.Should().Be(0);
    }

    [Fact]
    public async Task Root_is_singleton_and_delta_requires_an_accepted_root()
    {
        using var fixture = new Fixture();
        _ = await fixture.Coordinator.CreateRootSummaryAsync(fixture.Root("root-1", "body"), CancellationToken.None);

        var duplicateRoot = await fixture.Coordinator.CreateRootSummaryAsync(
            fixture.Root("root-2", "other body"),
            CancellationToken.None
        );
        var prematureDelta = await fixture.Coordinator.AppendSummaryDeltaAsync(
            new SummaryDeltaRequest(fixture.Scope("delta-1"), "delta"),
            CancellationToken.None
        );

        duplicateRoot.RejectionCode.Should().Be("root_summary_already_planned");
        prematureDelta.RejectionCode.Should().Be("root_summary_not_accepted");
        fixture.Publisher.PostCount.Should().Be(0);
    }

    [Fact]
    public async Task Finalize_is_local_and_requires_every_prior_action_to_be_terminal()
    {
        using var fixture = new Fixture();
        fixture.Store.AddOrGetReviewAction(
            new PersistedReviewAction(
                fixture.RoundId,
                "pending-1",
                PersistedReviewActionKind.ReplyToDiscussion,
                ReviewActionStatus.Planned,
                Sha256("pending"),
                null,
                null,
                null,
                Now,
                Now
            ),
            fixture.Scope("pending-1").Sources
        );

        var blocked = await fixture.Coordinator.FinalizeRoundAsync(
            new FinalizeRoundRequest(fixture.Scope("final-1"), NoOp: true),
            CancellationToken.None
        );
        fixture.Store.TryTransitionReviewAction(
            fixture.RoundId,
            "pending-1",
            ReviewActionStatus.Planned,
            ReviewActionStatus.CollectedOnly,
            null,
            null,
            Now.AddMinutes(1)
        );
        var finalized = await fixture.Coordinator.FinalizeRoundAsync(
            new FinalizeRoundRequest(fixture.Scope("final-2"), NoOp: true),
            CancellationToken.None
        );

        blocked.RejectionCode.Should().Be("actions_not_terminal");
        finalized.Status.Should().Be(ReviewActionStatus.Accepted);
        finalized.ProviderCommentId.Should().BeNull();
        fixture.Publisher.FindCallCount.Should().Be(0);
        fixture.Publisher.PostCount.Should().Be(0);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempSqliteDatabase _database = new();
        private readonly FakeTimeProvider _time = new(Now);
        private AuditSourceReference? _authorizedSource;

        public Fixture(
            EngagementRoundIntent intent = EngagementRoundIntent.CodeReview,
            bool createReviewRun = true,
            string providerName = "github"
        )
        {
            var storageProvider = providerName == "ado" ? "azure-devops" : providerName;
            Store = new ReviewStore(_database.ConnectionString, _time);
            Repo = new RepoIdentity
            {
                Provider = storageProvider,
                OrgOrOwner = "achieveai",
                Project = providerName == "ado" ? "LmDotnetTools" : null,
                RepoName = "LmDotnetTools",
                RepoStableId = providerName == "github" ? "R_node_123" : "ado-repo-123",
            };
            RepoId = Store.EnsureRepo(Repo);
            var engagement = Store.CreateOrGetEngagement(
                new PrEngagement(
                    0,
                    RepoId,
                    storageProvider,
                    "118",
                    PrLifecycleState.Open,
                    "head-1",
                    "base-1",
                    null,
                    new ProviderActivityWatermark(storageProvider, Now, "comment-1"),
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    Now
                )
            );
            EngagementId = engagement.Id;
            var round = Store.TryAdmitRound(
                new EngagementRound(
                    0,
                    engagement.Id,
                    intent,
                    EngagementRoundStatus.Pending,
                    "head-1",
                    "base-1",
                    null,
                    engagement.LatestActivity,
                    0,
                    null,
                    0,
                    null,
                    null,
                    null,
                    null,
                    null
                )
            )!;
            RoundId = round.Id;
            if (createReviewRun)
            {
                _ = Store.CreateOrGetReviewRun(
                    new ReviewRun
                    {
                        RepoId = RepoId,
                        PrId = "118",
                        HeadSha = "head-1",
                        BaseSha = "base-1",
                        TriggerWatermark = "comment-1",
                        ReviewKind = "full",
                        VariantId = "primary",
                        Mode = "collect-only",
                        EngagementRoundId = RoundId,
                        Stage = ReviewStage.Discovered,
                        WorkflowStatus = WorkflowStatus.Pending,
                        PrLifecycleState = PrLifecycleState.Open,
                    }
                );
            }
            Store
                .TryTransitionEngagementRound(
                    RoundId,
                    EngagementRoundStatus.Pending,
                    EngagementRoundStatus.Running,
                    Now
                )
                .Should()
                .BeTrue();
            _ = Store.StoreAuditRecord(
                new ModelTurnAuditRecord(
                    "source-1",
                    new MultiTurnAuditScope(EngagementId.ToString(), RoundId.ToString()),
                    "thread-1",
                    "run-1",
                    "generation-1",
                    null,
                    1,
                    MultiTurnAuditRecordTypes.ModelResponse,
                    "assistant",
                    "claude-opus-5",
                    "anthropic",
                    "exact evidence"u8.ToArray(),
                    Sha256("exact evidence"),
                    "exact evidence"u8.Length,
                    AuditCaptureOutcome.Complete,
                    null,
                    Now
                )
            );
            Provider = new MockPrProvider(
                providerName,
                [],
                new OpaqueCursor
                {
                    Provider = storageProvider,
                    Scope = "cursor",
                    CursorVersion = 1,
                    CursorPayload = "{}",
                }
            )
            {
                CurrentHeadSha = "head-1",
            };
            Publisher = new FakeReviewCommentPublisher(providerName);
            Coordinator = new ReviewPublicationCoordinator(
                Store,
                [Provider],
                [Publisher],
                _time,
                NullLoggerFactory.Instance
            );
        }

        public ReviewStore Store { get; }
        public RepoIdentity Repo { get; }
        public long RepoId { get; }
        public long EngagementId { get; }
        public long RoundId { get; }
        public MockPrProvider Provider { get; }
        public FakeReviewCommentPublisher Publisher { get; }
        public ReviewPublicationCoordinator Coordinator { get; }

        public PublicationScope Scope(string actionId, bool live = false) =>
            new(
                RoundId,
                actionId,
                RepoIdentity.ToPublisherNamespace(Repo.Provider),
                RepoId,
                "118",
                "head-1",
                _authorizedSource is { } source
                    ? [source]
                    : [new AuditSourceReference("source-1", Sha256("exact evidence"))],
                live
            );

        public RootSummaryRequest Root(string actionId, string body, bool live = false) =>
            new(Scope(actionId, live), body);

        public void AdvanceTime(TimeSpan amount) => _time.Advance(amount);

        public ProviderDiscussionRef ProviderComment(
            string threadId,
            string commentId,
            string providerObjectId,
            string? parentCommentId = null,
            string? permalink = null,
            string? path = null,
            string? side = null,
            int? startLine = null,
            int? endLine = null,
            string? status = null,
            string? iterationContext = null
        ) =>
            new(
                RepoIdentity.ToPublisherNamespace(Repo.Provider),
                threadId,
                commentId,
                providerObjectId,
                parentCommentId,
                permalink,
                path,
                side,
                startLine,
                endLine,
                status,
                iterationContext,
                Now,
                "reviewer@example.test",
                "provider discussion",
                ProviderDiscussionKind.Comment
            );

        public void SetEngagementSnapshot(params ProviderDiscussionRef[] activity) =>
            Provider.EngagementSnapshot = ProviderEngagementSnapshot.Create(
                PrLifecycleState.Open,
                "head-1",
                "base-1",
                new ProviderActivityWatermark(RepoIdentity.ToPublisherNamespace(Repo.Provider), Now, "upper"),
                activity
            );

        public void AuthorizeProviderTarget(ProviderDiscussionRef target)
        {
            var content = Encoding.UTF8.GetBytes(target.Body);
            var source = Store.StoreAuditRecord(
                new ModelTurnAuditRecord(
                    $"provider-target-{target.ThreadId}-{target.CommentId}",
                    new MultiTurnAuditScope(EngagementId.ToString(), RoundId.ToString()),
                    $"provider:{RepoIdentity.ToPublisherNamespace(Repo.Provider)}:{target.ThreadId}",
                    $"engagement-round:{RoundId}",
                    $"provider-object:{target.ProviderObjectId}",
                    null,
                    2,
                    EngagementDiscussionRoundExecutor.ProviderCommentRecordType,
                    "user",
                    null,
                    RepoIdentity.ToPublisherNamespace(Repo.Provider),
                    content,
                    Sha256(content),
                    content.Length,
                    AuditCaptureOutcome.Complete,
                    null,
                    Now
                )
            );
            _authorizedSource = new AuditSourceReference(source.Id, source.ContentSha256);
        }

        public string PublicationKey(string actionId, PersistedReviewActionKind kind) =>
            IdempotencyKey.Build(
                new IdempotencyKeyComponents(
                    RepoIdentity.ToPublisherNamespace(Repo.Provider),
                    Repo.OrgOrOwner,
                    Repo.Project,
                    Repo.RepoStableId!,
                    "118",
                    "typed-review-action",
                    kind.ToString(),
                    $"{RoundId}-{actionId}",
                    "head-1",
                    "primary"
                )
            );

        public void Dispose()
        {
            Store.Dispose();
            _database.Dispose();
        }
    }

    private static async Task WaitForStatusAsync(Func<ReviewActionStatus> read, ReviewActionStatus expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (read() != expected)
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static string PayloadSha<T>(T payload) =>
        Sha256(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                payload,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
            )
        );

    private static string Sha256(string content) => Sha256(Encoding.UTF8.GetBytes(content));

    private static string Sha256(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}
