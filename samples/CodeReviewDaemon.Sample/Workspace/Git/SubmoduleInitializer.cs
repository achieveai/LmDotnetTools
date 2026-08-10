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
    IReadOnlyList<SubmoduleDenied> Denied);

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
                relativeDir);
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
                SandboxReadLimits.RepositoryFileBytes);
            return;
        }

        if (read.Content is not { } gitmodules)
        {
            return; // No submodules declared at this level.
        }

        foreach (var entry in GitModulesParser.Parse(gitmodules))
        {
            var submodulePath = JoinRelative(relativeDir, entry.Path);

            var parsed = GitRemoteUrl.Parse(entry.Url);
            if (parsed.IsRelative)
            {
                parsed = parsed.Resolve(parentRemote);
            }

            // Canonicalize ONCE, here, so the allow decision and the clone are about the same URL. This used
            // to happen inside DecideFetch, where it was invisible to everything after it: the policy
            // approved dev.azure.com/{org}/{project}/_git/{repo} and `git submodule update` then cloned
            // whatever .gitmodules said — the legacy {org}.visualstudio.com/DefaultCollection/… form. A gate
            // that decides about one repository while the fetch reaches for another is wrong regardless of
            // whether the fetch happens to succeed. Canonicalizing here also means the RESOLVED url is what
            // a nested level receives as its parent remote, so a relative child of a legacy-host submodule
            // resolves against the modern host too.
            var url = GitRemoteUrl.CanonicalizeAdoLegacyHost(parsed);

            var decision = DecideFetch(url);
            if (!decision.IsAllowed)
            {
                _logger.LogWarning(
                    "Submodule '{Path}' ({Url}) denied: {Reason}",
                    submodulePath,
                    entry.Url,
                    decision.Reason);
                denied.Add(new SubmoduleDenied(submodulePath, entry.Url, decision.Reason));
                continue;
            }

            // Point git at the approved URL when canonicalization moved it. `git submodule init` only copies
            // a .gitmodules url into .git/config when one is not already set, so writing it first is what
            // makes `update` clone from the canonical form. Live, this is the MODISService sibling: the
            // legacy path's leading DefaultCollection segment reads as a project name to the modern service
            // ("TF200016: The following project does not exist: DefaultCollection"), and the daemon's ADO
            // credential is keyed to dev.azure.com, so the legacy host would have gone unauthenticated too.
            if (!await TryPointAtCanonicalUrlAsync(levelDir, entry, parsed, url, cancellationToken)
                .ConfigureAwait(false))
            {
                var reason =
                    $"could not point submodule '{entry.Name}' at its canonical URL; refusing to clone from "
                    + "the un-approved one";
                _logger.LogWarning("Submodule '{Path}' ({Url}) denied: {Reason}", submodulePath, entry.Url, reason);
                denied.Add(new SubmoduleDenied(submodulePath, entry.Url, reason));
                continue;
            }

            var result = await _git
                .RunAsync(["submodule", "update", "--init", "--", entry.Path], levelDir, cancellationToken)
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
                        $"git submodule update failed (exit {result.ExitCode}): {result.Stderr}"));
                continue;
            }

            initialized.Add(submodulePath);

            // Recurse: re-parse the nested .gitmodules and repeat under the resolved remote.
            await InitLevelAsync(
                    repoRoot,
                    submodulePath,
                    url,
                    initialized,
                    denied,
                    depth + 1,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes <c>submodule.&lt;name&gt;.url</c> into the level's local git config when canonicalization
    /// changed the URL, so the subsequent <c>submodule update --init</c> clones from the form the policy
    /// approved rather than the one <c>.gitmodules</c> declared. A no-op (returning true) when the URL was
    /// already canonical — the remap closes a specific legacy-host gap and has no business touching every
    /// other submodule's remote.
    /// <para>
    /// Returns false when the config write fails, which the caller turns into a denial. Falling through to
    /// the clone would be the worst outcome available: fetching the un-approved URL after the gate said yes
    /// to a different one.
    /// </para>
    /// </summary>
    private async Task<bool> TryPointAtCanonicalUrlAsync(
        string levelDir,
        SubmoduleEntry entry,
        GitRemoteUrl parsed,
        GitRemoteUrl canonical,
        CancellationToken cancellationToken)
    {
        if (string.Equals(parsed.Host, canonical.Host, StringComparison.Ordinal)
            && string.Equals(parsed.RepoPath, canonical.RepoPath, StringComparison.Ordinal))
        {
            return true;
        }

        // Only an https URL is ever rewritten (CanonicalizeAdoLegacyHost leaves every other kind alone), and
        // RepoPath keeps its percent-escapes — GitRemoteUrl deliberately does not decode them, which is both
        // what let the allow rule match "Microsoft%20Orleans" and what keeps this reconstructed URL valid.
        var canonicalUrl = $"https://{canonical.Host}{canonical.RepoPath}";
        var configured = await _git
            .RunAsync(["config", $"submodule.{entry.Name}.url", canonicalUrl], levelDir, cancellationToken)
            .ConfigureAwait(false);

        if (!configured.Succeeded)
        {
            return false;
        }

        _logger.LogDebug(
            "Submodule '{Name}' declares the legacy ADO host; cloning from the canonical {Url} instead.",
            entry.Name,
            canonicalUrl);
        return true;
    }

    /// <summary>
    /// Validates a (resolved, already-canonicalized) submodule URL: only HTTP(S) transports are permitted,
    /// and the host/path must be on the <see cref="OperationPolicy"/> allow-list for
    /// <see cref="SandboxOperation.FetchSubmodule"/>.
    /// </summary>
    private PolicyDecision DecideFetch(GitRemoteUrl url)
    {
        if (url.Kind is not (GitUrlKind.Https or GitUrlKind.Http))
        {
            return PolicyDecision.Deny(
                $"submodule transport '{url.Kind}' is not permitted (only HTTP/HTTPS)");
        }

        var request = new OperationRequest(
            SandboxOperation.FetchSubmodule,
            _provider,
            url.Host,
            "GET",
            $"{url.RepoPath}.git/info/refs?service=git-upload-pack");
        return _policy.Decide(request);
    }

    private static string JoinPath(string root, string relative) =>
        string.IsNullOrEmpty(relative) ? root : $"{root.TrimEnd('/')}/{relative.Trim('/')}";

    private static string JoinRelative(string baseDir, string child) =>
        string.IsNullOrEmpty(baseDir) ? child.Trim('/') : $"{baseDir.Trim('/')}/{child.Trim('/')}";
}
