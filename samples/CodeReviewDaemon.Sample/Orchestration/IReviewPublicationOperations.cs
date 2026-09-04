using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Orchestration;

internal interface IReviewPublicationOperations
{
    Task<PublicationOutcome> CreateRootSummaryAsync(RootSummaryRequest request, CancellationToken cancellationToken);

    Task<PublicationOutcome> AppendSummaryDeltaAsync(SummaryDeltaRequest request, CancellationToken cancellationToken);

    Task<PublicationOutcome> SubmitInlineFindingsAsync(
        InlineFindingsRequest request,
        CancellationToken cancellationToken
    );

    Task<PublicationOutcome> PostClarificationQuestionAsync(
        ClarificationQuestionRequest request,
        CancellationToken cancellationToken
    );

    Task<PublicationOutcome> ReplyToDiscussionAsync(
        DiscussionReplyRequest request,
        CancellationToken cancellationToken
    );

    Task<PublicationOutcome> FinalizeRoundAsync(FinalizeRoundRequest request, CancellationToken cancellationToken);
}

internal sealed record PublicationScope(
    long RoundId,
    string ActionId,
    string Provider,
    long RepoId,
    string PrId,
    string ExpectedHeadSha,
    IReadOnlyList<AuditSourceReference> Sources,
    bool LivePostingAuthorized = false
);

internal sealed record RootSummaryRequest(PublicationScope Scope, string Body);

internal sealed record SummaryDeltaRequest(PublicationScope Scope, string Body);

internal sealed record InlineFindingRequest(string Path, string Side, int? StartLine, int EndLine, string Body);

internal sealed record InlineFindingsRequest(PublicationScope Scope, IReadOnlyList<InlineFindingRequest> Findings);

internal sealed record ClarificationQuestionRequest(
    PublicationScope Scope,
    string Body,
    string? ProviderTargetId = null
);

internal sealed record DiscussionReplyRequest(PublicationScope Scope, string Body, string ProviderTargetId);

internal sealed record FinalizeRoundRequest(PublicationScope Scope, bool NoOp);

internal sealed record PublicationOutcome(
    ReviewActionStatus Status,
    string ActionId,
    string? ProviderReviewId,
    string? ProviderThreadId,
    string? ProviderCommentId,
    string? ProviderPermalink,
    bool RelationshipDegraded,
    DateTimeOffset? AcceptedAt,
    string? RejectionCode
);
