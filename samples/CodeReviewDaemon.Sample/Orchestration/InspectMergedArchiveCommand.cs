using System.Globalization;
using System.Text.Json.Nodes;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace.Git;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>Inspects an original merged-review branch without admitting a run or starting a review host.</summary>
internal static class InspectMergedArchiveCommand
{
    public static async Task<JsonObject> RunAsync(
        ScopedCommandLineResult command,
        CodeReviewDaemonOptions options,
        GitRunner git,
        string retentionRoot,
        IReadOnlyList<IPrProvider> providers,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        if (options.EnablePrPolling || options.EnableCommentPosting || options.EnableGitPush)
            throw new InvalidOperationException("Archive inspection requires polling and broad publication disabled.");
        var target = PrPollTargetBuilder
            .Build(options, logger)
            .Where(value =>
                string.Equals(value.Repo.DisplayName, command.RunPrRepoKey, StringComparison.OrdinalIgnoreCase)
                || string.Equals(value.Repo.NormalizedKey, command.RunPrRepoKey, StringComparison.OrdinalIgnoreCase)
            )
            .GroupBy(value => value.Repo.NormalizedKey)
            .Select(group => group.First())
            .Single();
        if (
            PrPollTargetBuilder
                .Build(options, logger)
                .Select(value => value.Repo)
                .DistinctBy(repo => repo.NormalizedKey, StringComparer.OrdinalIgnoreCase)
                .Count(repo =>
                    string.Equals(
                        ReviewBranchManager.RepoSlug(repo),
                        ReviewBranchManager.RepoSlug(target.Repo),
                        StringComparison.Ordinal
                    )
                ) != 1
        )
            throw new InvalidDataException("The artifact branch slug is ambiguous across configured repositories.");
        var branch = ReviewBranchManager.BuildReviewBranchName(
            target.Repo,
            int.Parse(command.RunPrId!, CultureInfo.InvariantCulture)
        );
        var tip = command.ArtifactTipSha!;
        var expectedRef = $"{tip}\trefs/heads/{branch}";
        var remote = await git.RunAsync(
            ["ls-remote", "--heads", "--", options.ResolvedStoreUrl!, $"refs/heads/{branch}"],
            AppContext.BaseDirectory,
            cancellationToken
        );
        if (!remote.Succeeded || !string.Equals(remote.Stdout.Trim(), expectedRef, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The exact original artifact branch is unavailable or has changed.");
        var provider = providers.Single(value => value.Provider == target.Provider);
        var pr =
            await provider.GetPullRequestAsync(target.Repo, command.RunPrId!, cancellationToken)
            ?? throw new InvalidDataException("The merged source PR is unavailable.");
        if (
            pr.PrId != command.RunPrId
            || pr.LifecycleState != PrLifecycleState.Merged
            || !string.Equals(pr.MergeCommitSha, command.LandedSha, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(pr.TargetRefName)
        )
            throw new InvalidDataException("The source PR does not match the exact merged request.");

        var prefix = $"PRs/{branch["review/".Length..]}/";
        var tree = await git.RunAsync(
            ["ls-tree", "-r", "-l", "-z", "--full-tree", tip, "--", prefix],
            retentionRoot,
            cancellationToken
        );
        if (!tree.Succeeded)
            throw new InvalidDataException("The original branch tip is not available in the host retention checkout.");
        var entries = tree.Stdout.TrimEnd('\r', '\n').Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (entries.Length is 0 or > 64)
            throw new InvalidDataException("Original review files are missing or exceed the archive bound.");
        long totalBytes = 0;
        var hasReview = false;
        var contextPaths = new List<string>();
        foreach (var entry in entries)
        {
            var tab = entry.IndexOf('\t');
            if (tab < 0)
                throw new InvalidDataException("Original review tree has a malformed entry.");
            var fields = entry[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var path = entry[(tab + 1)..];
            if (
                fields.Length != 4
                || fields[0] is not ("100644" or "100755")
                || fields[1] != "blob"
                || fields[2].Length != 40
                || !fields[2].All(Uri.IsHexDigit)
                || !long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var bytes)
                || bytes is < 0 or > 262144
                || !path.StartsWith(prefix, StringComparison.Ordinal)
                || path.Length > 512
                || path == prefix + "archive-notice.md"
                || path.Split('/')
                    .Any(segment =>
                        segment.Length == 0
                        || segment is "." or ".." or ".git"
                        || !segment.All(character =>
                            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'
                        )
                    )
                || !(path.EndsWith(".md", StringComparison.Ordinal) || path.EndsWith(".json", StringComparison.Ordinal))
            )
                throw new InvalidDataException("Original review tree contains an unsupported file.");
            totalBytes += bytes;
            hasReview |= path == prefix + "review.md";
            if (
                path.StartsWith(prefix + "PR_Context_", StringComparison.Ordinal)
                && path.EndsWith(".md", StringComparison.Ordinal)
            )
                contextPaths.Add(path);
        }
        if (!hasReview || totalBytes > 1048576)
            throw new InvalidDataException("Original review files are incomplete or exceed the archive bound.");

        var references = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in contextPaths)
        {
            var content = await git.RunAsync(["show", $"{tip}:{path}"], retentionRoot, cancellationToken);
            if (!content.Succeeded)
                throw new InvalidDataException("The original context metadata cannot be read.");
            foreach (var line in content.Stdout.Split('\n'))
            {
                var marker = "| Hosted conversation | `";
                if (!line.StartsWith(marker, StringComparison.Ordinal))
                    continue;
                var value = line[marker.Length..].Split('`', 2)[0];
                if (value.StartsWith("thread-", StringComparison.Ordinal) && value.Length <= 128)
                    references.Add(value);
            }
        }
        var after = await git.RunAsync(
            ["ls-remote", "--heads", "--", options.ResolvedStoreUrl!, $"refs/heads/{branch}"],
            AppContext.BaseDirectory,
            cancellationToken
        );
        if (!after.Succeeded || !string.Equals(after.Stdout.Trim(), expectedRef, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The original artifact branch changed during inspection.");
        return new JsonObject
        {
            ["Outcome"] = "inspect_only",
            ["Repository"] = target.Repo.NormalizedKey,
            ["PrId"] = pr.PrId,
            ["ArtifactBranch"] = branch,
            ["ArtifactTipSha"] = tip,
            ["HeadSha"] = pr.HeadSha,
            ["BaseSha"] = pr.BaseSha,
            ["LandedSha"] = pr.MergeCommitSha,
            ["ParentSha"] = command.ParentSha,
            ["ParentVerified"] = false,
            ["OriginalFileCount"] = entries.Length,
            ["OriginalBytes"] = totalBytes,
            ["HostedConversationIds"] = new JsonArray([
                .. references.Order(StringComparer.Ordinal).Select(id => (JsonNode)id),
            ]),
            ["ConversationAvailability"] = "unknown",
            ["ArchiveEligible"] = false,
            ["Notice"] =
                "Original conversation availability and historical host ownership are not established. No run was admitted or artifacts published.",
        };
    }
}
