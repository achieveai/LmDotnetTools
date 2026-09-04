using System.Globalization;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace.Git;

namespace CodeReviewDaemon.Sample.Orchestration;

internal delegate Task<KnowledgeExtractionOutcome> MergedCloseKnowledgeExtraction(
    PrEngagement engagement,
    CancellationToken cancellationToken
);

internal static class MergedCloseKnowledgeExtractionRegistration
{
    public static MergedCloseKnowledgeExtraction? Create(
        IServiceProvider services,
        CodeReviewDaemonOptions options,
        string? workspaceBase
    )
    {
        if (!options.EnableKnowledgeAgent && !options.EnableReviewFeedbackAgent)
        {
            return null;
        }

        var slots = services.GetService<ReviewSlotWorkspace>();
        var branchManager = services.GetService<ReviewBranchManager>();
        if (slots is null || branchManager is null)
        {
            return null;
        }

        var store = services.GetRequiredService<ReviewStore>();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var loopFactory = services.GetRequiredService<IReviewAgentLoopFactory>();
        var s2sPreparer = services.GetService<S2SReviewWorkspacePreparer>();
        var hostGit = new GitRunner(slots.HostRunner);
        var sweeperRepoRoot = MergedCloseRoundExecutorRegistration.ResolveSweeperRepoRoot(options, workspaceBase);
        var sweeperLeaf = Path.GetFileName(sweeperRepoRoot.TrimEnd('/', '\\'));
        var committer = new KnowledgeExtractionCommitter(
            hostGit,
            sweeperRepoRoot,
            loggerFactory.CreateLogger<KnowledgeExtractionCommitter>()
        );
        var knowledgeModelId = string.IsNullOrWhiteSpace(options.KnowledgeModelId) ? null : options.KnowledgeModelId;
        var extractionLogger = loggerFactory.CreateLogger("at-close-extraction");

        async Task<PreparedReviewWorkspace?> EnsureExtractionWorkspaceAsync(ReviewedPr pr, CancellationToken ct)
        {
            if (s2sPreparer is null)
            {
                return null;
            }

            var workspaceId = await s2sPreparer
                .EnsureWorkspaceForLeafAsync(sweeperLeaf, "Knowledge extraction store", ct)
                .ConfigureAwait(false);
            return new PreparedReviewWorkspace(sweeperLeaf, workspaceId, sweeperRepoRoot, pr.PrId);
        }

        async Task<KnowledgeExtractionResult> ExtractCuratedKnowledgeAsync(
            ReviewedPr pr,
            string notesInput,
            string sourcePrRef,
            string todayUtc,
            CancellationToken ct
        )
        {
            var workspace = await EnsureExtractionWorkspaceAsync(pr, ct).ConfigureAwait(false);
            await using var loop = loopFactory.Create(
                DaemonAgentFactory.CreateKnowledgeExtractionProfile(),
                modelId: knowledgeModelId,
                threadId: $"knowledge-extract-{pr.Provider}-{pr.PrId}",
                reviewWorkspace: workspace
            );
            var agent = new KnowledgeAgent(loop, slots.HostFileSystem, loggerFactory.CreateLogger<KnowledgeAgent>());
            return await agent
                .TryExtractAsync(sweeperRepoRoot, notesInput, sourcePrRef, todayUtc, ct)
                .ConfigureAwait(false);
        }

        async Task<KnowledgeExtractionResult> ExtractReviewFeedbackAsync(
            ReviewedPr pr,
            string notesInput,
            string sourcePrRef,
            string todayUtc,
            CancellationToken ct
        )
        {
            var workspace = await EnsureExtractionWorkspaceAsync(pr, ct).ConfigureAwait(false);
            await using var loop = loopFactory.Create(
                DaemonAgentFactory.CreateReviewFeedbackExtractionProfile(),
                modelId: knowledgeModelId,
                threadId: $"feedback-extract-{pr.Provider}-{pr.PrId}",
                reviewWorkspace: workspace
            );
            var agent = new ReviewFeedbackAgent(
                loop,
                slots.HostFileSystem,
                loggerFactory.CreateLogger<ReviewFeedbackAgent>()
            );
            return await agent
                .TryExtractAsync(sweeperRepoRoot, pr.Author, notesInput, sourcePrRef, todayUtc, ct)
                .ConfigureAwait(false);
        }

        return async (engagement, ct) =>
        {
            var repo =
                store.GetRepo(engagement.RepoId)
                ?? throw new InvalidOperationException($"Repository {engagement.RepoId} was not found.");
            if (!int.TryParse(engagement.PrId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var prNumber))
            {
                throw new InvalidOperationException($"PR id '{engagement.PrId}' is not numeric.");
            }

            var reviewed = new ReviewedPr(
                repo,
                RepoIdentity.ToPublisherNamespace(engagement.Provider),
                engagement.PrId,
                ReviewBranchManager.BuildReviewBranchName(repo, prNumber),
                store.GetPrAuthor(engagement.RepoId, engagement.PrId)
            );
            var sourcePrRef = $"{reviewed.Provider}/{reviewed.Repo.NormalizedKey}/{reviewed.PrId}";
            return await committer
                .RunAsync(
                    reviewed.Branch,
                    sourcePrRef,
                    async innerCt =>
                    {
                        var notesInput = await ReadPrNotesFromBranchAsync(
                                hostGit,
                                sweeperRepoRoot,
                                reviewed.Branch,
                                innerCt
                            )
                            .ConfigureAwait(false);
                        var todayUtc = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        var knowledge = options.EnableKnowledgeAgent
                            ? await ExtractCuratedKnowledgeAsync(reviewed, notesInput, sourcePrRef, todayUtc, innerCt)
                                .ConfigureAwait(false)
                            : KnowledgeExtractionResult.Declined(null);
                        var feedback = options.EnableReviewFeedbackAgent
                            ? await ExtractReviewFeedbackAsync(reviewed, notesInput, sourcePrRef, todayUtc, innerCt)
                                .ConfigureAwait(false)
                            : KnowledgeExtractionResult.Declined(null);
                        var combined = AtCloseExtractionSeam.Combine(knowledge, feedback);
                        if (combined.DroppedPass is { } dropped)
                        {
                            extractionLogger.LogWarning(
                                "At-close extraction for {SourcePr}: the {Pass} pass failed while the other wrote; "
                                    + "committing the write and dropping the failed pass for this PR.",
                                sourcePrRef,
                                dropped
                            );
                        }

                        return combined.Result;
                    },
                    ct
                )
                .ConfigureAwait(false);
        };
    }

    private static async Task<string> ReadPrNotesFromBranchAsync(
        GitRunner git,
        string repoRoot,
        string branch,
        CancellationToken cancellationToken
    )
    {
        _ = await git.RunAsync(["-C", repoRoot, "fetch", "origin"], repoRoot, cancellationToken).ConfigureAwait(false);

        var remoteRef = $"origin/{branch}";
        var notesRelPath = branch.StartsWith("review/", StringComparison.Ordinal)
            ? "PRs/" + branch["review/".Length..]
            : branch;
        var listed = await git.RunAsync(
                ["-C", repoRoot, "ls-tree", "-r", "--name-only", remoteRef, "--", notesRelPath],
                repoRoot,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (!listed.Succeeded || string.IsNullOrWhiteSpace(listed.Stdout))
        {
            return $"(no accumulated notes found under {notesRelPath})";
        }

        var builder = new System.Text.StringBuilder();
        foreach (
            var file in listed.Stdout.Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
        )
        {
            var show = await git.RunAsync(["-C", repoRoot, "show", $"{remoteRef}:{file}"], repoRoot, cancellationToken)
                .ConfigureAwait(false);
            if (show.Succeeded)
            {
                _ = builder.Append("## ").Append(file).Append('\n').Append(show.Stdout).Append("\n\n");
            }
        }

        var assembled = builder.ToString().Trim();
        return assembled.Length == 0 ? $"(no readable notes under {notesRelPath})" : assembled;
    }
}
