using System.Globalization;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace;
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
/// <item><b>Derives a single-segment leaf</b> (<c>review-&lt;provider&gt;-&lt;owner&gt;-&lt;repo&gt;-pr-&lt;n&gt;</c>)
///   that survives <c>FileWorkspaceStore.SanitizeDirectory</c> unchanged, so the daemon's host clone dir and
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
/// <para>
/// That three-step sequence is the <b>degraded</b> path, used when the reviewed repo is not a submodule of
/// the cross-repo store. The richer path is <see cref="AdoptSlotAsync"/>: when the Layer-1 pool has leased a
/// slot, the slot itself becomes the workspace, so the hosted review sees the store, the Knowledge Base and
/// the PR's own notes dir rather than a bare clone.
/// </para>
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

        var leaf = DeriveLeaf(repo, provider, run.PrId);
        var hostDir = $"{_workspaceBasePath.TrimEnd('/', '\\')}/{leaf}";
        var remote = TargetRemoteUrl(repo, provider);

        _logger.LogInformation(
            "Preparing S2S review workspace for PR {PrId}: leaf '{Leaf}', host dir '{HostDir}'.",
            run.PrId,
            leaf,
            hostDir);

        await CloneCheckoutAsync(remote, hostDir, run, cancellationToken).ConfigureAwait(false);

        var name = string.Format(CultureInfo.InvariantCulture, "Review PR #{0}", run.PrId);
        var workspaceId = await EnsureWorkspaceForLeafAsync(leaf, name, cancellationToken).ConfigureAwait(false);

        return new PreparedReviewWorkspace(leaf, workspaceId, hostDir, run.PrId);
    }

    /// <summary>
    /// The pooled-review counterpart of <see cref="PrepareAsync"/>: takes a slot ALREADY leased and
    /// populated by the Layer-1 pool (<c>ReviewSlotPreparer</c> has cloned/fetched/checked the PR head out
    /// and put the notes branch in place) and simply names it to LmStreaming as a workspace.
    /// </summary>
    /// <remarks>
    /// It deliberately runs <b>no git at all</b> — re-running the checkout here would fight the preparer for
    /// the same working tree. The slot's directory name IS the workspace leaf, which is why the pool is
    /// configured with a single-segment slot prefix on this path: the gateway mounts that leaf at
    /// <c>/workspace</c>, so the slot's <c>store/</c> and scratch children land at exactly the container paths
    /// the pooled review stage already computes — the whole point of mounting the slot rather than a bare
    /// per-PR clone.
    /// </remarks>
    public async Task<PreparedReviewWorkspace> AdoptSlotAsync(
        ReviewSlot slot,
        ReviewRun run,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(run);

        var leaf = Path.GetFileName(slot.HostPath.TrimEnd('/', '\\'));
        var sanitized = SanitizeLeaf(leaf);
        if (!string.Equals(leaf, sanitized, StringComparison.Ordinal))
        {
            // Startup already guards this; re-assert at the point of use so a slot root reconfigured at
            // runtime cannot silently mount a different, empty directory.
            throw new InvalidOperationException(
                $"Review slot directory '{leaf}' is not stable under LmStreaming's workspace-directory sanitizer "
                    + $"(it becomes '{sanitized}'), so the hosted conversation would be mounted on a different, "
                    + "empty directory.");
        }

        _logger.LogInformation(
            "Adopting leased review slot {Index} as the S2S workspace for PR {PrId}: leaf '{Leaf}'.",
            slot.Index,
            run.PrId,
            leaf);

        var name = string.Format(CultureInfo.InvariantCulture, "Review slot {0}", slot.Index);
        var workspaceId = await EnsureWorkspaceForLeafAsync(leaf, name, cancellationToken).ConfigureAwait(false);

        return new PreparedReviewWorkspace(leaf, workspaceId, slot.HostPath, run.PrId);
    }

    /// <summary>
    /// The host-process git this preparer clones with. Exposed so the caller can run further READ-ONLY git
    /// (the bounded diff + file manifest) against the checkout that was just prepared, instead of cloning a
    /// second copy of the same repo inside a daemon-owned sandbox.
    /// </summary>
    internal GitRunner HostGit => _hostGit;

    /// <summary>
    /// The single-segment host/workspace leaf for a PR. Runs the full repo identity through the SAME
    /// sanitization LmStreaming applies (<c>FileWorkspaceStore.SanitizeDirectory</c>: lowercase,
    /// whitespace→'-', strip path separators + invalid chars + '..'), so the leaf the daemon clones into is
    /// byte-for-byte the <c>DirectoryRelPath</c> LmStreaming stores and mounts.
    /// </summary>
    /// <remarks>
    /// The leaf carries provider + owner + repo, not just the PR number: two repos reviewed by the same
    /// daemon routinely share a PR number, and a number-only leaf would put both reviews in ONE directory —
    /// each clobbering the other's checkout, which is precisely the interference this path exists to prevent.
    /// </remarks>
    internal static string DeriveLeaf(RepoIdentity repo, string provider, string prId)
    {
        ArgumentNullException.ThrowIfNull(repo);

        var raw = $"review-{provider}-{repo.OrgOrOwner}-{repo.RepoName}-pr-{prId}";
        var sanitized = SanitizeLeaf(raw);
        // If sanitization emptied it (a pathological identity), fall back to a stable, safe constant so we
        // never hand the workspace API an empty DirectoryRelPath.
        return string.IsNullOrEmpty(sanitized) ? "review-pr" : sanitized;
    }

    /// <summary>
    /// Mirror of <c>FileWorkspaceStore.SanitizeDirectory</c> (kept in sync deliberately — the two must
    /// agree or the daemon clones into a different dir than LmStreaming mounts). Lowercases, collapses
    /// whitespace runs to '-', strips invalid filename chars + path separators, removes surviving '..',
    /// and trims leading/trailing '-'.
    /// </summary>
    /// <remarks>
    /// <c>internal</c> rather than private so startup can assert that a directory name it is about to hand
    /// LmStreaming survives this unchanged. A leaf that sanitizes to something else is the one failure mode
    /// that LOOKS like it worked: the gateway happily creates the renamed (empty) directory and the agent
    /// reviews nothing, reporting no findings rather than an error.
    /// </remarks>
    internal static string SanitizeLeaf(string raw)
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
    /// Finds an existing LmStreaming workspace whose <c>DirectoryRelPath</c> is <paramref name="leaf"/>
    /// (idempotent re-run) and returns its id; otherwise creates one pointing at the leaf with the review
    /// marketplace attached. The compare is against a leaf the caller has already made sanitize-stable, so a
    /// second run for the same leaf reuses the workspace rather than minting a duplicate.
    /// </summary>
    /// <remarks>
    /// Shared by all three producers of a leaf — the per-PR clone (<see cref="PrepareAsync"/>), the leased
    /// pool slot (<see cref="AdoptSlotAsync"/>) and the sweeper's knowledge-extraction store — so "a
    /// directory becomes a workspace" has exactly one implementation.
    /// </remarks>
    internal async Task<string> EnsureWorkspaceForLeafAsync(
        string leaf,
        string name,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaf);

        var marketplaces = string.IsNullOrWhiteSpace(_reviewMarketplace)
            ? (IReadOnlyList<string>)[]
            : [_reviewMarketplace];

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
/// <see cref="Leaf"/> the checkout was cloned into (= LmStreaming's stored <c>DirectoryRelPath</c>), the
/// <see cref="WorkspaceId"/> the factory provisions the conversation against, the <see cref="HostDir"/>
/// the checkout actually lives at on this host (<c>{WorkspaceBasePath}/{Leaf}</c>) — the directory the
/// gateway mounts for the hosted review, and the one the daemon takes its bounded diff from — and the
/// <see cref="PrId"/> the whole preparation was for, which titles the hosted conversation so a judge
/// following the posted deep-link can see WHICH PR the review they landed on belongs to.
/// </summary>
internal sealed record PreparedReviewWorkspace(string Leaf, string WorkspaceId, string HostDir, string PrId);
