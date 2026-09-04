using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using AchieveAi.LmDotnetTools.LmMultiTurn.Audit;

namespace LmStreaming.Sample.Services;

/// <summary>The delivery state of exact audit records for one review round.</summary>
public enum ReviewAuditDeliveryStatus
{
    Complete,
    Gap,
    Pending,
    Unavailable,
}

/// <summary>
/// Delivers exact multi-turn audit records to the review daemon's authenticated chunk-ingestion surface.
/// </summary>
public sealed class ReviewAuditBridge : IMultiTurnAuditSink
{
    public const int ChunkBytes = 64 * 1024;
    public const string AuthHeaderName = "X-Review-Bridge-Auth";

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new(
        System.Text.Json.JsonSerializerDefaults.Web
    );

    private readonly HttpClient _httpClient;
    private readonly string _secret;
    private readonly int _maxAttempts;
    private readonly TimeSpan _retryDelay;
    private readonly TimeSpan _attemptTimeout;
    private readonly ConcurrentDictionary<string, RoundDeliveryState> _rounds = new(StringComparer.Ordinal);

    public ReviewAuditBridge(
        HttpClient httpClient,
        string secret,
        int maxAttempts = 3,
        TimeSpan? retryDelay = null,
        TimeSpan? attemptTimeout = null
    )
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        RejectForwardedCredentials(_httpClient.DefaultRequestHeaders);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        _secret = secret;
        _maxAttempts = maxAttempts;
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(100);
        _attemptTimeout = attemptTimeout ?? TimeSpan.FromSeconds(30);
        if (_attemptTimeout <= TimeSpan.Zero && _attemptTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptTimeout));
        }
    }

    public ReviewAuditDeliveryStatus GetRoundStatus(MultiTurnAuditScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return _rounds.TryGetValue(RoundKey(scope), out var state) ? state.Status : ReviewAuditDeliveryStatus.Pending;
    }

    public async ValueTask RecordAsync(ModelTurnAuditRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var state = _rounds.GetOrAdd(RoundKey(record.Scope), _ => new RoundDeliveryState());
        state.Begin();
        try
        {
            var roundId = ParseScopeId(record.Scope.RoundId, nameof(record.Scope.RoundId));
            _ = ParseScopeId(record.Scope.EngagementId, nameof(record.Scope.EngagementId));
            ValidateRecord(record);

            await SendAsync(
                    HttpMethod.Post,
                    $"api/review-audit/records/{Uri.EscapeDataString(record.RecordId)}",
                    new ReviewAuditRecordRequest(
                        roundId,
                        record.ThreadId,
                        record.RunId,
                        record.GenerationId,
                        record.ParentTurnId,
                        record.Sequence,
                        record.RecordType,
                        record.Role,
                        record.ModelId,
                        record.ProviderId,
                        record.ContentSha256,
                        record.ByteCount,
                        record.Outcome.ToString(),
                        record.GapReason,
                        record.CapturedAtUtc
                    ),
                    readResponse: false,
                    cancellationToken
                )
                .ConfigureAwait(false);

            var content = record.Content;
            for (var offset = 0; offset < content.Length; offset += ChunkBytes)
            {
                var count = Math.Min(ChunkBytes, content.Length - offset);
                await SendAsync(
                        HttpMethod.Put,
                        $"api/review-audit/records/{Uri.EscapeDataString(record.RecordId)}/chunks/{offset / ChunkBytes}",
                        new ReviewAuditChunkRequest(offset, content.Slice(offset, count).ToArray()),
                        readResponse: false,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }

            var receipt = await SendAsync<ReviewAuditReceipt>(
                    HttpMethod.Post,
                    $"api/review-audit/records/{Uri.EscapeDataString(record.RecordId)}/complete",
                    new ReviewAuditCompleteRequest(record.ContentSha256, record.ByteCount, record.CapturedAtUtc),
                    cancellationToken
                )
                .ConfigureAwait(false);
            VerifyReceipt(record, roundId, receipt);
            state.Complete(record.Outcome);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            state.Cancel();
            throw;
        }
        catch (AuditCaptureException)
        {
            state.Fail();
            throw;
        }
        catch (Exception ex)
        {
            state.Fail();
            throw new AuditCaptureException("Review audit delivery failed.", ex);
        }
    }

    private async Task SendAsync(
        HttpMethod method,
        string path,
        object body,
        bool readResponse,
        CancellationToken cancellationToken
    )
    {
        if (readResponse)
        {
            _ = await SendAsync<System.Text.Json.JsonElement>(method, path, body, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        using var response = await SendCoreAsync(method, path, body, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object body, CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(method, path, body, cancellationToken).ConfigureAwait(false);
        using var bodyCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_attemptTimeout != Timeout.InfiniteTimeSpan)
        {
            bodyCancellation.CancelAfter(_attemptTimeout);
        }

        try
        {
            return await System
                    .Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<T>(
                        response.Content,
                        JsonOptions,
                        bodyCancellation.Token
                    )
                    .ConfigureAwait(false)
                ?? throw DeliveryFailure("Review audit completion returned no receipt.");
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new AuditCaptureException("Review audit completion returned an invalid receipt.", ex);
        }
    }

    private async Task<HttpResponseMessage> SendCoreAsync(
        HttpMethod method,
        string path,
        object body,
        CancellationToken cancellationToken
    )
    {
        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(method, path)
            {
                Content = JsonContent.Create(body, options: JsonOptions),
            };
            _ = request.Headers.TryAddWithoutValidation(AuthHeaderName, _secret);

            HttpResponseMessage response;
            using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (_attemptTimeout != Timeout.InfiniteTimeSpan)
            {
                attemptCancellation.CancelAfter(_attemptTimeout);
            }

            try
            {
                response = await _httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (attempt < _maxAttempts)
            {
                await DelayBeforeRetryAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (OperationCanceledException ex)
            {
                throw new AuditCaptureException("Review audit delivery exhausted its transient retry budget.", ex);
            }
            catch (HttpRequestException) when (attempt < _maxAttempts)
            {
                await DelayBeforeRetryAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException ex)
            {
                throw new AuditCaptureException("Review audit delivery exhausted its transient retry budget.", ex);
            }

            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            if (!IsTransient(response.StatusCode) || attempt == _maxAttempts)
            {
                var statusCode = response.StatusCode;
                response.Dispose();
                throw DeliveryFailure(
                    IsTransient(statusCode)
                        ? "Review audit delivery exhausted its transient retry budget."
                        : $"Review audit delivery was permanently rejected with HTTP {(int)statusCode}."
                );
            }

            response.Dispose();
            await DelayBeforeRetryAsync(cancellationToken).ConfigureAwait(false);
        }

        throw DeliveryFailure("Review audit delivery exhausted its transient retry budget.");
    }

    private Task DelayBeforeRetryAsync(CancellationToken cancellationToken) =>
        _retryDelay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(_retryDelay, cancellationToken);

    private static void RejectForwardedCredentials(System.Net.Http.Headers.HttpRequestHeaders headers)
    {
        if (headers.Authorization is not null || headers.Contains("X-Sbx-App-Key") || headers.Contains("X-S2S-Auth"))
        {
            throw new ArgumentException(
                "The review audit HttpClient must not carry unrelated credential headers.",
                nameof(headers)
            );
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    private static void ValidateRecord(ModelTurnAuditRecord record)
    {
        if (record.Content.Length != record.ByteCount)
        {
            throw DeliveryFailure("Review audit record content length does not match its metadata.");
        }

        var contentHash = Convert
            .ToHexString(System.Security.Cryptography.SHA256.HashData(record.Content.Span))
            .ToLowerInvariant();
        if (!StringComparer.Ordinal.Equals(contentHash, record.ContentSha256))
        {
            throw DeliveryFailure("Review audit record content hash does not match its metadata.");
        }
    }

    private static void VerifyReceipt(ModelTurnAuditRecord record, long roundId, ReviewAuditReceipt receipt)
    {
        if (
            !StringComparer.Ordinal.Equals(receipt.Id, record.RecordId)
            || receipt.EngagementRoundId != roundId
            || receipt.ByteCount < 0
            || !IsLowercaseSha256(receipt.ContentSha256)
            || !ReceiptOutcomeMatches(receipt.CaptureOutcome, record.Outcome)
        )
        {
            throw DeliveryFailure("Review audit completion receipt does not match the delivered record.");
        }
    }

    private static bool IsLowercaseSha256(string value) =>
        value.Length == 64 && value.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static bool ReceiptOutcomeMatches(
        System.Text.Json.JsonElement receiptOutcome,
        AuditCaptureOutcome expected
    ) =>
        receiptOutcome.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => StringComparer.OrdinalIgnoreCase.Equals(
                receiptOutcome.GetString(),
                expected.ToString()
            ),
            System.Text.Json.JsonValueKind.Number => receiptOutcome.TryGetInt32(out var value)
                && value == (int)expected,
            _ => false,
        };

    private static long ParseScopeId(string value, string parameterName)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            throw DeliveryFailure($"Review audit scope {parameterName} must be numeric.");
        }

        return parsed;
    }

    private static AuditCaptureException DeliveryFailure(string message) =>
        new("review_audit_delivery", new InvalidOperationException(message));

    private static string RoundKey(MultiTurnAuditScope scope) => $"{scope.EngagementId}:{scope.RoundId}";

    private sealed class RoundDeliveryState
    {
        private readonly object _gate = new();
        private int _pending;
        private bool _hasComplete;
        private bool _hasGap;
        private bool _unavailable;

        public ReviewAuditDeliveryStatus Status
        {
            get
            {
                lock (_gate)
                {
                    if (_unavailable)
                    {
                        return ReviewAuditDeliveryStatus.Unavailable;
                    }

                    if (_pending > 0 || (!_hasComplete && !_hasGap))
                    {
                        return ReviewAuditDeliveryStatus.Pending;
                    }

                    return _hasGap ? ReviewAuditDeliveryStatus.Gap : ReviewAuditDeliveryStatus.Complete;
                }
            }
        }

        public void Begin()
        {
            lock (_gate)
            {
                _pending++;
            }
        }

        public void Complete(AuditCaptureOutcome outcome)
        {
            lock (_gate)
            {
                _pending--;
                if (outcome == AuditCaptureOutcome.Complete)
                {
                    _hasComplete = true;
                }
                else
                {
                    _hasGap = true;
                }
            }
        }

        public void Cancel()
        {
            lock (_gate)
            {
                _pending--;
            }
        }

        public void Fail()
        {
            lock (_gate)
            {
                _pending--;
                _unavailable = true;
            }
        }
    }

    private sealed record ReviewAuditRecordRequest(
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

    private sealed record ReviewAuditChunkRequest(long ByteOffset, byte[] Content);

    private sealed record ReviewAuditCompleteRequest(
        string ContentSha256,
        long ByteCount,
        DateTimeOffset CompletedAtUtc
    );

    private sealed record ReviewAuditReceipt(
        string Id,
        long EngagementRoundId,
        string ContentSha256,
        long ByteCount,
        System.Text.Json.JsonElement CaptureOutcome
    );
}
