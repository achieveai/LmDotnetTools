extern alias lmstreaming;

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Audit;
using CodeReviewDaemon.Sample.Controllers;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using ReviewAuditBridge = lmstreaming::LmStreaming.Sample.Services.ReviewAuditBridge;
using ReviewAuditDeliveryStatus = lmstreaming::LmStreaming.Sample.Services.ReviewAuditDeliveryStatus;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

public sealed class ReviewAuditRouteTests
{
    private const string BridgeSecret = "audit-bridge-test-secret";
    private const string RecordId = "route-source-1";
    private static readonly DateTimeOffset CapturedAt = new(2026, 9, 2, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Enabled_ingestion_without_a_bridge_secret_refuses_startup()
    {
        using var factory = new DaemonWebAppFactory(enableReviewAuditIngestion: true);

        var start = () => _ = factory.Services;

        start.Should().Throw<InvalidOperationException>().WithMessage("*ReviewBridgeSecret*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-secret")]
    public async Task Missing_or_wrong_bridge_secret_returns_unauthorized(string? presentedSecret)
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/review-audit/rounds/1/status");
        if (presentedSecret is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Review-Bridge-Auth", presentedSecret);
        }

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Unknown_round_is_not_created_by_metadata_delivery()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();
        using var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/review-audit/records/{RecordId}",
            Metadata(roundId: 4_242, content: "{}"u8.ToArray())
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Identical_metadata_and_chunk_replay_is_idempotent_but_conflicting_replay_is_rejected()
    {
        using var factory = NewFactory();
        var store = factory.Services.GetRequiredService<ReviewStore>();
        var roundId = SeedRound(store);
        using var client = factory.CreateClient();
        var content = "{\"message\":\"exact\"}"u8.ToArray();

        using var begin = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/review-audit/records/{RecordId}",
            Metadata(roundId, content)
        );
        using var beginReplay = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/review-audit/records/{RecordId}",
            Metadata(roundId, content)
        );
        using var beginConflict = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/review-audit/records/{RecordId}",
            Metadata(roundId, content) with
            {
                Role = "user",
            }
        );
        using var chunk = await SendJsonAsync(
            client,
            HttpMethod.Put,
            $"/api/review-audit/records/{RecordId}/chunks/0",
            new AuditChunkRequest(0, content)
        );
        using var chunkReplay = await SendJsonAsync(
            client,
            HttpMethod.Put,
            $"/api/review-audit/records/{RecordId}/chunks/0",
            new AuditChunkRequest(0, content)
        );
        using var chunkConflict = await SendJsonAsync(
            client,
            HttpMethod.Put,
            $"/api/review-audit/records/{RecordId}/chunks/0",
            new AuditChunkRequest(0, "different"u8.ToArray())
        );
        using var complete = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/review-audit/records/{RecordId}/complete",
            new AuditCompleteRequest(Sha256(content), content.LongLength, CapturedAt.AddMinutes(1))
        );
        using var beginAfterComplete = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/review-audit/records/{RecordId}",
            Metadata(roundId, content)
        );
        using var chunkAfterComplete = await SendJsonAsync(
            client,
            HttpMethod.Put,
            $"/api/review-audit/records/{RecordId}/chunks/0",
            new AuditChunkRequest(0, content)
        );

        begin.StatusCode.Should().Be(HttpStatusCode.OK);
        beginReplay.StatusCode.Should().Be(HttpStatusCode.OK);
        beginConflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        chunk.StatusCode.Should().Be(HttpStatusCode.OK);
        chunkReplay.StatusCode.Should().Be(HttpStatusCode.OK);
        chunkConflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        complete.StatusCode.Should().Be(HttpStatusCode.OK);
        beginAfterComplete.StatusCode.Should().Be(HttpStatusCode.Conflict);
        chunkAfterComplete.StatusCode.Should().Be(HttpStatusCode.Conflict);
        store.ReadAuditContent(RecordId).Should().Equal(content);
    }

    [Fact]
    public async Task Decoded_chunk_larger_than_sixty_four_kibibytes_returns_payload_too_large()
    {
        using var factory = NewFactory();
        var roundId = SeedRound(factory.Services.GetRequiredService<ReviewStore>());
        using var client = factory.CreateClient();
        var content = new byte[ReviewStore.AuditChunkBytes + 1];
        using var begin = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/review-audit/records/{RecordId}",
            Metadata(roundId, content)
        );

        using var response = await SendJsonAsync(
            client,
            HttpMethod.Put,
            $"/api/review-audit/records/{RecordId}/chunks/0",
            new AuditChunkRequest(0, content)
        );

        begin.StatusCode.Should().Be(HttpStatusCode.OK);
        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Decoded_chunk_at_exactly_sixty_four_kibibytes_is_accepted()
    {
        using var factory = NewFactory();
        var roundId = SeedRound(factory.Services.GetRequiredService<ReviewStore>());
        using var client = factory.CreateClient();
        var content = new byte[ReviewStore.AuditChunkBytes];
        using var begin = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/review-audit/records/{RecordId}",
            Metadata(roundId, content)
        );

        using var response = await SendJsonAsync(
            client,
            HttpMethod.Put,
            $"/api/review-audit/records/{RecordId}/chunks/0",
            new AuditChunkRequest(0, content)
        );

        begin.StatusCode.Should().Be(HttpStatusCode.OK);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Structured_secret_fields_are_excluded_before_the_immutable_source_is_written()
    {
        using var factory = NewFactory();
        var store = factory.Services.GetRequiredService<ReviewStore>();
        var roundId = SeedRound(store);
        using var client = factory.CreateClient();
        const string rawJson =
            "{\"authorization\":\"Bearer fixture-a\",\"api_key\":\"fixture-b\",\"access_token\":\"fixture-c\","
            + "\"client_secret\":\"fixture-d\",\"password\":\"fixture-e\",\"X-Sbx-App-Key\":\"fixture-f\","
            + "\"X-S2S-Auth\":\"fixture-g\",\"ordinary\":\"keep-exactly\"}";
        var content = Encoding.UTF8.GetBytes(rawJson);
        using var begin = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/review-audit/records/{RecordId}",
            Metadata(roundId, content) with
            {
                RecordType = "tool_result",
            }
        );
        using var chunk = await SendJsonAsync(
            client,
            HttpMethod.Put,
            $"/api/review-audit/records/{RecordId}/chunks/0",
            new AuditChunkRequest(0, content)
        );
        using var complete = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/review-audit/records/{RecordId}/complete",
            new AuditCompleteRequest(Sha256(content), content.LongLength, CapturedAt.AddMinutes(1))
        );

        begin.StatusCode.Should().Be(HttpStatusCode.OK);
        chunk.StatusCode.Should().Be(HttpStatusCode.OK);
        complete.StatusCode.Should().Be(HttpStatusCode.OK);
        using var blindReplay = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/review-audit/records/{RecordId}/complete",
            new AuditCompleteRequest(Sha256(content), content.LongLength, CapturedAt.AddMinutes(1))
        );
        blindReplay
            .StatusCode.Should()
            .Be(
                HttpStatusCode.OK,
                "the persisted source-completion identity makes a lost accepted receipt replay-safe"
            );
        using var status = await SendAsync(client, HttpMethod.Get, $"/api/review-audit/rounds/{roundId}/status");
        var adopted = await status.Content.ReadFromJsonAsync<ReviewAuditRoundStatus>();

        var accepted = Encoding.UTF8.GetString(store.ReadAuditContent(RecordId));
        accepted.Should().Contain("keep-exactly");
        accepted.Should().Contain("__REVIEW_AUDIT_SECRET_EXCLUDED__");
        accepted.Should().NotContain("fixture-a");
        accepted.Should().NotContain("fixture-b");
        accepted.Should().NotContain("fixture-c");
        accepted.Should().NotContain("fixture-d");
        accepted.Should().NotContain("fixture-e");
        accepted.Should().NotContain("fixture-f");
        accepted.Should().NotContain("fixture-g");
        adopted.Should().NotBeNull();
        adopted!.Records.Should().ContainSingle();
        adopted.Records[0].ContentSha256.Should().Be(Sha256(store.ReadAuditContent(RecordId)));
        adopted.Records[0].ByteCount.Should().Be(store.ReadAuditContent(RecordId).LongLength);
        store.GetAuditRecord(RecordId)!.ContentSha256.Should().Be(Sha256(store.ReadAuditContent(RecordId)));
    }

    [Fact]
    public async Task Real_bridge_retries_a_lost_completion_response_against_the_real_controller()
    {
        using var factory = NewFactory();
        var store = factory.Services.GetRequiredService<ReviewStore>();
        var roundId = SeedRound(store);
        using var serverClient = factory.CreateClient();
        var completionAttempts = 0;
        using var bridgeClient = new HttpClient(
            new LoseFirstCompletionResponseHandler(serverClient, () => Interlocked.Increment(ref completionAttempts))
        )
        {
            BaseAddress = serverClient.BaseAddress,
        };
        var bridge = new ReviewAuditBridge(bridgeClient, BridgeSecret, maxAttempts: 2, retryDelay: TimeSpan.Zero);
        var original = "{\"authorization\":\"Bearer response-loss-fixture\",\"ordinary\":\"keep\"}"u8.ToArray();
        var sourceHash = Sha256(original);
        var source = new ModelTurnAuditRecord(
            RecordId,
            new MultiTurnAuditScope("1", roundId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            "thread-1",
            "run-1",
            "generation-1",
            null,
            1,
            MultiTurnAuditRecordTypes.ModelRequest,
            null,
            "claude-opus-5",
            "anthropic",
            original,
            sourceHash,
            original.LongLength,
            AuditCaptureOutcome.Complete,
            null,
            CapturedAt
        );

        await bridge.RecordAsync(source, CancellationToken.None);

        completionAttempts.Should().Be(2);
        bridge.GetRoundStatus(source.Scope).Should().Be(ReviewAuditDeliveryStatus.Complete);
        var accepted = store.GetAuditRecord(RecordId)!;
        accepted.SourceContentSha256.Should().Be(sourceHash);
        accepted.SourceByteCount.Should().Be(original.LongLength);
        using var statusResponse = await SendAsync(
            serverClient,
            HttpMethod.Get,
            $"/api/review-audit/rounds/{roundId}/status"
        );
        var status = await statusResponse.Content.ReadFromJsonAsync<ReviewAuditRoundStatus>();
        status!.Records.Should().ContainSingle();
        status.Records[0].SourceContentSha256.Should().Be(sourceHash);
        status.Records[0].SourceByteCount.Should().Be(original.LongLength);
        accepted.ContentSha256.Should().Be(Sha256(store.ReadAuditContent(RecordId)));
        accepted.ContentSha256.Should().NotBe(sourceHash);
        Encoding.UTF8.GetString(store.ReadAuditContent(RecordId)).Should().NotContain("response-loss-fixture");
    }

    [Fact]
    public async Task Lost_completion_response_can_retry_the_source_identity_and_adopt_the_accepted_excluded_identity()
    {
        using var factory = NewFactory();
        var store = factory.Services.GetRequiredService<ReviewStore>();
        var roundId = SeedRound(store);
        using var client = factory.CreateClient();
        var original = "{\"authorization\":\"Bearer response-loss-fixture\",\"ordinary\":\"keep\"}"u8.ToArray();
        var metadata = Metadata(roundId, original) with { RecordType = "model_request" };
        var completion = new AuditCompleteRequest(metadata.ContentSha256, metadata.ByteCount, CapturedAt.AddMinutes(1));
        using var begin = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/review-audit/records/{RecordId}",
            metadata
        );
        using var chunk = await SendJsonAsync(
            client,
            HttpMethod.Put,
            $"/api/review-audit/records/{RecordId}/chunks/0",
            new AuditChunkRequest(0, original)
        );

        // The first completion commits server-side. Its response is deliberately discarded, exactly like a
        // connection reset after the daemon accepted the request but before the bridge received the receipt.
        using (
            var lostResponse = await SendJsonAsync(
                client,
                HttpMethod.Post,
                $"/api/review-audit/records/{RecordId}/complete",
                completion
            )
        ) { }

        using var replay = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/review-audit/records/{RecordId}/complete",
            completion
        );
        var accepted = await replay.Content.ReadFromJsonAsync<AuditSourceRecord>();

        begin.StatusCode.Should().Be(HttpStatusCode.OK);
        chunk.StatusCode.Should().Be(HttpStatusCode.OK);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        accepted.Should().NotBeNull();
        accepted!.SourceContentSha256.Should().Be(metadata.ContentSha256);
        accepted.SourceByteCount.Should().Be(metadata.ByteCount);
        accepted.ContentSha256.Should().Be(Sha256(store.ReadAuditContent(RecordId)));
        accepted.ContentSha256.Should().NotBe(metadata.ContentSha256);
        Encoding.UTF8.GetString(store.ReadAuditContent(RecordId)).Should().NotContain("response-loss-fixture");
    }

    [Fact]
    public async Task Nested_tool_arguments_are_excluded_without_scanning_ordinary_prose()
    {
        using var factory = NewFactory();
        var store = factory.Services.GetRequiredService<ReviewStore>();
        var roundId = SeedRound(store);
        using var client = factory.CreateClient();
        var content = AuditMessageSerializer.SerializeMessage(
            new ToolCallMessage
            {
                FunctionName = "publish",
                FunctionArgs =
                    "{\"headers\":{\"Authorization\":\"Bearer nested-fixture\"},"
                    + "\"note\":\"the word password in prose is evidence, not a credential field\"}",
                ToolCallId = "tool-1",
            }
        );
        using var begin = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/review-audit/records/{RecordId}",
            Metadata(roundId, content) with
            {
                RecordType = "model_response",
            }
        );
        using var chunk = await SendJsonAsync(
            client,
            HttpMethod.Put,
            $"/api/review-audit/records/{RecordId}/chunks/0",
            new AuditChunkRequest(0, content)
        );
        using var complete = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/review-audit/records/{RecordId}/complete",
            new AuditCompleteRequest(Sha256(content), content.LongLength, CapturedAt.AddMinutes(1))
        );

        begin.StatusCode.Should().Be(HttpStatusCode.OK);
        chunk.StatusCode.Should().Be(HttpStatusCode.OK);
        complete.StatusCode.Should().Be(HttpStatusCode.OK);
        var accepted = Encoding.UTF8.GetString(store.ReadAuditContent(RecordId));
        accepted.Should().Contain("__REVIEW_AUDIT_SECRET_EXCLUDED__");
        accepted.Should().Contain("the word password in prose is evidence, not a credential field");
        accepted.Should().NotContain("nested-fixture");
    }

    [Fact]
    public async Task Invalid_top_level_json_is_rejected_and_staged_bytes_are_evicted()
    {
        using var factory = NewFactory();
        var store = factory.Services.GetRequiredService<ReviewStore>();
        var roundId = SeedRound(store);
        using var client = factory.CreateClient();
        var content = "not-json"u8.ToArray();
        var metadata = Metadata(roundId, content) with { RecordType = "model_response" };
        using var begin = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/review-audit/records/{RecordId}",
            metadata
        );
        using var chunk = await SendJsonAsync(
            client,
            HttpMethod.Put,
            $"/api/review-audit/records/{RecordId}/chunks/0",
            new AuditChunkRequest(0, content)
        );
        var completion = new AuditCompleteRequest(Sha256(content), content.LongLength, CapturedAt.AddMinutes(1));

        using var rejected = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/review-audit/records/{RecordId}/complete",
            completion
        );
        using var replay = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/review-audit/records/{RecordId}/complete",
            completion
        );

        begin.StatusCode.Should().Be(HttpStatusCode.OK);
        chunk.StatusCode.Should().Be(HttpStatusCode.OK);
        rejected.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await rejected.Content.ReadAsStringAsync()).Should().Contain("unparseable_structured_content");
        replay.StatusCode.Should().Be(HttpStatusCode.Conflict, "terminal rejection becomes a durable gap");
        var gap = store.GetAuditRecord(RecordId)!;
        gap.CaptureOutcome.Should().Be(AuditSourceCaptureOutcome.Gap);
        gap.GapReasonCode.Should().Be("unparseable_structured_content");
        store.ReadAuditContent(RecordId).Should().BeEmpty();
    }

    [Fact]
    public async Task Invalid_tool_argument_json_is_rejected_as_an_unsupported_sensitive_shape()
    {
        using var factory = NewFactory();
        var store = factory.Services.GetRequiredService<ReviewStore>();
        var roundId = SeedRound(store);
        using var client = factory.CreateClient();
        var content = AuditMessageSerializer.SerializeMessage(
            new ToolCallMessage
            {
                FunctionName = "publish",
                FunctionArgs = "not-json-and-unclassifiable",
                ToolCallId = "tool-1",
            }
        );
        using var begin = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/review-audit/records/{RecordId}",
            Metadata(roundId, content) with
            {
                RecordType = "model_response",
            }
        );
        using var chunk = await SendJsonAsync(
            client,
            HttpMethod.Put,
            $"/api/review-audit/records/{RecordId}/chunks/0",
            new AuditChunkRequest(0, content)
        );

        using var complete = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/review-audit/records/{RecordId}/complete",
            new AuditCompleteRequest(Sha256(content), content.LongLength, CapturedAt.AddMinutes(1))
        );

        begin.StatusCode.Should().Be(HttpStatusCode.OK);
        chunk.StatusCode.Should().Be(HttpStatusCode.OK);
        complete.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await complete.Content.ReadAsStringAsync()).Should().Contain("unsupported_sensitive_shape");
        using var replay = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/review-audit/records/{RecordId}/complete",
            new AuditCompleteRequest(Sha256(content), content.LongLength, CapturedAt.AddMinutes(1))
        );
        replay.StatusCode.Should().Be(HttpStatusCode.Conflict, "terminal rejection becomes a durable gap");
        var gap = store.GetAuditRecord(RecordId)!;
        gap.CaptureOutcome.Should().Be(AuditSourceCaptureOutcome.Gap);
        gap.GapReasonCode.Should().Be("unsupported_sensitive_shape");
        store.ReadAuditContent(RecordId).Should().BeEmpty();
    }

    [Fact]
    public async Task Unclassified_structured_sensitive_field_is_rejected_as_a_durable_gap()
    {
        using var factory = NewFactory();
        var store = factory.Services.GetRequiredService<ReviewStore>();
        var roundId = SeedRound(store);
        using var client = factory.CreateClient();
        var content = "{\"refresh_token\":\"fixture-secret\"}"u8.ToArray();
        using var begin = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/review-audit/records/{RecordId}",
            Metadata(roundId, content) with
            {
                RecordType = "model_request",
            }
        );
        using var chunk = await SendJsonAsync(
            client,
            HttpMethod.Put,
            $"/api/review-audit/records/{RecordId}/chunks/0",
            new AuditChunkRequest(0, content)
        );

        using var complete = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/review-audit/records/{RecordId}/complete",
            new AuditCompleteRequest(Sha256(content), content.LongLength, CapturedAt.AddMinutes(1))
        );

        begin.StatusCode.Should().Be(HttpStatusCode.OK);
        chunk.StatusCode.Should().Be(HttpStatusCode.OK);
        complete.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await complete.Content.ReadAsStringAsync()).Should().Contain("unsupported_sensitive_shape");
        var gap = store.GetAuditRecord(RecordId)!;
        gap.CaptureOutcome.Should().Be(AuditSourceCaptureOutcome.Gap);
        gap.GapReasonCode.Should().Be("unsupported_sensitive_shape");
        gap.SourceContentSha256.Should().Be(Sha256(content));
        gap.SourceByteCount.Should().Be(content.LongLength);
        gap.ContentSha256.Should().Be(Sha256([]));
        gap.ByteCount.Should().Be(0);
        store.ReadAuditContent(RecordId).Should().BeEmpty();
    }

    [Fact]
    public async Task Gap_record_persists_without_chunks_and_is_visible_in_status()
    {
        using var factory = NewFactory();
        var store = factory.Services.GetRequiredService<ReviewStore>();
        var roundId = SeedRound(store);
        using var client = factory.CreateClient();
        var emptyHash = Sha256([]);
        var metadata = Metadata(roundId, []) with
        {
            RecordType = "stream_gap",
            Role = null,
            CaptureOutcome = "Gap",
            GapReasonCode = "HttpIOException:ResponseEnded",
        };
        using var begin = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/review-audit/records/{RecordId}",
            metadata
        );
        using var complete = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/review-audit/records/{RecordId}/complete",
            new AuditCompleteRequest(emptyHash, 0, CapturedAt.AddMinutes(1))
        );
        using var replay = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/review-audit/records/{RecordId}/complete",
            new AuditCompleteRequest(emptyHash, 0, CapturedAt.AddMinutes(1))
        );
        using var status = await SendAsync(client, HttpMethod.Get, $"/api/review-audit/rounds/{roundId}/status");

        begin.StatusCode.Should().Be(HttpStatusCode.OK);
        complete.StatusCode.Should().Be(HttpStatusCode.OK);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        store.GetAuditRecord(RecordId)!.CaptureOutcome.Should().Be(AuditSourceCaptureOutcome.Gap);
        (await status.Content.ReadAsStringAsync()).Should().Contain("Gap");
    }

    [Fact]
    public async Task Chunk_without_rebegin_against_an_incomplete_sanitized_record_is_rejected()
    {
        using var factory = NewFactory();
        var store = factory.Services.GetRequiredService<ReviewStore>();
        var roundId = SeedRound(store);
        using var client = factory.CreateClient();
        var original = "{\"authorization\":\"Bearer must-not-persist\"}"u8.ToArray();
        var accepted = "{\"authorization\":\"__REVIEW_AUDIT_SECRET_EXCLUDED__\"}"u8.ToArray();
        store.BeginAuditRecord(
            new AuditSourceRecord(
                RecordId,
                roundId,
                "thread-1",
                "run-1",
                "generation-1",
                null,
                1,
                "model_response",
                "assistant",
                "claude-opus-5",
                "anthropic",
                Sha256(accepted),
                accepted.LongLength,
                Sha256(original),
                original.LongLength,
                AuditSourceCaptureOutcome.Complete,
                null,
                CapturedAt,
                null
            )
        );

        using var chunk = await SendJsonAsync(
            client,
            HttpMethod.Put,
            $"/api/review-audit/records/{RecordId}/chunks/0",
            new AuditChunkRequest(0, original)
        );

        chunk.StatusCode.Should().Be(HttpStatusCode.Conflict);
        store.CountAuditBlobs().Should().Be(0);
    }

    [Fact]
    public async Task Persisted_incomplete_record_can_resume_chunks_and_completion_after_process_restart()
    {
        using var factory = NewFactory();
        var store = factory.Services.GetRequiredService<ReviewStore>();
        var roundId = SeedRound(store);
        using var client = factory.CreateClient();
        var content = "{\"message\":\"resume-after-restart\"}"u8.ToArray();
        var completedAt = CapturedAt.AddMinutes(1);
        store.BeginAuditRecord(
            new AuditSourceRecord(
                RecordId,
                roundId,
                "thread-1",
                "run-1",
                "generation-1",
                null,
                1,
                "model_response",
                "assistant",
                "claude-opus-5",
                "anthropic",
                Sha256(content),
                content.LongLength,
                Sha256(content),
                content.LongLength,
                AuditSourceCaptureOutcome.Complete,
                null,
                CapturedAt,
                null
            )
        );

        using var chunk = await SendJsonAsync(
            client,
            HttpMethod.Put,
            $"/api/review-audit/records/{RecordId}/chunks/0",
            new AuditChunkRequest(0, content)
        );
        using var completion = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/review-audit/records/{RecordId}/complete",
            new AuditCompleteRequest(Sha256(content), content.LongLength, completedAt)
        );

        chunk.StatusCode.Should().Be(HttpStatusCode.OK);
        completion.StatusCode.Should().Be(HttpStatusCode.OK);
        store.GetAuditRecord(RecordId)!.CompletedAtUtc.Should().Be(completedAt);
        store.ReadAuditContent(RecordId).Should().Equal(content);
    }

    [Fact]
    public void Retry_after_process_restart_adopts_the_persisted_accepted_identity_from_status()
    {
        string connectionString;
        long roundId;
        var original = Encoding.UTF8.GetBytes("{\"authorization\":\"Bearer restart-fixture\"}");
        using (var database = new TempSqliteDatabase())
        {
            connectionString = database.ConnectionString;
            using (var firstStore = new ReviewStore(connectionString))
            {
                roundId = SeedRound(firstStore);
                var firstService = new ReviewAuditIngestionService(firstStore);
                var metadata = Metadata(roundId, original);
                firstService.Begin(
                    RecordId,
                    new ReviewAuditRecordRequest(
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
                        metadata.ContentSha256,
                        metadata.ByteCount,
                        metadata.CaptureOutcome,
                        metadata.GapReasonCode,
                        metadata.CapturedAtUtc
                    )
                );
                firstService.PutChunk(RecordId, 0, new ReviewAuditChunkRequest(0, original));
                _ = firstService.Complete(
                    RecordId,
                    new ReviewAuditCompleteRequest(Sha256(original), original.LongLength, CapturedAt.AddMinutes(1))
                );
            }

            using var restartedStore = new ReviewStore(connectionString);
            var accepted = restartedStore.GetAuditRecord(RecordId)!;
            accepted.ContentSha256.Should().NotBe(Sha256(original));
            restartedStore.ListAuditRecordsForRound(roundId).Should().ContainSingle().Which.Should().Be(accepted);
            Encoding.UTF8.GetString(restartedStore.ReadAuditContent(RecordId)).Should().NotContain("restart-fixture");
        }
    }

    [Fact]
    public async Task Later_redaction_writes_a_versioned_view_without_replacing_the_excluded_source()
    {
        using var factory = NewFactory();
        var store = factory.Services.GetRequiredService<ReviewStore>();
        var roundId = SeedRound(store);
        using var client = factory.CreateClient();
        var content = "{\"authorization\":\"Bearer later-fixture\",\"message\":\"keep\"}"u8.ToArray();
        using var begin = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/review-audit/records/{RecordId}",
            Metadata(roundId, content)
        );
        using var chunk = await SendJsonAsync(
            client,
            HttpMethod.Put,
            $"/api/review-audit/records/{RecordId}/chunks/0",
            new AuditChunkRequest(0, content)
        );
        using var complete = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/review-audit/records/{RecordId}/complete",
            new AuditCompleteRequest(Sha256(content), content.LongLength, CapturedAt.AddMinutes(1))
        );
        var sourceBeforeRedaction = store.ReadAuditContent(RecordId);
        var redactedView = "{\"authorization\":\"[removed-after-review]\",\"message\":\"keep\"}"u8.ToArray();
        var redaction = new AuditRedactionRecord(
            "redaction-route-1",
            RecordId,
            1,
            Sha256(redactedView),
            redactedView.LongLength,
            "[[18,50]]",
            "security_review",
            CapturedAt.AddMinutes(2)
        );

        _ = store.CreateAuditRedaction(redaction, redactedView);

        begin.StatusCode.Should().Be(HttpStatusCode.OK);
        chunk.StatusCode.Should().Be(HttpStatusCode.OK);
        complete.StatusCode.Should().Be(HttpStatusCode.OK);
        store.ReadAuditContent(RecordId).Should().Equal(sourceBeforeRedaction);
        store.ReadAuditRedactionContent(redaction.Id).Should().Equal(redactedView);
        sourceBeforeRedaction.Should().NotEqual(redactedView);
    }

    [Fact]
    public async Task Completion_and_status_are_replay_safe_and_never_return_source_content()
    {
        using var factory = NewFactory();
        var store = factory.Services.GetRequiredService<ReviewStore>();
        var roundId = SeedRound(store);
        using var client = factory.CreateClient();
        var content = "{\"message\":\"tail-sentinel\"}"u8.ToArray();
        var metadata = Metadata(roundId, content);
        var completion = new AuditCompleteRequest(metadata.ContentSha256, metadata.ByteCount, CapturedAt.AddMinutes(1));
        using var begin = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/review-audit/records/{RecordId}",
            metadata
        );
        using var chunk = await SendJsonAsync(
            client,
            HttpMethod.Put,
            $"/api/review-audit/records/{RecordId}/chunks/0",
            new AuditChunkRequest(0, content)
        );
        using var complete = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/review-audit/records/{RecordId}/complete",
            completion
        );
        using var replay = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/review-audit/records/{RecordId}/complete",
            completion
        );
        using var conflict = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/review-audit/records/{RecordId}/complete",
            completion with
            {
                ByteCount = completion.ByteCount + 1,
            }
        );
        using var status = await SendAsync(client, HttpMethod.Get, $"/api/review-audit/rounds/{roundId}/status");

        begin.StatusCode.Should().Be(HttpStatusCode.OK);
        chunk.StatusCode.Should().Be(HttpStatusCode.OK);
        complete.StatusCode.Should().Be(HttpStatusCode.OK);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        status.StatusCode.Should().Be(HttpStatusCode.OK);
        var statusJson = await status.Content.ReadAsStringAsync();
        statusJson.Should().Contain(RecordId);
        statusJson.Should().Contain("completedAtUtc");
        statusJson.Should().NotContain("tail-sentinel");
    }

    private static DaemonWebAppFactory NewFactory() =>
        new(enableReviewAuditIngestion: true, reviewBridgeSecret: BridgeSecret);

    private static AuditRecordRequest Metadata(long roundId, byte[] content) =>
        new(
            roundId,
            "thread-1",
            "run-1",
            "generation-1",
            ParentTurnId: null,
            Sequence: 1,
            RecordType: "model_response",
            Role: "assistant",
            ModelId: "claude-opus-5",
            ProviderId: "anthropic",
            ContentSha256: Sha256(content),
            ByteCount: content.LongLength,
            CaptureOutcome: "Complete",
            GapReasonCode: null,
            CapturedAtUtc: CapturedAt
        );

    private static async Task<HttpResponseMessage> SendJsonAsync<T>(
        HttpClient client,
        HttpMethod method,
        string route,
        T body
    )
    {
        using var request = new HttpRequestMessage(method, route) { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("X-Review-Bridge-Auth", BridgeSecret);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string route)
    {
        using var request = new HttpRequestMessage(method, route);
        request.Headers.TryAddWithoutValidation("X-Review-Bridge-Auth", BridgeSecret);
        return await client.SendAsync(request);
    }

    private static long SeedRound(ReviewStore store)
    {
        var repoId = store.EnsureRepo(
            new RepoIdentity
            {
                Provider = "github",
                OrgOrOwner = "achieveai",
                RepoName = "LmDotnetTools",
            }
        );
        var engagement = store.CreateOrGetEngagement(
            new PrEngagement(
                0,
                repoId,
                "github",
                "118",
                PrLifecycleState.Open,
                "head-1",
                "base-1",
                null,
                new ProviderActivityWatermark("github", CapturedAt, "comment-1"),
                null,
                null,
                null,
                null,
                null,
                null,
                CapturedAt
            )
        );
        return store
            .TryAdmitRound(
                new EngagementRound(
                    0,
                    engagement.Id,
                    EngagementRoundIntent.CodeReview,
                    EngagementRoundStatus.Pending,
                    "head-1",
                    "base-1",
                    null,
                    engagement.LatestActivity,
                    0,
                    null,
                    0,
                    null,
                    null,
                    null,
                    null,
                    null
                )
            )!
            .Id;
    }

    private static string Sha256(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private sealed record AuditRecordRequest(
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

    private sealed record AuditChunkRequest(long ByteOffset, byte[] Content);

    private sealed record AuditCompleteRequest(string ContentSha256, long ByteCount, DateTimeOffset CompletedAtUtc);

    private sealed class LoseFirstCompletionResponseHandler(HttpClient serverClient, Func<int> recordCompletionAttempt)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
            {
                _ = forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (request.Content is not null)
            {
                var content = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                forwarded.Content = new ByteArrayContent(content);
                foreach (var header in request.Content.Headers)
                {
                    _ = forwarded.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            var response = await serverClient.SendAsync(
                forwarded,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken
            );
            if (
                request.RequestUri!.AbsolutePath.EndsWith("/complete", StringComparison.Ordinal)
                && recordCompletionAttempt() == 1
            )
            {
                response.Dispose();
                throw new HttpRequestException("The first committed completion response was lost.");
            }

            return response;
        }
    }
}
