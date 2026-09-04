using System.Text.Json.Serialization;

namespace CodeReviewDaemon.Sample.Persistence.Models;

/// <summary>A monotonically ordered, provider-qualified external activity position.</summary>
internal sealed record ProviderActivityWatermark(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("publishedAt")] DateTimeOffset PublishedAt,
    [property: JsonPropertyName("stableObjectId")] string StableObjectId
) : IComparable<ProviderActivityWatermark>
{
    public int CompareTo(ProviderActivityWatermark? other)
    {
        if (other is null)
        {
            return 1;
        }

        var published = PublishedAt.CompareTo(other.PublishedAt);
        if (published != 0)
        {
            return published;
        }

        var provider = StringComparer.OrdinalIgnoreCase.Compare(Provider, other.Provider);
        return provider != 0 ? provider : StringComparer.Ordinal.Compare(StableObjectId, other.StableObjectId);
    }
}

/// <summary>The kind of durable work one engagement round performs.</summary>
internal enum EngagementRoundIntent
{
    CodeReview,
    DiscussionFollowUp,
    MergedClose,
}

/// <summary>The durable execution state of an engagement round.</summary>
internal enum EngagementRoundStatus
{
    Pending,
    Running,
    RetryPending,
    Completed,
    Superseded,
    Parked,
}

/// <summary>One stable coordinator for a provider/repository/pull-request tuple.</summary>
internal sealed record PrEngagement(
    long Id,
    long RepoId,
    string Provider,
    string PrId,
    PrLifecycleState Lifecycle,
    string LatestHeadSha,
    string LatestBaseSha,
    string? LastReviewedHeadSha,
    ProviderActivityWatermark LatestActivity,
    ProviderActivityWatermark? ConsumedActivity,
    DateTimeOffset? LastCompletedAt,
    DateTimeOffset? NextEligibleAt,
    long? ActiveRoundId,
    long? LatestRoundId,
    string? RootSummaryReceiptJson,
    DateTimeOffset UpdatedAt
);

/// <summary>One immutable admission envelope and its durable progress.</summary>
internal sealed record EngagementRound(
    long Id,
    long PrEngagementId,
    EngagementRoundIntent Intent,
    EngagementRoundStatus Status,
    string HeadSha,
    string BaseSha,
    ProviderActivityWatermark? ActivityLowerBound,
    ProviderActivityWatermark? ActivityUpperBound,
    long PriorObservationBoundary,
    long? ReviewRunId,
    int GovernedFailureCount,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? SupersededAt,
    DateTimeOffset? ParkedAt,
    string? ParkReason
);
