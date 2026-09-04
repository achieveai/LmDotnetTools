using CodeReviewDaemon.Sample.Orchestration;

namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>
/// In-memory <see cref="IReviewCommentPublisher"/>. Records every post (so a test can assert "posted
/// exactly once"), returns a deterministic response id per idempotency key, and supports
/// <see cref="SeedExistingComment(string, string)"/> to simulate a comment that already exists provider-side — the case
/// the <see cref="ReviewPoster"/> backstop scan must catch to avoid a double post.
/// </summary>
internal sealed class FakeReviewCommentPublisher : IReviewCommentPublisher
{
    private readonly Dictionary<string, PostedComment> _byKey = new(StringComparer.Ordinal);
    private SemaphoreSlim? _findRelease;
    private TaskCompletionSource? _blockedFindsObserved;
    private int _expectedBlockedFinds;
    private int _findCallCount;

    public FakeReviewCommentPublisher(string provider = "github") => Provider = provider;

    public string Provider { get; }

    /// <summary>Idempotency keys passed to <see cref="PostReviewCommentAsync"/>, in call order.</summary>
    public List<string> PostedKeys { get; } = [];

    /// <summary>Bodies passed to <see cref="PostReviewCommentAsync"/>, in call order.</summary>
    public List<string> PostedBodies { get; } = [];

    public List<GitHubInlineReviewRequest> InlineReviews { get; } = [];

    public List<GitHubInlineReplyRequest> InlineReplies { get; } = [];

    public List<GitHubFlatConversationComment> FlatConversationComments { get; } = [];

    public List<AdoThreadReplyRequest> ThreadReplies { get; } = [];

    public PostedComment? RichReceipt { get; set; }

    /// <summary>How many real posts happened (the exactly-once assertion target).</summary>
    public int PostCount => PostedKeys.Count;

    /// <summary>
    /// Pretend a comment for <paramref name="idempotencyKey"/> already exists provider-side (e.g. a prior
    /// attempt posted then crashed). <see cref="FindPostedCommentAsync"/> will return it without it
    /// counting as a post.
    /// </summary>
    public void SeedExistingComment(string idempotencyKey, string providerResponseId) =>
        SeedExistingComment(idempotencyKey, new PostedComment(providerResponseId));

    public void SeedExistingComment(string idempotencyKey, PostedComment comment) => _byKey[idempotencyKey] = comment;

    /// <summary>
    /// When set, <see cref="PostReviewCommentAsync"/> throws it instead of posting — the transient publisher
    /// failure that strands an outbox row in <see cref="CodeReviewDaemon.Sample.Persistence.Models.OutboxStatus.Sending"/>. Clear it
    /// to let a later attempt through, which is how a retry path is driven from a test.
    /// </summary>
    public Exception? PostFailure { get; set; }

    /// <summary>How many times the backstop scan was run. It is a real provider round-trip, so a caller that
    /// reaches the publisher at all shows up here even when it ends up posting nothing.</summary>
    public int FindCallCount => Volatile.Read(ref _findCallCount);

    public void BlockFinds()
    {
        _findRelease = new SemaphoreSlim(0);
        _blockedFindsObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public Task WaitForFindsAsync(int count)
    {
        _expectedBlockedFinds = count;
        if (FindCallCount >= count)
        {
            _blockedFindsObserved!.TrySetResult();
        }

        return _blockedFindsObserved!.Task;
    }

    public void ReleaseFinds(int count = 1) => _findRelease!.Release(count);

    public async Task<PostedComment?> FindPostedCommentAsync(
        ReviewCommentTarget target,
        string idempotencyKey,
        CancellationToken cancellationToken
    )
    {
        var count = Interlocked.Increment(ref _findCallCount);
        if (_findRelease is not null)
        {
            if (_expectedBlockedFinds > 0 && count >= _expectedBlockedFinds)
            {
                _blockedFindsObserved!.TrySetResult();
            }

            await _findRelease.WaitAsync(cancellationToken);
        }

        return _byKey.TryGetValue(idempotencyKey, out var comment) ? comment : null;
    }

    public Task<PostedComment> PostReviewCommentAsync(
        ReviewCommentTarget target,
        string idempotencyKey,
        string body,
        CancellationToken cancellationToken
    )
    {
        if (PostFailure is { } failure)
        {
            return Task.FromException<PostedComment>(failure);
        }

        PostedKeys.Add(idempotencyKey);
        PostedBodies.Add(body);
        var comment = new PostedComment($"resp-{PostCount}");
        _byKey[idempotencyKey] = comment;
        return Task.FromResult(comment);
    }

    public Task<PostedComment> SubmitInlineReviewAsync(
        ReviewCommentTarget target,
        string idempotencyKey,
        GitHubInlineReviewRequest review,
        CancellationToken cancellationToken
    )
    {
        InlineReviews.Add(review);
        return RecordRichPost(idempotencyKey);
    }

    public Task<PostedComment> ReplyToInlineReviewCommentAsync(
        ReviewCommentTarget target,
        string idempotencyKey,
        GitHubInlineReplyRequest reply,
        CancellationToken cancellationToken
    )
    {
        InlineReplies.Add(reply);
        return RecordRichPost(idempotencyKey);
    }

    public Task<PostedComment> PostFlatConversationCommentAsync(
        ReviewCommentTarget target,
        string idempotencyKey,
        GitHubFlatConversationComment comment,
        CancellationToken cancellationToken
    )
    {
        FlatConversationComments.Add(comment);
        return RecordRichPost(idempotencyKey);
    }

    public Task<PostedComment> ReplyToThreadAsync(
        ReviewCommentTarget target,
        string idempotencyKey,
        AdoThreadReplyRequest reply,
        CancellationToken cancellationToken
    )
    {
        ThreadReplies.Add(reply);
        return RecordRichPost(idempotencyKey);
    }

    /// <summary>Comments returned by <see cref="ListExistingReviewCommentsAsync"/> — seed to simulate a PR that
    /// already has prior review comments (the delta-awareness path).</summary>
    public List<ExistingReviewComment> ExistingComments { get; } = [];

    /// <summary>
    /// When set, <see cref="ListExistingReviewCommentsAsync"/> throws it instead of returning
    /// <see cref="ExistingComments"/> — the transient provider/auth failure that leaves a review with no dedup
    /// context (#225 item 2). Without this hook the degraded path has no way to be reached from a test, which
    /// is why it went unverified.
    /// </summary>
    public Exception? ListFailure { get; set; }

    /// <summary>How many times the listing was requested, throwing or not.</summary>
    public int ListCallCount { get; private set; }

    public Task<IReadOnlyList<ExistingReviewComment>> ListExistingReviewCommentsAsync(
        ReviewCommentTarget target,
        CancellationToken cancellationToken
    )
    {
        ListCallCount++;
        return ListFailure is { } failure
            ? Task.FromException<IReadOnlyList<ExistingReviewComment>>(failure)
            : Task.FromResult<IReadOnlyList<ExistingReviewComment>>([.. ExistingComments]);
    }

    private Task<PostedComment> RecordRichPost(string idempotencyKey)
    {
        if (PostFailure is { } failure)
        {
            return Task.FromException<PostedComment>(failure);
        }

        PostedKeys.Add(idempotencyKey);
        var comment = RichReceipt ?? new PostedComment($"resp-{PostCount}");
        _byKey[idempotencyKey] = comment;
        return Task.FromResult(comment);
    }
}
