using CodeReviewDaemon.Sample.Agents;

namespace CodeReviewDaemon.Sample.Tests.Agents;

public class KnowledgeDigestTests
{
    private const string KbRoot = "/workspace/store/KnowledgeBase";

    private static KnowledgeEntryMeta Entry(
        string file, string title, string[] tags, string scope = "system", string updated = "2026-07-01") =>
        new(file, title, tags, scope, [], updated);

    // ---- ExtractChangedPaths -------------------------------------------------------------------

    [Fact]
    public void ExtractChangedPaths_ReadsBothSidesOfDiffGitHeaders()
    {
        var diff = """
            diff --git a/src/LmCore/Agents/Runner.cs b/src/LmCore/Agents/Runner.cs
            index 111..222 100644
            --- a/src/LmCore/Agents/Runner.cs
            +++ b/src/LmCore/Agents/Runner.cs
            @@ -1 +1 @@
            -old
            +new
            diff --git a/tests/Foo/BarTests.cs b/tests/Foo/BarTests.cs
            """;

        KnowledgeDigest.ExtractChangedPaths(diff)
            .Should().Equal("src/LmCore/Agents/Runner.cs", "tests/Foo/BarTests.cs");
    }

    [Fact]
    public void ExtractChangedPaths_RenameReportsBothOldAndNewPath()
    {
        var diff = "diff --git a/old/Path.cs b/new/Path.cs";

        KnowledgeDigest.ExtractChangedPaths(diff).Should().Equal("old/Path.cs", "new/Path.cs");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no headers here")]
    public void ExtractChangedPaths_NoHeaders_ReturnsEmpty(string? diff)
    {
        KnowledgeDigest.ExtractChangedPaths(diff).Should().BeEmpty();
    }

    // ---- Ranking -------------------------------------------------------------------------------

    [Fact]
    public void SelectRelevant_TagMatchingAChangedPathOutranksNonMatch()
    {
        var entries = new[]
        {
            Entry("system/unrelated.md", "Filter excluded rows before pagination", ["pagination", "sql"]),
            Entry("system/matching.md", "Async callbacks must be invalidated", ["callbacks", "streaming"]),
        };

        var ranked = KnowledgeDigest.SelectRelevant(
            entries, ["src/LmCore/Streaming/CallbackPump.cs"], repoScope: null, maxEntries: 10);

        ranked[0].File.Should().Be("system/matching.md");
    }

    [Fact]
    public void SelectRelevant_EntryScopedToTheReviewedRepoOutranksEquallyMatchingSystemEntry()
    {
        var entries = new[]
        {
            Entry("system/shared.md", "Streaming contract", ["streaming"]),
            Entry("MyRepo/local.md", "Streaming contract", ["streaming"], scope: "MyRepo"),
        };

        var ranked = KnowledgeDigest.SelectRelevant(
            entries, ["src/Streaming/Pump.cs"], repoScope: "MyRepo", maxEntries: 10);

        ranked[0].File.Should().Be("MyRepo/local.md");
    }

    [Fact]
    public void SelectRelevant_ZeroMatches_StillSurfacesEntriesUpToTheCap()
    {
        // The KB is small and tag vocabulary is coarse; surfacing nothing would reproduce exactly the
        // blindness this digest exists to fix. Rank them, don't drop them.
        var entries = new[]
        {
            Entry("system/a.md", "Alpha", ["zzz"]),
            Entry("system/b.md", "Beta", ["yyy"]),
        };

        KnowledgeDigest.SelectRelevant(entries, ["totally/unrelated.txt"], null, maxEntries: 10)
            .Should().HaveCount(2);
    }

    [Fact]
    public void SelectRelevant_HonoursMaxEntries()
    {
        var entries = Enumerable.Range(0, 12)
            .Select(i => Entry($"system/e{i}.md", $"Entry {i}", ["t"]))
            .ToArray();

        KnowledgeDigest.SelectRelevant(entries, [], null, maxEntries: 5).Should().HaveCount(5);
    }

    [Fact]
    public void SelectRelevant_TiesBreakDeterministicallyByUpdatedThenFile()
    {
        var entries = new[]
        {
            Entry("system/b.md", "B", ["t"], updated: "2026-01-01"),
            Entry("system/a.md", "A", ["t"], updated: "2026-01-01"),
            Entry("system/c.md", "C", ["t"], updated: "2026-09-09"),
        };

        var ranked = KnowledgeDigest.SelectRelevant(entries, [], null, maxEntries: 10);

        // Newest first, then file ordinal — same input in any order yields the same list.
        ranked.Select(e => e.File).Should().Equal("system/c.md", "system/a.md", "system/b.md");
    }

    // ---- Render --------------------------------------------------------------------------------

    [Fact]
    public void Render_EmitsExactAbsolutePathsAndForbidsGrep()
    {
        var digest = KnowledgeDigest.Render(
            [Entry("system/alpha.md", "Alpha lesson", ["a", "b"])], KbRoot, charBudget: 10_000, omitted: 0);

        digest.Should().Contain("/workspace/store/KnowledgeBase/system/alpha.md");
        digest.Should().Contain("Alpha lesson");
        digest.Should().Contain("a, b");
        digest.Should().Contain("Read");
        digest.Should().Contain("Grep", "the digest must actively warn the agent off Grep");
    }

    [Fact]
    public void Render_TellsTheAgentToHandPathsToSubAgents()
    {
        var digest = KnowledgeDigest.Render(
            [Entry("system/alpha.md", "Alpha", ["a"])], KbRoot, charBudget: 10_000, omitted: 0);

        digest.Should().Contain("sub-agent");
    }

    [Fact]
    public void Render_NoEntries_ReturnsEmptySoTheCallerLeavesInputUntouched()
    {
        KnowledgeDigest.Render([], KbRoot, charBudget: 10_000, omitted: 0).Should().BeEmpty();
    }

    [Fact]
    public void Render_OverBudget_TruncatesAndReportsHowManyWereNotListed()
    {
        var entries = Enumerable.Range(0, 40)
            .Select(i => Entry($"system/entry-number-{i}.md", $"A reasonably long lesson title {i}", ["tag"]))
            .ToArray();

        var digest = KnowledgeDigest.Render(entries, KbRoot, charBudget: 900, omitted: 0);

        digest.Length.Should().BeLessThan(1200, "the budget must actually bound the output");
        digest.Should().MatchRegex(@"\d+ more entr");
        digest.Should().Contain("_toc.md", "the agent needs a route to the entries that did not fit");
    }

    [Fact]
    public void Render_CarriesOmittedCountFromRankingIntoTheFooter()
    {
        var digest = KnowledgeDigest.Render(
            [Entry("system/alpha.md", "Alpha", ["a"])], KbRoot, charBudget: 10_000, omitted: 7);

        digest.Should().Contain("7 more entr");
    }
}
