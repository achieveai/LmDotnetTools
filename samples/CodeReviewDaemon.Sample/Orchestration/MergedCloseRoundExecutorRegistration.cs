using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace.Git;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>Creates the durable merged-close executor when its pooled workspace dependencies are enabled.</summary>
internal static class MergedCloseRoundExecutorRegistration
{
    public static IEngagementRoundExecutor Create(
        IServiceProvider services,
        CodeReviewDaemonOptions options,
        string? workspaceBase
    )
    {
        var branchManager = services.GetService<ReviewBranchManager>();
        if (branchManager is null)
        {
            return new DisabledMergedCloseRoundExecutor();
        }

        var store = services.GetRequiredService<ReviewStore>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("merged-close");
        var extraction = services.GetService<MergedCloseKnowledgeExtraction>();
        if (extraction is null)
        {
            return new DisabledMergedCloseRoundExecutor();
        }

        // The store owns rounds, sources, questions, actions, and receipts. The merge commit, final
        // diff, final code state, complete discussion, and destination snapshot come from a provider
        // read, which is the one part a merged close cannot reconstruct from its own database.
        var readProviderEvidence = services.GetService<MergedCloseProviderEvidenceReader>();

        return new MergedCloseRoundExecutor(
            store,
            async (round, substep, cancellationToken) =>
            {
                var engagement = GetEngagement(store, round);
                switch (substep)
                {
                    case MergedCloseSubstep.EvidenceFrozen:
                    {
                        var repo =
                            store.GetRepo(engagement.RepoId)
                            ?? throw new InvalidOperationException($"Repository {engagement.RepoId} was not found.");
                        var frozen = readProviderEvidence is null
                            ? null
                            : await readProviderEvidence(engagement, repo, round, cancellationToken)
                                .ConfigureAwait(false);
                        if (frozen is null)
                        {
                            logger.LogWarning(
                                "Merged-close evidence for {Provider} PR {PrId} could not be frozen: the provider "
                                    + "evidence reader is {State}. The round retries rather than checkpointing a "
                                    + "partial inventory.",
                                engagement.Provider,
                                engagement.PrId,
                                readProviderEvidence is null ? "not registered" : "unable to read"
                            );
                            return MergedCloseSubstepResult.Retry;
                        }

                        var inventory = MergedCloseEvidenceFreezer.Freeze(store, engagement, repo, round, frozen);
                        return MergedCloseSubstepResult.Completed(
                            new MergedCloseSubstepReceipt(MergedCloseEvidenceFreezer.Serialize(inventory))
                        );
                    }

                    case MergedCloseSubstep.CandidatesBuilt:
                    {
                        // Persist the mechanically derived denominator before any model sees it, so a
                        // later substep can only label items a typed row already supports.
                        var candidates = MergedCloseCandidateInventory.Build(store, engagement.Id);
                        store.SaveCloseOutcomeItems(
                            round.Id,
                            [.. candidates.Select(candidate => ToUnverifiedItem(round.Id, candidate))]
                        );
                        return MergedCloseSubstepResult.Completed(
                            new MergedCloseSubstepReceipt(
                                MergedCloseReceiptPayload.Serialize(
                                    candidates.Select(candidate => candidate.Id).ToArray()
                                )
                            )
                        );
                    }

                    case MergedCloseSubstep.KnowledgePromoted:
                    {
                        var outcome = await extraction(engagement, cancellationToken).ConfigureAwait(false);
                        return outcome == KnowledgeExtractionOutcome.Failed
                            ? MergedCloseSubstepResult.Retry
                            : MergedCloseSubstepResult.Completed(
                                new MergedCloseSubstepReceipt(
                                    MergedCloseReceiptPayload.Serialize(new[] { outcome.ToString() })
                                )
                            );
                    }

                    case MergedCloseSubstep.Archived:
                        return await ArchiveAsync(
                                store,
                                branchManager,
                                options,
                                workspaceBase,
                                logger,
                                engagement,
                                cancellationToken
                            )
                            .ConfigureAwait(false);

                    case MergedCloseSubstep.Classified:
                    case MergedCloseSubstep.Verified:
                    case MergedCloseSubstep.DeveloperLearningPromoted:
                    case MergedCloseSubstep.ReportsWritten:
                    default:
                        // Tasks 17-19 supply the close analyst, the independent verifier, the report
                        // writer, and the developer-learning pass. Until one of them is wired it has no
                        // artifact to show, so it retries; it must never checkpoint a substep that did
                        // nothing, because every later substep would then skip real work for this PR.
                        logger.LogWarning(
                            "Merged-close substep {Substep} for {Provider} PR {PrId} has no implementation yet; "
                                + "the round retries and stays visibly incomplete.",
                            substep,
                            engagement.Provider,
                            engagement.PrId
                        );
                        return MergedCloseSubstepResult.Retry;
                }
            },
            TimeProvider.System
        );
    }

    /// <summary>
    /// Merges the accumulated notes branch, or records why it deliberately did not. Either way the
    /// substep produces a receipt naming the branch, so "archived" is never an empty claim.
    /// </summary>
    private static async Task<MergedCloseSubstepResult> ArchiveAsync(
        ReviewStore store,
        ReviewBranchManager branchManager,
        CodeReviewDaemonOptions options,
        string? workspaceBase,
        ILogger logger,
        PrEngagement engagement,
        CancellationToken cancellationToken
    )
    {
        var repo =
            store.GetRepo(engagement.RepoId)
            ?? throw new InvalidOperationException($"Repository {engagement.RepoId} was not found.");
        if (!int.TryParse(engagement.PrId, out var prNumber))
        {
            throw new InvalidOperationException($"PR id '{engagement.PrId}' is not numeric.");
        }

        var branch = ReviewBranchManager.BuildReviewBranchName(repo, prNumber);
        if (!options.MergeNotesBranchOnClose)
        {
            logger.LogInformation(
                "Merged-close left notes branch '{Branch}' for {Provider} PR {PrId} because merge-on-close is disabled.",
                branch,
                engagement.Provider,
                engagement.PrId
            );
            return MergedCloseSubstepResult.Completed(
                new MergedCloseSubstepReceipt(
                    MergedCloseReceiptPayload.Serialize(new[] { branch, "declined:merge_on_close_disabled" })
                )
            );
        }

        var merged = await branchManager
            .MergeToDefaultAsync(ResolveSweeperRepoRoot(options, workspaceBase), branch, "main", cancellationToken)
            .ConfigureAwait(false);
        return merged
            ? MergedCloseSubstepResult.Completed(
                new MergedCloseSubstepReceipt(MergedCloseReceiptPayload.Serialize(new[] { branch, "merged:main" }))
            )
            : MergedCloseSubstepResult.Retry;
    }

    private static PrEngagement GetEngagement(ReviewStore store, EngagementRound round) =>
        store.GetEngagement(round.PrEngagementId)
        ?? throw new InvalidOperationException($"Engagement {round.PrEngagementId} was not found.");

    /// <summary>
    /// Seeds a candidate as unverified. Every item starts Indeterminate with no proposed label, so an
    /// item the analyst never reaches still appears in the report rather than silently vanishing.
    /// </summary>
    private static CloseOutcomeItem ToUnverifiedItem(long closeRoundId, CloseOutcomeCandidate candidate) =>
        new(
            closeRoundId,
            candidate.Id,
            candidate.Kind,
            candidate.Summary,
            null,
            CloseOutcomeLabel.Indeterminate,
            CloseVerificationReasons.NoProposal,
            candidate.ObservedAtUtc,
            candidate.Sources
        );

    internal static string ResolveReviewPoolRoot(CodeReviewDaemonOptions options, string? workspaceBase)
    {
        var poolLeaf = string.IsNullOrWhiteSpace(options.ReviewPoolHostRoot)
            ? "review-pool"
            : Path.GetFileName(options.ReviewPoolHostRoot.TrimEnd('/', '\\'));
        var poolRoot =
            options.PerAppWorkspaceRooting && !string.IsNullOrWhiteSpace(workspaceBase)
                ? $"{workspaceBase.TrimEnd('/', '\\')}/{poolLeaf}"
            : !string.IsNullOrWhiteSpace(options.ReviewPoolHostRoot) ? options.ReviewPoolHostRoot
            : !string.IsNullOrWhiteSpace(workspaceBase) ? Path.Combine(workspaceBase, poolLeaf)
            : Path.Combine(AppContext.BaseDirectory, poolLeaf);
        return options.UseS2SReviewAgent && !string.IsNullOrWhiteSpace(workspaceBase)
            ? workspaceBase.TrimEnd('/', '\\')
            : poolRoot;
    }

    internal static string ResolveSweeperRepoRoot(CodeReviewDaemonOptions options, string? workspaceBase) =>
        Path.Combine(
            ResolveReviewPoolRoot(options, workspaceBase),
            options.UseS2SReviewAgent ? "review-sweeper-store" : "sweeper-store"
        );
}
