using System.Security.Cryptography;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;

namespace CodeReviewDaemon.Sample.Tests.Persistence;

public sealed class ReviewStoreAuditTests
{
    private static readonly DateTimeOffset CapturedAt = new(2026, 9, 2, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Audit_sink_persists_the_public_multi_turn_record_contract()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var content = "sink-record"u8.ToArray();
        var source = PublicRecord(roundId, content, "source-sink-1");
        AchieveAi.LmDotnetTools.LmMultiTurn.Audit.IMultiTurnAuditSink sink = store;

        await sink.RecordAsync(source);

        store.ReadAuditContent(source.RecordId).Should().Equal(content);
    }

    [Fact]
    public async Task Audit_sink_honors_cancellation_before_writing()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var source = PublicRecord(roundId, "cancelled"u8.ToArray(), "source-cancelled-1");
        AchieveAi.LmDotnetTools.LmMultiTurn.Audit.IMultiTurnAuditSink sink = store;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var write = async () => await sink.RecordAsync(source, cancellation.Token);

        await write.Should().ThrowAsync<OperationCanceledException>();
        store.GetAuditRecord(source.RecordId).Should().BeNull();
    }

    [Fact]
    public void Store_persists_the_public_multi_turn_audit_contract_without_reinterpreting_fields()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var content = "exact-provider-message"u8.ToArray();
        var source = PublicRecord(roundId, content, "source-public-1");

        var stored = store.StoreAuditRecord(source);
        var replayed = store.StoreAuditRecord(source);

        replayed.Should().Be(stored);
        stored.Id.Should().Be(source.RecordId);
        stored.EngagementRoundId.Should().Be(roundId);
        stored.ParentTurnId.Should().Be("parent-1");
        stored.Sequence.Should().Be(7);
        store.ReadAuditContent(source.RecordId).Should().Equal(content);
    }

    [Fact]
    public void Store_rejects_an_audit_scope_for_a_different_round()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var source = new AchieveAi.LmDotnetTools.LmMultiTurn.Audit.ModelTurnAuditRecord(
            "source-public-1",
            new AchieveAi.LmDotnetTools.LmMultiTurn.Audit.MultiTurnAuditScope("1", (roundId + 1).ToString()),
            "thread-1",
            "run-1",
            "generation-1",
            null,
            1,
            AchieveAi.LmDotnetTools.LmMultiTurn.Audit.MultiTurnAuditRecordTypes.StreamGap,
            null,
            "claude-opus-5",
            "anthropic",
            ReadOnlyMemory<byte>.Empty,
            Sha256([]),
            0,
            AchieveAi.LmDotnetTools.LmMultiTurn.Audit.AuditCaptureOutcome.Gap,
            "stream_interrupted",
            CapturedAt
        );

        var storeRecord = () => store.StoreAuditRecord(source);

        storeRecord.Should().Throw<ArgumentException>().WithMessage("*round scope*");
    }

    [Fact]
    public void Complete_audit_record_reconstructs_all_chunks_without_clipping_the_tail()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var content = Enumerable.Range(0, 160 * 1024).Select(i => (byte)(i % 251)).ToArray();
        var tail = "audit-tail-sentinel"u8.ToArray();
        tail.CopyTo(content, content.Length - tail.Length);
        var record = CompleteRecord("source-1", roundId, content);

        store.BeginAuditRecord(record);
        AppendChunks(store, record.Id, content);
        store.CompleteAuditRecord(record.Id, record.ContentSha256, record.ByteCount, CapturedAt.AddMinutes(1));

        store.ReadAuditContent(record.Id).Should().Equal(content);
        store.ReadAuditContent(record.Id)[^tail.Length..].Should().Equal(tail);
        store.GetAuditRecord(record.Id)!.CompletedAtUtc.Should().Be(CapturedAt.AddMinutes(1));
        store.ListAuditRecordsForRound(roundId).Should().ContainSingle().Which.Id.Should().Be(record.Id);
    }

    [Fact]
    public void Shared_chunks_deduplicate_by_hash_and_replay_is_idempotent()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var sharedChunk = Enumerable.Range(0, ReviewStore.AuditChunkBytes).Select(i => (byte)(i % 239)).ToArray();
        var first = CompleteRecord("source-1", roundId, sharedChunk);
        var second = CompleteRecord("source-2", roundId, sharedChunk) with
        {
            GenerationId = "generation-2",
            CapturedAtUtc = CapturedAt.AddMinutes(1),
        };

        store.BeginAuditRecord(first);
        store.BeginAuditRecord(first);
        store.AppendAuditChunk(first.Id, 0, 0, sharedChunk);
        store.AppendAuditChunk(first.Id, 0, 0, sharedChunk);
        store.CompleteAuditRecord(first.Id, first.ContentSha256, first.ByteCount, CapturedAt.AddMinutes(1));
        store.CompleteAuditRecord(first.Id, first.ContentSha256, first.ByteCount, CapturedAt.AddMinutes(1));
        store.BeginAuditRecord(first).CompletedAtUtc.Should().Be(CapturedAt.AddMinutes(1));
        var conflictingCompletion = () =>
            store.CompleteAuditRecord(first.Id, first.ContentSha256, first.ByteCount, CapturedAt.AddMinutes(9));
        conflictingCompletion.Should().Throw<InvalidOperationException>();
        store.BeginAuditRecord(second);
        store.AppendAuditChunk(second.Id, 0, 0, sharedChunk);
        store.CompleteAuditRecord(second.Id, second.ContentSha256, second.ByteCount, CapturedAt.AddMinutes(2));

        store.CountAuditBlobs().Should().Be(1);
        store.ReadAuditContent(first.Id).Should().Equal(sharedChunk);
        store.ReadAuditContent(second.Id).Should().Equal(sharedChunk);
    }

    [Fact]
    public void Conflicting_replay_of_a_record_or_chunk_is_rejected()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var content = "first"u8.ToArray();
        var record = CompleteRecord("source-1", roundId, content);
        store.BeginAuditRecord(record);
        store.AppendAuditChunk(record.Id, 0, 0, content);

        var conflictingRecord = () => store.BeginAuditRecord(record with { RecordType = "tool_result" });
        var conflictingChunk = () => store.AppendAuditChunk(record.Id, 0, 0, "other"u8.ToArray());

        conflictingRecord.Should().Throw<InvalidOperationException>();
        conflictingChunk.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Incomplete_record_cannot_be_read_as_complete()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var content = "not-complete"u8.ToArray();
        var record = CompleteRecord("source-1", roundId, content);
        store.BeginAuditRecord(record);
        store.AppendAuditChunk(record.Id, 0, 0, content);

        var read = () => store.ReadAuditContent(record.Id);

        read.Should().Throw<InvalidOperationException>().WithMessage("*not complete*");
    }

    [Fact]
    public void Completed_or_gap_record_rejects_additional_chunks()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var content = "complete-content"u8.ToArray();
        var complete = CompleteRecord("source-1", roundId, content);
        store.BeginAuditRecord(complete);
        store.AppendAuditChunk(complete.Id, 0, 0, content);
        store.CompleteAuditRecord(complete.Id, complete.ContentSha256, complete.ByteCount, CapturedAt.AddMinutes(1));
        var gap = GapRecord("gap-1", roundId) with { GenerationId = "generation-2" };
        store.BeginAuditRecord(gap);

        var appendToComplete = () => store.AppendAuditChunk(complete.Id, 1, content.Length, "more"u8.ToArray());
        var appendToGap = () => store.AppendAuditChunk(gap.Id, 0, 0, "content"u8.ToArray());

        appendToComplete.Should().Throw<InvalidOperationException>();
        appendToGap.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Completion_rejects_wrong_hash_length_or_noncontiguous_chunks()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var content = "complete-content"u8.ToArray();
        var record = CompleteRecord("source-1", roundId, content);
        store.BeginAuditRecord(record);
        store.AppendAuditChunk(record.Id, 1, content.Length, content);

        var complete = () =>
            store.CompleteAuditRecord(record.Id, record.ContentSha256, record.ByteCount, CapturedAt.AddMinutes(1));

        complete.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Gap_record_has_no_content_and_requires_a_reason()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var missingReason = GapRecord("gap-1", roundId) with { GapReasonCode = null };

        var beginMissingReason = () => store.BeginAuditRecord(missingReason);

        beginMissingReason.Should().Throw<ArgumentException>();
        var gap = GapRecord("gap-2", roundId);
        store.BeginAuditRecord(gap);
        store.GetAuditRecord(gap.Id)!.CaptureOutcome.Should().Be(AuditSourceCaptureOutcome.Gap);
        store.ReadAuditContent(gap.Id).Should().BeEmpty();
    }

    [Fact]
    public void Child_runs_may_reuse_local_sequences_without_colliding()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var first = GapRecord("gap-1", roundId) with { ThreadId = "thread-1", RunId = "child-1", Sequence = 1 };
        var second = GapRecord("gap-2", roundId) with { ThreadId = "thread-2", RunId = "child-2", Sequence = 1 };

        store.BeginAuditRecord(first);
        store.BeginAuditRecord(second);

        store.ListAuditRecordsForRound(roundId).Select(record => record.Id).Should().Equal("gap-1", "gap-2");
    }

    [Fact]
    public void Records_in_the_same_lineage_are_listed_in_sequence_order_when_timestamps_tie()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var first = GapRecord("z-record", roundId) with { Sequence = 1 };
        var second = GapRecord("a-record", roundId) with { Sequence = 2 };

        store.BeginAuditRecord(second);
        store.BeginAuditRecord(first);

        store.ListAuditRecordsForRound(roundId).Select(record => record.Id).Should().Equal("z-record", "a-record");
    }

    [Theory]
    [InlineData("Gap")]
    [InlineData("Redacted")]
    public void Zero_content_terminal_records_reject_a_hash_that_does_not_address_empty_content(string outcomeName)
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var outcome = Enum.Parse<AuditSourceCaptureOutcome>(outcomeName);
        var terminal = GapRecord("terminal-1", roundId) with
        {
            CaptureOutcome = outcome,
            ContentSha256 = Sha256("not-empty"u8),
        };

        var begin = () => store.BeginAuditRecord(terminal);

        begin.Should().Throw<ArgumentException>().WithMessage("*empty content*");
    }

    [Fact]
    public void Redaction_is_versioned_reconstructable_and_does_not_replace_source()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var sourceContent = "token=secret"u8.ToArray();
        var redactedContent = "token=[redacted]"u8.ToArray();
        var source = CompleteRecord("source-1", roundId, sourceContent);
        store.BeginAuditRecord(source);
        AppendChunks(store, source.Id, sourceContent);
        store.CompleteAuditRecord(source.Id, source.ContentSha256, source.ByteCount, CapturedAt.AddMinutes(1));
        var redaction = new AuditRedactionRecord(
            "redaction-1",
            source.Id,
            1,
            Sha256(redactedContent),
            redactedContent.Length,
            "[[6,12]]",
            "credential",
            CapturedAt.AddMinutes(2)
        );

        store.CreateAuditRedaction(redaction, redactedContent);
        store.CreateAuditRedaction(redaction, redactedContent);
        var conflicting = () =>
            store.CreateAuditRedaction(redaction with { ReasonCode = "different" }, redactedContent);

        conflicting.Should().Throw<InvalidOperationException>();
        store.ReadAuditContent(source.Id).Should().Equal(sourceContent);
        store.ReadAuditRedactionContent(redaction.Id).Should().Equal(redactedContent);
    }

    [Fact]
    public void Legacy_gap_is_idempotent_and_explicit()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);

        var first = store.RecordLegacyAuditGap(roundId, "legacy-source-1", "legacy_record_unavailable", CapturedAt);
        var repeated = store.RecordLegacyAuditGap(roundId, "legacy-source-1", "legacy_record_unavailable", CapturedAt);

        repeated.Should().Be(first);
        first.CaptureOutcome.Should().Be(AuditSourceCaptureOutcome.Gap);
        first.GapReasonCode.Should().Be("legacy_record_unavailable");
    }

    private static AchieveAi.LmDotnetTools.LmMultiTurn.Audit.ModelTurnAuditRecord PublicRecord(
        long roundId,
        byte[] content,
        string recordId
    ) =>
        new(
            recordId,
            new AchieveAi.LmDotnetTools.LmMultiTurn.Audit.MultiTurnAuditScope("1", roundId.ToString()),
            "thread-1",
            "run-1",
            "generation-1",
            "parent-1",
            7,
            AchieveAi.LmDotnetTools.LmMultiTurn.Audit.MultiTurnAuditRecordTypes.ModelResponse,
            "assistant",
            "claude-opus-5",
            "anthropic",
            content,
            Sha256(content),
            content.Length,
            AchieveAi.LmDotnetTools.LmMultiTurn.Audit.AuditCaptureOutcome.Complete,
            null,
            CapturedAt
        );

    private static void AppendChunks(ReviewStore store, string recordId, byte[] content)
    {
        for (var offset = 0; offset < content.Length; offset += ReviewStore.AuditChunkBytes)
        {
            var count = Math.Min(ReviewStore.AuditChunkBytes, content.Length - offset);
            store.AppendAuditChunk(
                recordId,
                offset / ReviewStore.AuditChunkBytes,
                offset,
                content.AsSpan(offset, count)
            );
        }
    }

    private static AuditSourceRecord CompleteRecord(string id, long roundId, byte[] content) =>
        new(
            id,
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
            content.Length,
            Sha256(content),
            content.Length,
            AuditSourceCaptureOutcome.Complete,
            null,
            CapturedAt,
            null
        );

    private static AuditSourceRecord GapRecord(string id, long roundId) =>
        new(
            id,
            roundId,
            "thread-1",
            "run-1",
            "generation-1",
            null,
            1,
            "stream_gap",
            null,
            "claude-opus-5",
            "anthropic",
            Sha256([]),
            0,
            Sha256([]),
            0,
            AuditSourceCaptureOutcome.Gap,
            "stream_interrupted",
            CapturedAt,
            CapturedAt
        );

    private static string Sha256(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

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
}
