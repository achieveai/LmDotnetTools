using AchieveAi.LmDotnetTools.LmMultiTurn.Audit;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Time.Testing;

namespace CodeReviewDaemon.Sample.Tests.Persistence;

public sealed class ReviewStoreRoundObservationSinkTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 19, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RecordAsync_assigns_monotonic_sequences_and_preserves_source_links()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var round = SeedRound(store);
        var source = StoreSource(store, round.Id, "source-1", "evidence");
        var sut = new ReviewStoreRoundObservationSink(store, new FakeTimeProvider(Now));

        await sut.RecordAsync(
            round.Id,
            [
                new RoundObservationDraft(
                    ObservationKind.Answer,
                    "answered from evidence",
                    [new AuditSourceReference(source.Id, source.ContentSha256)]
                ),
                new RoundObservationDraft(ObservationKind.DeliberateNoAction, "nothing else to add"),
            ],
            CancellationToken.None
        );
        await sut.RecordAsync(
            round.Id,
            [new RoundObservationDraft(ObservationKind.Correction, "corrected later")],
            CancellationToken.None
        );

        var persisted = store.ListRoundObservations(round.Id);
        persisted.Select(item => item.Sequence).Should().Equal(1, 2, 3);
        persisted.Select(item => item.ObservedAtUtc).Should().OnlyContain(value => value == Now);
        store
            .ListRoundObservationSources(persisted[0].Id)
            .Should()
            .Equal(new AuditSourceReference(source.Id, source.ContentSha256));
    }

    [Fact]
    public async Task RecordAsync_rejects_an_unknown_round_before_writing_anything()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var sut = new ReviewStoreRoundObservationSink(store, new FakeTimeProvider(Now));

        Func<Task> act = () =>
            sut.RecordAsync(999, [new RoundObservationDraft(ObservationKind.Gap, "unknown")], CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*round 999*");
    }

    private static EngagementRound SeedRound(ReviewStore store)
    {
        var repoId = store.EnsureRepo(
            new RepoIdentity
            {
                Provider = "github",
                OrgOrOwner = "achieveai",
                RepoName = "LmDotnetTools",
            }
        );
        var watermark = new ProviderActivityWatermark("github", Now, "head:118");
        var engagement = store.CreateOrGetEngagement(
            new PrEngagement(
                0,
                repoId,
                "github",
                "118",
                PrLifecycleState.Open,
                "head-sha",
                "base-sha",
                null,
                watermark,
                watermark,
                null,
                null,
                null,
                null,
                null,
                Now
            )
        );
        return store.TryAdmitRound(
            new EngagementRound(
                0,
                engagement.Id,
                EngagementRoundIntent.DiscussionFollowUp,
                EngagementRoundStatus.Pending,
                "head-sha",
                "base-sha",
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
        )!;
    }

    private static AuditSourceRecord StoreSource(ReviewStore store, long roundId, string id, string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        var engagementId = store.GetEngagementRound(roundId)!.PrEngagementId;
        return store.StoreAuditRecord(
            new ModelTurnAuditRecord(
                id,
                new MultiTurnAuditScope(engagementId.ToString(), roundId.ToString()),
                "thread",
                "run",
                "generation",
                null,
                1,
                MultiTurnAuditRecordTypes.ToolResult,
                null,
                null,
                null,
                bytes,
                hash,
                bytes.Length,
                AuditCaptureOutcome.Complete,
                null,
                Now
            )
        );
    }
}
