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

    /// <summary>Comments returned by <see cref="ListExistingReviewCommentsAsync"/> — seed to simulate a PR that
    /// already has prior review comments (the delta-awareness path).</summary>
    public List<ExistingReviewComment> ExistingComments { get; } = [];

    /// <summary>
    /// When set, <see cref="ListExistingReviewCommentsAsync"/> throws this instead of answering — the provider
    /// hiccup the executor's catch is written for.
    /// </summary>
    /// <remarks>
    /// Added because the failure path had NO seam and therefore no coverage: every test that reached the
    /// comment fetch got a successful empty list, so the catch block and the degraded brief it produces were
    /// unreachable from the test suite. An untestable branch is not a safe branch — it is one whose behaviour
    /// nobody has ever observed. Deliberately a nullable exception rather than a bool: the test says WHICH
    /// failure it is simulating, and an exception type that the executor's <c>when</c> filter excludes (say
    /// <see cref="OperationCanceledException"/>) can be scripted too, so the filter itself is testable.
    /// </remarks>
    public Exception? ListFailure { get; set; }

    /// <summary>
    /// How many times <see cref="ListExistingReviewCommentsAsync"/> was called — including the calls that
    /// threw. A test asserting on a degraded brief needs to know the fetch was actually ATTEMPTED, otherwise a
    /// brief that degraded for some unrelated reason reads as coverage of this one.
    /// </summary>
    public int ListCallCount { get; private set; }

    public Task<IReadOnlyList<ExistingReviewComment>> ListExistingReviewCommentsAsync(
        ReviewCommentTarget target,
        CancellationToken cancellationToken)
    {
        ListCallCount++;
        return ListFailure is { } failure
            ? Task.FromException<IReadOnlyList<ExistingReviewComment>>(failure)
            : Task.FromResult<IReadOnlyList<ExistingReviewComment>>([.. ExistingComments]);
    }
}
