namespace AchieveAi.LmDotnetTools.LmMultiTurn.Audit;

/// <summary>Names the immutable engagement and round that own an audit record.</summary>
public sealed record MultiTurnAuditScope(string EngagementId, string RoundId);

/// <summary>Describes whether source content is complete, missing, or represented by a redaction.</summary>
public enum AuditCaptureOutcome
{
    Complete,
    Gap,
    Redacted,
}

/// <summary>An exact, sequenced source record for one model-turn boundary.</summary>
public sealed record ModelTurnAuditRecord(
    string RecordId,
    MultiTurnAuditScope Scope,
    string ThreadId,
    string RunId,
    string GenerationId,
    string? ParentTurnId,
    long Sequence,
    string RecordType,
    string? Role,
    string? ModelId,
    string? ProviderId,
    ReadOnlyMemory<byte> Content,
    string ContentSha256,
    long ByteCount,
    AuditCaptureOutcome Outcome,
    string? GapReason,
    DateTimeOffset CapturedAtUtc
);

/// <summary>Stable record-type names written by the multi-turn audit seam.</summary>
public static class MultiTurnAuditRecordTypes
{
    public const string ModelRequest = "model_request";
    public const string ModelResponse = "model_response";
    public const string ToolResult = "tool_result";
    public const string StreamGap = "stream_gap";
}
