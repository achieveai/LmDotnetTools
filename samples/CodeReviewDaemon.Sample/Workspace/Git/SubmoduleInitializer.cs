using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Workspace.Git;

/// <summary>A submodule that was parsed but NOT initialized, with the reason it was refused.</summary>
/// <param name="Path">The submodule path relative to the superproject root.</param>
/// <param name="Url">The configured (unresolved) submodule URL.</param>
/// <param name="Reason">Audit-grade rationale (denied transport, off-allow-list, or fetch failure).</param>
internal sealed record SubmoduleDenied(string Path, string Url, string Reason);

/// <summary>The result of a selective submodule walk: what was initialized and what was refused.</summary>
internal sealed record SubmoduleInitOutcome(
    IReadOnlyList<string> InitializedPaths,
    IReadOnlyList<SubmoduleDenied> Denied
);

/// <summary>
/// Implements the plan §3 selective, recursive submodule init — never a blanket
/// <c>--init --recursive</c>. For each level it: (1) parses <c>.gitmodules</c> before any init;
/// (2) resolves + validates each URL against the transport and host/path allow-list
/// (<see cref="OperationPolicy"/>); (3) inits ONLY allowed submodules, one path at a time;
/// (4) recurses into each freshly checked-out submodule and repeats; (5) records every denied entry
/// and continues with the partial checkout (a denied submodule is absent and reported as context,
/// never a hard failure). Every git call goes through <see cref="GitRunner"/>, so the untrusted-code
/// hardening flags are always present.
/// </summary>
internal sealed class SubmoduleInitializer
{
    private const int MaxDepth = 10;

    private readonly GitRunner _git;
    private readonly ISandboxFileSystem _fileSystem;
    private readonly OperationPolicy _policy;
    private readonly string _provider;
    private readonly ILogger<SubmoduleInitializer> _logger;

    public SubmoduleInitializer(
        GitRunner git,
        ISandboxFileSystem fileSystem,
        OperationPolicy policy,
        string provider,
        ILogger<SubmoduleInitializer> logger
    )
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        _provider = provider;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Walks and selectively initializes submodules under <paramref name="repoRoot"/>, resolving
    /// relative URLs against <paramref name="repoRemote"/> (the superproject remote).
    /// </summary>
    public async Task<SubmoduleInitOutcome> InitializeAsync(
        string repoRoot,
        GitRemoteUrl repoRemote,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(repoRemote);

        var initialized = new List<string>();
        var denied = new List<SubmoduleDenied>();
        await InitLevelAsync(repoRoot, string.Empty, repoRemote, initialized, denied, 0, cancellationToken)
            .ConfigureAwait(false);
        return new SubmoduleInitOutcome(initialized, denied);
    }

    private async Task InitLevelAsync(
        string repoRoot,
        string relativeDir,
        GitRemoteUrl parentRemote,
        List<string> initialized,
        List<SubmoduleDenied> denied,
        int depth,
        CancellationToken cancellationToken
    )
    {
        if (depth >= MaxDepth)
        {
            _logger.LogWarning(
                "Submodule recursion depth {Depth} reached at '{Dir}'; not descending further.",
                depth,
                relativeDir
            );
            return;
        }

        var levelDir = JoinPath(repoRoot, relativeDir);
        var gitmodulesPath = JoinPath(levelDir, ".gitmodules");
        var read = await _fileSystem
            .ReadFileAsync(gitmodulesPath, SandboxReadLimits.RepositoryFileBytes, cancellationToken)
            .ConfigureAwait(false);
        if (read.TooLarge)
        {
            // Refused, and SAID so rather than falling into the "no submodules declared" branch below. The
            // two outcomes look identical from here and mean opposite things: one level has nothing to
            // descend into, the other has something we declined to read, and every submodule under it goes
            // uninitialized either way. Only the log can tell an operator which happened.
            _logger.LogWarning(
                "'.gitmodules' at '{Path}' exceeds the {Limit}-byte read limit; not descending into any "
                    + "submodule declared there.",
                gitmodulesPath,
                SandboxReadLimits.RepositoryFileBytes
            );
            return;
        }

        if (read.Content is not { } gitmodules)
        {
            return; // No submodules declared at this level.
        }

        foreach (var entry in GitModulesParser.Parse(gitmodules))
        {
            var submodulePath = JoinRelative(relativeDir, entry.Path);

            var url = GitRemoteUrl.Parse(entry.Url);
            if (url.IsRelative)
            {
                url = url.Resolve(parentRemote);
            }

            var decision = DecideFetch(url);
            if (!decision.IsAllowed)
            {
                _logger.LogWarning(
                    "Submodule '{Path}' ({Url}) denied: {Reason}",
                    submodulePath,
                    entry.Url,
                    decision.Reason
                );

                // Declining to init is not enough on a POOLED slot. SlotHygiene runs before this run's policy
                // exists and deliberately restores EVERY registered submodule's checkout to the recorded
                // gitlink, so a submodule a prior lease was allowed to initialize arrives here already
                // populated. Leaving it would put a repository this run's policy denies inside the reviewed
                // checkout (issue #218 item 12). Remove the worktree so the denial is enforced on disk, not
                // just recorded. Local-only: deinit touches no remote, so it is safe under any policy.
                var removal = await DeinitAsync(levelDir, entry.Path, cancellationToken).ConfigureAwait(false);
                var reason = decision.Reason;
                if (removal is { } failure)
                {
                    // Logged at Error, not Warning: the removal IS the enforcement, so a denial that did not
                    // take effect on disk is a security-relevant outcome an operator must be able to find.
                    _logger.LogError(
                        "Submodule '{Path}' was denied but its worktree could not be removed: {Failure}. Any "
                            + "checkout a prior lease left there is still present in this review.",
                        submodulePath,
                        failure
                    );
                    reason = $"{decision.Reason}; deinit failed: {failure}";
                }

                denied.Add(new SubmoduleDenied(submodulePath, entry.Url, reason));
                continue;
            }

            var result = await _git.RunAsync(
                    ["submodule", "update", "--init", "--", entry.Path],
                    levelDir,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (!result.Succeeded)
            {
                // Carry the git STDERR into the reason so a caller can classify the failure (a transient
                // auth/network fault vs. real local corruption — review #180) instead of treating every failed
                // init as corruption.
                denied.Add(
                    new SubmoduleDenied(
                        submodulePath,
                        entry.Url,
                        $"git submodule update failed (exit {result.ExitCode}): {result.Stderr}"
                    )
                );
                continue;
            }

            initialized.Add(submodulePath);

            // Recurse: re-parse the nested .gitmodules and repeat under the resolved remote.
            await InitLevelAsync(repoRoot, submodulePath, url, initialized, denied, depth + 1, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Removes the worktree of a submodule this run's policy denies, so content a PRIOR lease checked out
    /// under a different policy cannot cross into this review. Returns <c>null</c> on success, else the
    /// failure text to carry into the denial reason.
    /// <para>
    /// <c>--force</c> is required rather than incidental: the leftover checkout is, by construction, one this
    /// run never asked for, so git's refusal to discard local content is exactly the refusal to override.
    /// The call is unconditional because <c>deinit</c> on a submodule that was never initialized is already a
    /// no-op — cheaper and more reliable than parsing <c>git submodule status</c> prefixes to find out first.
    /// </para>
    /// </summary>
    private async Task<string?> DeinitAsync(string levelDir, string entryPath, CancellationToken cancellationToken)
    {
        var result = await _git.RunAsync(
                ["submodule", "deinit", "--force", "--", entryPath],
                levelDir,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (result.Succeeded)
        {
            return null;
        }

        var stderr = string.IsNullOrWhiteSpace(result.Stderr) ? "(no stderr)" : result.Stderr.Trim();
        return $"exit {result.ExitCode}: {stderr}";
    }

    /// <summary>
    /// Validates a (resolved) submodule URL: only HTTP(S) transports are permitted, and the host/path
    /// must be on the <see cref="OperationPolicy"/> allow-list for <see cref="SandboxOperation.FetchSubmodule"/>.
    /// </summary>
    private PolicyDecision DecideFetch(GitRemoteUrl url)
    {
        if (url.Kind is not (GitUrlKind.Https or GitUrlKind.Http))
        {
            return PolicyDecision.Deny($"submodule transport '{url.Kind}' is not permitted (only HTTP/HTTPS)");
        }

        // Azure DevOps legacy-host equivalence: a repo's own .gitmodules may declare the historical
        // {org}.visualstudio.com host form, but the per-run allow-list (and the modern ADO credential) are
        // keyed to dev.azure.com. Canonicalize the parsed URL BEFORE building the policy request so the
        // exact host+path allow rule matches. This is a fixed URL-shape rewrite, not a security relaxation:
        // a non-visualstudio.com URL is returned unchanged and still gated by the explicit allow-list.
        url = GitRemoteUrl.CanonicalizeAdoLegacyHost(url);

        var request = new OperationRequest(
            SandboxOperation.FetchSubmodule,
            _provider,
            url.Host,
            "GET",
            $"{url.RepoPath}.git/info/refs?service=git-upload-pack"
        );
        return _policy.Decide(request);
    }

    private static string JoinPath(string root, string relative) =>
        string.IsNullOrEmpty(relative) ? root : $"{root.TrimEnd('/')}/{relative.Trim('/')}";

    private static string JoinRelative(string baseDir, string child) =>
        string.IsNullOrEmpty(baseDir) ? child.Trim('/') : $"{baseDir.Trim('/')}/{child.Trim('/')}";
}
