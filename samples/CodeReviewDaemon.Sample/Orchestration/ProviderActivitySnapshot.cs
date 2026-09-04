using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>The provider-native category of one pull-request discussion object.</summary>
internal enum ProviderDiscussionKind
{
    Comment,
    Review,
    Deleted,
    System,
}

/// <summary>One provider-native discussion object with enough identity to audit, reply, and anchor it.</summary>
internal sealed record ProviderDiscussionRef(
    string Provider,
    string ThreadId,
    string CommentId,
    string ProviderObjectId,
    string? ParentCommentId,
    string? Permalink,
    string? Path,
    string? Side,
    int? StartLine,
    int? EndLine,
    string? Status,
    string? IterationContext,
    DateTimeOffset PublishedAt,
    string Author,
    string Body,
    ProviderDiscussionKind Kind
)
{
    public ProviderActivityWatermark Watermark => new(Provider, PublishedAt, ProviderObjectId);

    public bool CreatesDiscussionDemand => Kind is ProviderDiscussionKind.Comment or ProviderDiscussionKind.Review;
}

/// <summary>A frozen provider read used to observe one durable PR engagement.</summary>
internal sealed record ProviderEngagementSnapshot(
    PrLifecycleState Lifecycle,
    string HeadSha,
    string BaseSha,
    ProviderActivityWatermark LatestObserved,
    IReadOnlyList<ProviderDiscussionRef> ExternalActivity,
    IReadOnlyList<ProviderDiscussionRef> AncestorActivity
)
{
    private const int MaxAncestorCount = 100;
    private const int MaxAncestorDepth = 20;

    public static ProviderEngagementSnapshot Create(
        PrLifecycleState lifecycle,
        string headSha,
        string baseSha,
        ProviderActivityWatermark fallbackBoundary,
        IReadOnlyList<ProviderDiscussionRef> externalActivity,
        IReadOnlyList<ProviderDiscussionRef>? observedActivity = null
    )
    {
        ArgumentNullException.ThrowIfNull(fallbackBoundary);
        ArgumentNullException.ThrowIfNull(externalActivity);

        var ordered = externalActivity.OrderBy(activity => activity.Watermark).ToArray();
        return new ProviderEngagementSnapshot(
            lifecycle,
            headSha,
            baseSha,
            ordered.Length == 0 ? fallbackBoundary : ordered[^1].Watermark,
            ordered,
            AncestorClosure(ordered, observedActivity ?? ordered)
        );
    }

    private static IReadOnlyList<ProviderDiscussionRef> AncestorClosure(
        IReadOnlyList<ProviderDiscussionRef> externalActivity,
        IReadOnlyList<ProviderDiscussionRef> observedActivity
    )
    {
        var externalIds = externalActivity.Select(IdentityOf).ToHashSet();
        var candidates = observedActivity
            .Where(activity => activity.CreatesDiscussionDemand && !string.IsNullOrWhiteSpace(activity.Body))
            .GroupBy(IdentityOf)
            .ToDictionary(group => group.Key, group => group.Last());
        var frontier = externalActivity
            .Where(activity => !string.IsNullOrWhiteSpace(activity.ParentCommentId))
            .Select(activity => ParentIdentityOf(activity, activity.ParentCommentId!))
            .Distinct()
            .ToArray();
        var ancestors = new Dictionary<DiscussionIdentity, ProviderDiscussionRef>();

        for (var depth = 0; depth < MaxAncestorDepth && frontier.Length > 0; depth++)
        {
            var next = new List<DiscussionIdentity>();
            foreach (var parentId in frontier)
            {
                if (!candidates.TryGetValue(parentId, out var parent))
                {
                    continue;
                }

                var parentIdentity = IdentityOf(parent);
                if (!externalIds.Contains(parentIdentity))
                {
                    ancestors.TryAdd(parentIdentity, parent);
                    if (ancestors.Count == MaxAncestorCount)
                    {
                        return [.. ancestors.Values.OrderBy(activity => activity.Watermark)];
                    }
                }

                if (parent.ParentCommentId is { Length: > 0 } grandparentId)
                {
                    next.Add(ParentIdentityOf(parent, grandparentId));
                }
            }

            frontier = [.. next.Distinct()];
        }

        return [.. ancestors.Values.OrderBy(activity => activity.Watermark)];
    }

    private static DiscussionIdentity IdentityOf(ProviderDiscussionRef activity) =>
        new(activity.Provider, ThreadScopeOf(activity), activity.CommentId);

    private static DiscussionIdentity ParentIdentityOf(ProviderDiscussionRef activity, string parentCommentId) =>
        new(activity.Provider, ThreadScopeOf(activity), parentCommentId);

    private static string? ThreadScopeOf(ProviderDiscussionRef activity) =>
        activity.ProviderObjectId.StartsWith("thread:", StringComparison.Ordinal) ? activity.ThreadId : null;

    private readonly record struct DiscussionIdentity(string Provider, string? ThreadScope, string CommentId);
}
