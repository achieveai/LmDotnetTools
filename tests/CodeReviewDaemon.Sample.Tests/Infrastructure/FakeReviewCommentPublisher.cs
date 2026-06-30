using CodeReviewDaemon.Sample.Orchestration;

namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>
/// In-memory <see cref="IReviewCommentPublisher"/>. Records every post (so a test can assert "posted
/// exactly once"), returns a deterministic response id per idempotency key, and supports
/// <see cref="SeedExistingComment"/> to simulate a comment that already exists provider-side — the case
/// the <see cref="ReviewPoster"/> backstop scan must catch to avoid a double post.
/// </summary>
internal sealed class FakeReviewCommentPublisher : IReviewCommentPublisher
{
    private readonly Dictionary<string, PostedComment> _byKey = new(StringComparer.Ordinal);

    public FakeReviewCommentPublisher(string provider = "github") => Provider = provider;

    public string Provider { get; }

    /// <summary>Idempotency keys passed to <see cref="PostReviewCommentAsync"/>, in call order.</summary>
    public List<string> PostedKeys { get; } = [];

    /// <summary>Bodies passed to <see cref="PostReviewCommentAsync"/>, in call order.</summary>
    public List<string> PostedBodies { get; } = [];

    /// <summary>How many real posts happened (the exactly-once assertion target).</summary>
    public int PostCount => PostedKeys.Count;

    /// <summary>
    /// Pretend a comment for <paramref name="idempotencyKey"/> already exists provider-side (e.g. a prior
    /// attempt posted then crashed). <see cref="FindPostedCommentAsync"/> will return it without it
    /// counting as a post.
    /// </summary>
    public void SeedExistingComment(string idempotencyKey, string providerResponseId) =>
        _byKey[idempotencyKey] = new PostedComment(providerResponseId);

    public Task<PostedComment?> FindPostedCommentAsync(
        ReviewCommentTarget target,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        Task.FromResult(_byKey.TryGetValue(idempotencyKey, out var comment) ? comment : null);

    public Task<PostedComment> PostReviewCommentAsync(
        ReviewCommentTarget target,
        string idempotencyKey,
        string body,
        CancellationToken cancellationToken)
    {
        PostedKeys.Add(idempotencyKey);
        PostedBodies.Add(body);
        var comment = new PostedComment($"resp-{PostCount}");
        _byKey[idempotencyKey] = comment;
        return Task.FromResult(comment);
    }
}
