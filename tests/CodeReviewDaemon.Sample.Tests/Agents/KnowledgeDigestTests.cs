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

    [Fact]
    public void ParseChangedPaths_ReportsTruncationSoTheCallerCanStillReachTheDiffHeaders()
    {
        // A truncated listing is a PARTIAL answer that looks like a complete one: it is non-empty, so the
        // "fell back to the diff headers when empty" route never fires and the files past the cut are ranked
        // against nothing. The caller cannot recover what it is never told about.
        _ = KnowledgeDigest.ParseChangedPaths("src/A.cs\n" + SandboxLimits.TruncationMarker, out var truncated);

        truncated.Should().BeTrue();
    }

    [Fact]
    public void ParseChangedPaths_UntruncatedListing_ReportsNoTruncationAndKeepsEveryRecord()
    {
        var paths = KnowledgeDigest.ParseChangedPaths("src/A.cs\nsrc/B.cs", out var truncated);

        truncated.Should().BeFalse();
        paths.Should().Equal("src/A.cs", "src/B.cs");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\r\n\n")]
    public void ParseChangedPaths_Blank_ReturnsEmpty(string? nameOnly)
    {
        // "Blank" is absent or empty, NOT whitespace: a whitespace-only listing names a real file. The
        // bare-terminator cases are here so the emptiness rule stays asserted for the input that genuinely
        // carries no path.
        KnowledgeDigest.ParseChangedPaths(nameOnly).Should().BeEmpty();
    }

    [Fact]
    public void ParseChangedPaths_KeepsLeadingAndTrailingSpacesThatArePartOfTheFilename()
    {
        // git permits spaces at either end of a filename and `diff --git --name-only` does NOT quote for
        // them — quoting triggers on non-ASCII, control, quote and backslash bytes only. So " foo.cs"
        // arrives bare, and trimming it yields a path that matches nothing git ever reported.
        var nameOnly = " lead.cs\ntrail.cs \n";

        KnowledgeDigest.ParseChangedPaths(nameOnly).Should().Equal(" lead.cs", "trail.cs ");
    }

    [Fact]
    public void ParseChangedPaths_WhitespaceOnlyLineIsTheFilenameItIsNotEmptiness()
    {
        KnowledgeDigest.ParseChangedPaths("  \nsrc/A.cs\n").Should().Equal("  ", "src/A.cs");
    }

    [Theory]
    [InlineData("  ")]
    [InlineData("  \n")]
    public void ParseChangedPaths_AListingOfNothingButASpaceNamedFileIsThatFile(string nameOnly)
    {
        // The same rule as the line above, applied to the whole input. A PR that touches exactly one
        // space-named file produces a listing that is entirely whitespace, so a whitespace guard on the
        // input discards the one file it was meant to report — and the per-line rule never runs at all.
        KnowledgeDigest.ParseChangedPaths(nameOnly).Should().Equal("  ");
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

        digest.Text.Length.Should().BeLessThanOrEqualTo(900, "the budget must actually bound the output");
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

    // ---- Render: containment --------------------------------------------------------------------

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..\\..\\secrets.md")]
    [InlineData("system/../../outside.md")]
    [InlineData("..")]
    public void Render_RejectsAnEntryWhosePathLeavesTheKnowledgeBaseRoot(string file)
    {
        // The entries rendered here are read back from _index.jsonl ON DISK in the store, and the store's
        // KnowledgeBase/ is written by the knowledge agent — an LLM with file-write tools. A '..' in a
        // "file" value therefore reaches this renderer, and the absolute path it produces would point the
        // reviewer at something that is not knowledge, with no way to tell. Reject it, do not rewrite it.
        var digest = KnowledgeDigest.Render(
            [Entry(file, "Poisoned", ["x"])], KbRoot, charBudget: 10_000, omitted: 0);

        digest.Rendered.Should().BeEmpty("an entry that escapes the root must not be offered to the agent");
        digest.Rejected.Should().ContainSingle().Which.File.Should().Be(file);
        digest.Text.Should().BeEmpty(
            "with every entry rejected there are no paths to offer, and a header promising paths that are "
                + "not there reads exactly like a Knowledge Base that happens to be empty");
    }

    [Fact]
    public void Render_KeepsAnEntryWhoseDotDotStaysInsideTheRoot()
    {
        // Containment, not a blanket ban on '..': this path canonicalizes back inside the KB.
        var digest = KnowledgeDigest.Render(
            [Entry("system/../system/alpha.md", "Alpha", ["a"])], KbRoot, charBudget: 10_000, omitted: 0);

        digest.Rejected.Should().BeEmpty();
        digest.Text.Should().Contain("/workspace/store/KnowledgeBase/system/alpha.md");
    }

    [Fact]
    public void Render_ContainsALeadingSlashRatherThanReadingItAsAnAbsolutePath()
    {
        var digest = KnowledgeDigest.Render(
            [Entry("/etc/passwd", "Contained", ["a"])], KbRoot, charBudget: 10_000, omitted: 0);

        digest.Rejected.Should().BeEmpty();
        digest.Text.Should().Contain("/workspace/store/KnowledgeBase/etc/passwd");
    }

    [Fact]
    public void Render_RejectsAnEntryThatNamesNoFileAtAll()
    {
        var digest = KnowledgeDigest.Render(
            [Entry("./", "Nothing", ["a"])], KbRoot, charBudget: 10_000, omitted: 0);

        digest.Rendered.Should().BeEmpty();
        digest.Rejected.Should().ContainSingle();
    }

    [Fact]
    public void Render_RejectedEntriesDoNotCountTowardTheFootersPromiseOfMoreEntries()
    {
        // The footer tells the agent the entries it did not get are listed in _toc.md. A rejected entry is
        // one we deliberately refuse to route it to, so counting it there would be an invitation to go and
        // find the very thing that was rejected.
        var digest = KnowledgeDigest.Render(
            [Entry("system/alpha.md", "Alpha", ["a"]), Entry("../evil.md", "Evil", ["a"])],
            KbRoot,
            charBudget: 10_000,
            omitted: 0);

        digest.Rendered.Should().ContainSingle();
        digest.Text.Should().NotContain("more entr");
    }

    [Fact]
    public void Render_ValidatesEntriesThatSitBeyondTheCharacterBudgetToo()
    {
        // Budget pressure is the NORMAL case, not a corner — a live run renders 8013 of an 8192-char
        // budget. If validation happens lazily inside the render loop, an escaping entry past the cut is
        // never examined: it never reaches Rejected, nothing warns about it, and the footer counts it as
        // an entry the agent can go and fetch from _toc.md. That is the silent disappearance the rejection
        // logging exists to prevent, reintroduced on the common path.
        var entries = Enumerable.Range(0, 40)
            .Select(i => Entry($"system/entry-number-{i}.md", $"A reasonably long lesson title {i}", ["tag"]))
            .Append(Entry("../../etc/passwd", "Poisoned", ["tag"]))
            .ToArray();

        var digest = KnowledgeDigest.Render(entries, KbRoot, charBudget: 900, omitted: 0);

        digest.Rendered.Count.Should().BeLessThan(entries.Length, "the budget must have cut the list short");
        digest.Rejected.Should().ContainSingle().Which.File.Should().Be("../../etc/passwd");
    }

    [Fact]
    public void Render_RejectedEntryPastTheBudgetIsStillKeptOutOfTheFootersCount()
    {
        var sound = Enumerable.Range(0, 40)
            .Select(i => Entry($"system/entry-number-{i}.md", $"A reasonably long lesson title {i}", ["tag"]))
            .ToArray();
        var entries = sound.Append(Entry("../../etc/passwd", "Poisoned", ["tag"])).ToArray();

        var digest = KnowledgeDigest.Render(entries, KbRoot, charBudget: 900, omitted: 0);

        // Exactly the sound entries that did not fit — the rejected one is neither rendered nor promised,
        // and must not be double-counted as both refused and merely-omitted.
        var reported = int.Parse(
            System.Text.RegularExpressions.Regex.Match(digest.Text, @"(\d+) more entr").Groups[1].Value);
        reported.Should().Be(sound.Length - digest.Rendered.Count);
    }

    // ---- RenderTableOfContents ------------------------------------------------------------------

    [Fact]
    public void RenderTableOfContents_UsesTheCanonicalHeadingAndTheTocsAbsolutePath()
    {
        var block = KnowledgeDigest.RenderTableOfContents(
            "# Knowledge Base\n\n- [Alpha](system/alpha.md)\n", KbRoot, charBudget: 10_000).Text;

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
        KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 10_000).Text.Should().BeEmpty();
    }

    // ---- RenderTableOfContents: budget ----------------------------------------------------------

    /// <summary>A table of contents shaped like the one the KB regenerates: header, scope, link lines.</summary>
    private static string BigToc(int entries) =>
        "# Knowledge Base\n\n## system\n\n"
        + string.Concat(
            Enumerable.Range(0, entries).Select(
                i => $"- [A durable lesson about something number {i}](system/lesson-number-{i}.md)\n"));

    [Fact]
    public void RenderTableOfContents_HonoursTheSameBudgetAsTheRankedDigest()
    {
        // The fallback is the DEGRADED path — taken when the index is missing or torn — so leaving it
        // unbounded means the one prior-knowledge block with no cap is the one rendered after something has
        // already gone wrong. The live _toc.md is 4548 bytes over 28 entries and the KB only grows.
        var block = KnowledgeDigest.RenderTableOfContents(BigToc(200), KbRoot, charBudget: 2_000);

        block.Text.Length.Should().BeLessThanOrEqualTo(2_000);
    }

    [Fact]
    public void RenderTableOfContents_CutsBetweenEntriesNeverMidLink()
    {
        // A half-written link is worse than an absent one: the agent will try to open it, and the path it
        // reads will be a prefix of a real path rather than a real path.
        var block = KnowledgeDigest.RenderTableOfContents(BigToc(200), KbRoot, charBudget: 2_000);

        foreach (var line in block.Text.Split('\n').Where(l => l.StartsWith("- [", StringComparison.Ordinal)))
        {
            line.Should().EndWith(".md)", "every listed entry must be a complete, openable link");
        }
    }

    [Fact]
    public void RenderTableOfContents_KeepsTheFootersPromiseWhenItTruncates()
    {
        var block = KnowledgeDigest.RenderTableOfContents(BigToc(200), KbRoot, charBudget: 2_000);

        block.Listed.Should().BeGreaterThan(0).And.BeLessThan(200);
        block.Dropped.Should().Be(200 - block.Listed);
        block.Text.Should().Contain($"{block.Dropped} more entr");
        block.Text.Should().Contain("_toc.md", "the agent still needs a route to what was cut");
    }

    [Fact]
    public void RenderTableOfContents_UnderBudget_ListsEverythingAndPromisesNothingMore()
    {
        var block = KnowledgeDigest.RenderTableOfContents(BigToc(3), KbRoot, charBudget: 10_000);

        block.Listed.Should().Be(3);
        block.Dropped.Should().Be(0);
        block.Text.Should().NotContain("more entr");
        block.Text.Should().Contain("system/lesson-number-2.md");
    }

    [Fact]
    public void RenderTableOfContents_ReportsWhatItListedNotWhatItRead()
    {
        // The caller logs this instead of the raw file size. A log that reports what was READ rather than
        // what was DELIVERED is the same silent-failure shape the ranked digest's proof-of-use line fixed.
        var block = KnowledgeDigest.RenderTableOfContents(BigToc(200), KbRoot, charBudget: 2_000);

        block.Text.Split('\n').Count(l => l.StartsWith("- [", StringComparison.Ordinal))
            .Should().Be(block.Listed);
    }

    // ---- Both renderers: the budget is a HARD bound on model-authored content --------------------
    //
    // Titles, tags and scopes are written by the knowledge-extraction agent, and both renderers put them
    // into the block verbatim. An unbounded string from an LLM landing in a budgeted block means the block
    // is only nominally budgeted: it crowds the actual PR out of the reviewer's context window, which is
    // the failure the budget exists to prevent. Where metadata cannot fit, the metadata gives way and the
    // PATH stays whole - a truncated title is cosmetic, a truncated path is a link the agent cannot open.

    private const string LongTitle = // 20k of model-authored title, well past the 8 KiB production budget
        "A lesson whose title the extraction agent never learned to keep short ";

    [Fact]
    public void Render_OversizedFirstTitleStillRespectsTheBudget()
    {
        var title = string.Concat(Enumerable.Repeat(LongTitle, 300));

        var digest = KnowledgeDigest.Render(
            [Entry("system/alpha.md", title, ["tag"])], KbRoot, charBudget: 2_000, omitted: 0);

        digest.Text.Length.Should().BeLessThanOrEqualTo(2_000);
    }

    [Fact]
    public void Render_TruncatesTheTitleButKeepsTheExactPathIntact()
    {
        // The path is the load-bearing part of this whole feature: it is the one thing the parent copies
        // into a sub-agent's brief, and the sub-agent has no way to repair a path that arrives cut short.
        var title = string.Concat(Enumerable.Repeat(LongTitle, 300));

        var digest = KnowledgeDigest.Render(
            [Entry("system/alpha.md", title, ["tag"])], KbRoot, charBudget: 2_000, omitted: 0);

        digest.Text.Should().Contain($"{KbRoot}/system/alpha.md");
        digest.Rendered.Should().ContainSingle("a huge title must cost the title, not the entry");
    }

    [Fact]
    public void Render_OversizedTagsAndScopeStillRespectTheBudget()
    {
        var entry = new KnowledgeEntryMeta(
            "system/alpha.md",
            "Alpha",
            [.. Enumerable.Range(0, 2_000).Select(i => $"a-tag-the-agent-invented-{i}")],
            string.Concat(Enumerable.Repeat("scope-", 2_000)),
            [],
            "2026-07-01");

        var digest = KnowledgeDigest.Render([entry], KbRoot, charBudget: 2_000, omitted: 0);

        digest.Text.Length.Should().BeLessThanOrEqualTo(2_000);
        digest.Text.Should().Contain($"{KbRoot}/system/alpha.md");
    }

    [Fact]
    public void Render_BudgetTooSmallForEvenTheHeader_EmitsNothingRatherThanOverrun()
    {
        var digest = KnowledgeDigest.Render(
            [Entry("system/alpha.md", "Alpha", ["a"])], KbRoot, charBudget: 40, omitted: 0);

        digest.Text.Length.Should().BeLessThanOrEqualTo(40);
    }

    [Fact]
    public void Render_FooterIsInsideTheBudgetNotAddedAfterIt()
    {
        // The footer is appended once the entries are in. Unreserved, it is an unchecked append onto a
        // block already sitting at the limit - the same shape as the entry that skips the check.
        var entries = Enumerable.Range(0, 40)
            .Select(i => Entry($"system/entry-number-{i}.md", $"A reasonably long lesson title {i}", ["tag"]))
            .ToArray();

        foreach (var budget in new[] { 300, 500, 900, 1_500 })
        {
            KnowledgeDigest.Render(entries, KbRoot, budget, omitted: 0)
                .Text.Length.Should().BeLessThanOrEqualTo(budget, "budget {0} must bound the block", budget);
        }
    }

    [Fact]
    public void RenderTableOfContents_WithNoRecognisableEntriesIsStillBounded()
    {
        // THE regression this pass exists for. A torn or hand-edited _toc.md has no "- [Title](path)" lines,
        // so an entry-counted truncation gate never fires and the whole file is appended unbounded - on the
        // degraded path, which is the only path this renderer is ever used on.
        var junk = string.Concat(Enumerable.Repeat("this line is not a table of contents entry at all\n", 500));

        var block = KnowledgeDigest.RenderTableOfContents(junk, KbRoot, charBudget: 2_000);

        block.Text.Length.Should().BeLessThanOrEqualTo(2_000);
    }

    [Fact]
    public void RenderTableOfContents_WithNoRecognisableEntriesReportsThatItTruncated()
    {
        // Listed = 0, Dropped = 0 while silently discarding most of the file is a proof-of-delivery line
        // that lies. Truncation must be observable even when nothing countable was truncated.
        var junk = string.Concat(Enumerable.Repeat("this line is not a table of contents entry at all\n", 500));

        var block = KnowledgeDigest.RenderTableOfContents(junk, KbRoot, charBudget: 2_000);

        block.Truncated.Should().BeTrue();
        block.Text.Should().Contain("_toc.md", "the agent still needs a route to what was cut");
    }

    [Fact]
    public void RenderTableOfContents_UnderBudget_ReportsNoTruncation()
    {
        KnowledgeDigest.RenderTableOfContents(BigToc(3), KbRoot, charBudget: 10_000)
            .Truncated.Should().BeFalse();
    }

    [Fact]
    public void RenderTableOfContents_OversizedSingleEntryIsStillBounded()
    {
        var toc = "# Knowledge Base\n\n- [" + string.Concat(Enumerable.Repeat(LongTitle, 300))
            + "](system/alpha.md)\n";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 2_000);

        block.Text.Length.Should().BeLessThanOrEqualTo(2_000);
    }

    [Fact]
    public void RenderTableOfContents_OversizedSingleEntryKeepsItsLinkOpenable()
    {
        // Same rule as the ranked path: the title gives way, the link does not. A ToC line is only useful
        // because of what is inside its parentheses.
        var toc = "# Knowledge Base\n\n- [" + string.Concat(Enumerable.Repeat(LongTitle, 300))
            + "](system/alpha.md)\n";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 2_000);

        block.Text.Should().Contain("](system/alpha.md)");
        block.Listed.Should().Be(1);
    }

    [Fact]
    public void RenderTableOfContents_BudgetTooSmallForEvenTheHeader_EmitsNothingRatherThanOverrun()
    {
        KnowledgeDigest.RenderTableOfContents(BigToc(5), KbRoot, charBudget: 40)
            .Text.Length.Should().BeLessThanOrEqualTo(40);
    }

    // ---- Containment is applied BEFORE the entry cap --------------------------------------------

    [Fact]
    public void PartitionByContainment_SeparatesUsableEntriesFromEscapingOnes()
    {
        var entries = Enumerable.Range(0, 24)
            .Select(i => Entry($"../../etc/passwd-{i}", $"Poisoned {i}", ["runner"]))
            .Append(Entry("system/alpha.md", "Sound lesson about the runner", ["runner"]))
            .ToArray();

        var partition = KnowledgeDigest.PartitionByContainment(entries, KbRoot);

        partition.Usable.Should().ContainSingle().Which.File.Should().Be("system/alpha.md");
        partition.Refused.Should().HaveCount(24, "every refusal must still be reportable");
    }

    [Fact]
    public void PartitionBeforeSelect_SurfacesKnowledgeThatEscapingEntriesWouldHaveCrowdedOut()
    {
        // Capping at MaxKnowledgeEntries BEFORE containment lets invalid high-ranked entries eat every
        // retrieval slot: 24 escaping entries ahead of good knowledge surface NOTHING, which is exactly the
        // knowledge-blind review issue #255 exists to prevent - reached through the containment check that
        // was added to make retrieval safer. The cap has to count entries the agent can actually use.
        var entries = Enumerable.Range(0, 24)
            .Select(i => Entry($"../../etc/passwd-{i}", $"Runner lesson {i}", ["runner"], updated: "2026-08-01"))
            .Append(Entry("system/alpha.md", "Runner lesson", ["runner"], updated: "2026-01-01"))
            .ToArray();

        var partition = KnowledgeDigest.PartitionByContainment(entries, KbRoot);
        var selected = KnowledgeDigest.SelectRelevant(
            partition.Usable, ["src/Runner.cs"], "LmDotnetTools", maxEntries: 24);
        var digest = KnowledgeDigest.Render(
            selected, KbRoot, charBudget: 10_000, omitted: partition.Usable.Count - selected.Count);

        digest.Rendered.Should().ContainSingle().Which.File.Should().Be("system/alpha.md");
        digest.Text.Should().Contain($"{KbRoot}/system/alpha.md");
    }

    [Fact]
    public void PartitionByContainment_NoEscapes_KeepsEveryEntryAndRefusesNothing()
    {
        var entries = new[] { Entry("system/alpha.md", "Alpha", ["a"]), Entry("system/beta.md", "Beta", ["b"]) };

        var partition = KnowledgeDigest.PartitionByContainment(entries, KbRoot);

        partition.Usable.Should().HaveCount(2);
        partition.Refused.Should().BeEmpty();
    }

    // ---- Sweep: every OTHER model-authored string on a bounded surface ---------------------------

    [Fact]
    public void Render_OversizedPathDropsTheEntryRatherThanTruncatingIt()
    {
        // "file" is model-authored too, and it is the one field truncation must never touch: a cut path is
        // not a lesser version of the path, it is a path to nothing. So an entry whose path alone cannot fit
        // is dropped and counted, never emitted half-written.
        var entries = new[]
        {
            Entry("system/" + string.Concat(Enumerable.Repeat("deeply-nested-segment/", 200)) + "a.md", "A", ["a"]),
            Entry("system/beta.md", "Beta", ["b"]),
        };

        var digest = KnowledgeDigest.Render(entries, KbRoot, charBudget: 2_000, omitted: 0);

        digest.Text.Length.Should().BeLessThanOrEqualTo(2_000);
        digest.Text.Should().NotContain("deeply-nested-segment/deeply-nested-segment");
        digest.Rendered.Should().NotContain(entry => entry.Title == "A");

        // The entry AFTER the oversized one must still be surfaced. Asserting only that "A" is absent was
        // the hole in this test: absence holds whether the renderer skips the entry or stops dead at it,
        // so the assertion passed for two rounds while the loop was throwing away every later entry.
        digest.Rendered.Should().ContainSingle(entry => entry.Title == "Beta");
    }

    [Fact]
    public void Render_SkippedOversizedEntryIsCountedAsMissingRatherThanRendered()
    {
        // The skipped entry has to be reachable, and the footer is the only thing that says so. It is
        // counted off the resolved pool, so skipping needs no separate bookkeeping - this test is what
        // says so rather than an assumption that it does.
        var entries = new[]
        {
            Entry("system/" + string.Concat(Enumerable.Repeat("deeply-nested-segment/", 200)) + "a.md", "A", ["a"]),
            Entry("system/beta.md", "Beta", ["b"]),
        };

        var digest = KnowledgeDigest.Render(entries, KbRoot, charBudget: 2_000, omitted: 0);

        digest.Text.Should().Contain("1 more entry is not listed here");
    }

    [Fact]
    public void RenderTableOfContents_OversizedLinkDropsTheLineRatherThanBreakingIt()
    {
        var toc = "# Knowledge Base\n\n- [A](system/"
            + string.Concat(Enumerable.Repeat("deeply-nested-segment/", 200)) + "a.md)\n";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 2_000);

        block.Text.Length.Should().BeLessThanOrEqualTo(2_000);
        block.Text.Should().NotContain("deeply-nested-segment/deeply-nested-segment");
        block.Truncated.Should().BeTrue();
    }

    [Fact]
    public void DescribePaths_BoundsTheModelAuthoredPathListAndSaysHowManyItLeftOut()
    {
        // The digest is bounded, but the log lines that report which entries were surfaced and which were
        // refused join the SAME model-authored paths verbatim. A single 20k "file" value therefore writes a
        // 20 KiB line into the daemon's JSONL for every review that ranks it - and the refusal line is
        // reached precisely by the malformed entries most likely to carry one.
        var paths = Enumerable.Range(0, 50).Select(i => new string('x', 500) + i).ToArray();

        var described = KnowledgeDigest.DescribePaths(paths, charBudget: 400);

        described.Length.Should().BeLessThanOrEqualTo(400);
        described.Should().Contain("more");
    }

    [Fact]
    public void DescribePaths_UnderBudget_ListsEveryPathAndAddsNoSuffix()
    {
        KnowledgeDigest.DescribePaths(["system/alpha.md", "system/beta.md"], charBudget: 400)
            .Should().Be("system/alpha.md, system/beta.md");
    }

    [Fact]
    public void DescribePaths_FirstPathAloneOverBudget_StillReportsWithinTheBudget()
    {
        var described = KnowledgeDigest.DescribePaths([new string('x', 5_000), "system/beta.md"], charBudget: 60);

        described.Length.Should().BeLessThanOrEqualTo(60);
        described.Should().Contain("1 more");
    }

    [Fact]
    public void DescribePaths_OversizedPathDoesNotSuppressTheNamesOfTheOthers()
    {
        // The same shape as both render loops: a path that will not fit is a fact about THAT path, not a
        // signal that the line is full. It matters most here of all - this joiner exists because "file" is
        // model-authored and can be absurd, and the refusal line is reached by exactly the malformed
        // entries likeliest to carry an absurd one. Under a stop-at-the-first-oversized rule that single
        // entry costs the operator the names of every other refused entry alongside it, which are the ones
        // they can actually act on.
        var described = KnowledgeDigest.DescribePaths([new string('x', 5_000), "system/beta.md"], charBudget: 60);

        described.Should().Contain("system/beta.md");
    }

    // ---- Symmetry: the fallback owes every guarantee the ranked path makes ----------------------

    [Theory]
    [InlineData("../../outside.md")]
    [InlineData("/etc/passwd")]
    [InlineData("..\\..\\outside.md")]
    [InlineData("system/../../../outside.md")]
    public void RenderTableOfContents_RefusesLinksThatEscapeTheKnowledgeBase(string link)
    {
        // The ranked path has been containment-checked since round 2; the fallback appended whatever the ToC
        // said. A torn or hand-edited _toc.md is exactly what sends us down this path, so the degraded route
        // was the one still pointing the reviewer outside the Knowledge Base.
        var toc = $"# Knowledge Base\n\n- [Alpha]({link})\n- [Beta](system/beta.md)\n";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 10_000);

        block.Text.Should().NotContain(link);
        block.Text.Should().Contain("system/beta.md", "a contained entry must survive its neighbour's refusal");
        block.Listed.Should().Be(1);
    }

    [Fact]
    public void RenderTableOfContents_ReportsRefusedLinksSoTheyReachTheSameWarning()
    {
        var toc = "# Knowledge Base\n\n- [Alpha](../../outside.md)\n- [Beta](system/beta.md)\n";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 10_000);

        block.Refused.Should().Equal("../../outside.md");
    }

    [Fact]
    public void RenderTableOfContents_RefusalIsNotCountedAsBudgetDrop()
    {
        // Honest counts, same as the ranked path: an entry refused for escaping is a different fact from an
        // entry that did not fit, and a footer promising "1 more entry" in _toc.md would route the agent
        // straight back to the link we just refused.
        var toc = "# Knowledge Base\n\n- [Alpha](../../outside.md)\n- [Beta](system/beta.md)\n";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 10_000);

        block.Dropped.Should().Be(0);
        block.Truncated.Should().BeFalse();
    }

    [Fact]
    public void RenderTableOfContents_ContainedLinksAreNotRefused()
    {
        var toc = "# Knowledge Base\n\n## system\n\n- [Alpha](system/alpha.md)\n- [Beta](system/nested/beta.md)\n";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 10_000);

        block.Refused.Should().BeEmpty();
        block.Listed.Should().Be(2);
    }

    // ---- The fallback's line loop, for the same reason as the ranked one -------------------------

    [Fact]
    public void RenderTableOfContents_EntryWithAnOversizedLinkDoesNotSuppressTheEntriesAfterIt()
    {
        // FitTocLine returning null is NOT a "the budget is exhausted" signal - it is a fact about THIS
        // line. An entry whose "](link)" suffix alone exceeds the room fails it while the next entry, a
        // few dozen characters long, would fit with room to spare. Stopping at the first such line hands
        // the agent a header and nothing else, which is the knowledge-blind outcome this feature exists
        // to prevent, reached through one model-authored link.
        var toc = "# Knowledge Base\n\n- [A](system/"
            + string.Concat(Enumerable.Repeat("deeply-nested-segment/", 200))
            + "a.md)\n- [Beta](system/beta.md)\n";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 3_000);

        block.Text.Length.Should().BeLessThanOrEqualTo(3_000);
        block.Text.Should().NotContain("deeply-nested-segment/deeply-nested-segment");
        block.Text.Should().Contain("system/beta.md");
        block.Listed.Should().Be(1);
    }

    [Fact]
    public void RenderTableOfContents_OversizedProseLineDoesNotSuppressTheEntriesAfterIt()
    {
        // The other way a line can be unrenderable without the budget being spent: a non-entry line too
        // long to fit has no link to shorten, so it fails outright. This fallback runs precisely when
        // _toc.md is torn or hand-edited, which is exactly where a stray oversized line comes from.
        var toc = "# Knowledge Base\n\n" + new string('x', 5_000) + "\n- [Beta](system/beta.md)\n";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 3_000);

        block.Text.Length.Should().BeLessThanOrEqualTo(3_000);
        block.Text.Should().Contain("system/beta.md");
        block.Listed.Should().Be(1);
    }

    [Fact]
    public void RenderTableOfContents_SkippedLineIsCountedAsDroppedAndAdmitsTheCut()
    {
        var toc = "# Knowledge Base\n\n- [A](system/"
            + string.Concat(Enumerable.Repeat("deeply-nested-segment/", 200))
            + "a.md)\n- [Beta](system/beta.md)\n";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 3_000);

        block.Dropped.Should().Be(1);
        block.Truncated.Should().BeTrue();
    }

    // ---- Parsing what feeds the check, not just the check ---------------------------------------

    [Fact]
    public void ParseChangedPaths_DropsARecordTheCapHalvedRatherThanRankingAgainstIt()
    {
        // The generic output cap is character-exact, so on the sandbox path the cut can land inside a record
        // and leave a stump in front of the marker. "src/VeryLongFileNa" reads exactly like a real path and
        // is ranked against as one, silently, because the result is still non-empty. The marker opens with
        // "\n", so a record that survived a clean cut is followed by an EMPTY line and one that was halved is
        // not — that is the evidence, and it is available right here.
        var nameOnly = "src/A.cs\nsrc/VeryLongFileNa" + SandboxLimits.TruncationMarker;

        KnowledgeDigest.ParseChangedPaths(nameOnly, out var truncated).Should().Equal("src/A.cs");
        truncated.Should().BeTrue();
    }

    [Fact]
    public void ParseChangedPaths_KeepsTheRecordBeforeAMarkerTheListingCapPlacedOnALineBoundary()
    {
        // The other half of the pair, and the reason the listing keeps its own cap: when the producer cut on
        // a boundary, the last record IS whole and dropping it would cost a file the reviewer needs.
        var limits = new SandboxLimits { MaxArtifactPayloadChars = 16 };
        var nameOnly = limits.CapRecordListing("src/A.cs\nsrc/VeryLongFileName.cs\n");

        KnowledgeDigest.ParseChangedPaths(nameOnly, out var truncated).Should().Equal("src/A.cs");
        truncated.Should().BeTrue();
    }

    [Fact]
    public void ExtractChangedPaths_DropsAHalvedDiffHeaderRatherThanRankingAgainstIt()
    {
        // Same defect on the fallback route, which is reached exactly when the listing was unavailable. A
        // header cut after its " b/" separator still parses: the left side is a real path and the right side
        // is a stump, and both are added.
        var diff = "diff --git a/src/A.cs b/src/A.cs\n@@ -1 +1 @@\n+x\ndiff --git a/src/Beta.cs b/src/Bet"
            + SandboxLimits.TruncationMarker;

        KnowledgeDigest.ExtractChangedPaths(diff).Should().Equal("src/A.cs");
    }

    // ---- The link check is only as good as the link it is handed --------------------------------

    [Theory]
    [InlineData("</etc/passwd>")]
    [InlineData("< /etc/passwd >")]
    [InlineData("<../../outside.md>")]
    [InlineData("<a)/../../../../etc/passwd>")]
    [InlineData("<a b/../../../etc/passwd>")]
    public void RenderTableOfContents_RefusesAnAngleBracketLinkThatEscapesTheKnowledgeBase(string link)
    {
        // CommonMark lets a destination be wrapped in angle brackets, and the agent resolves Markdown the
        // standard way. The containment rule was correct; what reached it was "</etc/passwd>", which does not
        // begin with "/" and so sailed past the leading-slash rejection added for exactly this case.
        var toc = $"# Knowledge Base\n\n- [Alpha]({link})\n- [Beta](system/beta.md)\n";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 10_000);

        block.Text.Should().NotContain("/etc/passwd").And.NotContain("outside.md");
        block.Text.Should().Contain("system/beta.md", "a contained entry must survive its neighbour's refusal");
        block.Listed.Should().Be(1);
    }

    [Fact]
    public void RenderTableOfContents_ReportsTheNormalizedDestinationItRefused()
    {
        var toc = "# Knowledge Base\n\n- [Alpha](</etc/passwd>)\n- [Beta](system/beta.md)\n";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 10_000);

        block.Refused.Should().Equal("/etc/passwd");
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://evil.example/pwn.md")]
    [InlineData("C:\\Windows\\System32\\drivers\\etc\\hosts")]
    public void RenderTableOfContents_RefusesALinkThatIsNotARelativePathAtAll(string link)
    {
        // A URI is not a Knowledge Base entry, and TryResolveEntryPath cannot say so: it splits on "/", finds
        // "https:" and "evil.example" to be ordinary segments, and reports the link contained. The line is
        // then printed verbatim to an agent that will follow it as written.
        var toc = $"# Knowledge Base\n\n- [Alpha]({link})\n- [Beta](system/beta.md)\n";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 10_000);

        block.Text.Should().NotContain(link);
        block.Text.Should().Contain("system/beta.md");
        block.Listed.Should().Be(1);
    }

    [Fact]
    public void RenderTableOfContents_ChecksEveryLinkOnALineNotOnlyTheLast()
    {
        // LastIndexOf finds one link. A line carrying two validates the last and renders both, so the escape
        // only has to not be written last.
        var toc = "# Knowledge Base\n\n- [Alpha](/etc/passwd) see also [Beta](system/beta.md)\n"
            + "- [Gamma](system/gamma.md)\n";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 10_000);

        block.Text.Should().NotContain("/etc/passwd");
        block.Text.Should().Contain("system/gamma.md", "the refusal is about that line, not about the rest");
        block.Refused.Should().Contain("/etc/passwd");
    }

    [Fact]
    public void RenderTableOfContents_TwoSafeLinksOnOneLineAreBothKept()
    {
        var toc = "# Knowledge Base\n\n- [Alpha](system/alpha.md) see also [Beta](system/beta.md)\n";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 10_000);

        block.Refused.Should().BeEmpty();
        block.Text.Should().Contain("system/alpha.md").And.Contain("system/beta.md");
    }

    [Fact]
    public void RenderTableOfContents_DoesNotMisattributeALinkWhenAMultiLinkLineCannotFit()
    {
        // The title cut is anchored on the LAST "](", so on a two-link line everything between the first
        // link and the last is treated as title text. The line comes back as "- [Alpha… (truncated)](beta)":
        // a link labelled with one entry's title and pointing at a different entry. Misattributed knowledge
        // is worse than absent knowledge, so a multi-link line fits whole or is dropped and counted.
        var toc = "# Knowledge Base\n\n- [" + new string('A', 400)
            + "](system/alpha.md) see also [Beta](system/beta.md)\n";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 1_000);

        block.Text.Should().NotContain("(truncated)](system/beta.md)");
        block.Dropped.Should().Be(1);
        block.Truncated.Should().BeTrue();
    }

    [Fact]
    public void RenderTableOfContents_RefusesPlainRelativeTraversalOnANonFinalLink()
    {
        // The wide form of the defect, and the one that needs no exotic syntax at all - no angle brackets,
        // no URI scheme, no leading slash. LastIndexOf("](") lands before "ok.md", so the check validated the
        // SECOND destination, passed it, and printed the line verbatim, handing the agent the first. Fixing
        // only the angle-bracket normalization would have left this open.
        var toc = "# Knowledge Base\n\n- [a](../../../etc/passwd) and [b](ok.md)\n- [Beta](system/beta.md)\n";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 10_000);

        block.Text.Should().NotContain("../../../etc/passwd");
        block.Text.Should().Contain("system/beta.md", "the entry after the refused line must still be surfaced");
        block.Refused.Should().Contain("../../../etc/passwd");
        block.Listed.Should().Be(1);
    }

    [Fact]
    public void RenderTableOfContents_MultiLinkCutNeverEmitsADestinationThatAppearsNowhereInTheInput()
    {
        // The title cut is anchored on the LAST "](", so on a two-link line it lands at an arbitrary offset
        // INSIDE the first destination and the line is re-emitted as "- [a](system/xxx… (truncated)](ok.md)":
        // a path nobody wrote, over a link belonging to a different entry. That is precisely the half-written
        // path FitTocLine's own doc comment says it exists to prevent - the comment was right, and the
        // implementation honoured it only for single-link lines.
        var toc = "# Knowledge Base\n\n- [a](system/" + new string('x', 400) + ".md) and [b](system/ok.md)\n"
            + "- [Beta](system/beta.md)\n";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 1_000);

        // The two-link line is the only one here that could be cut, so a cut marker adjacent to ANY link is
        // the mangle: a destination assembled from a fragment of the first and the whole of the second.
        block.Text.Should().NotContain(" (truncated)](");
        block.Text.Should().NotContain("system/ok.md", "the line is dropped whole, never partially scrubbed");
        block.Text.Should().Contain("system/beta.md", "the entry after the dropped line must still be surfaced");
    }

    [Fact]
    public void RenderTableOfContents_SingleLinkCutNeverEmitsADestinationThatAppearsNowhereInTheInput()
    {
        // The same defect one parser over. Refusing to cut multi-link lines fixed the two-link shape but left
        // FitTocLine re-deriving the link with its own LastIndexOf("["+"]("), and on a SINGLE-link line whose
        // angle-bracketed destination contains "](" that anchor lands INSIDE the destination. The title cut
        // then falls at an arbitrary offset and the line re-renders over the tail fragment "b.md>" - a path
        // nobody wrote, on a line the containment check had already cleared as safe. Two parsers reading one
        // syntax will disagree eventually; only one of them can be right.
        var toc = "# Knowledge Base\n\n- [" + new string('t', 300) + "](<system/" + new string('x', 100)
            + "](b.md>)\n- [Beta](system/beta.md)\n";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 1_000);

        // The cut belongs in the title, so what follows it must be the destination the parser cleared,
        // starting where it started in the input - not the tail of it that a second reading mistook for one.
        block.Text.Should().NotContain(" (truncated)](b.md>");
        block.Text.Should().Contain(" (truncated)](<system/", "the cleared destination is carried whole");

        // Paired with what SHOULD be there: the line is kept and counted, not quietly discarded, and the
        // entry the cut left no room for is admitted. (A fitted line takes the whole remaining budget, so
        // nothing can follow it here - the honest count is what carries that fact, not a later entry.)
        block.Listed.Should().Be(1);
        block.Dropped.Should().Be(1);
        block.Truncated.Should().BeTrue();
    }
}
