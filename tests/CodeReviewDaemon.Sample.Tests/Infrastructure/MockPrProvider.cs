using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>
/// Test double for <see cref="IPrProvider"/> that returns a pre-seeded page of open PRs and records the
/// cursor it was handed on each poll, so a test can assert resync-from-null and cursor advancement (§12)
/// without a real GitHub/ADO host. The real providers land in P4.4.
/// </summary>
internal sealed class MockPrProvider : IPrProvider, IReviewCommentReader
{
    private readonly List<PullRequestDescriptor> _pullRequests;
    private readonly OpaqueCursor _nextCursor;

    public MockPrProvider(string provider, IReadOnlyList<PullRequestDescriptor> pullRequests, OpaqueCursor nextCursor)
    {
        Provider = provider;
        _pullRequests = [.. pullRequests];
        _nextCursor = nextCursor;
    }

    public string Provider { get; }

    /// <summary>The cursor passed on the most recent <see cref="ListOpenPullRequestsAsync"/> call.</summary>
    public OpaqueCursor? LastRequestedCursor { get; private set; }

    /// <summary>The recency cutoff passed on the most recent <see cref="ListOpenPullRequestsAsync"/> call.</summary>
    public DateTimeOffset? LastRecencyCutoff { get; private set; }

    /// <summary>Number of times the provider was polled.</summary>
    public int CallCount { get; private set; }

    /// <summary>Lifecycle returned by <see cref="GetPrStateAsync"/>; defaults to Open, settable per test.
    /// <para>
    /// Unlike <see cref="CurrentHeadSha"/>, the default here is a POSITIVE answer, not an indeterminate one:
    /// a test that leaves it alone models a PR the host says is still open, so the delivery-boundary lifecycle
    /// guard (#430) is exercised and agrees rather than being skipped.
    /// </para>
    /// </summary>
    public PrLifecycle PrState { get; set; } = PrLifecycle.Open;

    /// <summary>Number of times <see cref="GetPrStateAsync"/> was called — the non-vacuity signal for the
    /// lifecycle guard, which passes for the wrong reason if the host is never asked.</summary>
    public int PrStateCalls { get; private set; }

    /// <summary>Head SHA returned by <see cref="GetCurrentHeadShaAsync"/>; null models a host whose payload
    /// carries no head for this PR.
    /// <para>
    /// NOTE for anyone driving a review through this double: the default of null is INDETERMINATE, which the
    /// head-currency guard deliberately lets through. So a test that does not set this exercises a review with
    /// the guard effectively disabled — fine when the guard is not what the test is about, and silently
    /// guard-blind when it is. Set it to the run's own head to model an unchanged PR.
    /// </para>
    /// </summary>
    public string? CurrentHeadSha { get; set; }

    public IReadOnlyList<ExistingReviewComment> ExistingComments { get; set; } = [];

    public Task<PullRequestPage> ListOpenPullRequestsAsync(PrPollRequest request, CancellationToken cancellationToken)
    {
        CallCount++;
        LastRequestedCursor = request.Cursor;
        LastRecencyCutoff = request.RecencyCutoff;
        return Task.FromResult(new PullRequestPage { PullRequests = _pullRequests, NextCursor = _nextCursor });
    }

    public Task<PrLifecycle> GetPrStateAsync(RepoIdentity repo, string prId, CancellationToken cancellationToken)
    {
        PrStateCalls++;
        return Task.FromResult(PrState);
    }

    /// <summary>Number of times <see cref="GetCurrentHeadShaAsync"/> was called — the non-vacuity signal for
    /// the head-currency guard, which passes for the wrong reason if the host is never asked.</summary>
    public int HeadShaCalls { get; private set; }

    public Task<string?> GetCurrentHeadShaAsync(RepoIdentity repo, string prId, CancellationToken cancellationToken)
    {
        HeadShaCalls++;
        return Task.FromResult(CurrentHeadSha);
    }

    public Task<IReadOnlyList<ExistingReviewComment>> ListExistingReviewCommentsAsync(
        ReviewCommentTarget target,
        CancellationToken cancellationToken
    ) => Task.FromResult(ExistingComments);

    /// <summary>Number of times <see cref="GetPullRequestAsync"/> was called — the non-vacuity signal for a
    /// test that a single-PR command actually re-read the host rather than trusting a seeded page.</summary>
    public int GetPullRequestCalls { get; private set; }

    /// <summary>
    /// Fresh single-PR lookup, modelled by scanning the seeded <c>pullRequests</c> for a matching
    /// <see cref="PullRequestDescriptor.PrId"/>. Absent-from-the-seed IS the not-found simulation — no
    /// separate flag is needed, mirroring what a real provider's 404 means.
    /// </summary>
    public Task<PullRequestDescriptor?> GetPullRequestAsync(
        RepoIdentity repo,
        string prId,
        CancellationToken cancellationToken
    )
    {
        GetPullRequestCalls++;
        return Task.FromResult(_pullRequests.FirstOrDefault(pr => pr.PrId == prId));
    }

    /// <summary>
    /// Mutates the seeded data in place so a test can simulate the PR's host-side state changing BETWEEN
    /// two <see cref="GetPullRequestAsync"/> calls (task #81, round 4, item 1 — the TOCTOU window between
    /// <c>RunSinglePrCommand.PrepareAsync</c>'s fresh read and <c>AdmitAndRunAsync</c>'s re-read). Replaces
    /// the entry sharing <paramref name="updated"/>'s <see cref="PullRequestDescriptor.PrId"/>, adding it if
    /// no such entry exists yet.
    /// </summary>
    public void ReplacePullRequest(PullRequestDescriptor updated)
    {
        var index = _pullRequests.FindIndex(pr => pr.PrId == updated.PrId);
        if (index >= 0)
        {
            _pullRequests[index] = updated;
        }
        else
        {
            _pullRequests.Add(updated);
        }
    }

    /// <summary>
    /// Removes the entry for <paramref name="prId"/> in place, modelling the PR disappearing from the host
    /// between a Prepare-time read and an Admit-time re-read — the same not-found simulation
    /// <see cref="GetPullRequestAsync"/> already relies on for an absent seed.
    /// </summary>
    public void RemovePullRequest(string prId) => _pullRequests.RemoveAll(pr => pr.PrId == prId);
}
