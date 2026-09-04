using System.Text.Json;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// Reports are a projection of persisted typed rows. They must recompute, regenerate, and repeat
/// byte-for-byte without rerunning the analyst or verifier.
/// </summary>
public sealed class MergedCloseOutcomeWriterTests
{
    [Fact]
    public async Task Outcome_json_carries_every_item_with_its_labels_links_and_counts()
    {
        using var context = new WriterContext();
        context.SaveItems();

        await context.WriteAsync();

        using var document = JsonDocument.Parse(context.ReadJson());
        var root = document.RootElement;
        root.GetProperty("prId").GetString().Should().Be("118");
        root.GetProperty("counts").GetProperty("definitive").GetInt32().Should().Be(1);
        root.GetProperty("counts").GetProperty("indeterminate").GetInt32().Should().Be(1);

        var items = root.GetProperty("items").EnumerateArray().ToList();
        items
            .Select(item => item.GetProperty("candidateId").GetString())
            .Should()
            .Equal("observation:obs-finding", "question:q-1");
        var finding = items[0];
        finding.GetProperty("proposedLabel").GetString().Should().Be("NotAddressed");
        finding.GetProperty("verifiedLabel").GetString().Should().Be("NotAddressed");
        finding
            .GetProperty("verificationReasonCode")
            .GetString()
            .Should()
            .Be(CloseVerificationReasons.VerifiedAgainstFinalCode);
        finding
            .GetProperty("sources")
            .EnumerateArray()
            .Select(source => source.GetProperty("sourceRecordId").GetString())
            .Should()
            .Equal("src-finding");
    }

    [Fact]
    public async Task Counts_are_recomputed_from_rows_not_from_the_previous_render()
    {
        using var context = new WriterContext();
        context.SaveItems();
        await context.WriteAsync();

        // Downgrade the finding to Indeterminate the way a failed re-verification would.
        context.SaveItems(
            findingLabel: CloseOutcomeLabel.Indeterminate,
            reason: CloseVerificationReasons.UnsupportedCitation
        );
        await context.WriteAsync();

        using var document = JsonDocument.Parse(context.ReadJson());
        document.RootElement.GetProperty("counts").GetProperty("definitive").GetInt32().Should().Be(0);
        context.ReadMarkdown().Should().Contain("Definitive outcomes | 0");
    }

    [Fact]
    public async Task Rewriting_is_idempotent_apart_from_the_excluded_generation_timestamp()
    {
        using var context = new WriterContext();
        context.SaveItems();

        await context.WriteAsync(generatedAt: MergedCloseTestData.Start);
        var first = context.ReadJson();
        var firstMarkdown = context.ReadMarkdown();

        await context.WriteAsync(generatedAt: MergedCloseTestData.Start.AddDays(3));
        var second = context.ReadJson();

        ContentHashOf(second).Should().Be(ContentHashOf(first));
        context.ReadMarkdown().Should().Be(firstMarkdown);
        JsonDocument
            .Parse(second)
            .RootElement.GetProperty("generatedAtUtc")
            .GetString()
            .Should()
            .NotBe(JsonDocument.Parse(first).RootElement.GetProperty("generatedAtUtc").GetString());
    }

    [Fact]
    public async Task Deleted_markdown_regenerates_from_persisted_rows()
    {
        using var context = new WriterContext();
        context.SaveItems();
        await context.WriteAsync();
        var original = context.ReadMarkdown();
        File.Delete(context.MarkdownPath);

        await context.WriteAsync();

        context.ReadMarkdown().Should().Be(original);
    }

    [Fact]
    public async Task Markdown_reports_promotion_outcomes_including_declined_and_failed()
    {
        using var context = new WriterContext();
        context.SaveItems();
        context.SavePromotions(
            new PromotionOutcome(
                "obs-finding",
                PromotionDestinations.KnowledgeBase,
                PromotionDisposition.Written,
                "KnowledgeBase/retry.md",
                "hash-1",
                null
            ),
            new PromotionOutcome(
                "obs-finding",
                PromotionDestinations.DeveloperLearnings,
                PromotionDisposition.Failed,
                null,
                null,
                "write_rejected"
            )
        );

        await context.WriteAsync();

        var markdown = context.ReadMarkdown();
        markdown.Should().Contain("KnowledgeBase/retry.md");
        markdown.Should().Contain("Failed");
        markdown.Should().Contain("write_rejected");
    }

    [Fact]
    public async Task Report_never_states_a_self_assigned_score_or_testimonial()
    {
        using var context = new WriterContext();
        context.SaveItems();

        await context.WriteAsync();

        var markdown = context.ReadMarkdown();
        markdown.Should().NotContainAny("score", "excellent", "valuable", "great work", "/10");
    }

    [Fact]
    public async Task Reports_are_written_only_under_the_pull_request_notes_path()
    {
        using var context = new WriterContext();
        context.SaveItems();

        await context.WriteAsync();

        Directory
            .EnumerateFiles(context.NotesRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(context.NotesRoot, path).Replace('\\', '/'))
            .Should()
            .BeEquivalentTo("PRs/lmdotnettools-118/outcome.json", "PRs/lmdotnettools-118/OUTCOME.md");
    }

    private static string ContentHashOf(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("contentSha256").GetString()!;
    }

    private sealed class WriterContext : IDisposable
    {
        private readonly MergedCloseTestData _data = new();
        private readonly string _root = Directory.CreateTempSubdirectory("merged-close-notes").FullName;

        public WriterContext()
        {
            Round = _data.AddCompletedRound(EngagementRoundIntent.CodeReview);
            FindingSource = _data.AddSource(Round.Id, "src-finding", "the retry loop swallows the last error");
            _ = _data.AddObservation(
                Round.Id,
                "obs-finding",
                ObservationKind.Finding,
                "retry swallows errors",
                FindingSource,
                1
            );
            _ = _data.AddAskedQuestion(Round.Id, "q-1", "Was that deliberate?", FindingSource);
            CloseRound = _data.AddRound(EngagementRoundIntent.MergedClose);
        }

        public EngagementRound Round { get; }

        public EngagementRound CloseRound { get; }

        public AuditSourceRecord FindingSource { get; }

        public string NotesRoot => _root;

        public string MarkdownPath => Path.Combine(_root, "PRs", "lmdotnettools-118", "OUTCOME.md");

        public string JsonPath => Path.Combine(_root, "PRs", "lmdotnettools-118", "outcome.json");

        public void SaveItems(
            CloseOutcomeLabel findingLabel = CloseOutcomeLabel.NotAddressed,
            string reason = CloseVerificationReasons.VerifiedAgainstFinalCode
        ) =>
            _data.Store.SaveCloseOutcomeItems(
                CloseRound.Id,
                [
                    new CloseOutcomeItem(
                        CloseRound.Id,
                        "observation:obs-finding",
                        CloseCandidateKind.Finding,
                        "retry swallows errors",
                        "NotAddressed",
                        findingLabel,
                        reason,
                        MergedCloseTestData.Start,
                        [MergedCloseTestData.Reference(FindingSource)]
                    ),
                    new CloseOutcomeItem(
                        CloseRound.Id,
                        "question:q-1",
                        CloseCandidateKind.Question,
                        "Was that deliberate?",
                        null,
                        CloseOutcomeLabel.Indeterminate,
                        CloseVerificationReasons.NoProposal,
                        MergedCloseTestData.Start,
                        [MergedCloseTestData.Reference(FindingSource)]
                    ),
                ]
            );

        public void SavePromotions(params PromotionOutcome[] outcomes) =>
            _data.Store.SavePromotionOutcomes(CloseRound.Id, outcomes);

        public Task WriteAsync(DateTimeOffset? generatedAt = null) =>
            new MergedCloseOutcomeWriter(
                _data.Store,
                new FakeTimeProviderAt(generatedAt ?? MergedCloseTestData.Start)
            ).WriteAsync(CloseRound.Id, _root, CancellationToken.None);

        public string ReadJson() => File.ReadAllText(JsonPath);

        public string ReadMarkdown() => File.ReadAllText(MarkdownPath);

        public void Dispose()
        {
            _data.Dispose();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException) { }
        }
    }

    private sealed class FakeTimeProviderAt(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
