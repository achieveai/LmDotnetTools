using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

internal sealed class ReadyPrProvider(string provider = "github") : IPrProvider
{
    public string Provider { get; } = provider;

    public Task<PullRequestPage> ListOpenPullRequestsAsync(
        PrPollRequest request,
        CancellationToken cancellationToken
    ) => throw new NotSupportedException();

    public Task<PrStatus> GetPrStateAsync(RepoIdentity repo, string prId, CancellationToken cancellationToken) =>
        Task.FromResult(new PrStatus(PrLifecycle.Open, PrDraftState.Ready));
}
