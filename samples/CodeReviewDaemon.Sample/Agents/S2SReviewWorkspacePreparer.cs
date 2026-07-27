using System.Globalization;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace.Git;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// Prepares the shared-host workspace an S2S review provisions against, before
/// <see cref="S2SReviewAgentLoopFactory"/> opens the conversation. On the "shared host, shared gateway"
/// topology (the chosen simple path — the decoupled per-session in-sandbox git is the deferred
/// bifurcation) LmStreaming never clones anything: a workspace is a logical directory leaf the gateway
/// mounts, so the PR checkout must already exist on the shared gateway host at that leaf. This preparer
/// closes that gap for one PR:
/// <list type="number">
/// <item><b>Derives a single-segment leaf</b> (<c>review-pr-&lt;n&gt;</c>) that survives
///   <c>FileWorkspaceStore.SanitizeDirectory</c> unchanged, so the daemon's host clone dir and
///   LmStreaming's stored <c>DirectoryRelPath</c> name the SAME directory.</item>
/// <item><b>Clones the PR checkout host-side</b> into <c>{WorkspaceBasePath}/{leaf}</c> using the
///   daemon's own host-process git (the injected host-backed <see cref="GitRunner"/>, NOT the sandbox
///   runner) — probe → <c>clone</c> → <c>fetch origin &lt;baseSha&gt; &lt;headSha&gt;</c> →
///   <c>checkout --force &lt;headSha&gt;</c>, the same sequence the daemon's own checkout uses. The
///   gateway then mounts the already-populated host dir on first agent entry; no in-sandbox git needed.</item>
/// <item><b>Ensures the LmStreaming workspace exists</b> pointing at that leaf via the S2S client
///   (<c>GET api/workspaces</c> to find an existing one, else <c>POST api/workspaces</c>) with the
///   code-reviewer marketplace attached so the gateway surfaces the <c>code-reviewer:*</c> sub-agents.</item>
/// </list>
/// The returned <see cref="PreparedReviewWorkspace.WorkspaceId"/> is what the factory provisions against.
/// </summary>
internal sealed class S2SReviewWorkspacePreparer
{
    private readonly LmStreamingS2SClient _client;
    private readonly GitRunner _hostGit;
    private readonly string _workspaceBasePath;
    private readonly string? _reviewMarketplace;
    private readonly ILogger<S2SReviewWorkspacePreparer> _logger;

    public S2SReviewWorkspacePreparer(
        LmStreamingS2SClient client,
        GitRunner hostGit,
        string workspaceBasePath,
        string? reviewMarketplace,
        ILogger<S2SReviewWorkspacePreparer> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _hostGit = hostGit ?? throw new ArgumentNullException(nameof(hostGit));
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceBasePath);
        _workspaceBasePath = workspaceBasePath;
        _reviewMarketplace = reviewMarketplace;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Derives the leaf, host-clones the PR checkout under the shared workspace base, and ensures the
    /// LmStreaming workspace points at it. Returns the prepared leaf + minted/reused workspace id.
    /// </summary>
    public async Task<PreparedReviewWorkspace> PrepareAsync(
        ReviewRun run,
        RepoIdentity repo,
        string provider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(repo);

        var leaf = DeriveLeaf(run.PrId);
        var hostDir = $"{_workspaceBasePath.TrimEnd('/', '\\')}/{leaf}";
        var remote = TargetRemoteUrl(repo, provider);

        _logger.LogInformation(
            "Preparing S2S review workspace for PR {PrId}: leaf '{Leaf}', host dir '{HostDir}'.",
            run.PrId,
            leaf,
            hostDir);

        await CloneCheckoutAsync(remote, hostDir, run, cancellationToken).ConfigureAwait(false);

        var marketplaces = string.IsNullOrWhiteSpace(_reviewMarketplace)
            ? (IReadOnlyList<string>)[]
            : [_reviewMarketplace];
        var workspaceId = await EnsureWorkspaceAsync(run, leaf, marketplaces, cancellationToken)
            .ConfigureAwait(false);

        return new PreparedReviewWorkspace(leaf, workspaceId);
    }

    /// <summary>
    /// The single-segment host/workspace leaf for a PR. Runs the PR number through the SAME sanitization
    /// LmStreaming applies (<c>FileWorkspaceStore.SanitizeDirectory</c>: lowercase, whitespace→'-', strip
    /// path separators + invalid chars + '..'), so the leaf the daemon clones into is byte-for-byte the
    /// <c>DirectoryRelPath</c> LmStreaming stores and mounts. <c>review-pr-&lt;n&gt;</c> is already safe,
    /// but sanitizing defends against an odd PR id (e.g. an ADO ref with a slash).
    /// </summary>
    internal static string DeriveLeaf(string prId)
    {
        var raw = $"review-pr-{prId}";
        var sanitized = SanitizeLeaf(raw);
        // If sanitization emptied it (a pathological PR id), fall back to a stable, safe constant so we
        // never hand the workspace API an empty DirectoryRelPath.
        return string.IsNullOrEmpty(sanitized) ? "review-pr" : sanitized;
    }

    /// <summary>
    /// Mirror of <c>FileWorkspaceStore.SanitizeDirectory</c> (kept in sync deliberately — the two must
    /// agree or the daemon clones into a different dir than LmStreaming mounts). Lowercases, collapses
    /// whitespace runs to '-', strips invalid filename chars + path separators, removes surviving '..',
    /// and trims leading/trailing '-'.
    /// </summary>
    private static string SanitizeLeaf(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var lowered = raw.Trim().ToLowerInvariant();
        var collapsed = string.Join('-', lowered.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '/', '\\' };
        var sanitized = new string([.. collapsed.Where(c => !invalid.Contains(c))]);
        sanitized = sanitized.Replace("..", string.Empty);
        return sanitized.Trim('-');
    }

    /// <summary>
    /// Host-side clone of the PR checkout into <paramref name="hostDir"/>: probe (skip if already a work
    /// tree), else <c>git clone</c>, then fetch the exact base+head commits and force-checkout the head so
    /// the mounted tree reflects the code the PR PROPOSES. Failures throw so the review stage retries. This
    /// mirrors the daemon's own <c>CloneIfMissingAsync</c>/<c>FetchAndCheckoutHeadAsync</c> sequence.
    /// </summary>
    private async Task CloneCheckoutAsync(string remote, string hostDir, ReviewRun run, CancellationToken cancellationToken)
    {
        var probe = await _hostGit
            .RunAsync(["-C", hostDir, "rev-parse", "--is-inside-work-tree"], hostDir, cancellationToken)
            .ConfigureAwait(false);
        if (!probe.Succeeded)
        {
            var clone = await _hostGit
                .RunAsync(["clone", remote, hostDir], workingDirectory: null, cancellationToken)
                .ConfigureAwait(false);
            if (!clone.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Cloning '{remote}' for PR {run.PrId} into '{hostDir}' failed (exit {clone.ExitCode}): {clone.Stderr}");
            }
        }

        var fetch = await _hostGit
            .RunAsync(["-C", hostDir, "fetch", "origin", run.BaseSha, run.HeadSha], hostDir, cancellationToken)
            .ConfigureAwait(false);
        if (!fetch.Succeeded)
        {
            throw new InvalidOperationException(
                $"Fetching the PR commits for PR {run.PrId} failed (exit {fetch.ExitCode}): {fetch.Stderr}");
        }

        var checkout = await _hostGit
            .RunAsync(["-C", hostDir, "checkout", "--force", run.HeadSha], hostDir, cancellationToken)
            .ConfigureAwait(false);
        if (!checkout.Succeeded)
        {
            throw new InvalidOperationException(
                $"Checking out the PR head for PR {run.PrId} failed (exit {checkout.ExitCode}): {checkout.Stderr}");
        }
    }

    /// <summary>
    /// Finds an existing LmStreaming workspace whose <c>DirectoryRelPath</c> is the derived leaf (idempotent
    /// re-run) and returns its id; otherwise creates one pointing at the leaf with the review marketplace
    /// attached. The compare is against the leaf the daemon derived — which is already sanitize-stable — so a
    /// second run for the same PR reuses the workspace rather than colliding.
    /// </summary>
    private async Task<string> EnsureWorkspaceAsync(
        ReviewRun run,
        string leaf,
        IReadOnlyList<string> marketplaces,
        CancellationToken cancellationToken)
    {
        var existing = await _client.ListWorkspacesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var workspace in existing)
        {
            if (string.Equals(workspace.DirectoryRelPath, leaf, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Reusing existing S2S review workspace {WorkspaceId} for leaf '{Leaf}'.",
                    workspace.Id,
                    leaf);
                return workspace.Id;
            }
        }

        var name = string.Format(CultureInfo.InvariantCulture, "Review PR #{0}", run.PrId);
        var created = await _client
            .CreateWorkspaceAsync(name, leaf, marketplaces, cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation(
            "Created S2S review workspace {WorkspaceId} for leaf '{Leaf}' (marketplaces: {Count}).",
            created.Id,
            leaf,
            marketplaces.Count);
        return created.Id;
    }

    /// <summary>Builds the HTTPS clone URL for the target repo from its identity + provider (mirrors the
    /// daemon's own <c>TargetRemoteUrl</c>): ADO is <c>/{org}/{project}/_git/{repo}</c> on dev.azure.com;
    /// GitHub is <c>/{owner}/{repo}.git</c> on github.com.</summary>
    private static string TargetRemoteUrl(RepoIdentity repo, string provider) =>
        string.Equals(provider, "ado", StringComparison.Ordinal)
            ? $"https://dev.azure.com/{repo.OrgOrOwner}/{repo.Project}/_git/{repo.RepoName}"
            : $"https://github.com/{repo.OrgOrOwner}/{repo.RepoName}.git";
}

/// <summary>
/// The result of <see cref="S2SReviewWorkspacePreparer.PrepareAsync"/>: the single-segment
/// <see cref="Leaf"/> the checkout was cloned into (= LmStreaming's stored <c>DirectoryRelPath</c>) and
/// the <see cref="WorkspaceId"/> the factory provisions the conversation against.
/// </summary>
internal sealed record PreparedReviewWorkspace(string Leaf, string WorkspaceId);
