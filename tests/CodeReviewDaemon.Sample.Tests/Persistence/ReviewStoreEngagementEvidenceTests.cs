using System.Security.Cryptography;
using AchieveAi.LmDotnetTools.LmMultiTurn.Audit;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Data.Sqlite;

namespace CodeReviewDaemon.Sample.Tests.Persistence;

public sealed class ReviewStoreEngagementEvidenceTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Question_state_changes_are_compare_and_swap()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var source = StoreSource(store, roundId, "question-source", "ambiguous requirement");
        var question = Question(roundId, actionId: null);
        store.AddClarificationQuestion(question, [Reference(source)]);

        store
            .TryTransitionClarificationQuestion(
                question.Id,
                ClarificationQuestionState.Open,
                ClarificationQuestionState.Answered,
                ObservedAt.AddMinutes(1)
            )
            .Should()
            .BeTrue();
        store
            .TryTransitionClarificationQuestion(
                question.Id,
                ClarificationQuestionState.Open,
                ClarificationQuestionState.Contested,
                ObservedAt.AddMinutes(2)
            )
            .Should()
            .BeFalse("the expected state is stale");
        store.GetClarificationQuestion(question.Id)!.State.Should().Be(ClarificationQuestionState.Answered);
    }

    [Fact]
    public void Question_counts_as_asked_only_after_its_linked_action_is_accepted()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var source = StoreSource(store, roundId, "question-source", "ambiguous requirement");
        var action = Action(roundId, "ask-q-1");
        store.AddOrGetReviewAction(action, [Reference(source)]);
        store.AddClarificationQuestion(Question(roundId, action.ActionId), [Reference(source)]);

        store.ListAskedClarificationQuestions(roundId).Should().BeEmpty();

        store
            .TryTransitionReviewAction(
                roundId,
                action.ActionId,
                ReviewActionStatus.Planned,
                ReviewActionStatus.Accepted,
                providerReceiptJson: "{\"commentId\":\"41\"}",
                rejectionJson: null,
                ObservedAt.AddMinutes(1)
            )
            .Should()
            .BeTrue();

        var asked = store.ListAskedClarificationQuestions(roundId).Should().ContainSingle().Subject;
        asked.AskedAtUtc.Should().Be(ObservedAt.AddMinutes(1));
        asked.ProviderReceiptJson.Should().Be("{\"commentId\":\"41\"}");
    }

    [Fact]
    public void Accepted_runless_action_receipt_is_included_in_the_daemon_provider_object_inventory()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var round = store.GetEngagementRound(roundId)!;
        var engagement = store.GetEngagement(round.PrEngagementId)!;
        var source = StoreSource(store, roundId, "action-source", "publish exact wording");
        var action = Action(roundId, "reply-1") with { Kind = ReviewActionKind.ReplyToDiscussion };
        store.AddOrGetReviewAction(action, [Reference(source)]);
        store
            .TryTransitionReviewAction(
                roundId,
                action.ActionId,
                ReviewActionStatus.Planned,
                ReviewActionStatus.Accepted,
                "{\"provider\":\"github\",\"providerCommentId\":\"discussion-comment:41\"}",
                null,
                ObservedAt.AddMinutes(1)
            )
            .Should()
            .BeTrue();

        var receiptIds = store.GetPostedProviderReceiptIds(engagement.RepoId, engagement.PrId);

        receiptIds.Should().ContainSingle().Which.Should().Be("discussion-comment:41");
    }

    [Fact]
    public void Malformed_runless_action_receipt_is_ignored()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var round = store.GetEngagementRound(roundId)!;
        var engagement = store.GetEngagement(round.PrEngagementId)!;
        var source = StoreSource(store, roundId, "malformed-source", "publish exact wording");
        var action = Action(roundId, "malformed-reply") with { Kind = ReviewActionKind.ReplyToDiscussion };
        store.AddOrGetReviewAction(action, [Reference(source)]);
        store
            .TryTransitionReviewAction(
                roundId,
                action.ActionId,
                ReviewActionStatus.Planned,
                ReviewActionStatus.Accepted,
                "{",
                null,
                ObservedAt.AddMinutes(1)
            )
            .Should()
            .BeTrue();

        var receiptIds = store.GetPostedProviderReceiptIds(engagement.RepoId, engagement.PrId);

        receiptIds.Should().BeEmpty();
    }

    [Fact]
    public void Runless_action_receipt_from_another_pr_is_not_included()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var requestedRoundId = SeedRound(store, "118");
        var otherRoundId = SeedRound(store, "119");
        var requestedRound = store.GetEngagementRound(requestedRoundId)!;
        var requestedEngagement = store.GetEngagement(requestedRound.PrEngagementId)!;
        var source = StoreSource(store, otherRoundId, "other-pr-source", "publish elsewhere");
        var action = Action(otherRoundId, "other-pr-reply") with { Kind = ReviewActionKind.ReplyToDiscussion };
        store.AddOrGetReviewAction(action, [Reference(source)]);
        store
            .TryTransitionReviewAction(
                otherRoundId,
                action.ActionId,
                ReviewActionStatus.Planned,
                ReviewActionStatus.Accepted,
                "{\"providerCommentId\":\"other-pr-comment:41\"}",
                null,
                ObservedAt.AddMinutes(1)
            )
            .Should()
            .BeTrue();

        var receiptIds = store.GetPostedProviderReceiptIds(requestedEngagement.RepoId, requestedEngagement.PrId);

        receiptIds.Should().BeEmpty();
    }

    [Fact]
    public void Accepted_action_replay_returns_accepted_state_and_preserves_immutable_intent()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var source = StoreSource(store, roundId, "action-source", "publish exact wording");
        var action = Action(roundId, "summary-1");
        store.AddOrGetReviewAction(action, [Reference(source)]);
        store
            .TryTransitionReviewAction(
                roundId,
                action.ActionId,
                ReviewActionStatus.Planned,
                ReviewActionStatus.Accepted,
                "{\"commentId\":\"41\"}",
                null,
                ObservedAt.AddMinutes(1)
            )
            .Should()
            .BeTrue();

        var replay = store.AddOrGetReviewAction(action, [Reference(source)]);
        var read = store.GetReviewAction(roundId, action.ActionId);

        replay.Status.Should().Be(ReviewActionStatus.Accepted);
        replay.ProviderReceiptJson.Should().Be("{\"commentId\":\"41\"}");
        read.Should().Be(replay);
    }

    [Fact]
    public void Accepted_action_replay_rejects_changed_immutable_intent()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var source = StoreSource(store, roundId, "action-source", "publish exact wording");
        var action = Action(roundId, "summary-1");
        store.AddOrGetReviewAction(action, [Reference(source)]);
        store
            .TryTransitionReviewAction(
                roundId,
                action.ActionId,
                ReviewActionStatus.Planned,
                ReviewActionStatus.Accepted,
                "{\"commentId\":\"41\"}",
                null,
                ObservedAt.AddMinutes(1)
            )
            .Should()
            .BeTrue();

        var replay = () =>
            store.AddOrGetReviewAction(
                action with
                {
                    ProviderTargetJson = "{\"kind\":\"summary\"}",
                },
                [Reference(source)]
            );

        replay.Should().Throw<InvalidOperationException>().WithMessage("*conflicts*");
    }

    [Fact]
    public void Review_action_replay_is_idempotent_when_only_retry_time_changes()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var source = StoreSource(store, roundId, "action-source", "publish exact wording");
        var action = Action(roundId, "summary-1");

        var first = store.AddOrGetReviewAction(action, [Reference(source)]);
        var replay = store.AddOrGetReviewAction(
            action with
            {
                CreatedAtUtc = action.CreatedAtUtc.AddHours(1),
            },
            [Reference(source)]
        );

        replay.Should().Be(first);
        replay.CreatedAtUtc.Should().Be(action.CreatedAtUtc);
    }

    [Fact]
    public void Review_action_replay_is_idempotent_and_conflicting_replay_is_rejected()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var source = StoreSource(store, roundId, "action-source", "publish exact wording");
        var action = Action(roundId, "summary-1");

        var first = store.AddOrGetReviewAction(action, [Reference(source)]);
        var replay = store.AddOrGetReviewAction(action, [Reference(source)]);
        var conflictingPayload = () =>
            store.AddOrGetReviewAction(action with { PayloadSha256 = Sha256("different"u8) }, [Reference(source)]);
        var conflictingKind = () =>
            store.AddOrGetReviewAction(action with { Kind = ReviewActionKind.FinalizeRound }, [Reference(source)]);

        replay.Should().Be(first);
        conflictingPayload.Should().Throw<InvalidOperationException>().WithMessage("*conflicts*");
        conflictingKind.Should().Throw<InvalidOperationException>().WithMessage("*conflicts*");
    }

    [Fact]
    public void Round_observations_are_append_only_even_through_direct_sql()
    {
        using var db = new TempSqliteDatabase();
        using (var store = new ReviewStore(db.ConnectionString))
        {
            var roundId = SeedRound(store);
            var source = StoreSource(store, roundId, "observation-source", "supported fact");
            store.AppendRoundObservation(Observation(roundId), [Reference(source)]);
        }

        using var connection = SqliteConnectionFactory.Open(db.ConnectionString);
        var update = () =>
            Execute(connection, "UPDATE round_observation SET summary = 'rewritten' WHERE id = 'obs-1';");
        var delete = () => Execute(connection, "DELETE FROM round_observation WHERE id = 'obs-1';");

        update.Should().Throw<SqliteException>().WithMessage("*append-only*");
        delete.Should().Throw<SqliteException>().WithMessage("*append-only*");
        ReadScalar(connection, "SELECT summary FROM round_observation WHERE id = 'obs-1';")
            .Should()
            .Be("supported conclusion");
    }

    [Fact]
    public void Every_structured_entity_rejects_a_source_reference_with_the_wrong_exact_hash()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var source = StoreSource(store, roundId, "shared-source", "evidence");
        var wrong = new AuditSourceReference(source.Id, Sha256("other"u8));
        var action = Action(roundId, "ask-q-1");
        store.AddOrGetReviewAction(action, [Reference(source)]);
        store.AddClarificationQuestion(Question(roundId, action.ActionId), [Reference(source)]);

        var addQuestion = () =>
            store.AddClarificationQuestion(Question(roundId, actionId: null) with { Id = "q-bad" }, [wrong]);
        var addAction = () => store.AddOrGetReviewAction(Action(roundId, "action-bad"), [wrong]);
        var addAnswer = () =>
            store.AddClarificationCandidateAnswer(
                new ClarificationCandidateAnswer(
                    "answer-bad",
                    "q-1",
                    roundId,
                    "{\"commentId\":\"42\"}",
                    "candidate may answer yes",
                    ObservedAt
                ),
                [wrong]
            );
        var addObservation = () =>
            store.AppendRoundObservation(Observation(roundId) with { Id = "obs-bad", Sequence = 2 }, [wrong]);

        addQuestion.Should().Throw<ArgumentException>().WithMessage("*source*");
        addAction.Should().Throw<ArgumentException>().WithMessage("*source*");
        addAnswer.Should().Throw<ArgumentException>().WithMessage("*source*");
        addObservation.Should().Throw<ArgumentException>().WithMessage("*source*");
    }

    [Fact]
    public void Candidate_answer_may_cite_evidence_from_a_later_round_of_the_same_engagement()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var firstRoundId = SeedRound(store);
        var questionSource = StoreSource(store, firstRoundId, "question-source", "question evidence");
        store.AddClarificationQuestion(Question(firstRoundId, actionId: null), [Reference(questionSource)]);
        store
            .TryTransitionEngagementRound(
                firstRoundId,
                EngagementRoundStatus.Pending,
                EngagementRoundStatus.Superseded,
                ObservedAt.AddMinutes(1)
            )
            .Should()
            .BeTrue();
        var engagementId = store.GetEngagementRound(firstRoundId)!.PrEngagementId;
        var laterRoundId = store
            .TryAdmitRound(
                new EngagementRound(
                    0,
                    engagementId,
                    EngagementRoundIntent.DiscussionFollowUp,
                    EngagementRoundStatus.Pending,
                    "head-1",
                    "base-1",
                    null,
                    new ProviderActivityWatermark("github", ObservedAt.AddHours(1), "comment-2"),
                    0,
                    null,
                    0,
                    null,
                    null,
                    null,
                    null,
                    null
                )
            )!
            .Id;
        var answerSource = StoreSource(store, laterRoundId, "answer-source", "developer answer");
        var answer = new ClarificationCandidateAnswer(
            "answer-later",
            "q-1",
            laterRoundId,
            "{\"commentId\":\"42\"}",
            "candidate says yes",
            ObservedAt.AddHours(1)
        );

        store.AddClarificationCandidateAnswer(answer, [Reference(answerSource)]).Should().Be(answer);
    }

    [Fact]
    public void Candidate_answer_rejects_evidence_from_a_different_engagement()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var questionRoundId = SeedRound(store);
        var questionSource = StoreSource(store, questionRoundId, "question-source", "question evidence");
        store.AddClarificationQuestion(Question(questionRoundId, actionId: null), [Reference(questionSource)]);
        var otherRoundId = SeedRound(store, prId: "119");
        var answerSource = StoreSource(store, otherRoundId, "answer-source", "unrelated developer answer");
        var answer = new ClarificationCandidateAnswer(
            "answer-other-engagement",
            "q-1",
            otherRoundId,
            "{\"commentId\":\"42\"}",
            "candidate says yes",
            ObservedAt.AddHours(1)
        );

        var add = () => store.AddClarificationCandidateAnswer(answer, [Reference(answerSource)]);

        add.Should().Throw<ArgumentException>().WithMessage("*same engagement*");
    }

    [Fact]
    public void Asked_question_write_requires_an_accepted_linked_action()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var source = StoreSource(store, roundId, "question-source", "evidence");
        var askedWithoutAction = Question(roundId, actionId: null) with
        {
            ProviderReceiptJson = "{}",
            AskedAtUtc = ObservedAt,
        };
        var plannedAction = Action(roundId, "ask-q-2");
        store.AddOrGetReviewAction(plannedAction, [Reference(source)]);
        var askedWithPlanned = askedWithoutAction with { Id = "q-planned", ActionId = plannedAction.ActionId };

        var withoutAction = () => store.AddClarificationQuestion(askedWithoutAction, [Reference(source)]);
        var withPlanned = () => store.AddClarificationQuestion(askedWithPlanned, [Reference(source)]);

        withoutAction.Should().Throw<ArgumentException>().WithMessage("*accepted*");
        withPlanned.Should().Throw<ArgumentException>().WithMessage("*accepted*");
    }

    [Fact]
    public void Asked_question_listing_requires_the_linked_action_to_remain_accepted()
    {
        using var db = new TempSqliteDatabase();
        long roundId;
        using (var store = new ReviewStore(db.ConnectionString))
        {
            roundId = SeedRound(store);
            var source = StoreSource(store, roundId, "question-source", "evidence");
            var action = Action(roundId, "ask-q-1");
            store.AddOrGetReviewAction(action, [Reference(source)]);
            store.AddClarificationQuestion(Question(roundId, action.ActionId), [Reference(source)]);
            store
                .TryTransitionReviewAction(
                    roundId,
                    action.ActionId,
                    ReviewActionStatus.Planned,
                    ReviewActionStatus.Accepted,
                    "{\"commentId\":\"41\"}",
                    null,
                    ObservedAt.AddMinutes(1)
                )
                .Should()
                .BeTrue();
            store.ListAskedClarificationQuestions(roundId).Should().ContainSingle();
        }

        using (var connection = SqliteConnectionFactory.Open(db.ConnectionString))
        {
            Execute(connection, "UPDATE review_action SET status = 'Sending' WHERE action_id = 'ask-q-1';");
        }

        using var reopened = new ReviewStore(db.ConnectionString);
        reopened.ListAskedClarificationQuestions(roundId).Should().BeEmpty();
    }

    [Fact]
    public void Observation_source_links_are_append_only_through_direct_sql()
    {
        using var db = new TempSqliteDatabase();
        using (var store = new ReviewStore(db.ConnectionString))
        {
            var roundId = SeedRound(store);
            var source = StoreSource(store, roundId, "observation-source", "supported fact");
            store.AppendRoundObservation(Observation(roundId), [Reference(source)]);
        }

        using var connection = SqliteConnectionFactory.Open(db.ConnectionString);
        var delete = () => Execute(connection, "DELETE FROM round_observation_source WHERE observation_id = 'obs-1';");
        var update = () =>
            Execute(
                connection,
                "UPDATE round_observation_source SET source_content_sha256 = 'wrong' WHERE observation_id = 'obs-1';"
            );

        delete.Should().Throw<SqliteException>().WithMessage("*append-only*");
        update.Should().Throw<SqliteException>().WithMessage("*append-only*");
    }

    [Fact]
    public void Candidate_answers_and_observations_replay_idempotently_but_not_with_different_semantics()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var source = StoreSource(store, roundId, "shared-source", "evidence");
        store.AddClarificationQuestion(Question(roundId, actionId: null), [Reference(source)]);
        var answer = new ClarificationCandidateAnswer(
            "answer-1",
            "q-1",
            roundId,
            "{\"commentId\":\"42\"}",
            "candidate says yes",
            ObservedAt
        );
        var observation = Observation(roundId);

        store.AddClarificationCandidateAnswer(answer, [Reference(source)]).Should().Be(answer);
        store.AddClarificationCandidateAnswer(answer, [Reference(source)]).Should().Be(answer);
        store.AppendRoundObservation(observation, [Reference(source)]).Should().Be(observation);
        store.AppendRoundObservation(observation, [Reference(source)]).Should().Be(observation);

        var conflictingAnswer = () =>
            store.AddClarificationCandidateAnswer(
                answer with
                {
                    InterpretationSummary = "candidate says no",
                },
                [Reference(source)]
            );
        var conflictingObservation = () =>
            store.AppendRoundObservation(observation with { Summary = "different conclusion" }, [Reference(source)]);
        conflictingAnswer.Should().Throw<InvalidOperationException>();
        conflictingObservation.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Concurrent_identical_semantic_observations_append_one_durable_row()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var source = StoreSource(store, roundId, "answer-source", "the retry applies per run");
        var draft = new RoundObservationDraft(ObservationKind.Answer, "question q-1 is Answered", [Reference(source)]);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = Enumerable
            .Range(0, 8)
            .Select(_ =>
                Task.Run(async () =>
                {
                    await start.Task;
                    return store.AppendRoundObservationOnce("question-answer-round-1-q-1", roundId, draft, ObservedAt);
                })
            )
            .ToArray();

        start.SetResult();
        var results = await Task.WhenAll(attempts);

        results.Should().OnlyContain(result => result == results[0]);
        store.ListRoundObservations(roundId).Should().ContainSingle().Which.Should().Be(results[0]);
        store.ListRoundObservationSources(results[0].Id).Should().Equal(Reference(source));
    }

    private static ClarificationQuestion Question(long roundId, string? actionId) =>
        new(
            "q-1",
            roundId,
            "Should this preserve the previous value?",
            "{\"kind\":\"inline\",\"path\":\"src/A.cs\",\"line\":42}",
            "src/A.cs",
            42,
            "The acceptance criteria do not specify replacement semantics.",
            "Whether replacing an omitted value is correct.",
            actionId,
            ProviderReceiptJson: null,
            AskedAtUtc: null,
            ClarificationQuestionState.Open,
            ObservedAt,
            ObservedAt
        );

    private static ReviewAction Action(long roundId, string actionId) =>
        new(
            roundId,
            actionId,
            ReviewActionKind.PostClarificationQuestion,
            ReviewActionStatus.Planned,
            Sha256("typed semantic payload"u8),
            "{\"kind\":\"inline\",\"path\":\"src/A.cs\",\"line\":42}",
            ProviderReceiptJson: null,
            RejectionJson: null,
            ObservedAt,
            ObservedAt
        );

    private static RoundObservation Observation(long roundId) =>
        new("obs-1", roundId, 1, ObservationKind.ContextClaim, "supported conclusion", ObservedAt);

    private static AuditSourceReference Reference(AuditSourceRecord source) => new(source.Id, source.ContentSha256);

    private static AuditSourceRecord StoreSource(ReviewStore store, long roundId, string id, string text)
    {
        var content = System.Text.Encoding.UTF8.GetBytes(text);
        var engagementId = store.GetEngagementRound(roundId)!.PrEngagementId;
        var source = new ModelTurnAuditRecord(
            id,
            new MultiTurnAuditScope(engagementId.ToString(), roundId.ToString()),
            "thread-1",
            "run-1",
            id,
            null,
            1,
            MultiTurnAuditRecordTypes.ModelResponse,
            "assistant",
            "claude-opus-5",
            "anthropic",
            content,
            Sha256(content),
            content.Length,
            AuditCaptureOutcome.Complete,
            null,
            ObservedAt
        );
        return store.StoreAuditRecord(source);
    }

    private static long SeedRound(ReviewStore store, string prId = "118")
    {
        var repoId = store.EnsureRepo(
            new RepoIdentity
            {
                Provider = "github",
                OrgOrOwner = "achieveai",
                RepoName = "LmDotnetTools",
            }
        );
        var engagement = store.CreateOrGetEngagement(
            new PrEngagement(
                0,
                repoId,
                "github",
                prId,
                PrLifecycleState.Open,
                "head-1",
                "base-1",
                null,
                new ProviderActivityWatermark("github", ObservedAt, "comment-1"),
                null,
                null,
                null,
                null,
                null,
                null,
                ObservedAt
            )
        );
        return store
            .TryAdmitRound(
                new EngagementRound(
                    0,
                    engagement.Id,
                    EngagementRoundIntent.CodeReview,
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
            )!
            .Id;
    }

    private static string Sha256(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }

    private static string? ReadScalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()?.ToString();
    }
}
