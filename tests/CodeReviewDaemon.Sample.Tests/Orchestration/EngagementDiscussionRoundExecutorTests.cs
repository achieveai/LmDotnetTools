using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmMultiTurn.Audit;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

public sealed class EngagementDiscussionRoundExecutorTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 3, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Builds_ancestor_context_from_the_same_provider_snapshot_without_widening_the_window()
    {
        using var fixture = new Fixture();
        var root = fixture.Comment("root", Start, "original question");
        var reply = fixture.Comment(
            "reply",
            Start.AddMinutes(1),
            "the retry is per attempt",
            parentCommentId: root.CommentId
        );
        var round = fixture.SeedRound(root.Watermark, reply.Watermark);
        fixture.Provider.EngagementSnapshot = ProviderEngagementSnapshot.Create(
            PrLifecycleState.Open,
            "head-1",
            "base-1",
            reply.Watermark,
            [reply],
            [root]
        );

        _ = await fixture.CreateExecutor().ExecuteAsync(round, CancellationToken.None);

        fixture.Provider.EngagementSnapshotCalls.Should().Be(1);
        var context = fixture.Policy.Received.Should().ContainSingle().Subject;
        context.NewComments.Select(comment => comment.CommentId).Should().Equal(reply.CommentId);
        var ancestor = context.AncestorContext.Should().ContainSingle().Subject;
        ancestor.CommentId.Should().Be(root.CommentId);
        ancestor.Source.ContentSha256.Should().Be(Sha256(root.Body));
        fixture
            .Store.ReadAuditContent(ancestor.Source.SourceRecordId)
            .Should()
            .Equal(Encoding.UTF8.GetBytes(root.Body));
        fixture
            .Store.ListAuditRecordsForRound(round.Id)
            .Should()
            .Contain(record =>
                record.RecordType == EngagementDiscussionRoundExecutor.ProviderCommentRecordType
                && record.Id == ancestor.Source.SourceRecordId
            );
    }

    [Fact]
    public async Task Loads_only_observations_from_completed_rounds_before_the_frozen_boundary()
    {
        using var fixture = new Fixture();
        var lower = fixture.Comment("lower", Start, "already consumed");
        var upper = fixture.Comment("upper", Start.AddMinutes(1), "new discussion");
        var first = fixture.SeedRound(lower.Watermark, upper.Watermark);
        fixture.AppendObservation(first, "first settled conclusion");
        fixture.Complete(first);
        var second = fixture.SeedRound(lower.Watermark, upper.Watermark);
        fixture.AppendObservation(second, "second settled conclusion");
        fixture.Complete(second);
        var current = fixture.SeedRound(lower.Watermark, upper.Watermark, priorObservationBoundary: second.Id);
        fixture.AppendObservation(current, "partial conclusion from a retried current round");
        fixture.Provider.EngagementSnapshot = ProviderEngagementSnapshot.Create(
            PrLifecycleState.Open,
            "head-1",
            "base-1",
            upper.Watermark,
            []
        );

        _ = await fixture.CreateExecutor().ExecuteAsync(current, CancellationToken.None);

        fixture
            .Policy.Received.Should()
            .ContainSingle()
            .Subject.PriorObservations.Should()
            .Equal("first settled conclusion", "second settled conclusion");
    }

    [Fact]
    public async Task Prior_observation_cap_discloses_the_omission_and_links_the_complete_projection()
    {
        using var fixture = new Fixture();
        var lower = fixture.Comment("lower", Start, "already consumed");
        var upper = fixture.Comment("upper", Start.AddMinutes(1), "new discussion");
        var prior = fixture.SeedRound(lower.Watermark, upper.Watermark);
        foreach (var index in Enumerable.Range(1, 101))
        {
            fixture.AppendObservation(prior, $"settled conclusion {index}");
        }

        fixture.Complete(prior);
        var current = fixture.SeedRound(lower.Watermark, upper.Watermark, priorObservationBoundary: prior.Id);
        fixture.Provider.EngagementSnapshot = ProviderEngagementSnapshot.Create(
            PrLifecycleState.Open,
            "head-1",
            "base-1",
            upper.Watermark,
            []
        );

        _ = await fixture.CreateExecutor().ExecuteAsync(current, CancellationToken.None);

        var context = fixture.Policy.Received.Should().ContainSingle().Subject;
        context.PriorObservations.Should().HaveCount(100);
        context.PriorObservations.Should().NotContain("settled conclusion 1");
        context.PriorObservations.Should().Contain("settled conclusion 101");
        context.OmittedPriorObservationCount.Should().Be(1);
        context.PriorObservationSource.Should().NotBeNull();
        var source = context.PriorObservationSource!;
        Encoding
            .UTF8.GetString(fixture.Store.ReadAuditContent(source.SourceRecordId))
            .Should()
            .Contain("settled conclusion 1")
            .And.Contain("settled conclusion 101");
    }

    [Fact]
    public async Task Loads_the_newest_valid_completed_same_head_context_manifest_before_the_frozen_boundary()
    {
        using var fixture = new Fixture();
        var lower = fixture.Comment("lower", Start, "already consumed");
        var upper = fixture.Comment("upper", Start.AddMinutes(1), "new discussion");
        var older = fixture.SeedCodeReviewRound("head-1", lower.Watermark, upper.Watermark);
        fixture.SeedContextManifest(older, "older context");
        fixture.Complete(older);
        var newest = fixture.SeedCodeReviewRound("head-1", lower.Watermark, upper.Watermark);
        var expected = fixture.SeedContextManifest(newest, "newest context", reviewKind: "incremental");
        fixture.Complete(newest);
        var current = fixture.SeedRound(lower.Watermark, upper.Watermark, priorObservationBoundary: newest.Id);
        fixture.SetEmptyProviderSnapshot(upper.Watermark);

        _ = await fixture.CreateExecutor().ExecuteAsync(current, CancellationToken.None);

        fixture.Policy.Received.Should().ContainSingle().Subject.PriorContextManifest.Should().Be(expected);
    }

    [Fact]
    public async Task Does_not_load_a_context_manifest_from_a_different_head()
    {
        using var fixture = new Fixture();
        var lower = fixture.Comment("lower", Start, "already consumed");
        var upper = fixture.Comment("upper", Start.AddMinutes(1), "new discussion");
        var prior = fixture.SeedCodeReviewRound("head-old", lower.Watermark, upper.Watermark);
        fixture.SeedContextManifest(prior, "wrong-head context");
        fixture.Complete(prior);
        var current = fixture.SeedRound(lower.Watermark, upper.Watermark, priorObservationBoundary: prior.Id);
        fixture.SetEmptyProviderSnapshot(upper.Watermark);

        _ = await fixture.CreateExecutor().ExecuteAsync(current, CancellationToken.None);

        fixture.Policy.Received.Should().ContainSingle().Subject.PriorContextManifest.Should().BeNull();
    }

    [Fact]
    public async Task Does_not_load_a_context_manifest_from_a_non_completed_round()
    {
        using var fixture = new Fixture();
        var lower = fixture.Comment("lower", Start, "already consumed");
        var upper = fixture.Comment("upper", Start.AddMinutes(1), "new discussion");
        var prior = fixture.SeedCodeReviewRound("head-1", lower.Watermark, upper.Watermark);
        fixture.SeedContextManifest(prior, "unfinished context");
        fixture.Supersede(prior);
        var current = fixture.SeedRound(lower.Watermark, upper.Watermark, priorObservationBoundary: prior.Id);
        fixture.SetEmptyProviderSnapshot(upper.Watermark);

        _ = await fixture.CreateExecutor().ExecuteAsync(current, CancellationToken.None);

        fixture.Policy.Received.Should().ContainSingle().Subject.PriorContextManifest.Should().BeNull();
    }

    [Fact]
    public async Task Does_not_load_a_context_manifest_after_the_frozen_prior_boundary()
    {
        using var fixture = new Fixture();
        var lower = fixture.Comment("lower", Start, "already consumed");
        var upper = fixture.Comment("upper", Start.AddMinutes(1), "new discussion");
        var boundary = fixture.SeedCodeReviewRound("head-boundary", lower.Watermark, upper.Watermark);
        fixture.Complete(boundary);
        var afterBoundary = fixture.SeedCodeReviewRound("head-1", lower.Watermark, upper.Watermark);
        fixture.SeedContextManifest(afterBoundary, "too-new context");
        fixture.Complete(afterBoundary);
        var current = fixture.SeedRound(lower.Watermark, upper.Watermark, priorObservationBoundary: boundary.Id);
        fixture.SetEmptyProviderSnapshot(upper.Watermark);

        _ = await fixture.CreateExecutor().ExecuteAsync(current, CancellationToken.None);

        fixture.Policy.Received.Should().ContainSingle().Subject.PriorContextManifest.Should().BeNull();
    }

    [Fact]
    public async Task Treats_a_missing_context_manifest_as_normal_absence()
    {
        using var fixture = new Fixture();
        var lower = fixture.Comment("lower", Start, "already consumed");
        var upper = fixture.Comment("upper", Start.AddMinutes(1), "new discussion");
        var prior = fixture.SeedCodeReviewRound("head-1", lower.Watermark, upper.Watermark);
        fixture.Complete(prior);
        var current = fixture.SeedRound(lower.Watermark, upper.Watermark, priorObservationBoundary: prior.Id);
        fixture.SetEmptyProviderSnapshot(upper.Watermark);

        _ = await fixture.CreateExecutor().ExecuteAsync(current, CancellationToken.None);

        fixture.Policy.Received.Should().ContainSingle().Subject.PriorContextManifest.Should().BeNull();
    }

    [Fact]
    public async Task Rejects_a_context_manifest_whose_source_is_owned_by_another_round()
    {
        using var fixture = new Fixture();
        var lower = fixture.Comment("lower", Start, "already consumed");
        var upper = fixture.Comment("upper", Start.AddMinutes(1), "new discussion");
        var sourceOwner = fixture.SeedCodeReviewRound("head-source", lower.Watermark, upper.Watermark);
        var foreignSource = fixture.StoreAuditSource(sourceOwner, "foreign-source", "foreign context");
        fixture.Complete(sourceOwner);
        var prior = fixture.SeedCodeReviewRound("head-1", lower.Watermark, upper.Watermark);
        fixture.SeedContextManifest(prior, "unowned context", foreignSource);
        fixture.Complete(prior);
        var current = fixture.SeedRound(lower.Watermark, upper.Watermark, priorObservationBoundary: prior.Id);
        fixture.SetEmptyProviderSnapshot(upper.Watermark);

        _ = await fixture.CreateExecutor().ExecuteAsync(current, CancellationToken.None);

        fixture.Policy.Received.Should().ContainSingle().Subject.PriorContextManifest.Should().BeNull();
    }

    [Fact]
    public async Task Builds_context_from_the_rounds_exact_frozen_provider_window()
    {
        using var fixture = new Fixture();
        var lower = fixture.Comment("lower", Start, "already consumed");
        var included = fixture.Comment("external-2", Start.AddMinutes(1), "the retry is per-run");
        var daemonOwned = fixture.Comment("daemon-3", Start.AddMinutes(2), "daemon reply");
        var upper = fixture.Comment("external-4", Start.AddMinutes(3), "what about cancellation?");
        var afterUpper = fixture.Comment("external-5", Start.AddMinutes(4), "arrived in the next round");
        var round = fixture.SeedRound(lower.Watermark, upper.Watermark);
        fixture.Provider.EngagementSnapshot = ProviderEngagementSnapshot.Create(
            PrLifecycleState.Open,
            "head-1",
            "base-1",
            afterUpper.Watermark,
            [included, daemonOwned, upper, afterUpper]
        );
        fixture.AcceptReceipt(round, daemonOwned.ProviderObjectId);
        fixture.AddAskedQuestion(
            round,
            "q-open",
            ClarificationQuestionState.Open,
            "question-comment:17",
            "question-thread:9",
            "https://example.test/questions/17"
        );
        fixture.AddAskedQuestion(
            round,
            "q-answered",
            ClarificationQuestionState.Answered,
            "question-comment:18",
            "question-thread:10",
            "https://example.test/questions/18"
        );

        var result = await fixture.CreateExecutor().ExecuteAsync(round, CancellationToken.None);

        result.Should().Be(EngagementRoundStatus.RetryPending);
        fixture.Provider.EngagementSnapshotCalls.Should().Be(1);
        fixture.Provider.LastEngagementAfter.Should().Be(lower.Watermark);
        fixture
            .Provider.LastDaemonReceiptIds.Should()
            .BeEquivalentTo([daemonOwned.ProviderObjectId, "question-comment:17", "question-comment:18"]);

        var context = fixture.Policy.Received.Should().ContainSingle().Subject;
        context.RoundId.Should().Be(round.Id);
        context.HeadSha.Should().Be(round.HeadSha);
        context.NewComments.Select(comment => comment.CommentId).Should().Equal("external-2", "external-4");
        context
            .NewComments.Select(comment => comment.ProviderTargetId)
            .Should()
            .Equal(included.ProviderObjectId, upper.ProviderObjectId);
        context.NewComments.Should().OnlyContain(comment => comment.Source.ContentSha256 == Sha256(comment.Body));
        context
            .OpenQuestions.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                new OpenClarificationQuestion(
                    "q-open",
                    "Does the retry apply per attempt?",
                    "Whether retry state is scoped per attempt.",
                    "question-comment:17",
                    "question-thread:9",
                    "https://example.test/questions/17",
                    "ask-q-open"
                )
            );

        var auditRecords = fixture
            .Store.ListAuditRecordsForRound(round.Id)
            .Where(record => record.RecordType == EngagementDiscussionRoundExecutor.ProviderCommentRecordType)
            .ToArray();
        auditRecords.Should().HaveCount(2);
        auditRecords
            .Select(record => record.Id)
            .Should()
            .BeEquivalentTo(context.NewComments.Select(c => c.Source.SourceRecordId));
        foreach (var comment in context.NewComments)
        {
            fixture
                .Store.ReadAuditContent(comment.Source.SourceRecordId)
                .Should()
                .Equal(Encoding.UTF8.GetBytes(comment.Body));
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempSqliteDatabase _database = new();

        public Fixture()
        {
            Store = new ReviewStore(_database.ConnectionString);
            Repo = new RepoIdentity
            {
                Provider = "github",
                OrgOrOwner = "achieveai",
                RepoName = "LmDotnetTools",
            };
            RepoId = Store.EnsureRepo(Repo);
            Provider = new MockPrProvider(
                "github",
                [],
                new OpaqueCursor
                {
                    Provider = "github",
                    Scope = "cursor",
                    CursorVersion = 1,
                    CursorPayload = "{}",
                }
            );
        }

        public ReviewStore Store { get; }
        public RepoIdentity Repo { get; }
        public long RepoId { get; }
        public MockPrProvider Provider { get; }
        public RecordingDiscussionPolicy Policy { get; } = new();

        public ProviderDiscussionRef Comment(
            string id,
            DateTimeOffset publishedAt,
            string body,
            string? parentCommentId = null
        ) =>
            new(
                "github",
                parentCommentId ?? "thread-1",
                id,
                id,
                parentCommentId,
                $"https://example.test/comments/{id}",
                null,
                null,
                null,
                null,
                null,
                null,
                publishedAt,
                "octocat",
                body,
                ProviderDiscussionKind.Comment
            );

        public void SetEmptyProviderSnapshot(ProviderActivityWatermark upper) =>
            Provider.EngagementSnapshot = ProviderEngagementSnapshot.Create(
                PrLifecycleState.Open,
                "head-1",
                "base-1",
                upper,
                []
            );

        public EngagementRound SeedRound(
            ProviderActivityWatermark lower,
            ProviderActivityWatermark upper,
            long priorObservationBoundary = 0
        ) => SeedRound(EngagementRoundIntent.DiscussionFollowUp, "head-1", lower, upper, priorObservationBoundary);

        public EngagementRound SeedCodeReviewRound(
            string headSha,
            ProviderActivityWatermark lower,
            ProviderActivityWatermark upper
        ) => SeedRound(EngagementRoundIntent.CodeReview, headSha, lower, upper, 0);

        private EngagementRound SeedRound(
            EngagementRoundIntent intent,
            string headSha,
            ProviderActivityWatermark lower,
            ProviderActivityWatermark upper,
            long priorObservationBoundary
        )
        {
            var engagement = Store.CreateOrGetEngagement(
                new PrEngagement(
                    0,
                    RepoId,
                    "github",
                    "118",
                    PrLifecycleState.Open,
                    "head-1",
                    "base-1",
                    null,
                    upper,
                    lower,
                    null,
                    null,
                    null,
                    null,
                    null,
                    Start
                )
            );
            return Store.TryAdmitRound(
                new EngagementRound(
                    0,
                    engagement.Id,
                    intent,
                    EngagementRoundStatus.Pending,
                    headSha,
                    "base-1",
                    lower,
                    upper,
                    priorObservationBoundary,
                    null,
                    0,
                    null,
                    null,
                    null,
                    null,
                    null
                )
            )!;
        }

        public string SeedContextManifest(
            EngagementRound round,
            string claimText,
            AuditSourceRecord? source = null,
            string reviewKind = "full"
        )
        {
            source ??= StoreAuditSource(round, $"manifest-source-{round.Id}", claimText);
            var manifest = new DynamicContextManifest(
                DynamicContextManifest.SchemaVersion,
                round.Id,
                $"context-thread-{round.Id}",
                $"gatherer-{round.Id}",
                DynamicContextEvidenceValidator.GathererTemplate,
                2,
                [
                    new DynamicContextClaim(
                        $"claim-{round.Id}",
                        claimText,
                        ["file:src/Foo.cs"],
                        [new AuditSourceReference(source.Id, source.ContentSha256)]
                    ),
                ],
                [
                    new DynamicContextGap("repository", DynamicContextGapState.Linked, true, null),
                    new DynamicContextGap("head", DynamicContextGapState.Linked, true, null),
                    new DynamicContextGap("workspace", DynamicContextGapState.Linked, true, null),
                ]
            );
            var json = JsonSerializer.Serialize(manifest);
            var run = Store.CreateOrGetReviewRun(
                new ReviewRun
                {
                    RepoId = RepoId,
                    PrId = "118",
                    HeadSha = round.HeadSha,
                    BaseSha = round.BaseSha,
                    TriggerWatermark = $"round:{round.Id}",
                    ReviewKind = reviewKind,
                    VariantId = "primary",
                    Mode = "collect-only",
                    EngagementRoundId = round.Id,
                    Stage = ReviewStage.Posted,
                    WorkflowStatus = WorkflowStatus.Completed,
                    PrLifecycleState = PrLifecycleState.Open,
                }
            );
            _ = Store.AddArtifact(
                new ReviewArtifact
                {
                    ReviewRunId = run.Id,
                    ArtifactSchemaVersion = DaemonReviewStageExecutor.ContextManifestArtifactSchemaVersion,
                    ArtifactKind = DaemonReviewStageExecutor.ContextManifestArtifactKind,
                    Provider = "github",
                    Payload = json,
                }
            );
            return json;
        }

        public void AppendObservation(EngagementRound round, string summary)
        {
            var sequence = Store.ListRoundObservations(round.Id).Count + 1;
            Store.AppendRoundObservation(
                new RoundObservation(
                    $"observation-{round.Id}-{sequence}",
                    round.Id,
                    sequence,
                    ObservationKind.DiscussionContribution,
                    summary,
                    Start
                ),
                []
            );
        }

        public void Complete(EngagementRound round) => TransitionTerminal(round, EngagementRoundStatus.Completed);

        public void Supersede(EngagementRound round) => TransitionTerminal(round, EngagementRoundStatus.Superseded);

        private void TransitionTerminal(EngagementRound round, EngagementRoundStatus status)
        {
            Store
                .TryTransitionEngagementRound(
                    round.Id,
                    EngagementRoundStatus.Pending,
                    EngagementRoundStatus.Running,
                    Start
                )
                .Should()
                .BeTrue();
            Store
                .TryTransitionEngagementRound(round.Id, EngagementRoundStatus.Running, status, Start)
                .Should()
                .BeTrue();
        }

        public void AddAskedQuestion(
            EngagementRound round,
            string questionId,
            ClarificationQuestionState state,
            string providerCommentId,
            string providerThreadId,
            string providerPermalink
        )
        {
            var source = StoreAuditSource(round, $"question-source-{questionId}", "ambiguous retry scope");
            var actionId = $"ask-{questionId}";
            var action = new CodeReviewDaemon.Sample.Persistence.Models.ReviewAction(
                round.Id,
                actionId,
                CodeReviewDaemon.Sample.Persistence.Models.ReviewActionKind.PostClarificationQuestion,
                ReviewActionStatus.Planned,
                Sha256($"question payload {questionId}"),
                JsonSerializer.Serialize(new { providerTargetId = providerThreadId }),
                null,
                null,
                Start,
                Start
            );
            Store.AddOrGetReviewAction(action, [new AuditSourceReference(source.Id, source.ContentSha256)]);
            var receipt = JsonSerializer.Serialize(
                new
                {
                    providerCommentId,
                    providerThreadId,
                    providerPermalink,
                }
            );
            Store
                .TryTransitionReviewAction(
                    round.Id,
                    action.ActionId,
                    ReviewActionStatus.Planned,
                    ReviewActionStatus.Accepted,
                    receipt,
                    null,
                    Start
                )
                .Should()
                .BeTrue();
            Store.AddClarificationQuestion(
                new ClarificationQuestion(
                    questionId,
                    round.Id,
                    "Does the retry apply per attempt?",
                    action.ProviderTargetJson,
                    null,
                    null,
                    "The current discussion is ambiguous.",
                    "Whether retry state is scoped per attempt.",
                    actionId,
                    receipt,
                    Start,
                    state,
                    Start,
                    Start
                ),
                [new AuditSourceReference(source.Id, source.ContentSha256)]
            );
        }

        public void AcceptReceipt(EngagementRound round, string providerObjectId)
        {
            var source = StoreAuditSource(round, $"daemon-source-{round.Id}", "daemon reply");
            var action = new CodeReviewDaemon.Sample.Persistence.Models.ReviewAction(
                round.Id,
                "daemon-reply",
                CodeReviewDaemon.Sample.Persistence.Models.ReviewActionKind.ReplyToDiscussion,
                ReviewActionStatus.Planned,
                Sha256("daemon payload"),
                "{}",
                null,
                null,
                Start,
                Start
            );
            Store.AddOrGetReviewAction(action, [new AuditSourceReference(source.Id, source.ContentSha256)]);
            Store
                .TryTransitionReviewAction(
                    round.Id,
                    action.ActionId,
                    ReviewActionStatus.Planned,
                    ReviewActionStatus.Accepted,
                    $"{{\"providerCommentId\":\"{providerObjectId}\"}}",
                    null,
                    Start
                )
                .Should()
                .BeTrue();
        }

        public AuditSourceRecord StoreAuditSource(EngagementRound round, string id, string text)
        {
            var body = Encoding.UTF8.GetBytes(text);
            return Store.StoreAuditRecord(
                new ModelTurnAuditRecord(
                    id,
                    new MultiTurnAuditScope(round.PrEngagementId.ToString(), round.Id.ToString()),
                    "thread-1",
                    "run-1",
                    $"generation-{id}",
                    null,
                    1,
                    MultiTurnAuditRecordTypes.ModelResponse,
                    "assistant",
                    null,
                    "github",
                    body,
                    Sha256(text),
                    body.Length,
                    AuditCaptureOutcome.Complete,
                    null,
                    Start
                )
            );
        }

        public EngagementDiscussionRoundExecutor CreateExecutor() =>
            new(Store, [Provider], Policy, NullLogger<EngagementDiscussionRoundExecutor>.Instance);

        public void Dispose()
        {
            Store.Dispose();
            _database.Dispose();
        }
    }

    private sealed class RecordingDiscussionPolicy : IDiscussionRoundPolicy
    {
        public List<DiscussionRoundContext> Received { get; } = [];

        public Task<EngagementRoundStatus> ExecuteAsync(
            DiscussionRoundContext context,
            CancellationToken cancellationToken
        )
        {
            Received.Add(context);
            return Task.FromResult(EngagementRoundStatus.RetryPending);
        }
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
