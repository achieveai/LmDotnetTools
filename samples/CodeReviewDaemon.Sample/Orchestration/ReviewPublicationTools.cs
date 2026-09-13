using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>Run-scoped publication primitives. The agent owns wording, disposition and choice of action.</summary>
internal sealed class ReviewPublicationTools(
    ReviewRun run,
    RepoIdentity repo,
    string workflowInstanceId,
    ReviewPoster poster,
    IPrProvider provider,
    DiffManifest manifest,
    bool livePostingAuthorized,
    Func<bool> isActive
)
{
    private readonly SemaphoreSlim _actions = new(1, 1);
    private DateTimeOffset _deadline = DateTimeOffset.MaxValue;

    internal void LimitToDeadline(DateTimeOffset? deadline) => _deadline = deadline ?? DateTimeOffset.MinValue;

    private bool IsActive() => isActive() && DateTimeOffset.UtcNow < _deadline;

    /// <summary>Read-only receipt recovery remains allowed after the step's action grant expires.</summary>
    public Task ReconcileAsync(CancellationToken cancellationToken) =>
        poster.ReconcilePublicationsAsync(run.Id, repo, cancellationToken);

    public Task<JsonObject> PublishSummaryAsync(string actionId, string body, CancellationToken ct) =>
        PublishAsync(actionId, body, new ReviewCommentTarget(repo, run.PrId), ct);

    public Task<JsonObject> PublishInlineAsync(
        string actionId,
        string body,
        string path,
        int line,
        ReviewCommentSide side,
        CancellationToken ct
    )
    {
        // Exact path identity is required here; a suffix match is useful for reading old citations but
        // cannot authorize a write to a different provider file.
        var file = manifest.FindFile(path);
        if (file is null || !string.Equals(file.Path, path, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Inline path is outside this review's diff.");
        }
        var citation = DiffCitationVerifier.Verify(
            manifest,
            new DiffCitation(path, line, side == ReviewCommentSide.Left ? DiffSide.Old : DiffSide.New),
            run.HeadSha
        );
        if (
            citation.Outcome
            is not (CitationOutcome.VerifiedChanged or CitationOutcome.VerifiedDeleted or CitationOutcome.ContextOnly)
        )
        {
            throw new InvalidOperationException("Inline line is outside this review's diff.");
        }
        return PublishAsync(
            actionId,
            body,
            new ReviewCommentTarget(
                repo,
                run.PrId,
                ReviewCommentKind.Inline,
                CommitId: run.HeadSha,
                Path: path,
                Line: line,
                Side: side
            ),
            ct
        );
    }

    public Task<JsonObject> ReplyAsync(
        string actionId,
        string body,
        string providerThreadId,
        string parentCommentId,
        CancellationToken ct
    ) =>
        PublishAsync(
            actionId,
            body,
            new ReviewCommentTarget(
                repo,
                run.PrId,
                ReviewCommentKind.Reply,
                ProviderThreadId: providerThreadId,
                ReplyToProviderCommentId: parentCommentId
            ),
            ct
        );

    private async Task<JsonObject> PublishAsync(
        string actionId,
        string body,
        ReviewCommentTarget target,
        CancellationToken ct
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        await _actions.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsActive())
            {
                throw new InvalidOperationException("This publication scope is no longer active.");
            }
            await EnsureCurrentPrAsync(ct).ConfigureAwait(false);
            if (!IsActive())
            {
                throw new InvalidOperationException("This publication scope is no longer active.");
            }
            var subject = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(workflowInstanceId + "\0" + actionId))
            );
            var operation = "workflow-publish-" + target.Kind.ToString().ToLowerInvariant();
            var outcome = await poster
                .PostReviewAsync(
                    new PostReviewRequest(
                        run.Id,
                        new IdempotencyKeyComponents(
                            RepoIdentity.ToPublisherNamespace(repo.Provider),
                            repo.OrgOrOwner,
                            repo.Project,
                            repo.RepoStableId
                                ?? throw new InvalidOperationException(
                                    "Publication requires a stable repository identity."
                                ),
                            run.PrId,
                            operation,
                            "workflow-publication",
                            subject,
                            run.HeadSha,
                            run.VariantId
                        ),
                        target,
                        body,
                        livePostingAuthorized,
                        RequireConfirmedOutcome: true,
                        IsStillAuthorized: IsActive,
                        VerifyCurrentPr: EnsureCurrentPrAsync
                    ),
                    ct
                )
                .ConfigureAwait(false);
            return new JsonObject
            {
                ["Status"] = outcome.Kind.ToString(),
                ["ReceiptId"] = outcome.OutboxId,
                ["ProviderCommentId"] = outcome.ProviderResponseId,
            };
        }
        finally
        {
            _actions.Release();
        }
    }

    private async Task EnsureCurrentPrAsync(CancellationToken cancellationToken)
    {
        var lifecycle = await provider.GetPrStateAsync(repo, run.PrId, cancellationToken).ConfigureAwait(false);
        var head = await provider.GetCurrentHeadShaAsync(repo, run.PrId, cancellationToken).ConfigureAwait(false);
        if (lifecycle != PrLifecycle.Open || !string.Equals(head, run.HeadSha, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Publication requires the current open PR at the admitted head.");
        }
    }
}
