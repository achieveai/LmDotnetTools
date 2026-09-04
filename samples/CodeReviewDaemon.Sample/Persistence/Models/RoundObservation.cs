namespace CodeReviewDaemon.Sample.Persistence.Models;

internal enum ObservationKind
{
    ContextClaim,
    Finding,
    Question,
    Answer,
    Correction,
    DiscussionContribution,
    DeliberateNoAction,
    JudgeResult,
    Gap,
}

internal sealed record RoundObservation(
    string Id,
    long EngagementRoundId,
    long Sequence,
    ObservationKind Kind,
    string Summary,
    DateTimeOffset ObservedAtUtc
);
