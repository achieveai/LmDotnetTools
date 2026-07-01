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
        var gitmodules = await _fileSystem
            .ReadFileAsync(JoinPath(levelDir, ".gitmodules"), cancellationToken)
            .ConfigureAwait(false);
        if (gitmodules is null)
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
                    decision.Reason);
                denied.Add(new SubmoduleDenied(submodulePath, entry.Url, decision.Reason));
                continue;
            }

            var result = await _git
                .RunAsync(["submodule", "update", "--init", "--", entry.Path], levelDir, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Succeeded)
            {
                denied.Add(
                    new SubmoduleDenied(
                        submodulePath,
                        entry.Url,
                        $"git submodule update failed (exit {result.ExitCode})"));
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
    /// Validates a (resolved) submodule URL: only HTTP(S) transports are permitted, and the host/path
    /// must be on the <see cref="OperationPolicy"/> allow-list for <see cref="SandboxOperation.FetchSubmodule"/>.
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
