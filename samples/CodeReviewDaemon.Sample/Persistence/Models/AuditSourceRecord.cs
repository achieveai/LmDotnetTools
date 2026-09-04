namespace CodeReviewDaemon.Sample.Persistence.Models;

internal enum AuditSourceCaptureOutcome
{
    Complete,
    Gap,
    Redacted,
}

internal sealed record AuditSourceRecord(
    string Id,
    long EngagementRoundId,
    string ThreadId,
    string RunId,
    string GenerationId,
    string? ParentTurnId,
    long Sequence,
    string RecordType,
    string? Role,
    string? ModelId,
    string? ProviderId,
    string ContentSha256,
    long ByteCount,
    string SourceContentSha256,
    long SourceByteCount,
    AuditSourceCaptureOutcome CaptureOutcome,
    string? GapReasonCode,
    DateTimeOffset CapturedAtUtc,
    DateTimeOffset? CompletedAtUtc
);

internal sealed record AuditRedactionRecord(
    string Id,
    string SourceRecordId,
    int Version,
    string RedactedContentSha256,
    long RedactedByteCount,
    string AffectedRangesJson,
    string ReasonCode,
    DateTimeOffset CreatedAtUtc
);
