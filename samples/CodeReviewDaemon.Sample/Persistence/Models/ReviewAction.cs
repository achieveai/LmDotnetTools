namespace CodeReviewDaemon.Sample.Persistence.Models;

internal enum ReviewActionKind
{
    CreateRootSummary,
    AppendSummaryDelta,
    SubmitInlineFindings,
    PostClarificationQuestion,
    ReplyToDiscussion,
    FinalizeRound,
}

internal enum ReviewActionStatus
{
    Planned,
    CollectedOnly,
    Sending,
    Accepted,
    Rejected,
}

internal sealed record ReviewAction(
    long EngagementRoundId,
    string ActionId,
    ReviewActionKind Kind,
    ReviewActionStatus Status,
    string PayloadSha256,
    string? ProviderTargetJson,
    string? ProviderReceiptJson,
    string? RejectionJson,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc
);
