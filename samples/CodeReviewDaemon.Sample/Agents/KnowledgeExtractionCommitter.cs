using CodeReviewDaemon.Sample.Workspace.Git;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// Carries the at-close <see cref="KnowledgeAgent"/> extraction write into the store's default branch. On a
/// merged PR the sweeper store clone has no LOCAL notes branch, so this fetches and checks the PR's notes
/// branch out (<c>checkout -B &lt;branch&gt; origin/&lt;branch&gt;</c>) BEFORE extraction runs, so the layered
/// <c>KnowledgeBase/…</c> write lands on that branch; then — only when the gated extraction actually wrote an
/// entry — it stages, commits, and pushes <c>KnowledgeBase/</c> onto the notes branch, so the sweeper's
/// subsequent <see cref="ReviewBranchManager.MergeToDefaultAsync"/> (a merge of <c>origin/&lt;branch&gt;</c>)
/// carries the new/updated entry into the default branch. Without this the write stays uncommitted in the
/// sweeper worktree and is dropped by the merge's fresh checkout (and can dirty the tree so a later sweep
/// stalls). Best-effort by contract: any git or agent failure is logged and swallowed — extraction must
/// NEVER block the PR lifecycle (design §6).
/// </summary>
internal sealed class KnowledgeExtractionCommitter
{
    private const string KnowledgeBaseDir = "KnowledgeBase";

    /// <summary>Cap on push retries when the notes branch advanced under us (concurrent review/daemon).</summary>
    private const int MaxPushAttempts = 3;

    private readonly GitRunner _git;
    private readonly string _repoRoot;
    private readonly ILogger<KnowledgeExtractionCommitter> _logger;

    public KnowledgeExtractionCommitter(
        GitRunner git,
        string repoRoot,
        ILogger<KnowledgeExtractionCommitter> logger
    )
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        _repoRoot = repoRoot;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Checks the notes branch out, runs <paramref name="extractAsync"/> (the gated
    /// <see cref="KnowledgeAgent.TryExtractAsync"/> call), and — when it returns a write — commits and pushes
    /// <c>KnowledgeBase/</c> onto <paramref name="branch"/> with a <c>kb: extract from &lt;sourcePrRef&gt;</c>
    /// message. A gate result of <c>null</c> commits nothing (the checkout is left clean). Never throws for a
    /// git/agent/extraction/IO failure — every step is checked and, on failure, logged and swallowed
    /// (design §6); it does still validate its own arguments (throws <see cref="ArgumentException"/>/
    /// <see cref="ArgumentNullException"/> on a null/blank input, which is a programmer error, not a
    /// runtime condition).
    /// </summary>
    public async Task RunAsync(
        string branch,
        string sourcePrRef,
        Func<CancellationToken, Task<KnowledgeWriteResult?>> extractAsync,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePrRef);
        ArgumentNullException.ThrowIfNull(extractAsync);

        try
        {
            // The sweeper store clone has no local notes branch; fetch and check origin/<branch> out so the
            // KnowledgeBase write lands on the branch the sweeper later merges into the default branch.
            _ = await _git.RunAsync(["fetch", "origin"], _repoRoot, cancellationToken).ConfigureAwait(false);
            var checkedOut = await _git
                .RunAsync(["checkout", "-B", branch, $"origin/{branch}"], _repoRoot, cancellationToken)
                .ConfigureAwait(false);
            if (!checkedOut.Succeeded)
            {
                // Without a clean checkout of the notes branch, HEAD is left wherever it was (often the
                // default branch after a prior sweep) — running extraction now would commit the KB write
                // onto the wrong branch. Bail before the agent ever runs.
                _logger.LogWarning(
                    "Knowledge extraction commit for {SourcePr} could not check '{Branch}' out ({Stderr}); skipping.",
                    sourcePrRef, branch, checkedOut.Stderr);
                return;
            }

            var written = await extractAsync(cancellationToken).ConfigureAwait(false);
            if (written is null)
            {
                // Gate fired — nothing durable was written, so there is nothing to commit or push.
                return;
            }

            // Stage ONLY KnowledgeBase/ (the entry + regenerated _index.jsonl/_toc.md); commit and push onto
            // the notes branch so the sweeper's MergeToDefaultAsync fast-forwards it into the default branch.
            var added = await _git.RunAsync(["add", "--", KnowledgeBaseDir], _repoRoot, cancellationToken)
                .ConfigureAwait(false);
            if (!added.Succeeded)
            {
                _logger.LogWarning(
                    "Knowledge extraction commit for {SourcePr} on '{Branch}' could not stage {Dir} ({Stderr}); the entry is lost.",
                    sourcePrRef, branch, KnowledgeBaseDir, added.Stderr);
                return;
            }

            var committed = await _git
                .RunAsync(["commit", "-m", $"kb: extract from {sourcePrRef}"], _repoRoot, cancellationToken)
                .ConfigureAwait(false);
            if (!committed.Succeeded)
            {
                _logger.LogWarning(
                    "Knowledge extraction commit for {SourcePr} on '{Branch}' failed to commit ({Stderr}); the entry is lost.",
                    sourcePrRef, branch, committed.Stderr);
                return;
            }

            var pushed = await TryPushWithRebaseAsync(branch, cancellationToken).ConfigureAwait(false);
            if (!pushed)
            {
                // A non-fast-forward rejection (origin/<branch> advanced under us) must never be reported
                // as success: the sweeper's later merge would fetch origin/<branch> WITHOUT this commit and
                // silently drop the entry while logging that it was carried.
                _logger.LogWarning(
                    "Knowledge extraction commit for {SourcePr} could not push '{Branch}' after {Attempts} attempts; the entry is lost.",
                    sourcePrRef, branch, MaxPushAttempts);
                return;
            }

            _logger.LogInformation(
                "Knowledge extraction for {SourcePr} committed to notes branch '{Branch}' for the sweeper merge.",
                sourcePrRef, branch);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Knowledge extraction commit for {SourcePr} on '{Branch}' failed; the merge proceeds without it.",
                sourcePrRef, branch);
        }
    }

    /// <summary>
    /// Pushes <paramref name="branch"/> to <c>origin</c>, rebasing onto the remote and retrying up to
    /// <see cref="MaxPushAttempts"/> times when it advanced underneath us (a concurrent review/daemon's own
    /// KB commit), mirroring <see cref="ReviewBranchManager.TryPushWithRebaseAsync"/>. Returns <c>true</c> on
    /// the first successful push, <c>false</c> when every attempt is rejected.
    /// </summary>
    private async Task<bool> TryPushWithRebaseAsync(string branch, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxPushAttempts; attempt++)
        {
            var push = await _git.RunAsync(["push", "origin", branch], _repoRoot, cancellationToken)
                .ConfigureAwait(false);
            if (push.Succeeded)
            {
                return true;
            }

            if (attempt == MaxPushAttempts)
            {
                break;
            }

            // The remote moved; rebase our commit on top and retry.
            _ = await _git.RunAsync(["pull", "--rebase", "origin", branch], _repoRoot, cancellationToken)
                .ConfigureAwait(false);
        }

        return false;
    }
}
