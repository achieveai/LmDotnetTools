using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Security;
using Microsoft.AspNetCore.Mvc;

namespace CodeReviewDaemon.Sample.Controllers;

[ApiController]
[Route("api/review-audit")]
[ReviewBridgeAuth]
public sealed class ReviewAuditController(IServiceProvider services) : ControllerBase
{
    private ReviewStore Store => services.GetRequiredService<ReviewStore>();

    private ReviewAuditIngestionService Ingestion => services.GetRequiredService<ReviewAuditIngestionService>();

    [HttpPost("records/{recordId}")]
    public IActionResult BeginRecord(string recordId, [FromBody] ReviewAuditRecordRequest? request)
    {
        if (request is null || Store.GetEngagementRound(request.EngagementRoundId) is null)
        {
            return NotFound();
        }

        try
        {
            Ingestion.Begin(recordId, request);
            return Ok();
        }
        catch (InvalidOperationException)
        {
            return Conflict();
        }
        catch (ArgumentException ex)
        {
            return UnprocessableEntity(new ReviewAuditError("invalid_audit_record", ex.Message));
        }
    }

    [HttpPut("records/{recordId}/chunks/{chunkIndex}")]
    public IActionResult PutChunk(string recordId, int chunkIndex, [FromBody] ReviewAuditChunkRequest? request)
    {
        if (request?.Content is null)
        {
            return UnprocessableEntity(new ReviewAuditError("invalid_audit_chunk", "A chunk body is required."));
        }

        if (request.Content.Length > ReviewStore.AuditChunkBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        try
        {
            Ingestion.PutChunk(recordId, chunkIndex, request);
            return Ok();
        }
        catch (ArgumentOutOfRangeException)
        {
            return NotFound();
        }
        catch (InvalidOperationException)
        {
            return Conflict();
        }
        catch (ArgumentException ex)
        {
            return UnprocessableEntity(new ReviewAuditError("invalid_audit_chunk", ex.Message));
        }
    }

    [HttpPost("records/{recordId}/complete")]
    public IActionResult CompleteRecord(string recordId, [FromBody] ReviewAuditCompleteRequest? request)
    {
        if (request is null)
        {
            return UnprocessableEntity(
                new ReviewAuditError("invalid_audit_completion", "A completion body is required.")
            );
        }

        try
        {
            return Ok(Ingestion.Complete(recordId, request));
        }
        catch (UnparseableStructuredContentException)
        {
            return UnprocessableEntity(
                new ReviewAuditError("unparseable_structured_content", "Structured audit content is not valid JSON.")
            );
        }
        catch (UnsupportedSensitiveShapeException)
        {
            return UnprocessableEntity(
                new ReviewAuditError(
                    "unsupported_sensitive_shape",
                    "Structured audit content contains an unclassified sensitive field."
                )
            );
        }
        catch (ArgumentOutOfRangeException)
        {
            return NotFound();
        }
        catch (InvalidOperationException)
        {
            return Conflict();
        }
        catch (ArgumentException ex)
        {
            return UnprocessableEntity(new ReviewAuditError("invalid_audit_completion", ex.Message));
        }
    }

    [HttpGet("rounds/{roundId:long}/status")]
    public IActionResult GetRoundStatus(long roundId)
    {
        if (Store.GetEngagementRound(roundId) is null)
        {
            return NotFound();
        }

        return Ok(
            new ReviewAuditRoundStatus(
                roundId,
                [
                    .. Store
                        .ListAuditRecordsForRound(roundId)
                        .Select(record => new ReviewAuditRecordStatus(
                            record.Id,
                            record.ContentSha256,
                            record.ByteCount,
                            record.SourceContentSha256,
                            record.SourceByteCount,
                            record.CaptureOutcome.ToString(),
                            record.CapturedAtUtc,
                            record.CompletedAtUtc
                        )),
                ]
            )
        );
    }
}

internal sealed class ReviewAuditIngestionService
{
    private readonly ReviewStore _store;

    public ReviewAuditIngestionService(ReviewStore store) => _store = store;

    private const string ExcludedSecret = "__REVIEW_AUDIT_SECRET_EXCLUDED__";

    private static readonly HashSet<string> KnownSecretFields =
    [
        "authorization",
        "apikey",
        "accesstoken",
        "clientsecret",
        "password",
        "xsbxappkey",
        "xs2sauth",
    ];

    private static readonly HashSet<string> UnsupportedSensitiveFields =
    [
        "credential",
        "credentials",
        "privatekey",
        "refreshtoken",
        "signingkey",
    ];

    private readonly ConcurrentDictionary<string, PendingAuditRecord> _pending = new(StringComparer.Ordinal);

    public void Begin(string recordId, ReviewAuditRecordRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        var pending = PendingAuditRecord.Create(request);
        if (_store.GetAuditRecord(recordId) is { } persisted)
        {
            if (persisted.CompletedAtUtc is not null || !SourceMetadataMatches(persisted, request))
            {
                throw new InvalidOperationException(
                    $"Audit source record '{recordId}' is already persisted; query status to adopt its accepted identity."
                );
            }
        }

        _pending.AddOrUpdate(
            recordId,
            pending,
            (_, existing) =>
                existing.Metadata == request
                    ? existing
                    : throw new InvalidOperationException(
                        $"Audit source record '{recordId}' conflicts with staged metadata."
                    )
        );
    }

    public void PutChunk(string recordId, int chunkIndex, ReviewAuditChunkRequest request)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(chunkIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(request.ByteOffset);
        if (!_pending.TryGetValue(recordId, out var pending))
        {
            var persisted = _store.GetAuditRecord(recordId);
            if (persisted is null)
            {
                throw new ArgumentOutOfRangeException(nameof(recordId));
            }

            if (
                persisted.CompletedAtUtc is null
                && StringComparer.Ordinal.Equals(persisted.ContentSha256, persisted.SourceContentSha256)
                && persisted.ByteCount == persisted.SourceByteCount
            )
            {
                _store.AppendAuditChunk(recordId, chunkIndex, request.ByteOffset, request.Content);
                return;
            }

            throw new InvalidOperationException(
                $"Audit source record '{recordId}' is already persisted; query status to adopt its accepted identity."
            );
        }

        pending.PutChunk(chunkIndex, request.ByteOffset, request.Content);
    }

    public AuditSourceRecord Complete(string recordId, ReviewAuditCompleteRequest request)
    {
        if (!_pending.TryGetValue(recordId, out var pending))
        {
            var existing = _store.GetAuditRecord(recordId) ?? throw new ArgumentOutOfRangeException(nameof(recordId));
            if (
                existing.SourceByteCount != request.ByteCount
                || !StringComparer.Ordinal.Equals(existing.SourceContentSha256, request.ContentSha256)
            )
            {
                throw new InvalidOperationException($"Audit source record '{recordId}' completion conflicts.");
            }

            if (
                existing.CaptureOutcome == AuditSourceCaptureOutcome.Gap
                && existing.GapReasonCode is "unparseable_structured_content" or "unsupported_sensitive_shape"
            )
            {
                throw new InvalidOperationException(
                    $"Audit source record '{recordId}' was terminally rejected as {existing.GapReasonCode}."
                );
            }

            if (existing.CompletedAtUtc is null)
            {
                return _store.CompleteAuditRecord(
                    recordId,
                    existing.ContentSha256,
                    existing.ByteCount,
                    request.CompletedAtUtc
                );
            }

            if (existing.CompletedAtUtc != request.CompletedAtUtc)
            {
                throw new InvalidOperationException($"Audit source record '{recordId}' completion conflicts.");
            }

            return existing;
        }

        try
        {
            var original = pending.Assemble();
            if (
                original.LongLength != request.ByteCount
                || !StringComparer.Ordinal.Equals(Sha256(original), request.ContentSha256)
            )
            {
                throw new InvalidOperationException(
                    $"Audit source record '{recordId}' content conflicts with completion."
                );
            }

            var accepted = ExcludeStructuredSecrets(pending.Metadata.RecordType, original);
            var metadata = pending.Metadata;
            var acceptedHash = Sha256(accepted);
            var source = ToSourceRecord(
                recordId,
                metadata,
                acceptedHash,
                accepted.LongLength,
                request.ContentSha256,
                request.ByteCount,
                request.CompletedAtUtc
            );

            _store.BeginAuditRecord(source);
            for (var offset = 0; offset < accepted.Length; offset += ReviewStore.AuditChunkBytes)
            {
                var count = Math.Min(ReviewStore.AuditChunkBytes, accepted.Length - offset);
                _store.AppendAuditChunk(
                    recordId,
                    offset / ReviewStore.AuditChunkBytes,
                    offset,
                    accepted.AsSpan(offset, count)
                );
            }

            var persisted = metadata.CaptureOutcome.Equals("Complete", StringComparison.OrdinalIgnoreCase)
                ? _store.CompleteAuditRecord(recordId, acceptedHash, accepted.LongLength, request.CompletedAtUtc)
                : _store.GetAuditRecord(recordId)!;
            _ = _pending.TryRemove(recordId, out _);
            return persisted;
        }
        catch (System.Text.Json.JsonException ex)
        {
            RecordTerminalGap(recordId, pending, request.CompletedAtUtc, "unparseable_structured_content");
            throw new UnparseableStructuredContentException(ex);
        }
        catch (UnsupportedSensitiveShapeException)
        {
            RecordTerminalGap(recordId, pending, request.CompletedAtUtc, "unsupported_sensitive_shape");
            throw;
        }
    }

    private void RecordTerminalGap(
        string recordId,
        PendingAuditRecord pending,
        DateTimeOffset completedAtUtc,
        string reasonCode
    )
    {
        var metadata = pending.Metadata;
        var emptyHash = Sha256([]);
        _store.BeginAuditRecord(
            ToSourceRecord(
                recordId,
                metadata with
                {
                    CaptureOutcome = AuditSourceCaptureOutcome.Gap.ToString(),
                    GapReasonCode = reasonCode,
                },
                emptyHash,
                0,
                metadata.ContentSha256,
                metadata.ByteCount,
                completedAtUtc
            )
        );
        _ = _pending.TryRemove(recordId, out _);
    }

    private static bool SourceMetadataMatches(AuditSourceRecord persisted, ReviewAuditRecordRequest request) =>
        persisted.EngagementRoundId == request.EngagementRoundId
        && StringComparer.Ordinal.Equals(persisted.ThreadId, request.ThreadId)
        && StringComparer.Ordinal.Equals(persisted.RunId, request.RunId)
        && StringComparer.Ordinal.Equals(persisted.GenerationId, request.GenerationId)
        && StringComparer.Ordinal.Equals(persisted.ParentTurnId, request.ParentTurnId)
        && persisted.Sequence == request.Sequence
        && StringComparer.Ordinal.Equals(persisted.RecordType, request.RecordType)
        && StringComparer.Ordinal.Equals(persisted.Role, request.Role)
        && StringComparer.Ordinal.Equals(persisted.ModelId, request.ModelId)
        && StringComparer.Ordinal.Equals(persisted.ProviderId, request.ProviderId)
        && StringComparer.Ordinal.Equals(persisted.SourceContentSha256, request.ContentSha256)
        && persisted.SourceByteCount == request.ByteCount
        && StringComparer.OrdinalIgnoreCase.Equals(persisted.CaptureOutcome.ToString(), request.CaptureOutcome)
        && StringComparer.Ordinal.Equals(persisted.GapReasonCode, request.GapReasonCode)
        && persisted.CapturedAtUtc == request.CapturedAtUtc;

    private static AuditSourceRecord ToSourceRecord(
        string recordId,
        ReviewAuditRecordRequest metadata,
        string contentSha256,
        long byteCount,
        string sourceContentSha256,
        long sourceByteCount,
        DateTimeOffset completedAtUtc
    )
    {
        var outcome = Enum.Parse<AuditSourceCaptureOutcome>(metadata.CaptureOutcome, ignoreCase: true);
        return new AuditSourceRecord(
            recordId,
            metadata.EngagementRoundId,
            metadata.ThreadId,
            metadata.RunId,
            metadata.GenerationId,
            metadata.ParentTurnId,
            metadata.Sequence,
            metadata.RecordType,
            metadata.Role,
            metadata.ModelId,
            metadata.ProviderId,
            contentSha256,
            byteCount,
            sourceContentSha256,
            sourceByteCount,
            outcome,
            metadata.GapReasonCode,
            metadata.CapturedAtUtc,
            outcome == AuditSourceCaptureOutcome.Complete ? null : completedAtUtc
        );
    }

    private static byte[] ExcludeStructuredSecrets(string recordType, byte[] content)
    {
        if (recordType is not ("model_request" or "model_response" or "tool_result") || content.Length == 0)
        {
            return content;
        }

        using var document = JsonDocument.Parse(content);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteExcluded(writer, document.RootElement);
        }

        return stream.ToArray();
    }

    private static void WriteExcluded(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    var normalized = Normalize(property.Name);
                    if (KnownSecretFields.Contains(normalized))
                    {
                        writer.WriteStringValue(ExcludedSecret);
                    }
                    else
                    {
                        if (UnsupportedSensitiveFields.Contains(normalized))
                        {
                            throw new UnsupportedSensitiveShapeException();
                        }

                        if (property.Value.ValueKind == JsonValueKind.String && normalized == "functionargs")
                        {
                            writer.WriteStringValue(ExcludeRequiredEmbeddedJson(property.Value.GetString()));
                        }
                        else if (
                            property.Value.ValueKind == JsonValueKind.String
                            && normalized == "result"
                            && TryExcludeEmbeddedJson(property.Value.GetString(), out var excluded)
                        )
                        {
                            writer.WriteStringValue(excluded);
                        }
                        else
                        {
                            WriteExcluded(writer, property.Value);
                        }
                    }
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteExcluded(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.Undefined:
            case JsonValueKind.String:
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                element.WriteTo(writer);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(element), "Unknown JSON value kind.");
        }
    }

    private static string ExcludeRequiredEmbeddedJson(string? value)
    {
        if (!TryExcludeEmbeddedJson(value, out var excluded))
        {
            throw new UnsupportedSensitiveShapeException();
        }

        return excluded;
    }

    private static bool TryExcludeEmbeddedJson(string? value, out string excluded)
    {
        excluded = "";
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteExcluded(writer, document.RootElement);
            }

            excluded = System.Text.Encoding.UTF8.GetString(stream.ToArray());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Normalize(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant));

    private static string Sha256(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private sealed class PendingAuditRecord
    {
        private readonly object _gate = new();
        private readonly SortedDictionary<int, AuditChunk> _chunks = [];

        private PendingAuditRecord(ReviewAuditRecordRequest metadata) => Metadata = metadata;

        public ReviewAuditRecordRequest Metadata { get; }

        public static PendingAuditRecord Create(ReviewAuditRecordRequest metadata) => new(metadata);

        public void PutChunk(int index, long byteOffset, byte[] content)
        {
            var chunk = new AuditChunk(byteOffset, [.. content]);
            lock (_gate)
            {
                if (_chunks.TryGetValue(index, out var existing) && !existing.HasSameContent(chunk))
                {
                    throw new InvalidOperationException("An audit chunk replay conflicts with staged bytes.");
                }

                _chunks[index] = chunk;
            }
        }

        public byte[] Assemble()
        {
            lock (_gate)
            {
                using var stream = new MemoryStream();
                var expectedIndex = 0;
                long expectedOffset = 0;
                foreach (var (index, chunk) in _chunks)
                {
                    if (index != expectedIndex || chunk.ByteOffset != expectedOffset)
                    {
                        throw new InvalidOperationException("Audit chunks are not contiguous.");
                    }

                    stream.Write(chunk.Content);
                    expectedIndex++;
                    expectedOffset += chunk.Content.LongLength;
                }

                return stream.ToArray();
            }
        }

        private sealed record AuditChunk(long ByteOffset, byte[] Content)
        {
            public bool HasSameContent(AuditChunk other) =>
                ByteOffset == other.ByteOffset && Content.AsSpan().SequenceEqual(other.Content);
        }
    }
}

public sealed record ReviewAuditRecordRequest(
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
    string CaptureOutcome,
    string? GapReasonCode,
    DateTimeOffset CapturedAtUtc
);

public sealed record ReviewAuditChunkRequest(long ByteOffset, byte[] Content);

public sealed record ReviewAuditCompleteRequest(string ContentSha256, long ByteCount, DateTimeOffset CompletedAtUtc);

public sealed record ReviewAuditRecordStatus(
    string RecordId,
    string ContentSha256,
    long ByteCount,
    string SourceContentSha256,
    long SourceByteCount,
    string CaptureOutcome,
    DateTimeOffset CapturedAtUtc,
    DateTimeOffset? CompletedAtUtc
);

public sealed record ReviewAuditRoundStatus(long RoundId, IReadOnlyList<ReviewAuditRecordStatus> Records);

public sealed record ReviewAuditError(string Code, string Message);

internal sealed class UnsupportedSensitiveShapeException : InvalidOperationException;

internal sealed class UnparseableStructuredContentException(Exception innerException)
    : InvalidOperationException("Structured audit content is not valid JSON.", innerException);
