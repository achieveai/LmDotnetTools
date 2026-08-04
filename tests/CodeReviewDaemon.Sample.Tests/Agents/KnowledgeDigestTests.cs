using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Configuration;

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

    [Fact]
    public void ExtractChangedPaths_ReadsGitsQuotedHeaderForm()
    {
        // Git quotes a header path that carries non-ASCII or special bytes, octal-escaping each UTF-8 byte.
        // A parser that only knows the bare form silently drops the file — and a dropped file contributes
        // nothing to ranking, so the lesson that would have matched it never surfaces.
        var diff = """diff --git "a/src/Caf\303\251/Br\303\274cke.cs" "b/src/Caf\303\251/Br\303\274cke.cs" """.TrimEnd();

        KnowledgeDigest.ExtractChangedPaths(diff).Should().Equal("src/Café/Brücke.cs");
    }

    [Fact]
    public void ExtractChangedPaths_ReadsAQuotedRenameAsBothPaths()
    {
        var diff = """diff --git "a/old/na\"me.cs" "b/new/na\"me.cs" """.TrimEnd();

        KnowledgeDigest.ExtractChangedPaths(diff).Should().Equal("old/na\"me.cs", "new/na\"me.cs");
    }

    // ---- ParseChangedPaths ---------------------------------------------------------------------

    [Fact]
    public void ParseChangedPaths_ReadsOnePathPerLineAndDeduplicates()
    {
        var nameOnly = "src/A.cs\nsrc/B.cs\n\nsrc/A.cs\n";

        KnowledgeDigest.ParseChangedPaths(nameOnly).Should().Equal("src/A.cs", "src/B.cs");
    }

    [Fact]
    public void ParseChangedPaths_UnquotesGitsQuotedForm()
    {
        KnowledgeDigest.ParseChangedPaths("\"src/Caf\\303\\251.cs\"\n").Should().Equal("src/Café.cs");
    }

    [Fact]
    public void ParseChangedPaths_DropsTheTruncationMarkerRatherThanRankingAgainstIt()
    {
        var nameOnly = "src/A.cs\n" + SandboxLimits.TruncationMarker;

        KnowledgeDigest.ParseChangedPaths(nameOnly).Should().Equal("src/A.cs");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseChangedPaths_Blank_ReturnsEmpty(string? nameOnly)
    {
        KnowledgeDigest.ParseChangedPaths(nameOnly).Should().BeEmpty();
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

        digest.Text.Should().Contain("/workspace/store/KnowledgeBase/system/alpha.md");
        digest.Text.Should().Contain("Alpha lesson");
        digest.Text.Should().Contain("a, b");
        digest.Text.Should().Contain("Read");
        digest.Text.Should().Contain("Grep", "the digest must actively warn the agent off Grep");
    }

    [Fact]
    public void Render_TellsTheAgentToHandPathsToSubAgents()
    {
        var digest = KnowledgeDigest.Render(
            [Entry("system/alpha.md", "Alpha", ["a"])], KbRoot, charBudget: 10_000, omitted: 0);

        digest.Text.Should().Contain("sub-agent");
    }

    [Fact]
    public void Render_NoEntries_ReturnsEmptySoTheCallerLeavesInputUntouched()
    {
        KnowledgeDigest.Render([], KbRoot, charBudget: 10_000, omitted: 0).Text.Should().BeEmpty();
    }

    [Fact]
    public void Render_OverBudget_TruncatesAndReportsHowManyWereNotListed()
    {
        var entries = Enumerable.Range(0, 40)
            .Select(i => Entry($"system/entry-number-{i}.md", $"A reasonably long lesson title {i}", ["tag"]))
            .ToArray();

        var digest = KnowledgeDigest.Render(entries, KbRoot, charBudget: 900, omitted: 0);

        digest.Text.Length.Should().BeLessThan(1200, "the budget must actually bound the output");
        digest.Text.Should().MatchRegex(@"\d+ more entr");
        digest.Text.Should().Contain("_toc.md", "the agent needs a route to the entries that did not fit");
    }

    [Fact]
    public void Render_CarriesOmittedCountFromRankingIntoTheFooter()
    {
        var digest = KnowledgeDigest.Render(
            [Entry("system/alpha.md", "Alpha", ["a"])], KbRoot, charBudget: 10_000, omitted: 7);

        digest.Text.Should().Contain("7 more entr");
    }

    [Fact]
    public void Render_ReportsExactlyTheEntriesItPutInTheBlock()
    {
        // The caller logs this list as its proof that retrieval worked. If it reported what was SELECTED
        // rather than what was RENDERED, the budget cut-off would silently turn that proof into a lie —
        // the log would name entries the reviewer never received, which is the exact silent failure the
        // proof-of-use logging exists to make impossible.
        var entries = Enumerable.Range(0, 40)
            .Select(i => Entry($"system/entry-number-{i}.md", $"A reasonably long lesson title {i}", ["tag"]))
            .ToArray();

        var digest = KnowledgeDigest.Render(entries, KbRoot, charBudget: 900, omitted: 0);

        digest.Rendered.Should().NotBeEmpty();
        digest.Rendered.Count.Should().BeLessThan(entries.Length, "the budget must have dropped some entries");
        foreach (var entry in digest.Rendered)
        {
            digest.Text.Should().Contain(entry.File, "a reported entry must actually be in the block");
        }

        foreach (var entry in entries.Except(digest.Rendered))
        {
            digest.Text.Should().NotContain(entry.File, "an unreported entry must not be in the block");
        }
    }

    [Fact]
    public void Render_NoEntries_ReportsNothingAsRendered()
    {
        KnowledgeDigest.Render([], KbRoot, charBudget: 10_000, omitted: 3).Rendered.Should().BeEmpty();
    }

    // ---- RenderTableOfContents ------------------------------------------------------------------

    [Fact]
    public void RenderTableOfContents_UsesTheCanonicalHeadingAndTheTocsAbsolutePath()
    {
        var block = KnowledgeDigest.RenderTableOfContents(
            "# Knowledge Base\n\n- [Alpha](system/alpha.md)\n", KbRoot);

        block.Should().StartWith(
            "## Prior knowledge (Knowledge Base)",
            "the prompt teaches one heading and teaches that its absence means there is no KB at all");
        block.Should().Contain("/workspace/store/KnowledgeBase/_toc.md");
        block.Should().Contain("/workspace/store/KnowledgeBase/", "the links are relative to a root the agent needs");
        block.Should().Contain("[Alpha](system/alpha.md)", "the table of contents rides along verbatim");
        block.Should().Contain("Grep", "the fallback must warn the agent off Grep too");
        block.Should().Contain("sub-agent");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n")]
    public void RenderTableOfContents_Blank_ReturnsEmptySoTheCallerLeavesInputUntouched(string? toc)
    {
        KnowledgeDigest.RenderTableOfContents(toc, KbRoot).Should().BeEmpty();
    }
}
