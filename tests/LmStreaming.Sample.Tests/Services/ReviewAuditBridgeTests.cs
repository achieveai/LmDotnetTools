using System.Collections.Concurrent;
using System.Net;
using AchieveAi.LmDotnetTools.LmMultiTurn.Audit;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

public sealed class ReviewAuditBridgeTests
{
    private const string BridgeSecret = "review-bridge-secret";

    [Fact]
    public async Task RecordAsync_sends_metadata_exact_ordered_chunks_and_completion_with_header_only_auth()
    {
        var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path.EndsWith("/complete", StringComparison.Ordinal)
                ? JsonResponse(
                    new
                    {
                        id = "record-1",
                        engagementRoundId = 31,
                        contentSha256 = Hash(Content),
                        byteCount = Content.LongLength,
                        captureOutcome = "Complete",
                    }
                )
                : JsonResponse(new { });
        });
        using var http = NewHttp(handler);
        var bridge = new ReviewAuditBridge(http, BridgeSecret, maxAttempts: 3, retryDelay: TimeSpan.Zero);

        await bridge.RecordAsync(Record(Content), CancellationToken.None);

        var requests = handler.Requests.ToArray();
        requests
            .Select(request => (request.Method, request.Path))
            .Should()
            .Equal(
                (HttpMethod.Post, "/api/review-audit/records/record-1"),
                (HttpMethod.Put, "/api/review-audit/records/record-1/chunks/0"),
                (HttpMethod.Put, "/api/review-audit/records/record-1/chunks/1"),
                (HttpMethod.Post, "/api/review-audit/records/record-1/complete")
            );
        requests.Should().OnlyContain(request => request.ReviewBridgeAuth == BridgeSecret);
        requests.Should().OnlyContain(request => !request.Body.Contains(BridgeSecret, StringComparison.Ordinal));

        var metadata = System.Text.Json.JsonDocument.Parse(requests[0].Body).RootElement;
        metadata.GetProperty("engagementRoundId").GetInt64().Should().Be(31);
        metadata.GetProperty("threadId").GetString().Should().Be("thread-1");
        metadata.GetProperty("contentSha256").GetString().Should().Be(Hash(Content));

        var firstChunk = System.Text.Json.JsonDocument.Parse(requests[1].Body).RootElement;
        var secondChunk = System.Text.Json.JsonDocument.Parse(requests[2].Body).RootElement;
        firstChunk.GetProperty("byteOffset").GetInt64().Should().Be(0);
        secondChunk.GetProperty("byteOffset").GetInt64().Should().Be(ReviewAuditBridge.ChunkBytes);
        firstChunk.GetProperty("content").GetBytesFromBase64().Should().Equal(Content[..ReviewAuditBridge.ChunkBytes]);
        secondChunk.GetProperty("content").GetBytesFromBase64().Should().Equal(Content[ReviewAuditBridge.ChunkBytes..]);

        var completion = System.Text.Json.JsonDocument.Parse(requests[^1].Body).RootElement;
        completion.GetProperty("contentSha256").GetString().Should().Be(Hash(Content));
        completion.GetProperty("byteCount").GetInt64().Should().Be(Content.LongLength);
        bridge.GetRoundStatus(new MultiTurnAuditScope("17", "31")).Should().Be(ReviewAuditDeliveryStatus.Complete);
    }

    [Theory]
    [InlineData("Authorization", "Bearer provider-oauth-token")]
    [InlineData("X-Sbx-App-Key", "sandbox-app-key")]
    [InlineData("X-S2S-Auth", "conversation-s2s-secret")]
    public void Constructor_rejects_an_http_client_that_would_forward_unrelated_credentials(
        string headerName,
        string headerValue
    )
    {
        var handler = new RecordingHandler(_ => JsonResponse(new { }));
        using var http = NewHttp(handler);
        http.DefaultRequestHeaders.TryAddWithoutValidation(headerName, headerValue);

        var act = () => new ReviewAuditBridge(http, BridgeSecret);

        act.Should().Throw<ArgumentException>().WithMessage("*credential header*");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task RecordAsync_accepts_the_real_controller_numeric_capture_outcome_receipt()
    {
        var handler = new RecordingHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/complete", StringComparison.Ordinal)
                ? JsonResponse(
                    new
                    {
                        id = "record-1",
                        engagementRoundId = 31,
                        contentSha256 = Hash(Content),
                        byteCount = Content.LongLength,
                        captureOutcome = 0,
                    }
                )
                : JsonResponse(new { })
        );
        using var http = NewHttp(handler);
        var bridge = new ReviewAuditBridge(http, BridgeSecret, retryDelay: TimeSpan.Zero);

        await bridge.RecordAsync(Record(Content), CancellationToken.None);

        bridge.GetRoundStatus(new MultiTurnAuditScope("17", "31")).Should().Be(ReviewAuditDeliveryStatus.Complete);
    }

    [Fact]
    public async Task RecordAsync_marks_gap_records_as_gap_after_an_accepted_receipt()
    {
        var handler = new RecordingHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/complete", StringComparison.Ordinal)
                ? JsonResponse(
                    new
                    {
                        id = "record-1",
                        engagementRoundId = 31,
                        contentSha256 = Hash([]),
                        byteCount = 0,
                        captureOutcome = "Gap",
                    }
                )
                : JsonResponse(new { })
        );
        using var http = NewHttp(handler);
        var bridge = new ReviewAuditBridge(http, BridgeSecret, retryDelay: TimeSpan.Zero);
        var gap = Record([]) with
        {
            Outcome = AuditCaptureOutcome.Gap,
            RecordType = MultiTurnAuditRecordTypes.StreamGap,
            GapReason = "HttpIOException:ResponseEnded",
        };

        await bridge.RecordAsync(gap, CancellationToken.None);

        bridge.GetRoundStatus(gap.Scope).Should().Be(ReviewAuditDeliveryStatus.Gap);
    }

    [Fact]
    public async Task RecordAsync_reports_pending_while_delivery_is_in_flight()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadataSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/review-audit/records/record-1")
            {
                metadataSeen.SetResult();
                await release.Task;
            }

            return request.RequestUri.AbsolutePath.EndsWith("/complete", StringComparison.Ordinal)
                ? JsonResponse(
                    new
                    {
                        id = "record-1",
                        engagementRoundId = 31,
                        contentSha256 = Hash(Content),
                        byteCount = Content.LongLength,
                        captureOutcome = "Complete",
                    }
                )
                : JsonResponse(new { });
        });
        using var http = NewHttp(handler);
        var bridge = new ReviewAuditBridge(http, BridgeSecret, retryDelay: TimeSpan.Zero);

        var delivery = bridge.RecordAsync(Record(Content), CancellationToken.None).AsTask();
        await metadataSeen.Task;

        bridge.GetRoundStatus(new MultiTurnAuditScope("17", "31")).Should().Be(ReviewAuditDeliveryStatus.Pending);
        release.SetResult();
        await delivery;
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task RecordAsync_retries_transient_failures_with_the_same_record_and_chunk_identity(
        HttpStatusCode transientStatus
    )
    {
        var failedOnce = false;
        var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/chunks/0", StringComparison.Ordinal) && !failedOnce)
            {
                failedOnce = true;
                return JsonResponse(new { }, transientStatus);
            }

            return path.EndsWith("/complete", StringComparison.Ordinal)
                ? JsonResponse(
                    new
                    {
                        id = "record-1",
                        engagementRoundId = 31,
                        contentSha256 = Hash(Content),
                        byteCount = Content.LongLength,
                        captureOutcome = "Complete",
                    }
                )
                : JsonResponse(new { });
        });
        using var http = NewHttp(handler);
        var bridge = new ReviewAuditBridge(http, BridgeSecret, maxAttempts: 3, retryDelay: TimeSpan.Zero);

        await bridge.RecordAsync(Record(Content), CancellationToken.None);

        handler.Requests.Count(request => request.Path.EndsWith("/chunks/0", StringComparison.Ordinal)).Should().Be(2);
        handler
            .Requests.Where(request => request.Path.EndsWith("/chunks/0", StringComparison.Ordinal))
            .Select(request => request.Body)
            .Distinct(StringComparer.Ordinal)
            .Should()
            .ContainSingle("a retry must replay the same indexed bytes");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordAsync_retries_transport_failures_with_the_same_request_body(bool cancellationShaped)
    {
        var failedOnce = false;
        var handler = new RecordingHandler(request =>
        {
            if (!failedOnce)
            {
                failedOnce = true;
                return Task.FromException<HttpResponseMessage>(
                    cancellationShaped
                        ? new TaskCanceledException("transport timeout")
                        : new HttpRequestException("connection reset")
                );
            }

            return Task.FromResult(
                request.RequestUri!.AbsolutePath.EndsWith("/complete", StringComparison.Ordinal)
                    ? JsonResponse(
                        new
                        {
                            id = "record-1",
                            engagementRoundId = 31,
                            contentSha256 = Hash(Content),
                            byteCount = Content.LongLength,
                            captureOutcome = "Complete",
                        }
                    )
                    : JsonResponse(new { })
            );
        });
        using var http = NewHttp(handler);
        var bridge = new ReviewAuditBridge(http, BridgeSecret, maxAttempts: 3, retryDelay: TimeSpan.Zero);

        await bridge.RecordAsync(Record(Content), CancellationToken.None);

        handler
            .Requests.Take(2)
            .Select(request => request.Body)
            .Should()
            .AllBeEquivalentTo(handler.Requests.First().Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task RecordAsync_treats_auth_and_conflict_rejections_as_permanent(HttpStatusCode status)
    {
        var handler = new RecordingHandler(_ => JsonResponse(new { }, status));
        using var http = NewHttp(handler);
        var bridge = new ReviewAuditBridge(http, BridgeSecret, maxAttempts: 3, retryDelay: TimeSpan.Zero);

        var act = () => bridge.RecordAsync(Record(Content), CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<AuditCaptureException>();
        handler.Requests.Should().ContainSingle("permanent failures must not be amplified by retries");
        bridge.GetRoundStatus(new MultiTurnAuditScope("17", "31")).Should().Be(ReviewAuditDeliveryStatus.Unavailable);
    }

    [Fact]
    public async Task RecordAsync_exhausts_bounded_transient_retries_and_marks_the_round_unavailable()
    {
        var handler = new RecordingHandler(_ => JsonResponse(new { }, HttpStatusCode.ServiceUnavailable));
        using var http = NewHttp(handler);
        var bridge = new ReviewAuditBridge(http, BridgeSecret, maxAttempts: 3, retryDelay: TimeSpan.Zero);

        var act = () => bridge.RecordAsync(Record(Content), CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<AuditCaptureException>();
        handler.Requests.Should().HaveCount(3);
        bridge.GetRoundStatus(new MultiTurnAuditScope("17", "31")).Should().Be(ReviewAuditDeliveryStatus.Unavailable);
    }

    [Fact]
    public async Task RecordAsync_accepts_the_server_identity_after_structured_secret_exclusion()
    {
        var accepted = System.Text.Encoding.UTF8.GetBytes("{\"authorization\":\"__REVIEW_AUDIT_SECRET_EXCLUDED__\"}");
        var handler = new RecordingHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/complete", StringComparison.Ordinal)
                ? JsonResponse(
                    new
                    {
                        id = "record-1",
                        engagementRoundId = 31,
                        contentSha256 = Hash(accepted),
                        byteCount = accepted.LongLength,
                        captureOutcome = "Complete",
                    }
                )
                : JsonResponse(new { })
        );
        using var http = NewHttp(handler);
        var bridge = new ReviewAuditBridge(http, BridgeSecret, retryDelay: TimeSpan.Zero);

        await bridge.RecordAsync(Record(Content), CancellationToken.None);

        bridge.GetRoundStatus(new MultiTurnAuditScope("17", "31")).Should().Be(ReviewAuditDeliveryStatus.Complete);
    }

    [Fact]
    public async Task RecordAsync_rejects_content_whose_hash_does_not_match_its_source_metadata()
    {
        var handler = new RecordingHandler(_ => JsonResponse(new { }));
        using var http = NewHttp(handler);
        var bridge = new ReviewAuditBridge(http, BridgeSecret, retryDelay: TimeSpan.Zero);
        var record = Record(Content) with { ContentSha256 = Hash([1, 2, 3]) };

        var act = () => bridge.RecordAsync(record, CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<AuditCaptureException>();
        handler.Requests.Should().BeEmpty("invalid source identity must fail before metadata is dispatched");
    }

    [Fact]
    public async Task RecordAsync_marks_the_round_unavailable_when_a_later_record_fails_source_validation()
    {
        var handler = new RecordingHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/complete", StringComparison.Ordinal)
                ? JsonResponse(
                    new
                    {
                        id = "record-1",
                        engagementRoundId = 31,
                        contentSha256 = Hash(Content),
                        byteCount = Content.LongLength,
                        captureOutcome = "Complete",
                    }
                )
                : JsonResponse(new { })
        );
        using var http = NewHttp(handler);
        var bridge = new ReviewAuditBridge(http, BridgeSecret, retryDelay: TimeSpan.Zero);
        await bridge.RecordAsync(Record(Content), CancellationToken.None);
        var invalid = Record(Content) with { RecordId = "record-2", ContentSha256 = Hash([1, 2, 3]) };

        var act = () => bridge.RecordAsync(invalid, CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<AuditCaptureException>();
        bridge.GetRoundStatus(invalid.Scope).Should().Be(ReviewAuditDeliveryStatus.Unavailable);
    }

    [Fact]
    public async Task RecordAsync_times_out_a_never_completing_attempt_within_the_bridge_budget()
    {
        var handler = new NeverCompletingHandler();
        using var http = NewHttp(handler);
        var bridge = new ReviewAuditBridge(
            http,
            BridgeSecret,
            maxAttempts: 1,
            retryDelay: TimeSpan.Zero,
            attemptTimeout: TimeSpan.FromMilliseconds(50)
        );

        var act = () => bridge.RecordAsync(Record(Content), CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<AuditCaptureException>();
        bridge.GetRoundStatus(new MultiTurnAuditScope("17", "31")).Should().Be(ReviewAuditDeliveryStatus.Unavailable);
    }

    [Fact]
    public async Task RecordAsync_times_out_a_never_completing_receipt_body_within_the_bridge_budget()
    {
        var handler = new StalledReceiptBodyHandler();
        using var http = NewHttp(handler);
        var bridge = new ReviewAuditBridge(
            http,
            BridgeSecret,
            maxAttempts: 1,
            retryDelay: TimeSpan.Zero,
            attemptTimeout: TimeSpan.FromMilliseconds(50)
        );

        var act = () => bridge.RecordAsync(Record(Content), CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<AuditCaptureException>();
        bridge.GetRoundStatus(new MultiTurnAuditScope("17", "31")).Should().Be(ReviewAuditDeliveryStatus.Unavailable);
    }

    [Fact]
    public async Task RecordAsync_propagates_caller_cancellation_without_marking_the_round_unavailable()
    {
        var handler = new NeverCompletingHandler();
        using var http = NewHttp(handler);
        var bridge = new ReviewAuditBridge(
            http,
            BridgeSecret,
            maxAttempts: 1,
            retryDelay: TimeSpan.Zero,
            attemptTimeout: TimeSpan.FromMinutes(1)
        );
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var act = () => bridge.RecordAsync(Record(Content), cancellation.Token).AsTask();

        await act.Should().ThrowAsync<OperationCanceledException>();
        bridge.GetRoundStatus(new MultiTurnAuditScope("17", "31")).Should().Be(ReviewAuditDeliveryStatus.Pending);
    }

    [Fact]
    public async Task RecordAsync_rejects_a_completion_receipt_with_the_wrong_record_identity()
    {
        var handler = new RecordingHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/complete", StringComparison.Ordinal)
                ? JsonResponse(
                    new
                    {
                        id = "different-record",
                        engagementRoundId = 31,
                        contentSha256 = Hash(Content),
                        byteCount = Content.LongLength,
                        captureOutcome = "Complete",
                    }
                )
                : JsonResponse(new { })
        );
        using var http = NewHttp(handler);
        var bridge = new ReviewAuditBridge(http, BridgeSecret, retryDelay: TimeSpan.Zero);

        var act = () => bridge.RecordAsync(Record(Content), CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<AuditCaptureException>();
        bridge.GetRoundStatus(new MultiTurnAuditScope("17", "31")).Should().Be(ReviewAuditDeliveryStatus.Unavailable);
    }

    [Fact]
    public async Task RecordAsync_is_safe_for_concurrent_records_in_the_same_round()
    {
        var handler = new RecordingHandler(request =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/complete", StringComparison.Ordinal))
            {
                return JsonResponse(new { });
            }

            var recordId = request.RequestUri.Segments[^2].TrimEnd('/');
            return JsonResponse(
                new
                {
                    id = recordId,
                    engagementRoundId = 31,
                    contentSha256 = Hash(Content),
                    byteCount = Content.LongLength,
                    captureOutcome = "Complete",
                }
            );
        });
        using var http = NewHttp(handler);
        var bridge = new ReviewAuditBridge(http, BridgeSecret, retryDelay: TimeSpan.Zero);
        var records = Enumerable.Range(0, 16).Select(index => Record(Content) with { RecordId = $"record-{index}" });

        await Task.WhenAll(records.Select(record => bridge.RecordAsync(record, CancellationToken.None).AsTask()));

        bridge.GetRoundStatus(new MultiTurnAuditScope("17", "31")).Should().Be(ReviewAuditDeliveryStatus.Complete);
        handler.Requests.Count(request => request.Path.EndsWith("/complete", StringComparison.Ordinal)).Should().Be(16);
    }

    private static readonly byte[] Content =
    [
        .. Enumerable.Range(0, ReviewAuditBridge.ChunkBytes + 11).Select(index => (byte)(index % 251)),
    ];

    private static ModelTurnAuditRecord Record(byte[] content) =>
        new(
            "record-1",
            new MultiTurnAuditScope("17", "31"),
            "thread-1",
            "run-1",
            "generation-1",
            null,
            1,
            MultiTurnAuditRecordTypes.ModelRequest,
            null,
            "model-1",
            "provider-1",
            content,
            Hash(content),
            content.LongLength,
            AuditCaptureOutcome.Complete,
            null,
            new DateTimeOffset(2026, 9, 2, 1, 2, 3, TimeSpan.Zero)
        );

    private static string Hash(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();

    private static HttpClient NewHttp(HttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("http://review-daemon/") };

    private static HttpResponseMessage JsonResponse(object value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(value),
                System.Text.Encoding.UTF8,
                "application/json"
            ),
        };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
            _respond = request => Task.FromResult(respond(request));

        public RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) => _respond = respond;

        public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Enqueue(
                new RecordedRequest(
                    request.Method,
                    request.RequestUri!.AbsolutePath,
                    body,
                    request.Headers.TryGetValues("X-Review-Bridge-Auth", out var values)
                        ? values.SingleOrDefault()
                        : null
                )
            );
            return await _respond(request);
        }
    }

    private sealed class NeverCompletingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancellation-aware delay completed unexpectedly.");
        }
    }

    private sealed class StalledReceiptBodyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                request.RequestUri!.AbsolutePath.EndsWith("/complete", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StreamContent(new NeverCompletingStream()),
                    }
                    : JsonResponse(new { })
            );
    }

    private sealed class NeverCompletingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancellation-aware stream read completed unexpectedly.");
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed record RecordedRequest(HttpMethod Method, string Path, string Body, string? ReviewBridgeAuth);
}
