using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// Pins <see cref="IPrProvider.GetPrStateAsync"/> as part of the provider seam (not a GitHub-only member):
/// the PR-lifecycle sweep resolves each reviewed PR's terminal state through the interface, so any fake or
/// real provider must expose it. Driving the shared <see cref="MockPrProvider"/> double purely through the
/// <see cref="IPrProvider"/> reference proves the member is callable off the abstraction.
/// </summary>
public sealed class IPrProviderContractTests
{
    private static readonly RepoIdentity Repo = new()
    {
        Provider = "github",
        OrgOrOwner = "acme",
        RepoName = "widgets",
    };

    private static IPrProvider Provider(PrLifecycle state) =>
        new MockPrProvider("github", [], Cursor()) { PrState = state };

    [Fact]
    public void Provider_activity_watermarks_order_equal_timestamps_by_provider_qualified_stable_id()
    {
        var timestamp = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        var later = new ProviderActivityWatermark("github", timestamp, "review-comment:11");
        var earlier = new ProviderActivityWatermark("GITHUB", timestamp, "review-comment:10");

        new[] { later, earlier }.Order().Should().Equal(earlier, later);
    }

    [Fact]
    public void Provider_activity_watermark_is_derived_from_the_last_activity_after_filtering()
    {
        var timestamp = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        var activity = new[]
        {
            Discussion("issue-comment:3", timestamp.AddMinutes(1)),
            Discussion("issue-comment:2", timestamp),
        };

        ProviderEngagementSnapshot
            .Create(
                PrLifecycleState.Open,
                "head",
                "base",
                new ProviderActivityWatermark("github", timestamp.AddMinutes(2), "review:daemon"),
                activity
            )
            .LatestObserved.Should()
            .Be(activity[0].Watermark);
    }

    [Fact]
    public void Provider_activity_watermark_falls_back_to_the_supplied_boundary_when_no_external_activity_remains()
    {
        var boundary = new ProviderActivityWatermark(
            "github",
            new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero),
            "seed:0"
        );

        ProviderEngagementSnapshot
            .Create(PrLifecycleState.Open, "head", "base", boundary, [])
            .LatestObserved.Should()
            .Be(boundary);
    }

    [Fact]
    public void Ado_ancestor_closure_keeps_duplicate_raw_comment_ids_inside_their_own_threads()
    {
        var publishedAt = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        var firstParent = AdoDiscussion("800", "2", "thread:800:comment:2", null, publishedAt);
        var secondParent = AdoDiscussion("900", "2", "thread:900:comment:2", null, publishedAt.AddMinutes(1));
        var reply = AdoDiscussion("900", "3", "thread:900:comment:3", "2", publishedAt.AddMinutes(2));

        var snapshot = ProviderEngagementSnapshot.Create(
            PrLifecycleState.Open,
            "head",
            "base",
            new ProviderActivityWatermark("ado", publishedAt, "seed:0"),
            [reply],
            [secondParent, firstParent, reply]
        );

        snapshot
            .AncestorActivity.Should()
            .ContainSingle()
            .Which.ProviderObjectId.Should()
            .Be(secondParent.ProviderObjectId);
    }

    [Fact]
    public async Task Engagement_snapshot_is_callable_through_the_interface()
    {
        var expected = ProviderEngagementSnapshot.Create(
            PrLifecycleState.Open,
            "head",
            "base",
            new ProviderActivityWatermark("github", new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero), "seed:0"),
            []
        );
        var provider = (MockPrProvider)Provider(PrLifecycle.Open);
        provider.EngagementSnapshot = expected;

        var actual = await provider.GetEngagementSnapshotAsync(
            Repo,
            "42",
            after: null,
            new HashSet<string>(StringComparer.Ordinal),
            CancellationToken.None
        );

        actual.Should().Be(expected);
    }

    [Fact]
    public async Task GetPrState_is_callable_through_the_interface()
    {
        (await Provider(PrLifecycle.Open).GetPrStateAsync(Repo, "42", CancellationToken.None))
            .Should()
            .Be(PrLifecycle.Open);
        (await Provider(PrLifecycle.Merged).GetPrStateAsync(Repo, "42", CancellationToken.None))
            .Should()
            .Be(PrLifecycle.Merged);
        (await Provider(PrLifecycle.Abandoned).GetPrStateAsync(Repo, "42", CancellationToken.None))
            .Should()
            .Be(PrLifecycle.Abandoned);
    }

    private static ProviderDiscussionRef Discussion(string id, DateTimeOffset publishedAt) =>
        new(
            "github",
            id,
            id,
            id,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            publishedAt,
            "developer",
            "body",
            ProviderDiscussionKind.Comment
        );

    private static ProviderDiscussionRef AdoDiscussion(
        string threadId,
        string commentId,
        string providerObjectId,
        string? parentCommentId,
        DateTimeOffset publishedAt
    ) =>
        new(
            "ado",
            threadId,
            commentId,
            providerObjectId,
            parentCommentId,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            publishedAt,
            "developer",
            "body",
            ProviderDiscussionKind.Comment
        );

    private static OpaqueCursor Cursor() =>
        new()
        {
            Provider = "github",
            Scope = "acme/widgets:open-prs",
            CursorVersion = PrPollingService.CursorVersion,
            CursorPayload = "{}",
            HighWaterMark = null,
        };
}
