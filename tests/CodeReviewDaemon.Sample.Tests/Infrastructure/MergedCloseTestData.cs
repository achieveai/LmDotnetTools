using System.Security.Cryptography;
using System.Text;
using AchieveAi.LmDotnetTools.LmMultiTurn.Audit;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>
/// Seeds a merged pull-request engagement whose typed evidence rows are the deterministic denominator
/// the merged-close chain must reproduce. Tests own the shape of the evidence; the production code
/// under test never gets to choose which rows exist.
/// </summary>
internal sealed class MergedCloseTestData : IDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 9, 2, 9, 0, 0, TimeSpan.Zero);

    private readonly TempSqliteDatabase _database = new();
    private int _sourceSequence;

    public MergedCloseTestData(TimeProvider? timeProvider = null)
    {
        Store = new ReviewStore(_database.ConnectionString, timeProvider);
        RepoId = Store.EnsureRepo(
            new RepoIdentity
            {
                Provider = "github",
                OrgOrOwner = "achieveai",
                RepoName = "LmDotnetTools",
            }
        );
        var watermark = new ProviderActivityWatermark("github", Start, "merge:118");
        Engagement = Store.CreateOrGetEngagement(
            new PrEngagement(
                0,
                RepoId,
                "github",
                "118",
                PrLifecycleState.Merged,
                "merge-head",
                "base-1",
                "merge-head",
                watermark,
                watermark,
                null,
                null,
                null,
                null,
                null,
                Start
            )
        );
    }

    public ReviewStore Store { get; }

    public long RepoId { get; }

    public PrEngagement Engagement { get; }

    public string ConnectionString => _database.ConnectionString;

    /// <summary>Admits a round and immediately completes it so a later round can be admitted.</summary>
    public EngagementRound AddCompletedRound(EngagementRoundIntent intent, string headSha = "merge-head")
    {
        var round = AddRound(intent, headSha);
        _ = Store.TryTransitionEngagementRound(
            round.Id,
            EngagementRoundStatus.Pending,
            EngagementRoundStatus.Running,
            Start
        );
        _ = Store.TryTransitionEngagementRound(
            round.Id,
            EngagementRoundStatus.Running,
            EngagementRoundStatus.Completed,
            Start
        );
        return Store.GetEngagementRound(round.Id)!;
    }

    public EngagementRound AddRound(EngagementRoundIntent intent, string headSha = "merge-head")
    {
        var watermark = new ProviderActivityWatermark("github", Start, "merge:118");
        return Store.TryAdmitRound(
                new EngagementRound(
                    0,
                    Engagement.Id,
                    intent,
                    EngagementRoundStatus.Pending,
                    headSha,
                    "base-1",
                    watermark,
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
            ) ?? throw new InvalidOperationException($"A {intent} round could not be admitted.");
    }

    public AuditSourceRecord AddSource(long roundId, string id, string text)
    {
        var content = Encoding.UTF8.GetBytes(text);
        return Store.StoreAuditRecord(
            new ModelTurnAuditRecord(
                id,
                new MultiTurnAuditScope(Engagement.Id.ToString(), roundId.ToString()),
                "thread-1",
                "run-1",
                id,
                null,
                Interlocked.Increment(ref _sourceSequence),
                MultiTurnAuditRecordTypes.ModelResponse,
                "assistant",
                "claude-opus-5",
                "anthropic",
                content,
                Sha256(content),
                content.Length,
                AuditCaptureOutcome.Complete,
                null,
                Start
            )
        );
    }

    public RoundObservation AddObservation(
        long roundId,
        string id,
        ObservationKind kind,
        string summary,
        AuditSourceRecord source,
        long sequence = 0,
        DateTimeOffset? observedAt = null
    ) =>
        Store.AppendRoundObservation(
            new RoundObservation(id, roundId, sequence, kind, summary, observedAt ?? Start),
            [Reference(source)]
        );

    /// <summary>
    /// Adds a question and only then accepts its action. The store stamps <c>asked_at</c> during that
    /// transition, so a question created after acceptance would never count as asked.
    /// </summary>
    public ClarificationQuestion AddAskedQuestion(
        long roundId,
        string id,
        string wording,
        AuditSourceRecord source,
        ClarificationQuestionState finalState = ClarificationQuestionState.Open
    )
    {
        var actionId = $"ask-{id}";
        AddPlannedAction(roundId, actionId, ReviewActionKind.PostClarificationQuestion, source);
        _ = Store.AddClarificationQuestion(
            new ClarificationQuestion(
                id,
                roundId,
                wording,
                "{\"kind\":\"inline\",\"path\":\"src/A.cs\",\"line\":42}",
                "src/A.cs",
                42,
                "Evidence for " + id,
                "Conclusion withheld for " + id,
                actionId,
                null,
                null,
                ClarificationQuestionState.Open,
                Start,
                Start
            ),
            [Reference(source)]
        );
        AcceptAction(roundId, actionId);

        if (finalState != ClarificationQuestionState.Open)
        {
            _ = Store.TryTransitionClarificationQuestion(
                id,
                ClarificationQuestionState.Open,
                finalState,
                Start.AddMinutes(2)
            );
        }

        return Store.GetClarificationQuestion(id)!;
    }

    public ReviewAction AddAcceptedAction(
        long roundId,
        string actionId,
        ReviewActionKind kind,
        AuditSourceRecord source
    )
    {
        AddPlannedAction(roundId, actionId, kind, source);
        AcceptAction(roundId, actionId);
        return Store.GetReviewAction(roundId, actionId)!;
    }

    private void AddPlannedAction(long roundId, string actionId, ReviewActionKind kind, AuditSourceRecord source) =>
        Store.AddOrGetReviewAction(
            new ReviewAction(
                roundId,
                actionId,
                kind,
                ReviewActionStatus.Planned,
                Sha256(Encoding.UTF8.GetBytes(actionId)),
                "{\"kind\":\"inline\",\"path\":\"src/A.cs\",\"line\":42}",
                null,
                null,
                Start,
                Start
            ),
            [Reference(source)]
        );

    private void AcceptAction(long roundId, string actionId) =>
        Store.TryTransitionReviewAction(
            roundId,
            actionId,
            ReviewActionStatus.Planned,
            ReviewActionStatus.Accepted,
            $"{{\"commentId\":\"{actionId}\"}}",
            null,
            Start.AddMinutes(1)
        );

    public static AuditSourceReference Reference(AuditSourceRecord source) => new(source.Id, source.ContentSha256);

    public static string Sha256(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    public void Dispose()
    {
        Store.Dispose();
        _database.Dispose();
    }
}
