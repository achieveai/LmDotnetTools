using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// Task #81, Command A — <c>--list-candidate-prs</c>. A read-only, bounded proposal of open PRs across
/// the configured ENABLED repos, for an operator to eyeball before approving one for <see cref="RunSinglePrCommand"/>.
/// Deliberately takes no <see cref="Persistence.ReviewStore"/> or <see cref="PrOrchestrator"/> — a
/// compile-time proof this path cannot create a run, allocate a slot, or touch the poll cursor. Every
/// poll is a fresh resync (<see cref="PrPollRequest.Cursor"/> is always <c>null</c>): there is no store to
/// have read a persisted cursor from in the first place.
/// </summary>
internal static class ListCandidatePrsCommand
{
    /// <summary>Hard cap on candidates returned, summed across every enabled repo — an operator triage
    /// list, not a full inventory.</summary>
    public const int MaxCandidates = 10;

    public static async Task<IReadOnlyList<CandidatePr>> RunAsync(
        CodeReviewDaemonOptions options,
        IReadOnlyList<IPrProvider> providers,
        ILogger logger,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null
    )
    {
        var time = timeProvider ?? TimeProvider.System;
        var targets = PrPollTargetBuilder.Build(options, logger);
        var candidates = new List<CandidatePr>();

        foreach (var target in targets)
        {
            if (candidates.Count >= MaxCandidates)
            {
                break;
            }

            var provider = PrPollTargetBuilder.ResolveProvider(providers, target);
            if (provider is null)
            {
                logger.LogWarning(
                    "No IPrProvider registered for '{Provider}'; skipping candidate listing for '{RepoKey}'.",
                    target.Provider,
                    target.Repo.DisplayName
                );
                continue;
            }

            // Same recency cutoff/filter semantics as PrPollingService (second security-review round, item
            // 2): a candidate list that proposes a PR the poller would have skipped as too old is misleading.
            var cutoff = PrRecencyFilter.ComputeCutoff(target.MaxPrAgeDays, time);
            var request = new PrPollRequest
            {
                Repo = target.Repo,
                Scope = target.Scope,
                Cursor = null,
                RecencyCutoff = cutoff,
            };
            var page = await provider.ListOpenPullRequestsAsync(request, cancellationToken).ConfigureAwait(false);
            var kept = PrRecencyFilter.Apply(cutoff, page.PullRequests);
            if (kept.Count < page.PullRequests.Count)
            {
                logger.LogInformation(
                    "Recency filter ({Days}d) on {Scope}: proposing {Kept} of {Total} open PR(s); {Skipped} outside the window.",
                    target.MaxPrAgeDays,
                    target.Scope,
                    kept.Count,
                    page.PullRequests.Count,
                    page.PullRequests.Count - kept.Count
                );
            }

            foreach (var pr in kept)
            {
                if (candidates.Count >= MaxCandidates)
                {
                    break;
                }

                candidates.Add(
                    new CandidatePr
                    {
                        Provider = target.Provider,
                        RepoKey = target.Repo.DisplayName,
                        PrId = pr.PrId,
                        Title = pr.Title,
                        Author = pr.Author,
                        LifecycleState = pr.LifecycleState,
                        HeadSha = pr.HeadSha,
                        BaseSha = pr.BaseSha,
                        TriggerWatermark = pr.TriggerWatermark,
                    }
                );
            }
        }

        return candidates;
    }
}

/// <summary>
/// One candidate an operator can approve for <see cref="RunSinglePrCommand"/> — everything needed to
/// judge the PR, and nothing else (no tokens, no model/feature-flag configuration).
/// </summary>
internal sealed record CandidatePr
{
    public required string Provider { get; init; }

    /// <summary>The configured <c>EnabledRepos</c> key this candidate was found under (<see cref="RepoIdentity.DisplayName"/>).</summary>
    public required string RepoKey { get; init; }

    public required string PrId { get; init; }

    public string? Title { get; init; }

    public string? Author { get; init; }

    public required PrLifecycleState LifecycleState { get; init; }

    public required string HeadSha { get; init; }

    public required string BaseSha { get; init; }

    public required string TriggerWatermark { get; init; }
}
