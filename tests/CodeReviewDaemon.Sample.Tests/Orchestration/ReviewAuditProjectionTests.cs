using System.Security.Cryptography;
using System.Text;
using AchieveAi.LmDotnetTools.LmMultiTurn.Audit;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

public sealed class ReviewAuditProjectionTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Twenty_kibibyte_source_remains_exact_while_markdown_is_bounded_and_discloses_omitted_bytes()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var content = Encoding.UTF8.GetBytes(new string('x', 20 * 1024) + "TAIL-SENTINEL");
        var source = StoreSource(store, roundId, "source-large", content);

        var projection = ReviewNotesArtifactBuilder.BuildAuditProjection(
            store,
            roundId,
            [new AuditSourceReference(source.Id, source.ContentSha256)]
        );

        store.ReadAuditContent(source.Id).Should().Equal(content);
        projection.Length.Should().BeLessThanOrEqualTo(UntrustedTranscriptText.MaxArtifactChars);
        projection.Should().Contain("source-large");
        projection.Should().Contain(source.ContentSha256);
        projection.Should().Contain("source reference(s) not named in this projection: 0");
        projection.Should().Contain("item(s) deliberately omitted from this projection: 0");
        projection.Should().MatchRegex(@"byte\(s\) deliberately omitted from this projection: [1-9][0-9,]*");
        projection
            .Should()
            .NotContain("TAIL-SENTINEL", "the tail remains available only through the exact source link");
    }

    [Theory]
    [InlineData("plain", "PLAIN-REASONING-SENTINEL")]
    [InlineData("summary", "SUMMARY-REASONING-SENTINEL")]
    [InlineData("encrypted", "ENCRYPTED-REASONING-SENTINEL")]
    public void Projection_never_renders_reasoning_payloads(string visibility, string sentinel)
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var content = Encoding.UTF8.GetBytes(
            $$"""{"$type":"reasoning","reasoning":"{{sentinel}}","role":"assistant","visibility":"{{visibility}}"}"""
        );
        var source = StoreSource(store, roundId, $"source-reasoning-{visibility}", content);

        var projection = ReviewNotesArtifactBuilder.BuildAuditProjection(store, roundId, [Reference(source)]);

        store.ReadAuditContent(source.Id).Should().Equal(content, "canonical source bytes remain available for replay");
        projection.Should().NotContain(sentinel);
        projection.Should().Contain("Reasoning payload withheld from projection");
        projection.Should().Contain(source.Id).And.Contain(source.ContentSha256);
        projection.Should().Contain("item(s) deliberately omitted from this projection: 1");
        projection.Should().Contain($"byte(s) deliberately omitted from this projection: {content.Length:N0}");
    }

    [Theory]
    [InlineData("plain", "PLAIN-REQUEST-REASONING-SENTINEL")]
    [InlineData("summary", "SUMMARY-REQUEST-REASONING-SENTINEL")]
    [InlineData("encrypted", "ENCRYPTED-REQUEST-REASONING-SENTINEL")]
    public void Projection_never_renders_reasoning_payloads_nested_in_model_requests(string visibility, string sentinel)
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var content = Encoding.UTF8.GetBytes(
            """{"messages":[{"runtime_type":"ReasoningMessage","message":{"$type":"reasoning","reasoning":"__SENTINEL__","role":"assistant","visibility":"__VISIBILITY__"}}]}"""
                .Replace("__SENTINEL__", sentinel, StringComparison.Ordinal)
                .Replace("__VISIBILITY__", visibility, StringComparison.Ordinal)
        );
        var source = StoreSource(
            store,
            roundId,
            $"request-reasoning-{visibility}",
            content,
            MultiTurnAuditRecordTypes.ModelRequest
        );

        var projection = ReviewNotesArtifactBuilder.BuildAuditProjection(store, roundId, [Reference(source)]);

        store
            .ReadAuditContent(source.Id)
            .Should()
            .Equal(content, "canonical request bytes remain available for replay");
        projection.Should().NotContain(sentinel);
        projection.Should().Contain("Reasoning payload withheld from projection");
        projection.Should().Contain(source.Id).And.Contain(source.ContentSha256);
        projection.Should().Contain("item(s) deliberately omitted from this projection: 1");
        projection.Should().Contain($"byte(s) deliberately omitted from this projection: {content.Length:N0}");
    }

    [Fact]
    public void Projection_withholds_reasoning_in_a_later_model_request_entry()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var content =
            """{"messages":[{"runtime_type":"TextMessage","message":{"$type":"text","text":"ordinary","role":"user"}},{"runtime_type":"ReasoningMessage","message":{"$type":"reasoning","reasoning":"LATER-REASONING-SENTINEL","role":"assistant","visibility":"encrypted"}}]}"""u8.ToArray();
        var source = StoreSource(
            store,
            roundId,
            "mixed-request-with-reasoning",
            content,
            MultiTurnAuditRecordTypes.ModelRequest
        );

        var projection = ReviewNotesArtifactBuilder.BuildAuditProjection(store, roundId, [Reference(source)]);

        projection.Should().NotContain("LATER-REASONING-SENTINEL");
        projection.Should().NotContain("ordinary", "the whole source record is the projection unit");
        projection.Should().Contain("Reasoning payload withheld from projection");
        projection.Should().Contain("item(s) deliberately omitted from this projection: 1");
        projection.Should().Contain($"byte(s) deliberately omitted from this projection: {content.Length:N0}");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    public void Projection_fails_closed_for_non_object_model_request_entries(string entry)
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var sentinel = "NON-OBJECT-REQUEST-SENTINEL";
        var content = Encoding.UTF8.GetBytes($$"""{"messages":[{{entry}}],"untrusted":"{{sentinel}}"}""");
        var source = StoreSource(
            store,
            roundId,
            $"non-object-request-{entry}",
            content,
            MultiTurnAuditRecordTypes.ModelRequest
        );

        var projection = ReviewNotesArtifactBuilder.BuildAuditProjection(store, roundId, [Reference(source)]);

        projection.Should().NotContain(sentinel);
        projection.Should().Contain("Model request payload withheld because its canonical shape could not be verified");
        projection.Should().Contain("item(s) deliberately omitted from this projection: 1");
    }

    [Fact]
    public void Projection_renders_verified_non_reasoning_model_request()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var content =
            """{"messages":[{"runtime_type":"TextMessage","message":{"$type":"text","text":"VISIBLE-REQUEST-SENTINEL","role":"user"}}]}"""u8.ToArray();
        var source = StoreSource(
            store,
            roundId,
            "verified-text-request",
            content,
            MultiTurnAuditRecordTypes.ModelRequest
        );

        var projection = ReviewNotesArtifactBuilder.BuildAuditProjection(store, roundId, [Reference(source)]);

        projection.Should().Contain("VISIBLE-REQUEST-SENTINEL");
        projection.Should().NotContain("payload withheld");
        projection.Should().Contain("item(s) deliberately omitted from this projection: 0");
    }

    [Theory]
    [InlineData("{not-json", "MALFORMED-REQUEST-SENTINEL")]
    [InlineData("{\"messages\":[{\"runtime_type\":\"TextMessage\"}]}", "AMBIGUOUS-REQUEST-SENTINEL")]
    public void Projection_fails_closed_when_model_request_shape_cannot_be_verified(
        string requestPrefix,
        string sentinel
    )
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var content = Encoding.UTF8.GetBytes(requestPrefix + sentinel);
        var source = StoreSource(
            store,
            roundId,
            "unverified-model-request",
            content,
            MultiTurnAuditRecordTypes.ModelRequest
        );

        var projection = ReviewNotesArtifactBuilder.BuildAuditProjection(store, roundId, [Reference(source)]);

        store.ReadAuditContent(source.Id).Should().Equal(content);
        projection.Should().NotContain(sentinel);
        projection.Should().Contain("Model request payload withheld because its canonical shape could not be verified");
        projection.Should().Contain("item(s) deliberately omitted from this projection: 1");
        projection.Should().Contain($"byte(s) deliberately omitted from this projection: {content.Length:N0}");
    }

    [Fact]
    public void Projection_distinguishes_deliberate_item_omission_from_unavailable_source()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var first = StoreSource(store, roundId, "source-first", Encoding.UTF8.GetBytes(new string('a', 10_000)));
        var second = StoreSource(store, roundId, "source-second", Encoding.UTF8.GetBytes(new string('b', 10_000)));
        var missing = new AuditSourceReference("source-missing", Sha256("missing"u8));

        var projection = ReviewNotesArtifactBuilder.BuildAuditProjection(
            store,
            roundId,
            [Reference(first), Reference(second), missing]
        );

        projection.Should().Contain("source-first");
        projection.Should().Contain("source-second");
        projection.Should().Contain("source-missing");
        projection.Should().MatchRegex(@"item\(s\) deliberately omitted from this projection: [1-9][0-9]*");
        projection.Should().Contain("GAP");
        projection.Should().Contain("source record unavailable");
        projection.Should().NotContain("source-missing\n(empty)");
    }

    [Fact]
    public void Projection_discloses_when_many_source_references_cannot_be_named_within_the_budget()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var references = Enumerable
            .Range(1, 120)
            .Select(index => new AuditSourceReference(
                $"missing-source-{index:D3}",
                Sha256(Encoding.UTF8.GetBytes($"missing-{index}"))
            ))
            .ToArray();

        var projection = ReviewNotesArtifactBuilder.BuildAuditProjection(store, roundId, references);

        projection.Length.Should().BeLessThanOrEqualTo(UntrustedTranscriptText.MaxArtifactChars);
        projection.Should().Contain("missing-source-001");
        projection.Should().NotContain("missing-source-120");
        projection.Should().MatchRegex(@"source reference\(s\) not named in this projection: [1-9][0-9]*");
        projection.Should().Contain("GAP");
    }

    [Fact]
    public void Truncated_body_has_an_in_place_omission_marker()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var source = StoreSource(store, roundId, "source-large", Encoding.UTF8.GetBytes(new string('x', 20 * 1024)));

        var projection = ReviewNotesArtifactBuilder.BuildAuditProjection(store, roundId, [Reference(source)]);

        projection.Should().Contain("[daemon: truncated —");
        projection.Should().Contain("further characters omitted]");
    }

    [Fact]
    public void Projection_truncation_does_not_split_a_utf16_surrogate_pair()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var content = string.Concat(Enumerable.Repeat("😀", 10_000));
        var source = StoreSource(store, roundId, "source-emoji", Encoding.UTF8.GetBytes(content));

        var projection = ReviewNotesArtifactBuilder.BuildAuditProjection(store, roundId, [Reference(source)]);

        projection.Length.Should().BeLessThanOrEqualTo(UntrustedTranscriptText.MaxArtifactChars);
        projection.Should().Contain("[daemon: truncated —");
        projection.Any(char.IsSurrogate).Should().BeTrue();
        var fenceBodyStart = projection.IndexOf("```text\n", StringComparison.Ordinal) + "```text\n".Length;
        var markerStart = projection.IndexOf("\n\n[daemon: truncated —", fenceBodyStart, StringComparison.Ordinal);
        var emittedPrefix = projection[fenceBodyStart..markerStart];
        ProjectionAccountingValue(projection, "byte(s) included in this projection")
            .Should()
            .Be(Encoding.UTF8.GetByteCount(emittedPrefix));
        for (var index = 0; index < projection.Length; index++)
        {
            if (char.IsHighSurrogate(projection[index]))
            {
                index.Should().BeLessThan(projection.Length - 1);
                char.IsLowSurrogate(projection[index + 1]).Should().BeTrue();
                index++;
            }
            else
            {
                char.IsLowSurrogate(projection[index]).Should().BeFalse();
            }
        }
    }

    [Fact]
    public void Projection_discloses_real_source_references_that_cannot_be_named_within_the_budget()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var references = Enumerable
            .Range(1, 120)
            .Select(index =>
                Reference(
                    StoreSource(store, roundId, $"real-source-{index:D3}", Encoding.UTF8.GetBytes($"body-{index}"))
                )
            )
            .ToArray();

        var projection = ReviewNotesArtifactBuilder.BuildAuditProjection(store, roundId, references);

        projection.Should().Contain("real-source-001");
        projection.Should().NotContain("real-source-120");
        projection.Should().MatchRegex(@"source reference\(s\) not named in this projection: [1-9][0-9]*");
        projection.Should().MatchRegex(@"item\(s\) deliberately omitted from this projection: [1-9][0-9]*");
    }

    [Fact]
    public void Projection_names_every_requested_source_when_the_complete_blocks_fit()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var references = Enumerable
            .Range(1, 20)
            .Select(index => new AuditSourceReference(
                $"missing-source-{index:D3}",
                Sha256(Encoding.UTF8.GetBytes($"missing-{index}"))
            ))
            .ToArray();

        var projection = ReviewNotesArtifactBuilder.BuildAuditProjection(store, roundId, references);

        foreach (var source in references)
        {
            projection.Should().Contain(source.SourceRecordId);
            projection.Should().Contain(source.ContentSha256);
        }

        projection.Should().Contain("source reference(s) not named in this projection: 0");
    }

    [Fact]
    public void Projection_rejects_a_reference_whose_hash_does_not_match_the_exact_source()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var roundId = SeedRound(store);
        var source = StoreSource(store, roundId, "source-1", "exact content"u8.ToArray());

        var build = () =>
            ReviewNotesArtifactBuilder.BuildAuditProjection(
                store,
                roundId,
                [new AuditSourceReference(source.Id, Sha256("mutated tail"u8))]
            );

        build.Should().Throw<InvalidOperationException>().WithMessage("*hash*");
    }

    private static long ProjectionAccountingValue(string projection, string label)
    {
        var prefix = $"- {label}: ";
        var line = projection
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(value => value.StartsWith(prefix, StringComparison.Ordinal));
        return long.Parse(
            line[prefix.Length..].Trim(),
            System.Globalization.NumberStyles.AllowThousands,
            System.Globalization.CultureInfo.InvariantCulture
        );
    }

    private static AuditSourceReference Reference(AuditSourceRecord source) => new(source.Id, source.ContentSha256);

    private static AuditSourceRecord StoreSource(
        ReviewStore store,
        long roundId,
        string id,
        byte[] content,
        string recordType = MultiTurnAuditRecordTypes.ModelResponse
    ) =>
        store.StoreAuditRecord(
            new ModelTurnAuditRecord(
                id,
                new MultiTurnAuditScope("1", roundId.ToString()),
                "thread-1",
                "run-1",
                id,
                null,
                SourceSequence(id),
                recordType,
                "assistant",
                "claude-opus-5",
                "anthropic",
                content,
                Sha256(content),
                content.Length,
                AuditCaptureOutcome.Complete,
                null,
                ObservedAt
            )
        );

    private static int SourceSequence(string id) =>
        id.StartsWith("real-source-", StringComparison.Ordinal)
            ? int.Parse(id["real-source-".Length..], System.Globalization.CultureInfo.InvariantCulture)
        : id == "source-second" ? 2
        : 1;

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
                new ProviderActivityWatermark("github", ObservedAt, "comment-1"),
                null,
                null,
                null,
                null,
                null,
                null,
                ObservedAt
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
}
