using System.Globalization;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace.Git;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>Inventories existing remote review branches without admitting work or changing a checkout.</summary>
internal static class ListReviewBranchesCommand
{
    public static async Task<IReadOnlyList<ReviewBranchInventoryEntry>> RunAsync(
        CodeReviewDaemonOptions options,
        GitRunner git,
        IReadOnlyList<IPrProvider> providers,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        var storeUrl = options.ResolvedStoreUrl;
        if (string.IsNullOrWhiteSpace(storeUrl))
            throw new InvalidOperationException("A configured review store is required for branch inventory.");

        var remote = await git.RunAsync(
                ["ls-remote", "--heads", "--", storeUrl, "refs/heads/review/*"],
                AppContext.BaseDirectory,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (!remote.Succeeded)
            throw new InvalidOperationException(
                "Review branch inventory failed; historical PR discovery was not attempted."
            );

        var targets = PrPollTargetBuilder.Build(options, logger);
        var entries = new List<ReviewBranchInventoryEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (
            var line in remote.Stdout.Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
        )
        {
            var fields = line.Split('\t');
            const string prefix = "refs/heads/";
            if (
                fields.Length != 2
                || fields[0].Length != 40
                || !fields[0].All(Uri.IsHexDigit)
                || !fields[1].StartsWith(prefix + "review/", StringComparison.Ordinal)
                || !seen.Add(fields[1])
            )
                throw new InvalidDataException("Review branch inventory returned malformed or duplicate refs.");

            var branch = fields[1][prefix.Length..];
            var tip = fields[0];
            var isCurrentScheme = ReviewBranchManager.TryParseReviewBranch(branch, out var slug, out var prNumber);
            if (
                (!isCurrentScheme && !ReviewBranchManager.TryParseLegacyReviewBranch(branch, out slug, out prNumber))
                || prNumber <= 0
            )
            {
                entries.Add(new(branch, tip, null, null, null, "unrecognized_branch"));
                continue;
            }

            var matches = targets
                .Where(target =>
                    isCurrentScheme
                        ? string.Equals(ReviewBranchManager.RepoSlug(target.Repo), slug, StringComparison.Ordinal)
                        : string.Equals(ReviewBranchManager.LegacyRepoSlug(target.Repo), slug, StringComparison.Ordinal)
                            && branch.StartsWith(
                                $"review/{RepoIdentity.ToPublisherNamespace(target.Repo.Provider)}/",
                                StringComparison.Ordinal
                            )
                )
                .GroupBy(target => target.Repo.NormalizedKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            var prId = prNumber.ToString(CultureInfo.InvariantCulture);
            if (matches.Length != 1)
            {
                entries.Add(
                    new(
                        branch,
                        tip,
                        null,
                        prId,
                        null,
                        matches.Length == 0 ? "unconfigured_repository" : "ambiguous_repository"
                    )
                );
                continue;
            }

            var target = matches[0];
            // A matching slug is a discovery hint, not authorization to merge or delete this ref.
            var expected = ReviewBranchManager.BuildReviewBranchName(target.Repo, prNumber);
            if (
                !string.Equals(branch, expected, StringComparison.Ordinal)
                && !branch.StartsWith(
                    $"review/{RepoIdentity.ToPublisherNamespace(target.Repo.Provider)}/",
                    StringComparison.Ordinal
                )
            )
            {
                entries.Add(new(branch, tip, target.Repo.DisplayName, prId, null, "identity_requires_reconciliation"));
                continue;
            }
            try
            {
                var provider =
                    providers.SingleOrDefault(provider =>
                        string.Equals(provider.Provider, target.Provider, StringComparison.OrdinalIgnoreCase)
                    ) ?? throw new InvalidOperationException("No unique source provider is configured.");
                var source = await provider
                    .GetPullRequestAsync(target.Repo, prId, cancellationToken)
                    .ConfigureAwait(false);
                if (source is null || !string.Equals(source.PrId, prId, StringComparison.Ordinal))
                {
                    entries.Add(new(branch, tip, target.Repo.DisplayName, prId, null, "source_identity_unavailable"));
                    continue;
                }
                var lifecycle = source.LifecycleState switch
                {
                    PrLifecycleState.Open => "Open",
                    PrLifecycleState.Merged => "Merged",
                    PrLifecycleState.Abandoned => "Abandoned",
                    _ => null,
                };
                entries.Add(
                    new(
                        branch,
                        tip,
                        target.Repo.DisplayName,
                        prId,
                        lifecycle,
                        lifecycle is null ? "lifecycle_unavailable" : "discovered"
                    )
                    {
                        RepositoryKey = target.Repo.NormalizedKey,
                        SourceCreatedAt = source.CreatedAt,
                        SourceClosedAt = source.ClosedAt,
                        TargetRefName = source.TargetRefName,
                        SourceHeadSha = source.HeadSha,
                        SourceBaseSha = source.BaseSha,
                        MergeCommitSha = source.MergeCommitSha,
                        ObservedAt = DateTimeOffset.UtcNow,
                    }
                );
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogWarning(
                    "Could not determine lifecycle for {Repository} PR {PrId} ({ErrorType}).",
                    target.Repo.DisplayName,
                    prId,
                    error.GetType().Name
                );
                entries.Add(new(branch, tip, target.Repo.DisplayName, prId, null, "lifecycle_unavailable"));
            }
        }
        return
        [
            .. entries
                .OrderBy(entry => entry.SourceClosedAt ?? DateTimeOffset.MaxValue)
                .ThenBy(entry => entry.SourceCreatedAt ?? DateTimeOffset.MaxValue)
                .ThenBy(entry => entry.Branch, StringComparer.Ordinal),
        ];
    }
}

internal sealed record ReviewBranchInventoryEntry(
    string Branch,
    string TipSha,
    string? Repository,
    string? PrId,
    string? Lifecycle,
    string Status
)
{
    public string? RepositoryKey { get; init; }
    public DateTimeOffset? SourceCreatedAt { get; init; }
    public DateTimeOffset? SourceClosedAt { get; init; }
    public string? TargetRefName { get; init; }
    public string? SourceHeadSha { get; init; }
    public string? SourceBaseSha { get; init; }
    public string? MergeCommitSha { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }

    /// <summary>Terminal and unresolved branches must be dispositioned before new PR intake.</summary>
    public bool BlocksNewIntake => Status != "discovered" || Lifecycle != "Open";
}
