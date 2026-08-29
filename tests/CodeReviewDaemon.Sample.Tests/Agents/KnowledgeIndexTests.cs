using System.Text.Json;
using CodeReviewDaemon.Sample.Agents;

namespace CodeReviewDaemon.Sample.Tests.Agents;

public class KnowledgeIndexTests
{
    private const string WellFormedEntry = """
        ---
        title: X
        tags: [a, b]
        scope: system
        sourcePrs: ["github/o-r/42"]
        updated: 2026-07-06
        ---
        # X
        body
        """;

    [Fact]
    public void ParseFrontmatter_WellFormedEntry_ReturnsExactMeta()
    {
        var meta = KnowledgeIndex.ParseFrontmatter("system/x.md", WellFormedEntry);

        meta.Should().NotBeNull();
        meta!.File.Should().Be("system/x.md");
        meta.Title.Should().Be("X");
        meta.Tags.Should().Equal("a", "b");
        meta.Scope.Should().Be("system");
        meta.SourcePrs.Should().Equal("github/o-r/42");
        meta.Updated.Should().Be("2026-07-06");
    }

    [Fact]
    public void ParseFrontmatter_NoFrontmatterBlock_ReturnsNull()
    {
        const string markdown = """
            # Heading
            body without any frontmatter
            """;

        var meta = KnowledgeIndex.ParseFrontmatter("system/x.md", markdown);

        meta.Should().BeNull();
    }

    [Fact]
    public void RenderIndex_TwoEntries_RendersSortedJsonLinesWithStableKeyOrder()
    {
        var beta = new KnowledgeEntryMeta("system/beta.md", "Beta", ["t1"], "system", ["pr/2"], "2026-07-06");
        var alpha = new KnowledgeEntryMeta("system/alpha.md", "Alpha", ["t2", "t3"], "system", ["pr/1"], "2026-07-05");

        // Pass beta first to prove RenderIndex sorts by File ordinal (alpha < beta).
        var index = KnowledgeIndex.RenderIndex([beta, alpha]);

        var lines = index.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(2);

        var expectedKeys = new[] { "file", "title", "tags", "scope", "sourcePrs", "updated" };

        using var first = JsonDocument.Parse(lines[0]);
        first.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal(expectedKeys);
        first.RootElement.GetProperty("file").GetString().Should().Be("system/alpha.md");
        first.RootElement.GetProperty("title").GetString().Should().Be("Alpha");

        using var second = JsonDocument.Parse(lines[1]);
        second.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal(expectedKeys);
        second.RootElement.GetProperty("file").GetString().Should().Be("system/beta.md");
        second.RootElement.GetProperty("title").GetString().Should().Be("Beta");
    }

    [Fact]
    public void ParseIndex_RoundTripsRenderIndex()
    {
        var entries = new[]
        {
            new KnowledgeEntryMeta("system/alpha.md", "Alpha", ["t2", "t3"], "system", ["pr/1"], "2026-07-05"),
            new KnowledgeEntryMeta("Repo/beta.md", "Beta", ["t1"], "Repo", ["pr/2"], "2026-07-06"),
        };

        var parsed = KnowledgeIndex.ParseIndex(KnowledgeIndex.RenderIndex(entries));

        // RenderIndex sorts by File ordinal, so "Repo/beta.md" precedes "system/alpha.md".
        parsed.Should().HaveCount(2);
        parsed[0].Should().BeEquivalentTo(entries[1]);
        parsed[1].Should().BeEquivalentTo(entries[0]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  \n")]
    public void ParseIndex_NullOrBlank_ReturnsEmpty(string? jsonl)
    {
        KnowledgeIndex.ParseIndex(jsonl).Should().BeEmpty();
    }

    [Fact]
    public void ParseIndex_SkipsMalformedAndFilelessLinesInsteadOfThrowing()
    {
        // A torn/partial line and a line with no "file" key must not cost us the healthy entries around
        // them: the index is regenerated best-effort, and one bad line must never blind a whole review.
        var jsonl = string.Join(
            '\n',
            """{"file":"system/good1.md","title":"Good 1","tags":["a"],"scope":"system","sourcePrs":[],"updated":"2026-07-05"}""",
            """{"file":"system/torn.md","title":"Torn""",
            "not json at all",
            """{"title":"No file key","tags":[],"scope":"system","sourcePrs":[],"updated":"2026-07-05"}""",
            "",
            """{"file":"system/good2.md","title":"Good 2","tags":["b","c"],"scope":"system","sourcePrs":["pr/9"],"updated":"2026-07-06"}"""
        );

        var parsed = KnowledgeIndex.ParseIndex(jsonl);

        parsed.Select(e => e.File).Should().Equal("system/good1.md", "system/good2.md");
        parsed[1].Tags.Should().Equal("b", "c");
        parsed[1].SourcePrs.Should().Equal("pr/9");
    }

    [Fact]
    public void ParseIndex_MissingOptionalKeys_DefaultToEmptyRatherThanNull()
    {
        var parsed = KnowledgeIndex.ParseIndex("""{"file":"system/bare.md"}""");

        var entry = parsed.Should().ContainSingle().Subject;
        entry.Title.Should().BeEmpty();
        entry.Scope.Should().BeEmpty();
        entry.Updated.Should().BeEmpty();
        entry.Tags.Should().BeEmpty();
        entry.SourcePrs.Should().BeEmpty();
    }

    [Fact]
    public void ParseIndex_StopsAtTheRecordCeilingAndSaysThatItDid()
    {
        // The digest caps what the reviewer is SHOWN - an entry count and a character budget - and that was
        // mistaken for a bound on the work. Every record in the file was parsed, materialized, partitioned,
        // sanitized, scored and sorted, and only then were the top few taken: the index was trusted for its
        // SIZE by the same code that establishes it must not be trusted for its CONTENT.
        var jsonl = string.Join(
            '\n',
            Enumerable.Range(0, 50).Select(i => $$"""{"file":"system/e{{i}}.md","title":"E{{i}}"}""")
        );

        var parsed = KnowledgeIndex.ParseIndex(jsonl, maxRecords: 10, out var truncated);

        parsed.Should().HaveCount(10, "the ceiling governs the work, not just the output");
        parsed[0].File.Should().Be("system/e0.md");
        truncated.Should().BeTrue("a silently shortened index reads exactly like a small one");
    }

    [Fact]
    public void ParseIndex_CountsMalformedRecordsAgainstTheCeilingToo()
    {
        // Counted over records EXAMINED, not records KEPT. Bounding only the kept ones leaves a file of a
        // million unparseable lines fully scanned - each one still costs a parse attempt - which is the same
        // unbounded work wearing a different hat. Here every record is junk, so nothing is kept and the
        // ceiling has to be what stops the scan.
        var jsonl = string.Join('\n', Enumerable.Repeat("not json at all", 50));

        var parsed = KnowledgeIndex.ParseIndex(jsonl, maxRecords: 10, out var truncated);

        parsed.Should().BeEmpty();
        truncated.Should().BeTrue("the scan stopped at the ceiling rather than reading every junk line");
    }

    [Fact]
    public void ParseIndex_RefusesASingleRecordTooLongToBeOne()
    {
        // The record COUNT alone does not bound the work: one line can be arbitrarily long by itself and the
        // JSON reader would parse all of it. A metadata record is a few hundred characters, so an oversized
        // one is refused the way a torn line is - it costs only itself, and its neighbours survive.
        var huge = new string('x', 64 * 1024);
        var jsonl = string.Join(
            '\n',
            $$"""{"file":"system/huge.md","title":"{{huge}}"}""",
            """{"file":"system/ok.md","title":"OK"}"""
        );

        var parsed = KnowledgeIndex.ParseIndex(jsonl);

        parsed.Select(e => e.File).Should().Equal("system/ok.md");
    }

    [Fact]
    public void ParseIndex_DoesNotReportTruncationForAnIndexThatSimplyEndsAtTheCeiling()
    {
        // The over-refusal pin: "truncated" must mean records were LEFT, not that the file happened to be
        // exactly as long as the ceiling. Reporting a complete index as shortened would send a maintainer
        // looking for a broken extraction that is not there.
        var jsonl = string.Join(
            '\n',
            Enumerable.Range(0, 10).Select(i => $$"""{"file":"system/e{{i}}.md","title":"E{{i}}"}""")
        );

        var parsed = KnowledgeIndex.ParseIndex(jsonl, maxRecords: 10, out var truncated);

        parsed.Should().HaveCount(10);
        truncated.Should().BeFalse("every record was read, so nothing was left behind");
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void ParseIndex_ReadsEveryLineEndingTheSame(string lineEnding)
    {
        // The scan handles line endings itself now, where the two normalizing string copies used to. A
        // reader that walks the text by hand is a reader that can get "\r\n" wrong and split one record into
        // two - so all three spellings are pinned rather than assumed.
        var jsonl = string.Join(
            lineEnding,
            """{"file":"system/a.md","title":"A"}""",
            """{"file":"system/b.md","title":"B"}""",
            """{"file":"system/c.md","title":"C"}"""
        );

        var parsed = KnowledgeIndex.ParseIndex(jsonl);

        parsed.Select(e => e.File).Should().Equal("system/a.md", "system/b.md", "system/c.md");
        parsed.Select(e => e.Title).Should().Equal("A", "B", "C");
    }
}
