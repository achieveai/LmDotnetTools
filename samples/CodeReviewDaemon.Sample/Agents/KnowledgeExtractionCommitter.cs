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
    /// message. A gate result of <c>null</c> commits nothing (the checkout is left clean). Never throws.
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
            _ = await _git.RunAsync(["checkout", "-B", branch, $"origin/{branch}"], _repoRoot, cancellationToken)
                .ConfigureAwait(false);

            var written = await extractAsync(cancellationToken).ConfigureAwait(false);
            if (written is null)
            {
                // Gate fired — nothing durable was written, so there is nothing to commit or push.
                return;
            }

            // Stage ONLY KnowledgeBase/ (the entry + regenerated _index.jsonl/_toc.md); commit and push onto
            // the notes branch so the sweeper's MergeToDefaultAsync fast-forwards it into the default branch.
            _ = await _git.RunAsync(["add", "--", KnowledgeBaseDir], _repoRoot, cancellationToken)
                .ConfigureAwait(false);
            _ = await _git.RunAsync(["commit", "-m", $"kb: extract from {sourcePrRef}"], _repoRoot, cancellationToken)
                .ConfigureAwait(false);
            _ = await _git.RunAsync(["push", "origin", branch], _repoRoot, cancellationToken).ConfigureAwait(false);

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
}
