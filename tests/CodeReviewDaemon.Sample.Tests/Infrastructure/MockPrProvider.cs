using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>
/// Test double for <see cref="IPrProvider"/> that returns a pre-seeded page of open PRs and records the
/// cursor it was handed on each poll, so a test can assert resync-from-null and cursor advancement (§12)
/// without a real GitHub/ADO host. The real providers land in P4.4.
/// </summary>
internal sealed class MockPrProvider : IPrProvider
{
    private readonly IReadOnlyList<PullRequestDescriptor> _pullRequests;
    private readonly OpaqueCursor _nextCursor;

    public MockPrProvider(
        string provider,
        IReadOnlyList<PullRequestDescriptor> pullRequests,
        OpaqueCursor nextCursor)
    {
        Provider = provider;
        _pullRequests = pullRequests;
        _nextCursor = nextCursor;
    }

    public string Provider { get; }

    /// <summary>The cursor passed on the most recent <see cref="ListOpenPullRequestsAsync"/> call.</summary>
    public OpaqueCursor? LastRequestedCursor { get; private set; }

    /// <summary>The recency cutoff passed on the most recent <see cref="ListOpenPullRequestsAsync"/> call.</summary>
    public DateTimeOffset? LastRecencyCutoff { get; private set; }

    /// <summary>Number of times the provider was polled.</summary>
    public int CallCount { get; private set; }

    /// <summary>Lifecycle returned by <see cref="GetPrStateAsync"/>; defaults to Open, settable per test.</summary>
    public PrLifecycle PrState { get; set; } = PrLifecycle.Open;

    public Task<PullRequestPage> ListOpenPullRequestsAsync(PrPollRequest request, CancellationToken cancellationToken)
    {
        CallCount++;
        LastRequestedCursor = request.Cursor;
        LastRecencyCutoff = request.RecencyCutoff;
        return Task.FromResult(new PullRequestPage
        {
            PullRequests = _pullRequests,
            NextCursor = _nextCursor,
        });
    }

    public Task<PrLifecycle> GetPrStateAsync(RepoIdentity repo, string prId, CancellationToken cancellationToken) =>
        Task.FromResult(PrState);
}
