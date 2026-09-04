namespace CodeReviewDaemon.Sample.Persistence.Models;

/// <summary>The typed row family a merged-close candidate was mechanically derived from.</summary>
internal enum CloseCandidateKind
{
    /// <summary>A clarification question the reviewer actually asked.</summary>
    Question,

    /// <summary>An actionable finding or suggestion the reviewer recorded.</summary>
    Finding,

    /// <summary>A substantive comment, reply, answer, or correction.</summary>
    SubstantiveContribution,

    /// <summary>A provider-accepted publication, which carries the provider thread disposition.</summary>
    ProviderAction,
}

/// <summary>
/// The verified semantic outcome of one close candidate. Everything the verifier could not re-derive
/// from frozen evidence stays <see cref="Indeterminate"/>, which is the only non-definitive value.
/// </summary>
internal enum CloseOutcomeLabel
{
    /// <summary>Nothing definitive could be established from the frozen sources.</summary>
    Indeterminate,

    /// <summary>The item is supported by its cited sources with no further semantic claim.</summary>
    Confirmed,

    /// <summary>The merged final code no longer contains what the item named.</summary>
    Addressed,

    /// <summary>The merged final code still contains what the item named.</summary>
    NotAddressed,

    /// <summary>No equivalent point existed in the discussion before this item was posted.</summary>
    Novel,

    /// <summary>Someone else raised an equivalent point, with their own timestamp and source.</summary>
    IndependentlyRaised,
}

/// <summary>How a single promotion pass ended. Every pass records one, including the ones that do not write.</summary>
internal enum PromotionDisposition
{
    Written,
    Declined,
    Failed,
}

/// <summary>The append-only destinations merged close promotes into, in the order they must run.</summary>
internal static class PromotionDestinations
{
    public const string KnowledgeBase = "KnowledgeBase";
    public const string DeveloperLearnings = "DeveloperLearnings";
}

/// <summary>
/// One persisted merged-close outcome row: the deterministic candidate, what the analyst proposed, and
/// what the verifier independently confirmed. Reports recompute every aggregate from these rows.
/// </summary>
internal sealed record CloseOutcomeItem(
    long EngagementRoundId,
    string CandidateId,
    CloseCandidateKind Kind,
    string Summary,
    string? ProposedLabel,
    CloseOutcomeLabel VerifiedLabel,
    string VerificationReasonCode,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyList<AuditSourceReference> Sources
);

/// <summary>The durable result of promoting one source observation into one destination.</summary>
internal sealed record PromotionOutcome(
    string SourceObservationId,
    string DestinationKind,
    PromotionDisposition Disposition,
    string? DestinationPath,
    string? DestinationContentHash,
    string? ReasonCode
);
