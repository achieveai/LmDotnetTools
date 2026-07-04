using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// Turns the operator allow-list (<see cref="CodeReviewDaemonOptions.EnabledRepos"/>) into the
/// <see cref="PrPollTarget"/> set the <see cref="PrPollingService"/> watches — the single consumer of
/// that config. An <c>owner/repo</c> (2 segments) entry is a GitHub repo; an <c>org/project/repo</c>
/// (3 segments) entry is an ADO repo. ADO targets are emitted only when <c>EnableAdoProvider</c> is set
/// (otherwise no <c>ado</c> provider is registered to serve them). The poll <see cref="PrPollTarget.Mode"/>
/// follows <c>EnableCommentPosting</c> so the safe default stays collect-only. Malformed entries are
/// logged and skipped rather than failing daemon boot.
/// </summary>
internal static class PrPollTargetBuilder
{
    public static IReadOnlyList<PrPollTarget> Build(CodeReviewDaemonOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        var mode = options.EnableCommentPosting ? "post" : "collect-only";
        var targets = new List<PrPollTarget>();

        foreach (var entry in options.EnabledRepos)
        {
            var segments = (entry ?? string.Empty)
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var target = segments.Length switch
            {
                2 => GitHubTarget(segments, mode, options.ReviewModelId),
                3 => AdoTarget(segments, mode, options.ReviewModelId, options.EnableAdoProvider, logger),
                _ => null,
            };

            if (target is not null)
            {
                targets.Add(target);
            }
            else if (segments.Length is not (2 or 3))
            {
                logger.LogWarning(
                    "Ignoring malformed EnabledRepos entry '{Entry}': expected 'owner/repo' or 'org/project/repo'.",
                    entry);
            }
        }

        return targets;
    }

    private static PrPollTarget GitHubTarget(string[] segments, string mode, string? modelId) =>
        new()
        {
            Provider = "github",
            Repo = new RepoIdentity
            {
                Provider = "github",
                OrgOrOwner = segments[0],
                RepoName = segments[1],
            },
            Scope = $"{segments[0]}/{segments[1]}:open-prs",
            Mode = mode,
            ModelId = modelId,
        };

    private static PrPollTarget? AdoTarget(string[] segments, string mode, string? modelId, bool enableAdoProvider, ILogger logger)
    {
        if (!enableAdoProvider)
        {
            logger.LogWarning(
                "Skipping ADO repo '{Repo}' because EnableAdoProvider is off; no 'ado' provider is registered.",
                string.Join('/', segments));
            return null;
        }

        return new PrPollTarget
        {
            Provider = "ado",
            Repo = new RepoIdentity
            {
                Provider = "azure-devops",
                OrgOrOwner = segments[0],
                Project = segments[1],
                RepoName = segments[2],
            },
            Scope = $"{segments[0]}/{segments[1]}/{segments[2]}:active-prs",
            Mode = mode,
            ModelId = modelId,
        };
    }
}
