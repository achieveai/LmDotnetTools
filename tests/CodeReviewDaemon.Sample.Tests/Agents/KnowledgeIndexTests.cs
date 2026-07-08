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
        var beta = new KnowledgeEntryMeta(
            "system/beta.md", "Beta", ["t1"], "system", ["pr/2"], "2026-07-06");
        var alpha = new KnowledgeEntryMeta(
            "system/alpha.md", "Alpha", ["t2", "t3"], "system", ["pr/1"], "2026-07-05");

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
}
