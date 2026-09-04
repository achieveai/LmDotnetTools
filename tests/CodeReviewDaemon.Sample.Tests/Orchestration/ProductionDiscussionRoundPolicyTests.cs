using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace.Git;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PersistedReviewActionKind = CodeReviewDaemon.Sample.Persistence.Models.ReviewActionKind;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

public sealed class ProductionDiscussionRoundPolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_loop_without_spawn_suppression_is_refused_before_the_model_turn()
    {
        using var database = new TempSqliteDatabase();
        using var store = new ReviewStore(database.ConnectionString);
        var repo = new RepoIdentity
        {
            Provider = "github",
            OrgOrOwner = "achieveai",
            RepoName = "LmDotnetTools",
        };
        var repoId = store.EnsureRepo(repo);
        var watermark = new ProviderActivityWatermark("github", Start, "comment-1");
        var engagement = store.CreateOrGetEngagement(
            new PrEngagement(
                0,
                repoId,
                "github",
                "118",
                PrLifecycleState.Open,
                "head-exact",
                "base-exact",
                null,
                watermark,
                null,
                null,
                null,
                null,
                null,
                null,
                Start
            )
        );
        var round = store.TryAdmitRound(
            new EngagementRound(
                0,
                engagement.Id,
                EngagementRoundIntent.DiscussionFollowUp,
                EngagementRoundStatus.Pending,
                "head-exact",
                "base-exact",
                null,
                watermark,
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
        store
            .TryTransitionEngagementRound(round.Id, EngagementRoundStatus.Pending, EngagementRoundStatus.Running, Start)
            .Should()
            .BeTrue();

        var httpHandler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "api/workspaces", "[]")
            .OnJson(
                HttpMethod.Post,
                "api/workspaces",
                "{\"id\":\"workspace-118\",\"name\":\"Discussion PR #118\","
                    + "\"directoryRelPath\":\"review-github-achieveai-lmdotnettools-pr-118\","
                    + "\"marketplaces\":[\"code-reviewer\"]}"
            );
        using var http = new HttpClient(httpHandler) { BaseAddress = new Uri("https://review-host.test/") };
        var preparer = new S2SReviewWorkspacePreparer(
            new LmStreamingS2SClient(http, "secret", "app", "key"),
            new GitRunner(new FakeSandboxCommandRunner()),
            "/workspaces",
            "code-reviewer",
            NullLogger<S2SReviewWorkspacePreparer>.Instance
        );
        var loops = new FakeReviewAgentLoopFactory
        {
            DecorateCreatedAgent = agent =>
            {
                agent.SuppressSpawning = null;
                return agent;
            },
        };
        var time = new FakeTimeProvider(Start);
        var publisher = new FakeReviewCommentPublisher();
        var publication = new ReviewPublicationCoordinator(
            store,
            [CreateProvider()],
            [publisher],
            time,
            NullLoggerFactory.Instance
        );
        var policy = new ProductionDiscussionRoundPolicy(
            store,
            preparer,
            loops,
            new ReviewStoreRoundObservationSink(store, time),
            publication,
            new CodeReviewDaemonOptions
            {
                ReviewModelId = "gpt-5.6-sol",
                ToolAssistedReasoningEffort = "xhigh",
                ReviewStageDeadlineMinutes = 7,
            },
            time,
            NullLoggerFactory.Instance
        );
        var context = new DiscussionRoundContext(
            round.Id,
            round.HeadSha,
            [new DiscussionComment("comment-1", "octocat", "Why?", new AuditSourceReference("source-1", "sha-1"))],
            []
        );

        var act = () => policy.ExecuteAsync(context, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*spawn-suppression*");
        var agent = loops.CreatedAgents.Should().ContainSingle().Subject;
        agent.ReceivedInputs.Should().BeEmpty();
        agent.Lifecycle.Should().Equal(FakeMultiTurnAgent.DisposeEvent);
    }

    [Fact]
    public async Task A_no_op_turn_finalizes_locally_and_returns_completed_without_provider_noise()
    {
        using var database = new TempSqliteDatabase();
        using var store = new ReviewStore(database.ConnectionString);
        var repo = new RepoIdentity
        {
            Provider = "github",
            OrgOrOwner = "achieveai",
            RepoName = "LmDotnetTools",
        };
        var repoId = store.EnsureRepo(repo);
        var watermark = new ProviderActivityWatermark("github", Start, "comment-1");
        var engagement = store.CreateOrGetEngagement(
            new PrEngagement(
                0,
                repoId,
                "github",
                "118",
                PrLifecycleState.Open,
                "head-exact",
                "base-exact",
                null,
                watermark,
                null,
                null,
                null,
                null,
                null,
                null,
                Start
            )
        );
        var round = store.TryAdmitRound(
            new EngagementRound(
                0,
                engagement.Id,
                EngagementRoundIntent.DiscussionFollowUp,
                EngagementRoundStatus.Pending,
                "head-exact",
                "base-exact",
                null,
                watermark,
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
        store
            .TryTransitionEngagementRound(round.Id, EngagementRoundStatus.Pending, EngagementRoundStatus.Running, Start)
            .Should()
            .BeTrue();

        var httpHandler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "api/workspaces", "[]")
            .OnJson(
                HttpMethod.Post,
                "api/workspaces",
                "{\"id\":\"workspace-118\",\"name\":\"Discussion PR #118\","
                    + "\"directoryRelPath\":\"review-github-achieveai-lmdotnettools-pr-118\","
                    + "\"marketplaces\":[\"code-reviewer\"]}"
            );
        using var http = new HttpClient(httpHandler) { BaseAddress = new Uri("https://review-host.test/") };
        var git = new FakeSandboxCommandRunner();
        var preparer = new S2SReviewWorkspacePreparer(
            new LmStreamingS2SClient(http, "secret", "app", "key"),
            new GitRunner(git),
            "/workspaces",
            "code-reviewer",
            NullLogger<S2SReviewWorkspacePreparer>.Instance
        );
        var loops = new FakeReviewAgentLoopFactory { DefaultText = "Nothing useful to add." };
        var options = new CodeReviewDaemonOptions
        {
            ReviewModelId = "gpt-5.6-sol",
            ToolAssistedReasoningEffort = "xhigh",
            ReviewStageDeadlineMinutes = 7,
            EnableCommentPosting = true,
        };
        var time = new FakeTimeProvider(Start);
        var publisher = new FakeReviewCommentPublisher();
        var publication = new ReviewPublicationCoordinator(
            store,
            [CreateProvider()],
            [publisher],
            time,
            NullLoggerFactory.Instance
        );
        var policy = new ProductionDiscussionRoundPolicy(
            store,
            preparer,
            loops,
            new ReviewStoreRoundObservationSink(store, time),
            publication,
            options,
            time,
            NullLoggerFactory.Instance
        );
        var sourceRecord = store.RecordLegacyAuditGap(round.Id, "source-1", "test_source", Start);
        var source = new AuditSourceReference(sourceRecord.Id, sourceRecord.ContentSha256);
        var context = new DiscussionRoundContext(
            round.Id,
            round.HeadSha,
            [new DiscussionComment("comment-1", "octocat", "Why?", source)],
            []
        );

        var status = await policy.ExecuteAsync(context, CancellationToken.None);

        var finalization = store.ListReviewActions(round.Id).Should().ContainSingle().Subject;
        finalization.Kind.Should().Be(PersistedReviewActionKind.FinalizeRound);
        finalization.Status.Should().Be(ReviewActionStatus.Accepted);
        status.Should().Be(EngagementRoundStatus.Completed);
        store.GetEngagementRound(round.Id)!.Status.Should().Be(EngagementRoundStatus.Running);
        publisher.FindCallCount.Should().Be(0);
        publisher.PostCount.Should().Be(0);
        loops.CreatedProfiles.Should().ContainSingle().Which.Id.Should().Be(DaemonAgentFactory.DiscussionProfileId);
        loops.ModelIds.Should().Equal(options.ReviewModelId);
        loops.ReasoningEfforts.Should().Equal(options.ToolAssistedReasoningEffort);
        loops.WorkspaceIds.Should().Equal("workspace-118");
        loops.ThreadIds.Should().Equal($"engagement-{engagement.Id}-discussion-round-{round.Id}");
        loops.ReviewScopes.Should().Equal(new ReviewConversationScope(engagement.Id.ToString(), round.Id.ToString()));
        loops
            .ReviewPublicationScopes.Should()
            .Equal(
                new ReviewPublicationConversationScope(
                    round.Id,
                    "github",
                    repoId,
                    "118",
                    "head-exact",
                    LivePostingAuthorized: true
                )
            );
        var agent = loops.CreatedAgents.Should().ContainSingle().Subject;
        agent.ReceivedInputs.Should().ContainSingle();
        agent.Deadlines.Should().Equal(Start.AddMinutes(7));
        agent.SuppressionScopesOpened.Should().Be(1);
        agent.SuppressionScopesClosed.Should().Be(1);
        agent.Lifecycle.Should().Equal(FakeMultiTurnAgent.RunEvent, FakeMultiTurnAgent.DisposeEvent);
        store.ListReviewRuns(ReviewStage.Discovered, 10).Should().BeEmpty("discussion rounds own no ReviewRun");
        string[] expectedFetch =
        [
            "git",
            .. GitRunner.HardeningArgs,
            .. GitRunner.CompatibilityArgs,
            .. GitRunner.IdentityArgs,
            "-C",
            "/workspaces/review-github-achieveai-lmdotnettools-pr-118",
            "fetch",
            "origin",
            "base-exact",
            "head-exact",
        ];
        string[] expectedCheckout =
        [
            "git",
            .. GitRunner.HardeningArgs,
            .. GitRunner.CompatibilityArgs,
            .. GitRunner.IdentityArgs,
            "-C",
            "/workspaces/review-github-achieveai-lmdotnettools-pr-118",
            "checkout",
            "--force",
            "head-exact",
        ];
        git.Commands.Should().ContainSingle(command => command.Argv.SequenceEqual(expectedFetch));
        git.Commands.Should().ContainSingle(command => command.Argv.SequenceEqual(expectedCheckout));
    }

    [Fact]
    public async Task An_evidence_backed_candidate_is_persisted_before_its_question_is_answered()
    {
        using var database = new TempSqliteDatabase();
        using var store = new ReviewStore(database.ConnectionString);
        var repo = new RepoIdentity
        {
            Provider = "github",
            OrgOrOwner = "achieveai",
            RepoName = "LmDotnetTools",
        };
        var repoId = store.EnsureRepo(repo);
        var watermark = new ProviderActivityWatermark("github", Start, "comment-answer");
        var engagement = store.CreateOrGetEngagement(
            new PrEngagement(
                0,
                repoId,
                "github",
                "118",
                PrLifecycleState.Open,
                "head-exact",
                "base-exact",
                null,
                watermark,
                null,
                null,
                null,
                null,
                null,
                null,
                Start
            )
        );
        var round = store.TryAdmitRound(
            new EngagementRound(
                0,
                engagement.Id,
                EngagementRoundIntent.DiscussionFollowUp,
                EngagementRoundStatus.Pending,
                "head-exact",
                "base-exact",
                null,
                watermark,
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
        store
            .TryTransitionEngagementRound(round.Id, EngagementRoundStatus.Pending, EngagementRoundStatus.Running, Start)
            .Should()
            .BeTrue();

        var questionSourceRecord = store.RecordLegacyAuditGap(round.Id, "question-source", "test_source", Start);
        var questionSource = new AuditSourceReference(questionSourceRecord.Id, questionSourceRecord.ContentSha256);
        var answerSourceRecord = store.RecordLegacyAuditGap(round.Id, "answer-source", "test_source", Start);
        var answerSource = new AuditSourceReference(answerSourceRecord.Id, answerSourceRecord.ContentSha256);
        var questionReceipt = "{\"providerCommentId\":\"question-comment:17\"}";
        var questionAction = new CodeReviewDaemon.Sample.Persistence.Models.ReviewAction(
            round.Id,
            "ask-q-open",
            PersistedReviewActionKind.PostClarificationQuestion,
            ReviewActionStatus.Planned,
            "question-payload",
            "{\"providerTargetId\":\"question-comment:17\"}",
            null,
            null,
            Start,
            Start
        );
        store.AddOrGetReviewAction(questionAction, [questionSource]);
        store
            .TryTransitionReviewAction(
                round.Id,
                questionAction.ActionId,
                ReviewActionStatus.Planned,
                ReviewActionStatus.Accepted,
                questionReceipt,
                null,
                Start
            )
            .Should()
            .BeTrue();
        var question = new ClarificationQuestion(
            "q-open",
            round.Id,
            "Does the retry apply per attempt?",
            questionAction.ProviderTargetJson,
            null,
            null,
            "The retry scope is ambiguous.",
            "Whether retry state is scoped per attempt.",
            questionAction.ActionId,
            questionReceipt,
            Start,
            ClarificationQuestionState.Open,
            Start,
            Start
        );
        store.AddClarificationQuestion(question, [questionSource]);

        var httpHandler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "api/workspaces", "[]")
            .OnJson(
                HttpMethod.Post,
                "api/workspaces",
                "{\"id\":\"workspace-118\",\"name\":\"Discussion PR #118\","
                    + "\"directoryRelPath\":\"review-github-achieveai-lmdotnettools-pr-118\","
                    + "\"marketplaces\":[\"code-reviewer\"]}"
            );
        using var http = new HttpClient(httpHandler) { BaseAddress = new Uri("https://review-host.test/") };
        var preparer = new S2SReviewWorkspacePreparer(
            new LmStreamingS2SClient(http, "secret", "app", "key"),
            new GitRunner(new FakeSandboxCommandRunner()),
            "/workspaces",
            "code-reviewer",
            NullLogger<S2SReviewWorkspacePreparer>.Instance
        );
        var loops = new FakeReviewAgentLoopFactory
        {
            DefaultText =
                $"Evidence confirms per-run.\n\n```discussion-decision\nrelevant: true\nquestions:\n"
                + "  - questionId: q-open\n"
                + "    state: answered\n"
                + "    candidates: [comment-answer]\n"
                + $"    evidence: [\"{answerSource.SourceRecordId}:{answerSource.ContentSha256}\"]\n```",
        };
        var time = new FakeTimeProvider(Start);
        var publication = new ReviewPublicationCoordinator(
            store,
            [CreateProvider()],
            [new FakeReviewCommentPublisher()],
            time,
            NullLoggerFactory.Instance
        );
        var policy = new ProductionDiscussionRoundPolicy(
            store,
            preparer,
            loops,
            new ReviewStoreRoundObservationSink(store, time),
            publication,
            new CodeReviewDaemonOptions
            {
                ReviewModelId = "gpt-5.6-sol",
                ToolAssistedReasoningEffort = "xhigh",
                ReviewStageDeadlineMinutes = 7,
            },
            time,
            NullLoggerFactory.Instance
        );
        var context = new DiscussionRoundContext(
            round.Id,
            round.HeadSha,
            [
                new DiscussionComment(
                    "comment-answer",
                    "octocat",
                    "The retry applies per run.",
                    answerSource,
                    ParentCommentId: "question-comment:17",
                    ThreadId: "question-thread:9"
                ),
            ],
            [
                new OpenClarificationQuestion(
                    question.Id,
                    question.Wording,
                    question.WithheldConclusion,
                    "question-comment:17",
                    "question-thread:9",
                    ActionId: question.ActionId
                ),
            ]
        );

        var status = await policy.ExecuteAsync(context, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));
        var replayStatus = await policy.ExecuteAsync(context, CancellationToken.None);

        status.Should().Be(EngagementRoundStatus.Completed);
        replayStatus.Should().Be(EngagementRoundStatus.Completed, "an identical round replay is idempotent");
        store.GetClarificationQuestion(question.Id)!.State.Should().Be(ClarificationQuestionState.Answered);
        var candidate = store.ListClarificationCandidateAnswers(question.Id).Should().ContainSingle().Subject;
        candidate.ObservedInRoundId.Should().Be(round.Id);
        candidate.ProviderReferenceJson.Should().Contain("comment-answer");
        candidate.InterpretationSummary.Should().Contain(nameof(ClarificationQuestionState.Answered));
        store
            .ListRoundObservations(round.Id)
            .Count(observation => observation.Kind == ObservationKind.Answer)
            .Should()
            .Be(1, "an identical replay must not duplicate the durable answer observation");
    }

    [Fact]
    public async Task A_conflicting_question_state_blocks_local_finalization()
    {
        using var database = new TempSqliteDatabase();
        using var store = new ReviewStore(database.ConnectionString);
        var repoId = store.EnsureRepo(
            new RepoIdentity
            {
                Provider = "github",
                OrgOrOwner = "achieveai",
                RepoName = "LmDotnetTools",
            }
        );
        var watermark = new ProviderActivityWatermark("github", Start, "comment-answer");
        var engagement = store.CreateOrGetEngagement(
            new PrEngagement(
                0,
                repoId,
                "github",
                "118",
                PrLifecycleState.Open,
                "head-exact",
                "base-exact",
                null,
                watermark,
                null,
                null,
                null,
                null,
                null,
                null,
                Start
            )
        );
        var round = store.TryAdmitRound(
            new EngagementRound(
                0,
                engagement.Id,
                EngagementRoundIntent.DiscussionFollowUp,
                EngagementRoundStatus.Pending,
                "head-exact",
                "base-exact",
                null,
                watermark,
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
        store
            .TryTransitionEngagementRound(round.Id, EngagementRoundStatus.Pending, EngagementRoundStatus.Running, Start)
            .Should()
            .BeTrue();

        var questionSourceRecord = store.RecordLegacyAuditGap(round.Id, "question-source", "test_source", Start);
        var questionSource = new AuditSourceReference(questionSourceRecord.Id, questionSourceRecord.ContentSha256);
        var answerSourceRecord = store.RecordLegacyAuditGap(round.Id, "answer-source", "test_source", Start);
        var answerSource = new AuditSourceReference(answerSourceRecord.Id, answerSourceRecord.ContentSha256);
        store.AddClarificationQuestion(
            new ClarificationQuestion(
                "q-conflict",
                round.Id,
                "Does the retry apply per attempt?",
                null,
                null,
                null,
                "The retry scope is ambiguous.",
                "Whether retry state is scoped per attempt.",
                null,
                null,
                null,
                ClarificationQuestionState.Superseded,
                Start,
                Start
            ),
            [questionSource]
        );

        var httpHandler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "api/workspaces", "[]")
            .OnJson(
                HttpMethod.Post,
                "api/workspaces",
                "{\"id\":\"workspace-118\",\"name\":\"Discussion PR #118\","
                    + "\"directoryRelPath\":\"review-github-achieveai-lmdotnettools-pr-118\","
                    + "\"marketplaces\":[\"code-reviewer\"]}"
            );
        using var http = new HttpClient(httpHandler) { BaseAddress = new Uri("https://review-host.test/") };
        var preparer = new S2SReviewWorkspacePreparer(
            new LmStreamingS2SClient(http, "secret", "app", "key"),
            new GitRunner(new FakeSandboxCommandRunner()),
            "/workspaces",
            "code-reviewer",
            NullLogger<S2SReviewWorkspacePreparer>.Instance
        );
        var loops = new FakeReviewAgentLoopFactory
        {
            DefaultText =
                $"Answer.\n\n```discussion-decision\nrelevant: true\nquestions:\n"
                + "  - questionId: q-conflict\n"
                + "    state: answered\n"
                + "    candidates: [comment-answer]\n"
                + $"    evidence: [\"{answerSource.SourceRecordId}:{answerSource.ContentSha256}\"]\n```",
        };
        var time = new FakeTimeProvider(Start);
        var publication = new RecordingPublicationOperations();
        var policy = new ProductionDiscussionRoundPolicy(
            store,
            preparer,
            loops,
            new ReviewStoreRoundObservationSink(store, time),
            publication,
            new CodeReviewDaemonOptions
            {
                ReviewModelId = "gpt-5.6-sol",
                ToolAssistedReasoningEffort = "xhigh",
                ReviewStageDeadlineMinutes = 7,
            },
            time,
            NullLoggerFactory.Instance
        );
        var context = new DiscussionRoundContext(
            round.Id,
            round.HeadSha,
            [
                new DiscussionComment(
                    "comment-answer",
                    "octocat",
                    "The retry applies per run.",
                    answerSource,
                    ParentCommentId: "question-comment:17",
                    ThreadId: "question-thread:9"
                ),
            ],
            [
                new OpenClarificationQuestion(
                    "q-conflict",
                    "Does the retry apply per attempt?",
                    "Whether retry state is scoped per attempt.",
                    "question-comment:17",
                    "question-thread:9"
                ),
            ]
        );

        var status = await policy.ExecuteAsync(context, CancellationToken.None);

        status.Should().Be(EngagementRoundStatus.RetryPending);
        publication.FinalizationCalls.Should().Be(0, "a conflicting question state must block finalization");
        store.GetClarificationQuestion("q-conflict")!.State.Should().Be(ClarificationQuestionState.Superseded);
        store.ListClarificationCandidateAnswers("q-conflict").Should().ContainSingle();
        store
            .ListRoundObservations(round.Id)
            .Should()
            .NotContain(
                observation => observation.Kind == ObservationKind.Answer,
                "a failed state transition must not claim the question was answered"
            );
    }

    [Fact]
    public async Task Terminal_typed_actions_become_exact_once_mechanical_observations_before_finalization()
    {
        using var database = new TempSqliteDatabase();
        using var store = new ReviewStore(database.ConnectionString);
        var repoId = store.EnsureRepo(
            new RepoIdentity
            {
                Provider = "github",
                OrgOrOwner = "achieveai",
                RepoName = "LmDotnetTools",
            }
        );
        var watermark = new ProviderActivityWatermark("github", Start, "comment-1");
        var engagement = store.CreateOrGetEngagement(
            new PrEngagement(
                0,
                repoId,
                "github",
                "118",
                PrLifecycleState.Open,
                "head-exact",
                "base-exact",
                null,
                watermark,
                null,
                null,
                null,
                null,
                null,
                null,
                Start
            )
        );
        var round = store.TryAdmitRound(
            new EngagementRound(
                0,
                engagement.Id,
                EngagementRoundIntent.DiscussionFollowUp,
                EngagementRoundStatus.Pending,
                "head-exact",
                "base-exact",
                null,
                watermark,
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
        store
            .TryTransitionEngagementRound(round.Id, EngagementRoundStatus.Pending, EngagementRoundStatus.Running, Start)
            .Should()
            .BeTrue();
        var sourceRecord = store.RecordLegacyAuditGap(round.Id, "source-1", "test_source", Start);
        var source = new AuditSourceReference(sourceRecord.Id, sourceRecord.ContentSha256);

        var httpHandler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "api/workspaces", "[]")
            .OnJson(
                HttpMethod.Post,
                "api/workspaces",
                "{\"id\":\"workspace-118\",\"name\":\"Discussion PR #118\","
                    + "\"directoryRelPath\":\"review-github-achieveai-lmdotnettools-pr-118\","
                    + "\"marketplaces\":[\"code-reviewer\"]}"
            );
        using var http = new HttpClient(httpHandler) { BaseAddress = new Uri("https://review-host.test/") };
        var preparer = new S2SReviewWorkspacePreparer(
            new LmStreamingS2SClient(http, "secret", "app", "key"),
            new GitRunner(new FakeSandboxCommandRunner()),
            "/workspaces",
            "code-reviewer",
            NullLogger<S2SReviewWorkspacePreparer>.Instance
        );
        var loops = new FakeReviewAgentLoopFactory
        {
            DefaultText = "Typed operations completed.\n\n```discussion-decision\nrelevant: false\n```",
        };
        loops.DecorateCreatedAgent = agent => new ActionDuringTurnLoop(
            agent,
            () =>
            {
                AddTerminalAction(
                    store,
                    round.Id,
                    "reply-accepted",
                    PersistedReviewActionKind.ReplyToDiscussion,
                    ReviewActionStatus.Accepted,
                    source,
                    receipt: "{\"providerCommentId\":\"comment-42\",\"providerPermalink\":\"https://example.test/c/42\"}"
                );
                AddTerminalAction(
                    store,
                    round.Id,
                    "finding-rejected",
                    PersistedReviewActionKind.SubmitInlineFindings,
                    ReviewActionStatus.Rejected,
                    source,
                    rejection: "{\"code\":\"stale_head\"}"
                );
                AddTerminalAction(
                    store,
                    round.Id,
                    "delta-collected",
                    PersistedReviewActionKind.AppendSummaryDelta,
                    ReviewActionStatus.CollectedOnly,
                    source
                );
            }
        );
        var time = new FakeTimeProvider(Start);
        var publication = new ReviewPublicationCoordinator(
            store,
            [CreateProvider()],
            [new FakeReviewCommentPublisher()],
            time,
            NullLoggerFactory.Instance
        );
        var policy = new ProductionDiscussionRoundPolicy(
            store,
            preparer,
            loops,
            new ReviewStoreRoundObservationSink(store, time),
            publication,
            new CodeReviewDaemonOptions
            {
                ReviewModelId = "gpt-5.6-sol",
                ToolAssistedReasoningEffort = "xhigh",
                ReviewStageDeadlineMinutes = 7,
            },
            time,
            NullLoggerFactory.Instance
        );
        var context = new DiscussionRoundContext(
            round.Id,
            round.HeadSha,
            [new DiscussionComment("comment-1", "octocat", "Why?", source)],
            []
        );

        var status = await policy.ExecuteAsync(context, CancellationToken.None);
        var replayStatus = await policy.ExecuteAsync(context, CancellationToken.None);

        status.Should().Be(EngagementRoundStatus.Completed);
        replayStatus.Should().Be(EngagementRoundStatus.Completed);
        var publicationObservations = store
            .ListRoundObservations(round.Id)
            .Where(observation => observation.Id.StartsWith("review-action-observation-", StringComparison.Ordinal))
            .ToArray();
        publicationObservations.Should().HaveCount(3, "replaying the same terminal actions must append nothing");
        publicationObservations
            .Select(observation => $"{observation.Kind}: {observation.Summary}")
            .Should()
            .ContainSingle(
                summary =>
                    summary.Contains("DiscussionContribution", StringComparison.Ordinal)
                    && summary.Contains("reply-accepted", StringComparison.Ordinal)
                    && summary.Contains("https://example.test/c/42", StringComparison.Ordinal)
                    && !summary.Contains("payload-", StringComparison.Ordinal),
                "the durable mechanical summaries were {0}",
                string.Join(" | ", publicationObservations.Select(item => item.Summary))
            );
        publicationObservations
            .Should()
            .ContainSingle(observation =>
                observation.Kind == ObservationKind.Gap
                && observation.Summary.Contains("finding-rejected", StringComparison.Ordinal)
                && observation.Summary.Contains("stale_head", StringComparison.Ordinal)
            );
        publicationObservations
            .Should()
            .ContainSingle(observation =>
                observation.Kind == ObservationKind.Gap
                && observation.Summary.Contains("delta-collected", StringComparison.Ordinal)
                && observation.Summary.Contains("not sent", StringComparison.OrdinalIgnoreCase)
            );
        publicationObservations
            .Select(observation => store.ListRoundObservationSources(observation.Id))
            .Should()
            .OnlyContain(sources => sources.SequenceEqual(new[] { source }));
    }

    [Fact]
    public async Task A_non_terminal_typed_action_blocks_local_finalization_without_claiming_an_outcome()
    {
        using var database = new TempSqliteDatabase();
        using var store = new ReviewStore(database.ConnectionString);
        var repoId = store.EnsureRepo(
            new RepoIdentity
            {
                Provider = "github",
                OrgOrOwner = "achieveai",
                RepoName = "LmDotnetTools",
            }
        );
        var watermark = new ProviderActivityWatermark("github", Start, "comment-1");
        var engagement = store.CreateOrGetEngagement(
            new PrEngagement(
                0,
                repoId,
                "github",
                "118",
                PrLifecycleState.Open,
                "head-exact",
                "base-exact",
                null,
                watermark,
                null,
                null,
                null,
                null,
                null,
                null,
                Start
            )
        );
        var round = store.TryAdmitRound(
            new EngagementRound(
                0,
                engagement.Id,
                EngagementRoundIntent.DiscussionFollowUp,
                EngagementRoundStatus.Pending,
                "head-exact",
                "base-exact",
                null,
                watermark,
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
        store
            .TryTransitionEngagementRound(round.Id, EngagementRoundStatus.Pending, EngagementRoundStatus.Running, Start)
            .Should()
            .BeTrue();
        var sourceRecord = store.RecordLegacyAuditGap(round.Id, "source-1", "test_source", Start);
        var source = new AuditSourceReference(sourceRecord.Id, sourceRecord.ContentSha256);
        _ = store.AddOrGetReviewAction(
            new CodeReviewDaemon.Sample.Persistence.Models.ReviewAction(
                round.Id,
                "reply-in-flight",
                PersistedReviewActionKind.ReplyToDiscussion,
                ReviewActionStatus.Sending,
                "payload-in-flight",
                null,
                null,
                null,
                Start,
                Start
            ),
            [source]
        );

        var httpHandler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "api/workspaces", "[]")
            .OnJson(
                HttpMethod.Post,
                "api/workspaces",
                "{\"id\":\"workspace-118\",\"name\":\"Discussion PR #118\","
                    + "\"directoryRelPath\":\"review-github-achieveai-lmdotnettools-pr-118\","
                    + "\"marketplaces\":[\"code-reviewer\"]}"
            );
        using var http = new HttpClient(httpHandler) { BaseAddress = new Uri("https://review-host.test/") };
        var preparer = new S2SReviewWorkspacePreparer(
            new LmStreamingS2SClient(http, "secret", "app", "key"),
            new GitRunner(new FakeSandboxCommandRunner()),
            "/workspaces",
            "code-reviewer",
            NullLogger<S2SReviewWorkspacePreparer>.Instance
        );
        var time = new FakeTimeProvider(Start);
        var publication = new RecordingPublicationOperations();
        var policy = new ProductionDiscussionRoundPolicy(
            store,
            preparer,
            new FakeReviewAgentLoopFactory
            {
                DefaultText = "No semantic response.\n\n```discussion-decision\nrelevant: false\n```",
            },
            new ReviewStoreRoundObservationSink(store, time),
            publication,
            new CodeReviewDaemonOptions
            {
                ReviewModelId = "gpt-5.6-sol",
                ToolAssistedReasoningEffort = "xhigh",
                ReviewStageDeadlineMinutes = 7,
            },
            time,
            NullLoggerFactory.Instance
        );
        var context = new DiscussionRoundContext(
            round.Id,
            round.HeadSha,
            [new DiscussionComment("comment-1", "octocat", "Why?", source)],
            []
        );

        var status = await policy.ExecuteAsync(context, CancellationToken.None);

        status.Should().Be(EngagementRoundStatus.RetryPending);
        publication.FinalizationCalls.Should().Be(0);
        store.ListReviewActions(round.Id).Should().ContainSingle().Which.Status.Should().Be(ReviewActionStatus.Sending);
        store
            .ListRoundObservations(round.Id)
            .Should()
            .NotContain(observation =>
                observation.Id.StartsWith("review-action-observation-", StringComparison.Ordinal)
            );
    }

    [Theory]
    [InlineData("Planned")]
    [InlineData("Sending")]
    public async Task A_replayed_non_terminal_typed_action_blocks_local_finalization(string actionStatusName)
    {
        using var database = new TempSqliteDatabase();
        using var store = new ReviewStore(database.ConnectionString);
        var scenario = CreateClarificationScenario(store);
        var actionStatus = Enum.Parse<ReviewActionStatus>(actionStatusName);
        _ = store.AddOrGetReviewAction(
            new CodeReviewDaemon.Sample.Persistence.Models.ReviewAction(
                scenario.Round.Id,
                "reply-in-flight",
                PersistedReviewActionKind.ReplyToDiscussion,
                actionStatus,
                "payload-in-flight",
                null,
                null,
                null,
                Start,
                Start
            ),
            [scenario.AnswerSource]
        );
        var publication = new RecordingPublicationOperations();
        var policy = CreatePolicy(
            store,
            new FakeReviewAgentLoopFactory
            {
                DefaultText = "No semantic response.\n\n```discussion-decision\nrelevant: false\n```",
            },
            publication
        );

        var status = await policy.ExecuteAsync(scenario.Context, CancellationToken.None);

        status.Should().Be(EngagementRoundStatus.RetryPending);
        publication.FinalizationCalls.Should().Be(0);
        store.GetReviewAction(scenario.Round.Id, "reply-in-flight")!.Status.Should().Be(actionStatus);
        store
            .ListRoundObservations(scenario.Round.Id)
            .Should()
            .NotContain(observation =>
                observation.Id.StartsWith("review-action-observation-", StringComparison.Ordinal)
            );
    }

    [Fact]
    public async Task A_persisted_candidate_replays_the_missing_question_transition_answer_and_finalization()
    {
        using var database = new TempSqliteDatabase();
        using var store = new ReviewStore(database.ConnectionString);
        var scenario = CreateClarificationScenario(store);
        AddExpectedCandidate(store, scenario, ClarificationQuestionState.Answered);
        var publication = new RecordingPublicationOperations();
        var policy = CreatePolicy(store, CreateAnsweredLoop(scenario), publication);

        var status = await policy.ExecuteAsync(scenario.Context, CancellationToken.None);

        status.Should().Be(EngagementRoundStatus.Completed);
        store.ListClarificationCandidateAnswers(scenario.Question.Id).Should().ContainSingle();
        store.GetClarificationQuestion(scenario.Question.Id)!.State.Should().Be(ClarificationQuestionState.Answered);
        AnswerCount(store, scenario.Round.Id).Should().Be(1);
        publication.FinalizationCalls.Should().Be(1);
    }

    [Fact]
    public async Task A_persisted_question_transition_replays_the_missing_answer_and_finalization()
    {
        using var database = new TempSqliteDatabase();
        using var store = new ReviewStore(database.ConnectionString);
        var scenario = CreateClarificationScenario(store);
        AddExpectedCandidate(store, scenario, ClarificationQuestionState.Answered);
        store
            .TryTransitionClarificationQuestion(
                scenario.Question.Id,
                ClarificationQuestionState.Open,
                ClarificationQuestionState.Answered,
                Start
            )
            .Should()
            .BeTrue();
        var publication = new RecordingPublicationOperations();
        var policy = CreatePolicy(store, CreateAnsweredLoop(scenario), publication);

        var status = await policy.ExecuteAsync(scenario.Context, CancellationToken.None);

        status.Should().Be(EngagementRoundStatus.Completed);
        store.ListClarificationCandidateAnswers(scenario.Question.Id).Should().ContainSingle();
        store.GetClarificationQuestion(scenario.Question.Id)!.State.Should().Be(ClarificationQuestionState.Answered);
        AnswerCount(store, scenario.Round.Id).Should().Be(1);
        publication.FinalizationCalls.Should().Be(1);
    }

    [Fact]
    public async Task A_persisted_answer_replays_only_the_missing_finalization()
    {
        using var database = new TempSqliteDatabase();
        using var store = new ReviewStore(database.ConnectionString);
        var scenario = CreateClarificationScenario(store);
        AddExpectedCandidate(store, scenario, ClarificationQuestionState.Answered);
        store
            .TryTransitionClarificationQuestion(
                scenario.Question.Id,
                ClarificationQuestionState.Open,
                ClarificationQuestionState.Answered,
                Start
            )
            .Should()
            .BeTrue();
        var answer = ExpectedAnswer(scenario);
        var answerIdentity = Encoding.UTF8.GetBytes($"{scenario.Round.Id}\n{answer.Kind}\n{answer.Summary}");
        _ = store.AppendRoundObservationOnce(
            $"question-observation-{scenario.Round.Id}-{Sha256(answerIdentity)[..24]}",
            scenario.Round.Id,
            answer,
            scenario.Round.StartedAt!.Value
        );
        var publication = new RecordingPublicationOperations();
        var policy = CreatePolicy(store, CreateAnsweredLoop(scenario), publication);

        var status = await policy.ExecuteAsync(scenario.Context, CancellationToken.None);

        status.Should().Be(EngagementRoundStatus.Completed);
        store.ListClarificationCandidateAnswers(scenario.Question.Id).Should().ContainSingle();
        store.GetClarificationQuestion(scenario.Question.Id)!.State.Should().Be(ClarificationQuestionState.Answered);
        AnswerCount(store, scenario.Round.Id).Should().Be(1);
        publication.FinalizationCalls.Should().Be(1);
    }

    private static ClarificationScenario CreateClarificationScenario(ReviewStore store)
    {
        var repoId = store.EnsureRepo(
            new RepoIdentity
            {
                Provider = "github",
                OrgOrOwner = "achieveai",
                RepoName = "LmDotnetTools",
            }
        );
        var watermark = new ProviderActivityWatermark("github", Start, "comment-answer");
        var engagement = store.CreateOrGetEngagement(
            new PrEngagement(
                0,
                repoId,
                "github",
                "118",
                PrLifecycleState.Open,
                "head-exact",
                "base-exact",
                null,
                watermark,
                null,
                null,
                null,
                null,
                null,
                null,
                Start
            )
        );
        var round = store.TryAdmitRound(
            new EngagementRound(
                0,
                engagement.Id,
                EngagementRoundIntent.DiscussionFollowUp,
                EngagementRoundStatus.Pending,
                "head-exact",
                "base-exact",
                null,
                watermark,
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
        store
            .TryTransitionEngagementRound(round.Id, EngagementRoundStatus.Pending, EngagementRoundStatus.Running, Start)
            .Should()
            .BeTrue();
        round = store.GetEngagementRound(round.Id)!;
        var questionSourceRecord = store.RecordLegacyAuditGap(round.Id, "question-source", "test_source", Start);
        var questionSource = new AuditSourceReference(questionSourceRecord.Id, questionSourceRecord.ContentSha256);
        var answerSourceRecord = store.RecordLegacyAuditGap(round.Id, "answer-source", "test_source", Start);
        var answerSource = new AuditSourceReference(answerSourceRecord.Id, answerSourceRecord.ContentSha256);
        var question = new ClarificationQuestion(
            "q-replay",
            round.Id,
            "Does the retry apply per attempt?",
            null,
            null,
            null,
            "The retry scope is ambiguous.",
            "Whether retry state is scoped per attempt.",
            null,
            null,
            null,
            ClarificationQuestionState.Open,
            Start,
            Start
        );
        store.AddClarificationQuestion(question, [questionSource]);
        var context = new DiscussionRoundContext(
            round.Id,
            round.HeadSha,
            [
                new DiscussionComment(
                    "comment-answer",
                    "octocat",
                    "The retry applies per run.",
                    answerSource,
                    ParentCommentId: "question-comment:17",
                    ThreadId: "question-thread:9"
                ),
            ],
            [
                new OpenClarificationQuestion(
                    question.Id,
                    question.Wording,
                    question.WithheldConclusion,
                    "question-comment:17",
                    "question-thread:9"
                ),
            ]
        );
        return new ClarificationScenario(engagement, round, question, answerSource, context);
    }

    private static FakeReviewAgentLoopFactory CreateAnsweredLoop(ClarificationScenario scenario) =>
        new()
        {
            DefaultText =
                $"Evidence confirms per-run.\n\n```discussion-decision\nrelevant: true\nquestions:\n"
                + $"  - questionId: {scenario.Question.Id}\n"
                + "    state: answered\n"
                + "    candidates: [comment-answer]\n"
                + $"    evidence: [\"{scenario.AnswerSource.SourceRecordId}:{scenario.AnswerSource.ContentSha256}\"]\n```",
        };

    private static ProductionDiscussionRoundPolicy CreatePolicy(
        ReviewStore store,
        FakeReviewAgentLoopFactory loops,
        IReviewPublicationOperations publication
    )
    {
        var httpHandler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "api/workspaces", "[]")
            .OnJson(
                HttpMethod.Post,
                "api/workspaces",
                "{\"id\":\"workspace-118\",\"name\":\"Discussion PR #118\","
                    + "\"directoryRelPath\":\"review-github-achieveai-lmdotnettools-pr-118\","
                    + "\"marketplaces\":[\"code-reviewer\"]}"
            );
        var http = new HttpClient(httpHandler) { BaseAddress = new Uri("https://review-host.test/") };
        var preparer = new S2SReviewWorkspacePreparer(
            new LmStreamingS2SClient(http, "secret", "app", "key"),
            new GitRunner(new FakeSandboxCommandRunner()),
            "/workspaces",
            "code-reviewer",
            NullLogger<S2SReviewWorkspacePreparer>.Instance
        );
        var time = new FakeTimeProvider(Start);
        return new ProductionDiscussionRoundPolicy(
            store,
            preparer,
            loops,
            new ReviewStoreRoundObservationSink(store, time),
            publication,
            new CodeReviewDaemonOptions
            {
                ReviewModelId = "gpt-5.6-sol",
                ToolAssistedReasoningEffort = "xhigh",
                ReviewStageDeadlineMinutes = 7,
            },
            time,
            NullLoggerFactory.Instance
        );
    }

    private static void AddExpectedCandidate(
        ReviewStore store,
        ClarificationScenario scenario,
        ClarificationQuestionState state
    )
    {
        var providerReferenceJson = JsonSerializer.Serialize(
            new { commentIds = new[] { "comment-answer" } },
            DaemonReviewStageExecutor.PayloadOptions
        );
        var identity = Encoding.UTF8.GetBytes(
            $"{scenario.Round.Id}\n{scenario.Question.Id}\n{providerReferenceJson}\n{state}"
        );
        _ = store.AddClarificationCandidateAnswer(
            new ClarificationCandidateAnswer(
                $"candidate-answer-{scenario.Round.Id}-{Sha256(identity)[..24]}",
                scenario.Question.Id,
                scenario.Round.Id,
                providerReferenceJson,
                $"question {scenario.Question.Id} is {state}",
                scenario.Round.StartedAt!.Value
            ),
            [scenario.AnswerSource]
        );
    }

    private static RoundObservationDraft ExpectedAnswer(ClarificationScenario scenario) =>
        new(
            ObservationKind.Answer,
            $"question {scenario.Question.Id} is Answered on candidate(s) comment-answer",
            [scenario.AnswerSource]
        );

    private static int AnswerCount(ReviewStore store, long roundId) =>
        store.ListRoundObservations(roundId).Count(observation => observation.Kind == ObservationKind.Answer);

    private static string Sha256(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private sealed record ClarificationScenario(
        PrEngagement Engagement,
        EngagementRound Round,
        ClarificationQuestion Question,
        AuditSourceReference AnswerSource,
        DiscussionRoundContext Context
    );

    private static void AddTerminalAction(
        ReviewStore store,
        long roundId,
        string actionId,
        PersistedReviewActionKind kind,
        ReviewActionStatus status,
        AuditSourceReference source,
        string? receipt = null,
        string? rejection = null
    )
    {
        var action = store.AddOrGetReviewAction(
            new CodeReviewDaemon.Sample.Persistence.Models.ReviewAction(
                roundId,
                actionId,
                kind,
                ReviewActionStatus.Planned,
                $"payload-{actionId}",
                null,
                null,
                null,
                Start,
                Start
            ),
            [source]
        );
        if (action.Status == status)
        {
            return;
        }

        store
            .TryTransitionReviewAction(roundId, actionId, ReviewActionStatus.Planned, status, receipt, rejection, Start)
            .Should()
            .BeTrue();
    }

    private sealed class ActionDuringTurnLoop(IMultiTurnAgent inner, Action beforeFirstMessage)
        : DelegatingLoop(inner),
            IReviewLoopWrapper,
            IDeadlineBoundedReviewLoop
    {
        private bool _acted;

        public IMultiTurnAgent Inner => Wrapped;

        public void UseDeadline(DateTimeOffset deadlineUtc) =>
            (Wrapped as IDeadlineBoundedReviewLoop)?.UseDeadline(deadlineUtc);

        public override async IAsyncEnumerable<IMessage> ExecuteRunAsync(
            UserInput userInput,
            [EnumeratorCancellation] CancellationToken ct = default
        )
        {
            if (!_acted)
            {
                _acted = true;
                beforeFirstMessage();
            }

            await foreach (var message in Wrapped.ExecuteRunAsync(userInput, ct).WithCancellation(ct))
            {
                yield return message;
            }
        }
    }

    private sealed class RecordingPublicationOperations : IReviewPublicationOperations
    {
        public int FinalizationCalls { get; private set; }

        public Task<PublicationOutcome> CreateRootSummaryAsync(
            RootSummaryRequest request,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<PublicationOutcome> AppendSummaryDeltaAsync(
            SummaryDeltaRequest request,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<PublicationOutcome> SubmitInlineFindingsAsync(
            InlineFindingsRequest request,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<PublicationOutcome> PostClarificationQuestionAsync(
            ClarificationQuestionRequest request,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<PublicationOutcome> ReplyToDiscussionAsync(
            DiscussionReplyRequest request,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<PublicationOutcome> FinalizeRoundAsync(
            FinalizeRoundRequest request,
            CancellationToken cancellationToken
        )
        {
            FinalizationCalls++;
            return Task.FromResult(
                new PublicationOutcome(
                    ReviewActionStatus.Accepted,
                    request.Scope.ActionId,
                    null,
                    null,
                    null,
                    null,
                    false,
                    Start,
                    null
                )
            );
        }
    }

    private static MockPrProvider CreateProvider() =>
        new(
            "github",
            [],
            new OpaqueCursor
            {
                Provider = "github",
                Scope = "cursor",
                CursorVersion = 1,
                CursorPayload = "{}",
            }
        )
        {
            CurrentHeadSha = "head-exact",
        };
}
