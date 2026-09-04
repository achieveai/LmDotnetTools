namespace CodeReviewDaemon.Sample.Persistence.Models;

internal enum ClarificationQuestionState
{
    Open,
    Answered,
    Contested,
    Superseded,
    UnansweredAtMerge,
}

internal sealed record AuditSourceReference(string SourceRecordId, string ContentSha256);

internal sealed record ClarificationQuestion(
    string Id,
    long EngagementRoundId,
    string Wording,
    string? ProviderTargetJson,
    string? FilePath,
    int? Line,
    string EvidenceSummary,
    string WithheldConclusion,
    string? ActionId,
    string? ProviderReceiptJson,
    DateTimeOffset? AskedAtUtc,
    ClarificationQuestionState State,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc
);

internal sealed record ClarificationCandidateAnswer(
    string Id,
    string QuestionId,
    long ObservedInRoundId,
    string ProviderReferenceJson,
    string InterpretationSummary,
    DateTimeOffset ObservedAtUtc
);
