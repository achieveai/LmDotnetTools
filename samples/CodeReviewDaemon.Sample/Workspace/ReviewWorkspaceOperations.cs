using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Workspace;

/// <summary>Shared deterministic preparation, containment policy and exact Git evidence extracted from the legacy executor.</summary>
internal sealed class ReviewWorkspaceOperations(CodeReviewDaemonOptions options, ILogger logger)
{
    private const string ReviewBotDefaultBranch = "main";
    private readonly CodeReviewDaemonOptions _options = options;
    private readonly ILogger _logger = logger;
    private readonly SlotPrepareFailureEscalator _slotPrepareFailureEscalator = new();

    private static string PosixJoin(string root, string relative) => $"{root.TrimEnd('/')}/{relative.Trim('/')}";

    public bool AllowsCrossRepoCoLocation(ReviewRun run, RepoIdentity repo) => !run.IsForkPr && !run.IsTargetRepoPublic;

    public static string TargetRemoteUrl(RepoIdentity repo, string provider) =>
        GitRemoteUrl.CloneUrlFor(provider, repo.OrgOrOwner, repo.Project, repo.RepoName);

    public async Task<PreparedCheckout> PrepareWithRecoveryAsync(
        IReviewSlotPreparer preparer,
        ReviewRun run,
        string storeRoot,
        string scratchRoot,
        string storeUrl,
        string submoduleRelPath,
        string branch,
        string notesRelPath,
        OperationPolicy policy,
        CancellationToken cancellationToken
    )
    {
        Task<PreparedCheckout> PrepareOnceAsync() =>
            preparer.PrepareAsync(
                run,
                storeRoot,
                scratchRoot,
                storeUrl,
                submoduleRelPath,
                branch,
                ReviewBotDefaultBranch,
                notesRelPath,
                policy,
                cancellationToken
            );

        async Task<PreparedCheckout> RecloneAndRetryOnceAsync()
        {
            await preparer.RecloneStoreAsync(storeRoot, storeUrl, cancellationToken).ConfigureAwait(false);
            var retried = await PrepareOnceAsync().ConfigureAwait(false);
            _slotPrepareFailureEscalator.RecordSuccess(storeRoot);
            return retried;
        }

        try
        {
            var prepared = await PrepareOnceAsync().ConfigureAwait(false);
            _slotPrepareFailureEscalator.RecordSuccess(storeRoot);
            return prepared;
        }
        catch (Exception ex) when (ex is SlotNeedsRecloneException or SlotCorruptException)
        {
            _logger.LogWarning(
                ex,
                "Run {RunId}: pooled store {StoreRoot} is corrupt; re-cloning and retrying prepare once.",
                run.Id,
                storeRoot
            );
            return await RecloneAndRetryOnceAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
            when (ex is not (SlotAddressUnusableException or SlotProbeUnansweredException or OperationCanceledException)
            )
        {
            if (!_slotPrepareFailureEscalator.RecordFailureAndShouldEscalate(storeRoot, ex.Message))
            {
                throw;
            }

            _logger.LogWarning(
                ex,
                "Run {RunId}: pooled store {StoreRoot} failed prepare identically {Count} times in a row with a "
                    + "failure the classifier does not recognize as corruption; escalating to a re-clone "
                    + "REGARDLESS of classification so an unmodelled git failure shape cannot wedge the slot "
                    + "forever (issue #582).",
                run.Id,
                storeRoot,
                SlotPrepareFailureEscalator.MaxConsecutiveFailures
            );
            return await RecloneAndRetryOnceAsync().ConfigureAwait(false);
        }
    }

    public async Task<string?> ResolveStoreSubmodulePathAsync(
        ISandboxFileSystem fileSystem,
        string storeRoot,
        RepoIdentity repo,
        string provider
    )
    {
        var gitmodules = await ReadGitmodulesAsync(fileSystem, storeRoot, CancellationToken.None).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(gitmodules))
        {
            return null;
        }

        var targetUrl = GitRemoteUrl.Parse(TargetRemoteUrl(repo, provider));
        var entry = GitModulesParser.Parse(gitmodules).FirstOrDefault(e => SubmoduleTargetsRepo(e.Url, targetUrl));
        return entry?.Path;
    }

    public async Task<string?> ReadGitmodulesAsync(
        ISandboxFileSystem fileSystem,
        string storeRoot,
        CancellationToken cancellationToken
    )
    {
        var path = PosixJoin(storeRoot, ".gitmodules");
        var read = await fileSystem
            .ReadFileAsync(path, SandboxReadLimits.RepositoryFileBytes, cancellationToken)
            .ConfigureAwait(false);
        if (read.TooLarge)
        {
            _logger.LogWarning(
                "'.gitmodules' at '{Path}' exceeds the {Limit}-byte read limit; treating the store as "
                    + "declaring no submodule for this repository.",
                path,
                SandboxReadLimits.RepositoryFileBytes
            );
        }

        return read.Content;
    }

    public async Task<string> BuildFileManifestAsync(
        GitRunner git,
        string targetDir,
        CancellationToken cancellationToken
    )
    {
        var lsFiles = await git.RunAsync(["-C", targetDir, "ls-files"], targetDir, cancellationToken)
            .ConfigureAwait(false);
        if (!lsFiles.Succeeded)
        {
            _logger.LogWarning(
                "Target file manifest unavailable (git ls-files exit {ExitCode}): {Stderr}",
                lsFiles.ExitCode,
                lsFiles.Stderr
            );
            return string.Empty;
        }

        // A record listing, and trimmed of line terminators ONLY, for both of the reasons spelled out on the
        // changed-path listing below: the agent is told to Read these paths verbatim, so a record the cap
        // halved names a file that does not exist, and a blanket Trim() would rewrite the first and last
        // records of the manifest into paths git never reported.
        return _options.Limits.CapRecordListing(lsFiles.Stdout.Trim('\n', '\r'));
    }

    public async Task<string> BuildChangedPathsAsync(
        GitRunner git,
        string targetDir,
        ReviewRun run,
        CancellationToken cancellationToken
    )
    {
        var nameOnly = await git.RunAsync(
                ["-C", targetDir, "diff", "--name-only", "--no-renames", $"{run.BaseSha}...{run.HeadSha}"],
                targetDir,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (!nameOnly.Succeeded)
        {
            _logger.LogWarning(
                "Run {RunId}: changed-path listing unavailable (git diff --name-only exit {ExitCode}): {Stderr}; "
                    + "prior-knowledge ranking falls back to the bounded diff headers.",
                run.Id,
                nameOnly.ExitCode,
                nameOnly.Stderr
            );
            return string.Empty;
        }

        // Trimmed of line terminators ONLY. git allows a filename to begin or end with a space and does not
        // quote for one, so a blanket Trim() here would rewrite the first and last records into paths git
        // never reported — and the ranking downstream would then fail to match the very files they name.
        //
        // Capped as a RECORD LISTING rather than as a generic payload: this is the one artifact here that is
        // strictly one path per line, so it is the one where cutting between records is worth what it costs.
        return _options.Limits.CapRecordListing(nameOnly.Stdout.Trim('\n', '\r'));
    }

    public string DescribeUncomparableOrThrow(ReviewRun run, MergeBaseOutcome mergeBase, SandboxCommandResult diff)
    {
        if (mergeBase != MergeBaseOutcome.UnrelatedHistories)
        {
            throw new InvalidOperationException(
                $"Fetching the diff for run {run.Id} failed (exit {diff.ExitCode}): {diff.Stderr}"
            );
        }

        // Warning, and carrying the merge-base outcome explicitly: this is the log line that distinguishes
        // "the daemon decided the commits are uncomparable" from "the diff blew up and the daemon threw",
        // which are one keystroke apart in this method and produce completely different run outcomes.
        _logger.LogWarning(
            "Run {RunId}: git could not diff {BaseSha}...{HeadSha} (exit {ExitCode}: {Stderr}) and the merge "
                + "base search ended in {MergeBase}, so the two commits provably share no ancestor. Recording "
                + "an uncomparable-commits verdict instead of failing the stage — no fetch depth and no retry "
                + "can create an ancestor that does not exist.",
            run.Id,
            run.BaseSha,
            run.HeadSha,
            diff.ExitCode,
            FirstLine(diff.Stderr),
            mergeBase
        );

        return $"`{run.BaseSha}` and `{run.HeadSha}` share no common ancestor. The daemon walked both "
            + "histories back to their root commits and found no merge base, so there is no `base...head` "
            + $"range for git to diff (it reported: `{FirstLine(diff.Stderr)}`).";
    }

    public IReadOnlyList<SubmoduleAllowRule> BuildStoreSubmoduleAllowList(ReviewRun run, RepoIdentity repo)
    {
        // The reviewed repo's own submodule + the shared Contracts layer are always allow-listed. The host
        // and repo-path shape are provider-specific — GitHub is /{owner}/{repo} on github.com, Azure DevOps
        // is /{org}/{project}/_git/{repo} on dev.azure.com — and both are spelled by GitRemoteUrl, the SAME
        // builder TargetRemoteUrl uses, so the rule matches the exact URL SubmoduleTargetsRepo resolves.
        // Not "mirroring" it (issue #478): a mirror is two interpolations someone has to keep in step, and
        // the day one of them learns to encode a spaced Azure DevOps org while the other does not, this
        // security matcher stops matching legitimately allow-listed repos with no error anywhere.
        var isAdo = GitRemoteUrl.IsAzureDevOps(repo.Provider);
        var host = GitRemoteUrl.HostFor(repo.Provider);

        // Two entry points, because two kinds of name arrive here. The reviewed repo's own name comes from
        // the repo IDENTITY in human form (the same value the REST callers pass), so it is encoded — that is
        // what makes this rule the path TargetRemoteUrl actually clones. Every other name is a configured
        // ReviewedRepoSubmodules/CrossRepoSiblings entry, whose documented contract is the URL's own spelling
        // ("Microsoft%20Orleans", not "Microsoft Orleans"), so it goes in verbatim; re-encoding one would
        // make it %2520 and silently drop it off the allow-list.
        string RepoPath(string urlFormName) =>
            GitRemoteUrl.RepoPathForUrlSegment(repo.Provider, repo.OrgOrOwner, repo.Project, urlFormName);

        // ...and because those names go in VERBATIM, a raw character the URL form must percent-encode — a
        // space above all — builds a rule that can never match the (never-decoded) path the parser hands the
        // matcher. That failure is SILENT: the submodule is simply never allowed, exactly as if it had not
        // been configured. Say so, or the only symptom is a dependency that mysteriously never initializes.
        void WarnIfNotUrlForm(string setting, string configuredName)
        {
            foreach (var segment in configuredName.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                // Escaping the segment must be a no-op once the escape INTRODUCER is discounted, so an
                // already-correct "Microsoft%20Orleans" (which would otherwise round-trip to %2520) is quiet.
                var urlForm = Uri.EscapeDataString(segment).Replace("%25", "%", StringComparison.Ordinal);
                if (!string.Equals(urlForm, segment, StringComparison.Ordinal))
                {
                    _logger.LogWarning(
                        "{Setting} entry '{Name}' is not in URL form: segment '{Segment}' contains a character "
                            + "that must be percent-encoded ('{UrlForm}'). Configured names are matched verbatim "
                            + "against the undecoded request path, so this entry will never match and the "
                            + "submodule stays denied.",
                        setting,
                        configuredName,
                        segment,
                        urlForm
                    );
                    return;
                }
            }
        }

        var rules = new List<SubmoduleAllowRule>
        {
            new(host, GitRemoteUrl.RepoPathFor(repo.Provider, repo.OrgOrOwner, repo.Project, repo.RepoName)),
            new(host, RepoPath("Contracts")),
        };

        // The reviewed repo's OWN first-party submodules (its direct dependencies) are allow-listed
        // UNCONDITIONALLY — unlike CrossRepoSiblings below, these are the target's own dependency graph
        // (needed to build/understand it), not store-level siblings, so the fork/public confidentiality gate
        // does not apply. Still fail-closed: only the explicit configured names are permitted; a submodule an
        // attacker adds or repoints to any other name/host is denied.
        foreach (var submodule in _options.ReviewedRepoSubmodules)
        {
            WarnIfNotUrlForm(nameof(CodeReviewDaemonOptions.ReviewedRepoSubmodules), submodule);
            rules.Add(new SubmoduleAllowRule(host, RepoPath(submodule)));
        }

        if (AllowsCrossRepoCoLocation(run, repo))
        {
            foreach (var sibling in _options.CrossRepoSiblings)
            {
                // GitHub siblings are configured as owner/repo (an absolute path, in the URL's own spelling);
                // ADO siblings are a bare name resolving under the same org/project as the reviewed repo.
                WarnIfNotUrlForm(nameof(CodeReviewDaemonOptions.CrossRepoSiblings), sibling);
                rules.Add(new SubmoduleAllowRule(host, isAdo ? RepoPath(sibling) : $"/{sibling}"));
            }
        }

        return rules;
    }

    public static bool SubmoduleTargetsRepo(string submoduleUrl, GitRemoteUrl targetUrl)
    {
        var url = GitRemoteUrl.Parse(submoduleUrl);
        return string.Equals(url.Host, targetUrl.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                url.RepoPath.TrimEnd('/'),
                targetUrl.RepoPath.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase
            );
    }

    public static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "no error output";
        }

        var trimmed = text.Trim();
        var newline = trimmed.IndexOf('\n');
        return newline < 0 ? trimmed : trimmed[..newline].TrimEnd('\r');
    }
}
