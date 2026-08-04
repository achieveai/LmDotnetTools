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
    // The ranked route resolves "file" itself rather than going through the link rule, so the character
    // reference has to be read here too. "..&#x2F;..&#x2F;etc/passwd" contains no literal "/" outside the
    // last segment, so the split sees ONE ordinary segment, the path reduces to something contained, and the
    // agent is handed an absolute path that decodes to <root>/../../etc/passwd. Joining onto the root is
    // what makes a leading slash harmless here; it does nothing about a separator spelled as an entity.
    [InlineData("..&#x2F;..&#x2F;etc/passwd")]
    // And the NAMED spelling of the same separator, which is the reason the rule is now a refusal rather
    // than a decode. WebUtility.HtmlDecode implements a pre-HTML5 table: it resolves "&#x2F;" and leaves
    // "&sol;" exactly as written, while a GFM reader resolves both. Reading the decoded spelling therefore
    // closed one half of the hole and left the other open - we picked an entity set once and picked wrong.
    [InlineData("..&sol;..&sol;etc/passwd")]
    [InlineData("..&bsol;..&bsol;secrets.md")]
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
    public void Render_KeepsAnEntryWhoseTitleLinksToASiblingDocAndClearsOnlyTheTitle()
    {
        // The case that decides the remedy, and it is not adversarial at all: an extraction agent doing its
        // job writes a title pointing at the repo's own docs, which are OUTSIDE the Knowledge Base root
        // because repo docs simply are. Refusing the entry for it would delete a sound lesson from the
        // digest over a decoration - reintroducing the knowledge-blindness this feature exists to remove,
        // on the primary route, triggered by a title. The link is what must not survive; the entry is what
        // must. Only the LOAD-BEARING field has to be contained, and File was cleared separately.
        var digest = KnowledgeDigest.Render(
            [Entry("system/ado.md", "Follow the [ADO onboarding guide](../../docs/ado.md) before first run", ["ado"])],
            KbRoot,
            charBudget: 10_000,
            omitted: 0);

        digest.Text.Should().NotContain("../../docs/ado.md", "the escaping link must not reach the agent");
        digest.Text.Should().Contain(
            "/workspace/store/KnowledgeBase/system/ado.md",
            "the entry itself is sound and must still be surfaced - that pairing is the whole argument");
        digest.Rendered.Should().ContainSingle().Which.File.Should().Be("system/ado.md");
        digest.Rejected.Should().BeEmpty();
        digest.Neutralized.Should().ContainSingle().Which.File.Should().Be("system/ado.md");
    }

    [Theory]
    [InlineData("see [x](../../../etc/passwd) for context")]
    [InlineData("plain [x](</etc/passwd>)")]
    public void Render_ClearsATitleThatCarriesAnEscapingLink(string title)
    {
        // Same shape as the _toc.md gate, on the PRIMARY route: containment was applied to entry.File and to
        // nothing else, while Title, tags and scope are written by the same knowledge-extraction agent and
        // are rendered into the block verbatim. A link in a title resolves for the reviewer exactly like a
        // link anywhere else, so the check has to see the whole rendered entry.
        //
        // The VERDICT differs from the fallback route's, though, because the situations differ. On a _toc.md
        // line the link IS the entry - strip it and nothing is left, so refusal is the only remedy available.
        // Here the link sits in decoration next to a File the agent can still open. Whole field, never part
        // of one: replacing a value outright cannot emit the half-path or the misattributed title that
        // editing INSIDE a value produced twice on the other route.
        var digest = KnowledgeDigest.Render(
            [Entry("system/alpha.md", title, ["a"]), Entry("system/beta.md", "Beta", ["b"])],
            KbRoot,
            charBudget: 10_000,
            omitted: 0);

        digest.Text.Should().NotContain("/etc/passwd");
        digest.Text.Should().Contain("system/beta.md", "a clean entry must survive its neighbour's scrub");
        digest.Text.Should().Contain(
            "- system/alpha.md", "a cleared title falls back to the file path, as a blank one already did");
        digest.Rendered.Should().HaveCount(2);
        digest.Rejected.Should().BeEmpty();
        digest.Neutralized.Should().ContainSingle().Which.File.Should().Be("system/alpha.md");
    }

    [Fact]
    public void Render_ClearsATitleThatCarriesAReferenceStyleLinkAndItsDefinition()
    {
        // The same syntax the _toc.md gate now refuses, arriving by the PRIMARY route. Every metadata check
        // runs through TocLinks, which keys on "](", and a reference-style link contains none - so a title
        // holding both halves is carried into the block verbatim. A title is not line-anchored, but nothing
        // strips newlines out of one either: _index.jsonl is JSON, "\n" is an ordinary character in a JSON
        // string, and RenderEntry interpolates the value as it stands. So one field can put a definition at
        // the start of a rendered line and a reference above it, and the agent resolves the pair itself.
        var digest = KnowledgeDigest.Render(
            [
                Entry("system/alpha.md", "See [a][outside]\n\n[outside]: ../../../etc/passwd", ["a"]),
                Entry("system/beta.md", "Beta", ["b"]),
            ],
            KbRoot,
            charBudget: 10_000,
            omitted: 0);

        digest.Text.Should().NotContain("/etc/passwd");
        digest.Text.Should().NotContain("[outside]", "half a reference is still a live link once the other half arrives");
        digest.Text.Should().Contain("system/beta.md", "a clean entry must survive its neighbour's scrub");
        digest.Text.Should().Contain(
            "- system/alpha.md", "a cleared title falls back to the file path, as a blank one already did");
        digest.Rendered.Should().HaveCount(2);
        digest.Rejected.Should().BeEmpty();
        digest.Neutralized.Should().ContainSingle().Which.File.Should().Be("system/alpha.md");
    }

    [Fact]
    public void Render_DropsOnlyTheTagThatCarriesAnEscapingLinkAndKeepsTheRest()
    {
        // Per-VALUE, not per-entry and not per-character: the offending tag goes whole and its neighbours
        // stay. A tag list is already a list of independent values, so there is nothing to fabricate by
        // dropping one - unlike a cut inside a value, which is where this file's earlier defects lived.
        var digest = KnowledgeDigest.Render(
            [Entry("system/alpha.md", "Alpha", ["safe", "see [x](../../../etc/passwd)", "also-safe"])],
            KbRoot,
            charBudget: 10_000,
            omitted: 0);

        digest.Text.Should().NotContain("/etc/passwd");
        digest.Text.Should().Contain("tags: safe, also-safe");
        digest.Text.Should().Contain("Alpha", "an untouched title is not collateral of a bad tag");
        digest.Rendered.Should().ContainSingle();
        digest.Neutralized.Should().ContainSingle().Which.File.Should().Be("system/alpha.md");
    }

    [Fact]
    public void Render_ClearsAScopeThatCarriesAnEscapingLink()
    {
        var digest = KnowledgeDigest.Render(
            [Entry("system/alpha.md", "Alpha", ["a"], scope: "see [x](/etc/passwd)")],
            KbRoot,
            charBudget: 10_000,
            omitted: 0);

        digest.Text.Should().NotContain("/etc/passwd");
        digest.Text.Should().Contain("scope: (unscoped)", "a cleared scope takes the existing blank fallback");
        digest.Rendered.Should().ContainSingle();
        digest.Neutralized.Should().ContainSingle();
    }

    [Fact]
    public void Render_LeavesACleanEntryOutOfTheNeutralizedList()
    {
        // The counter has to mean something: if it fires on entries nothing was done to, an operator reading
        // it learns nothing about extraction quality, which is the only reason it is reported.
        var digest = KnowledgeDigest.Render(
            [Entry("system/alpha.md", "Alpha", ["a"])], KbRoot, charBudget: 10_000, omitted: 0);

        digest.Neutralized.Should().BeEmpty();
        digest.Rendered.Should().ContainSingle();
    }

    [Fact]
    public void Render_KeepsAnEntryWhoseTitleCarriesAContainedLink()
    {
        // Containment, not a ban on Markdown in titles - the same distinction the path check already draws.
        var digest = KnowledgeDigest.Render(
            [Entry("system/alpha.md", "see [x](system/notes.md)", ["a"])], KbRoot, charBudget: 10_000,
            omitted: 0);

        digest.Rejected.Should().BeEmpty();
        digest.Text.Should().Contain("system/notes.md");
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

    // ---- Duplicate index records must not consume retrieval slots ------------------------------------

    [Fact]
    public void DeduplicateBeforeSelect_SurfacesKnowledgeThatRepeatedRecordsWouldHaveCrowdedOut()
    {
        // A merge that concatenated _index.jsonl with itself is an anticipated broken input - it is the very
        // shape KnowledgeIndex.MaxIndexRecords documents. Identical paths score identically, so the copies
        // sort adjacent and take consecutive slots: 20 distinct entries duplicated fill all 24 slots with 12
        // files, and 8 usable entries the reviewer needed are dropped for records it already has.
        var store = Enumerable.Range(0, 20)
            .Select(i => Entry($"system/entry-{i:D2}.md", $"Runner lesson {i}", ["runner"]))
            .ToArray();
        var entries = store.Concat(store).ToArray();

        var partition = KnowledgeDigest.PartitionByContainment(entries, KbRoot);
        var sanitized = KnowledgeDigest.SanitizeMetadata(partition.Usable, KbRoot);
        var deduplicated = KnowledgeDigest.Deduplicate(sanitized.Entries, KbRoot);
        var selected = KnowledgeDigest.SelectRelevant(
            deduplicated.Entries, ["src/Runner.cs"], "LmDotnetTools", maxEntries: 24);

        // Assert what SHOULD be there: every distinct entry survives, including the tail the duplicates ate.
        selected.Select(entry => entry.File).Should().OnlyHaveUniqueItems();
        selected.Should().HaveCount(20);
        selected.Select(entry => entry.File).Should().Contain("system/entry-19.md");
        deduplicated.Collapsed.Should().HaveCount(20, "every collapsed record must still be reportable");
        deduplicated.Conflicting.Should().BeEmpty("identical copies are repetition, not a torn index");
    }

    [Fact]
    public void Deduplicate_KeysOnTheResolvedPathSoASpelledDetourIsStillTheSameEntry()
    {
        // Keyed on the raw "file" string, 'system/../system/alpha.md' and 'system/alpha.md' stay distinct
        // and the reviewer is handed the same entry twice under two spellings - the duplicate this exists to
        // remove, wearing the one disguise an LLM-authored path most easily puts on.
        var entries = new[]
        {
            Entry("system/alpha.md", "Alpha", ["a"]),
            Entry("system/../system/alpha.md", "Alpha", ["a"]),
            Entry("system/beta.md", "Beta", ["b"]),
        };

        var deduplicated = KnowledgeDigest.Deduplicate(entries, KbRoot);

        deduplicated.Entries.Select(entry => entry.File).Should().Equal("system/alpha.md", "system/beta.md");
        deduplicated.Collapsed.Should().ContainSingle().Which.File.Should().Be("system/../system/alpha.md");
    }

    [Fact]
    public void Deduplicate_KeepsTheNewestRecordAndReportsConflictingCopiesAsATornIndex()
    {
        // Two records for one path that DISAGREE are not repetition - that is a torn or half-merged index,
        // and the operator needs to know, because whichever copy loses is knowledge the reviewer will not see.
        var entries = new[]
        {
            Entry("system/alpha.md", "Stale title", ["a"], updated: "2026-01-01"),
            Entry("system/alpha.md", "Current title", ["a"], updated: "2026-08-01"),
        };

        var deduplicated = KnowledgeDigest.Deduplicate(entries, KbRoot);

        deduplicated.Entries.Should().ContainSingle().Which.Title.Should().Be("Current title");
        deduplicated.Conflicting.Should().ContainSingle().Which.File.Should().Be("system/alpha.md");
    }

    [Fact]
    public void Deduplicate_AStoreWithoutRepeatsIsHandedBackUntouched()
    {
        // The partner pin. Collapsing anything here - or reordering - would silently shrink a healthy store,
        // and the ranking below depends on the order it is given.
        var entries = new[]
        {
            Entry("system/alpha.md", "Alpha", ["a"]),
            Entry("system/beta.md", "Beta", ["b"]),
            Entry("testing/gamma.md", "Gamma", ["c"], scope: "testing"),
        };

        var deduplicated = KnowledgeDigest.Deduplicate(entries, KbRoot);

        deduplicated.Entries.Should().Equal(entries);
        deduplicated.Collapsed.Should().BeEmpty();
        deduplicated.Conflicting.Should().BeEmpty();
    }

    [Fact]
    public void RenderTableOfContents_ListsARepeatedEntryOnceAndDoesNotPromiseItAgain()
    {
        // The neighbour route into the same prompt, and the same broken input: a _toc.md concatenated with
        // itself spends the character budget listing entries the reviewer already has. The footer arithmetic
        // has to move with it - counting a duplicate as "1 more entry in _toc.md" routes the agent back to
        // the line it just read.
        var toc = string.Join(
            "\n",
            Enumerable.Range(0, 3).Select(i => $"- [Entry {i}](system/entry-{i}.md)"));

        var block = KnowledgeDigest.RenderTableOfContents(toc + "\n" + toc, KbRoot, charBudget: 10_000);

        block.Listed.Should().Be(3);
        block.Duplicates.Should().Be(3);
        block.Dropped.Should().Be(0, "a duplicate is not an entry waiting in _toc.md");
        block.Text.Should().Contain("system/entry-0.md");
        (block.Text.Split("(system/entry-0.md)").Length - 1).Should().Be(1);
    }

    [Fact]
    public void RenderTableOfContents_ATableWithoutRepeatsKeepsEveryLine()
    {
        // Partner pin for the neighbour route: nothing is collapsed out of a healthy table of contents.
        var toc = "# Knowledge Base\n\n- [Alpha](system/alpha.md)\n- [Beta](system/beta.md)";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 10_000);

        block.Listed.Should().Be(2);
        block.Duplicates.Should().Be(0);
        block.Text.Should().Contain("- [Alpha](system/alpha.md)");
        block.Text.Should().Contain("- [Beta](system/beta.md)");
    }

    [Fact]
    public void SanitizeBeforeSelect_SurfacesKnowledgeThatAnEscapingTagWouldHaveOutranked()
    {
        // Ranking on metadata that is about to be DELETED is its own crowding-out, distinct from the
        // containment one above: these 24 entries are contained, so the partition keeps them, and their only
        // match for "runner" is a tag that Render will strip before the reviewer ever sees it. Scored raw,
        // they take every slot on the strength of that tag and push out the one entry that genuinely matched
        // - so the delivered set does not contain the relevance that justified selecting it.
        var entries = Enumerable
            .Range(0, 24)
            .Select(i => Entry(
                $"system/decoy-{i}.md",
                $"Lesson {i}",
                ["[runner](../../../etc/passwd)"],
                updated: "2026-08-01"))
            .Append(Entry("system/alpha.md", "Runner lesson", ["runner"], updated: "2026-01-01"))
            .ToArray();

        var partition = KnowledgeDigest.PartitionByContainment(entries, KbRoot);
        var sanitized = KnowledgeDigest.SanitizeMetadata(partition.Usable, KbRoot);
        var selected = KnowledgeDigest.SelectRelevant(
            sanitized.Entries, ["src/Runner.cs"], "LmDotnetTools", maxEntries: 24);
        var digest = KnowledgeDigest.Render(
            selected, KbRoot, charBudget: 100_000, omitted: sanitized.Entries.Count - selected.Count);

        digest
            .Rendered.Should()
            .Contain(
                entry => entry.File == "system/alpha.md",
                "the entry that really matched the changed path must not lose its slot to a tag that is deleted before delivery");
        digest.Rendered.Should().HaveCount(24, "the decoys are cleaned and kept, not refused - only outranked");
        sanitized
            .Neutralized.Should()
            .HaveCount(24)
            .And.OnlyContain(
                entry => entry.Tags.Any(tag => tag.Contains("etc/passwd", StringComparison.Ordinal)),
                "the diagnostic carries the ORIGINAL entry, so it can still name what the extraction agent wrote");

        // Cleaning must leave File alone, because File is the JOIN KEY the caller uses to say which
        // neutralized entries actually reached the reviewer, and to fold the two sources of cleaning
        // together. If cleaning rewrote it, that intersection would quietly come back empty and the warning
        // would report "0 reached the reviewer" over a digest that surfaced all of them.
        sanitized
            .Entries.Select(entry => entry.File)
            .Should()
            .Equal(
                partition.Usable.Select(entry => entry.File),
                "a cleaned entry has to stay identifiable as the entry it was cleaned from");
    }

    [Fact]
    public void SelectRelevant_ScoresTheTitleTheReviewerWillActuallyRead()
    {
        // Round 11's finding one level down, and it survived that fix because the field is not deleted here,
        // it is SUBSTITUTED: a title cleared for carrying an escaping link renders as the file path, which is
        // exactly where this entry's "runner" match lives. Scoring the stored title gives it zero, so 24
        // newer entries that match nothing take every slot on recency alone - while the block the reviewer
        // receives names "system/runner.md", carrying the very token that would have ranked it.
        //
        // The invariant, not the patch: the scorer and the renderer must read an entry through the SAME
        // expression. Anything else re-opens as soon as one of them changes.
        var entries = Enumerable
            .Range(0, 24)
            .Select(i => Entry($"system/decoy-{i}.md", $"Lesson {i}", ["unrelated"], updated: "2026-08-01"))
            .Append(
                Entry(
                    "system/runner.md",
                    "see [x](../../../etc/passwd) notes",
                    ["kb"],
                    updated: "2026-01-01"))
            .ToArray();

        var partition = KnowledgeDigest.PartitionByContainment(entries, KbRoot);
        var sanitized = KnowledgeDigest.SanitizeMetadata(partition.Usable, KbRoot);
        var selected = KnowledgeDigest.SelectRelevant(
            sanitized.Entries, ["src/Runner.cs"], "LmDotnetTools", maxEntries: 24);

        selected
            .Should()
            .Contain(
                entry => entry.File == "system/runner.md",
                "the entry is delivered under a title that matches the changed path, so it has to be ranked on that title");
        selected.Should().HaveCount(24);
    }

    [Fact]
    public void SelectRelevant_ScoresAPathOnlyWhereThePathIsWhatGetsRendered()
    {
        // The other half of the same expression, and the reason it is "effective title" rather than "title
        // and path": the path is scored only when it IS the rendered title. Tokenizing File unconditionally
        // would be the easy over-correction - it makes the test above pass too - and it hands every entry the
        // tokens of its own directory and filename, which the reviewer never reads as that entry's subject.
        //
        // Discriminating on purpose: the decoy's PATH carries "blank" while its title does not, and it is the
        // newer of the two. Score the path regardless and the decoy ties and wins on recency; score what is
        // rendered and it scores nothing at all.
        var entries = new[]
        {
            Entry("system/blank-notes.md", "Unrelated lesson", ["kb"], updated: "2026-08-01"),
            Entry("system/other.md", "Blank lesson", ["kb"], updated: "2026-01-01"),
        };

        var selected = KnowledgeDigest.SelectRelevant(
            entries, ["src/Blank.cs"], "LmDotnetTools", maxEntries: 1);

        selected
            .Should()
            .ContainSingle()
            .Which.File.Should()
            .Be(
                "system/other.md",
                "a titled entry is ranked on its title; its path is not a second set of tokens the reviewer never sees as its subject");
    }

    [Fact]
    public void SelectRelevant_ScoresTheFileOfABlankTitledEntry()
    {
        // And the fallback direction, so "score what is rendered" is pinned from both sides: a blank title
        // renders as the path, so the path is what this entry is ranked on.
        var entries = new[]
        {
            Entry("system/lesson.md", "Unrelated lesson", ["kb"], updated: "2026-08-01"),
            Entry("system/blank.md", "  ", ["kb"], updated: "2026-01-01"),
        };

        var selected = KnowledgeDigest.SelectRelevant(
            entries, ["src/Blank.cs"], "LmDotnetTools", maxEntries: 1);

        selected
            .Should()
            .ContainSingle()
            .Which.File.Should()
            .Be(
                "system/blank.md",
                "the blank-title entry is delivered under its path, so its path is what selected it");
    }

    [Fact]
    public void Render_NeutralizedIsNotASubsetOfRendered_WhenTheBudgetCutsTheCleanedEntry()
    {
        // Neutralized is filled BEFORE the character budget is applied and Rendered after, so the two lists
        // can disagree - which is correct, because what the extraction agent wrote is true whether or not
        // there was room to print it. What is NOT correct is reading the first as the second: a caller that
        // reports Neutralized as "kept and still surfaced" names an entry the reviewer never received, and a
        // proof of delivery that can name undelivered entries proves nothing at all.
        var alpha = Entry("system/alpha.md", "Alpha", ["a"]);
        var cleaned = Entry(
            $"system/{new string('c', 300)}.md",
            "Read [the guide](../../../etc/passwd)",
            ["b"]);

        // Sized from a real render of the entry that must fit, so the budget cannot silently drift into
        // "everything fits" or "nothing fits" - both of which would pass a weaker pair of assertions.
        var roomForAlphaAlone = KnowledgeDigest.Render([alpha], KbRoot, charBudget: 100_000, omitted: 1);
        var block = KnowledgeDigest.Render(
            [alpha, cleaned], KbRoot, charBudget: roomForAlphaAlone.Text.Length + 50, omitted: 0);

        block.Neutralized.Should().ContainSingle().Which.File.Should().Be(cleaned.File);
        block.Rendered.Should().NotContain(entry => entry.File == cleaned.File);
        block
            .Rendered.Should()
            .Contain(entry => entry.File == "system/alpha.md", "the entry that did fit still has to ship");
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

    [Theory]
    [InlineData("  - [x](../../../etc/passwd)")]
    [InlineData("* [x](../../../etc/passwd)")]
    [InlineData("1. [x](../../../etc/passwd)")]
    [InlineData("See [notes](../../../etc/passwd).")]
    public void RenderTableOfContents_RefusesAnEscapingLinkOnALineThatIsNotOurOwnEntryShape(string line)
    {
        // Containment was gated on IsTocEntry, which recognises the one shape OUR generator emits: a line
        // starting with exactly "- [". Every other line was rendered verbatim without ever being parsed. An
        // indented entry, a "*" bullet, an ordered list and a link in prose are all ordinary Markdown - the
        // agent resolves them the same way - so the gate excused from the check precisely the lines this
        // degraded route exists to handle, a torn or hand-edited _toc.md.
        var toc = $"# Knowledge Base\n\n{line}\n- [Beta](system/beta.md)\n";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 10_000);

        block.Text.Should().NotContain("../../../etc/passwd");
        block.Text.Should().Contain("system/beta.md", "a contained entry must survive its neighbour's refusal");
        block.Refused.Should().Contain("../../../etc/passwd");
        block.Listed.Should().Be(1);
    }

    [Fact]
    public void RenderTableOfContents_RefusingANonEntryLineLeavesTheEntryCountsAlone()
    {
        // The refusal counter is subtracted from a total that counts ENTRY lines, so a non-entry refusal must
        // not move it - "1 entry, 1 listed, 1 refused" reports minus one dropped. Checked because this
        // arithmetic has already gone negative once, when one line could carry two refused links.
        var toc = "# Knowledge Base\n\nSee [notes](../../../etc/passwd).\n- [Beta](system/beta.md)\n";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 10_000);

        block.Listed.Should().Be(1);
        block.Dropped.Should().Be(0);
        block.Refused.Should().Contain("../../../etc/passwd");
    }

    [Fact]
    public void RenderTableOfContents_DoesNotRewriteAProseLineIntoAnEntryToMakeItFit()
    {
        // The title cut writes "- [" back on the front and slices from index 3, so it is only meaningful on a
        // line that HAS that prefix. Now that non-entry lines are parsed for links too, a long prose line
        // carrying one safe link reaches the cut, and applying it there would invent an entry that the
        // _toc.md never contained - out of the middle of a sentence.
        var toc = "# Knowledge Base\n\nSee " + new string('w', 400) + " [notes](system/ok.md) for more.\n"
            + "- [Beta](system/beta.md)\n";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 1_000);

        block.Text.Should().NotContain(" (truncated)", "a prose line has no title to cut");
        block.Text.Should().Contain("system/beta.md", "the entry after the dropped line must still be surfaced");
        block.Listed.Should().Be(1);
    }

    [Theory]
    [InlineData("- [a]( <x)/../../../../etc/passwd> )", "x)/../../../../etc/passwd")]
    [InlineData(@"- [a](x\)/../../../../etc/passwd)", @"x\)/../../../../etc/passwd")]
    [InlineData("- [a](x(y)/../../../../etc/passwd)", "x(y)/../../../../etc/passwd")]
    [InlineData(@"- [a](.\./.\./.\./etc/passwd)", @".\./.\./.\./etc/passwd")]
    public void RenderTableOfContents_EndsADestinationWhereCommonMarkEndsIt(string line, string destination)
    {
        // Three ways to make our reading of where the destination ENDS disagree with the agent's, each one
        // leaving a contained prefix in front of the check and the whole escaping path in front of the agent.
        // CommonMark permits whitespace after "](", so the angle form is not always at that offset; a "\)" is
        // an escaped literal, not a terminator; and a bare destination may carry BALANCED parens, so the
        // first ")" is not necessarily the last. Cutting at the first ")" validates "<x", "x\" and "x(y" -
        // all three contained, all three a prefix of a path that walks out of the store.
        //
        // The fourth row is the same disagreement about the destination's CONTENT rather than its end, and it
        // runs the other way: ".\./" is a literal ".." once the escape is resolved, while read verbatim - with
        // the backslash taken for the Windows separator it also is - it is a harmless "./". Read either way
        // alone and one of these two rows walks out; the rule has to hold under both.
        var toc = $"# Knowledge Base\n\n{line}\n- [Beta](system/beta.md)\n";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 10_000);

        block.Text.Should().NotContain("etc/passwd");
        block.Text.Should().Contain("system/beta.md", "a contained entry must survive its neighbour's refusal");
        block.Refused.Should().Contain(destination);
    }

    [Fact]
    public void RenderTableOfContents_RefusesALineWhoseDestinationIsNeverClosed()
    {
        // An unterminated destination parsed to ZERO links, and zero links read as "nothing to check" - so
        // the line went out verbatim without the containment rule ever being consulted. That is the failure
        // this whole route exists to prevent, arrived at through the parser instead of through the rule: an
        // undelimitable destination is an UNRENDERABLE line, not a safe one.
        var toc = "# Knowledge Base\n\n- [a](../../../etc/passwd\n- [Beta](system/beta.md)\n";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 10_000);

        block.Text.Should().NotContain("etc/passwd");
        block.Text.Should().Contain("system/beta.md", "a contained entry must survive its neighbour's refusal");
        block.Refused.Should().Contain("../../../etc/passwd");
    }

    [Fact]
    public void RenderTableOfContents_RefusesALineWhoseContainedDestinationIsNeverClosed()
    {
        // Refused for being undelimitable, NOT for where the fragment appears to point: this destination
        // reads as contained. We cannot know where it was meant to end, so we cannot know what the agent
        // resolves - and "it looked fine as far as we got" is the reasoning that lost every earlier round.
        var toc = "# Knowledge Base\n\n- [a](system/alpha.md\n- [Beta](system/beta.md)\n";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 10_000);

        block.Text.Should().NotContain("system/alpha.md");
        block.Text.Should().Contain("system/beta.md", "a contained entry must survive its neighbour's refusal");
        block.Refused.Should().Contain("system/alpha.md");
    }

    [Fact]
    public void Render_ClearsATitleWhoseLinkDestinationIsNeverClosed()
    {
        // The metadata route reads links with the SAME parser, so it inherited the same fail-open: a title
        // whose destination never closes parsed to zero links and was carried into the block verbatim.
        var entries = new[] { Entry("system/alpha.md", "Read [the guide](../../../etc/passwd", ["auth"]) };

        var block = KnowledgeDigest.Render(entries, KbRoot, charBudget: 10_000, omitted: 0);

        block.Text.Should().NotContain("etc/passwd");
        block.Text.Should().Contain("/workspace/store/KnowledgeBase/system/alpha.md", "the entry is kept");
        block.Neutralized.Should().ContainSingle().Which.File.Should().Be("system/alpha.md");
    }

    [Fact]
    public void RenderTableOfContents_RefusesALineWhoseAngleDestinationDoesNotCloseItsLink()
    {
        // The ")" that closes an angle-delimited link is the NEXT thing after the ">", not the next one
        // anywhere on the line. Searching the whole remainder for it read this line as ONE contained link to
        // "system/ok.md" and consumed the escaping second link along with it - never parsed, never checked,
        // printed verbatim to an agent that resolves Markdown properly and finds the link we missed.
        var toc =
            "# Knowledge Base\n\n- [a](<system/ok.md> [b](../../../etc/passwd)\n- [Beta](system/beta.md)\n";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 10_000);

        block.Text.Should().NotContain("etc/passwd");
        block.Text.Should().Contain("system/beta.md", "a contained entry must survive its neighbour's refusal");
        block
            .Refused.Should()
            .ContainSingle()
            .Which.Should()
            .Contain(
                "../../../etc/passwd",
                "the refusal has to name the part that could not be delimited, not the innocent-looking prefix"
            );
    }

    [Fact]
    public void RenderTableOfContents_RefusesAReferenceStyleLinkAndItsDefinition()
    {
        // A reference-style link carries no "](" at all, so the scanner that keys on it returns ZERO links
        // and the line sails through as clean input - the same "nothing to check reads as nothing wrong"
        // state the undelimited-destination rule closed, reached by a form the scanner cannot see rather
        // than by one it gave up on. Failing closed on the undelimitable does not help here: nothing is
        // undelimitable, there is simply nothing to delimit. The destination lives on the DEFINITION line,
        // so both halves have to be refused - either one alone still hands the agent a working link.
        var toc =
            "# Knowledge Base\n\n- [Alpha](system/alpha.md)\nSee [a][outside] for more.\n\n[outside]: ../../../etc/passwd\n";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 10_000);

        block.Text.Should().NotContain("etc/passwd");
        block.Text.Should().NotContain("[a][outside]", "the half that names the reference is no use on its own, and it is what the agent follows");
        block.Text.Should().Contain("system/alpha.md", "a contained entry must survive its neighbour's refusal");
        block
            .Refused.Should()
            .HaveCount(2, "both the reference and the definition that gives it a destination are refused, and each is reported")
            .And.Contain(refused => refused.Contains("etc/passwd", StringComparison.Ordinal));
    }

    [Fact]
    public void RenderTableOfContents_RefusesADestinationThatOnlyEscapesOnceCharacterReferencesAreDecoded()
    {
        // A third reading of the same destination, alongside raw and unescaped. "&#x2F;etc/passwd" does not
        // start with "/", carries no colon so no scheme, and joins inside the root - accepted under both
        // existing readings. CommonMark decodes character references in destinations, so the agent that
        // resolves this link properly reads "/etc/passwd" and opens it. Same shape as the backslash: the
        // text has more than one reading and we do not control which one the consumer applies.
        var toc = "# Knowledge Base\n\n- [a](&#x2F;etc/passwd)\n- [Beta](system/beta.md)\n";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 10_000);

        block.Text.Should().NotContain("etc/passwd");
        block.Text.Should().Contain("system/beta.md", "a contained entry must survive its neighbour's refusal");
        block.Refused.Should().ContainSingle().Which.Should().Contain("etc/passwd");
    }

    [Fact]
    public void RenderTableOfContents_RefusesADestinationSpelledWithANamedCharacterReference()
    {
        // The decoded reading closed "&#x2F;" and left "&sol;" wide open: WebUtility.HtmlDecode implements a
        // pre-HTML5 entity table, so it resolves the numeric spelling and returns the named one untouched,
        // while a GFM reader resolves both. Every test above then agrees the destination is an ordinary
        // contained relative path. The remedy is not a better table - assembling one is a fresh instance of
        // the bug, since we would be picking an entity set a second time - it is refusing the ampersand.
        var toc = "# Knowledge Base\n\n- [a](&sol;etc/passwd)\n- [Beta](system/beta.md)\n";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 10_000);

        block.Text.Should().NotContain("etc/passwd");
        block.Text.Should().Contain("system/beta.md", "a contained entry must survive its neighbour's refusal");
        block.Refused.Should().ContainSingle().Which.Should().Contain("&sol;etc/passwd",
            "the refusal reports the destination as the file spells it, entity and all");
    }

    [Fact]
    public void Render_ClearsATitleWhoseDestinationIsSpelledWithANamedCharacterReference()
    {
        // The same spelling on the neighbour route, because a rule that reaches one of them is half a rule.
        var entries = new[] { Entry("system/alpha.md", "See [x](&sol;etc/passwd)", ["auth"]) };

        var block = KnowledgeDigest.Render(entries, KbRoot, charBudget: 10_000, omitted: 0);

        block.Text.Should().NotContain("etc/passwd");
        block.Text.Should().Contain("/workspace/store/KnowledgeBase/system/alpha.md", "the entry is kept");
        block.Neutralized.Should().ContainSingle().Which.File.Should().Be("system/alpha.md");
    }

    [Fact]
    public void Render_KeepsATitleWhoseTextMerelyContainsAnAmpersand()
    {
        // The over-refusal pin for the gate above, and the reason it is worth writing: the ampersand rule
        // belongs to DESTINATIONS and paths, not to prose. "Auth & Sessions" is an ordinary lesson title
        // that an extraction agent will write sooner or later, and clearing it would cost real retrieval to
        // buy nothing - there is no destination here for an entity to be a separator in.
        var entries = new[] { Entry("system/alpha.md", "Auth & Sessions", ["auth"]) };

        var block = KnowledgeDigest.Render(entries, KbRoot, charBudget: 10_000, omitted: 0);

        block.Text.Should().Contain("Auth & Sessions", "a title is text, and text may contain an ampersand");
        block.Neutralized.Should().BeEmpty();
    }

    [Fact]
    public void RenderTableOfContents_RefusesAReferenceDefinitionWhoseLabelEscapesItsBracket()
    {
        // The definition test stops at the first "]" it finds, and a label may CONTAIN an escaped one. In
        // "[foo\]]: dest" the first "]" is the escaped one, so the character after it is "]" rather than ":"
        // and the line reads as ordinary prose. The label is used in shortcut form, which carries no "][",
        // so neither half of the gate fires and both lines are printed to an agent that resolves them.
        var toc = "# Knowledge Base\n\n- [Alpha](system/alpha.md)\nSee [foo\\]] for more.\n\n[foo\\]]: ../../../etc/passwd\n";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 10_000);

        block.Text.Should().NotContain("etc/passwd");
        block.Text.Should().Contain("system/alpha.md", "a contained entry must survive its neighbour's refusal");
        block.Refused.Should().Contain(refused => refused.Contains("etc/passwd", StringComparison.Ordinal));
    }

    [Fact]
    public void RenderTableOfContents_RefusesAReferenceDefinitionWhoseLabelSpansTwoLines()
    {
        // CommonMark lets a link label contain a newline, so this is one ordinary definition and one ordinary
        // shortcut reference to it. Every check ran per line and each line looked like prose: "[foo" has no
        // "]" to find, "bar]: ..." does not begin with "[", and neither carries "][". Both halves reached an
        // agent that reads CommonMark properly, which is the exact failure the per-line gate was added for.
        var toc = "# Knowledge Base\n\n- [Alpha](system/alpha.md)\nSee [foo\nbar] for more.\n\n[foo\nbar]: ../../../etc/passwd\n";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 10_000);

        block.Text.Should().NotContain("etc/passwd");
        block.Text.Should().NotContain("[foo", "half a reference is still live once the other half arrives");
        block.Text.Should().Contain("system/alpha.md", "a contained entry must survive its neighbour's refusal");
        block.Refused.Should().Contain(refused => refused.Contains("etc/passwd", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_ClearsATitleWhoseReferenceLabelSpansTwoLines()
    {
        // The neighbour route into the same prompt. A title is not one line by construction - _index.jsonl is
        // JSON and "\n" is an ordinary character inside a string - so the same two-line definition arrives
        // here, and RenderEntry interpolates the value as it stands.
        var entries = new[] { Entry("system/alpha.md", "Runner rules\n\n[foo\nbar]: ../../../etc/passwd", ["runner"]) };

        var digest = KnowledgeDigest.Render(entries, KbRoot, charBudget: 10_000, omitted: 0);

        digest.Text.Should().NotContain("etc/passwd");
        digest.Text.Should().Contain("system/alpha.md", "the entry itself is sound; only its title was cleared");
        digest.Neutralized.Should().ContainSingle().Which.File.Should().Be("system/alpha.md");
    }

    [Theory]
    [InlineData("- [Alpha](system/alpha.md)")]
    [InlineData("- [Array [0] lookup](system/alpha.md)")]
    [InlineData("> - [Alpha](system/alpha.md)")]
    [InlineData("- [Alpha\\[](system/alpha.md)")]
    [InlineData("- [Alpha](<system/a](alpha.md>)")]
    public void RenderTableOfContents_KeepsAnEntryWhoseBracketsBalanceOnItsOwnLine(string entry)
    {
        // The over-refusal pin for the rule above. "A label may continue past this line" is a coarse test,
        // and refusing every line carrying a bracket would have been coarser still and would have emptied the
        // block - every entry in the file is "- [Title](path)". The last case is the one that forced the
        // "](" exemption: a destination may contain a "]" of its own, and reading that as an orphaned closer
        // refused a link the containment check had already cleared.
        var block = KnowledgeDigest.RenderTableOfContents(
            "# Knowledge Base\n\n" + entry + "\n", KbRoot, charBudget: 10_000);

        block.Text.Should().Contain("alpha.md");
        block.Refused.Should().BeEmpty();
    }

    [Theory]
    [InlineData("> [outside]: ../../../etc/passwd")]
    [InlineData("- [outside]: ../../../etc/passwd")]
    [InlineData("  > - [outside]: ../../../etc/passwd")]
    public void RenderTableOfContents_RefusesAReferenceDefinitionInsideABlockContainer(string definition)
    {
        // TrimStart removes indentation and nothing else, so a block-quote marker or a list bullet in front
        // of the definition means the first character is not "[" and the line is never examined. CommonMark
        // recognises a definition inside either container, and the shortcut reference that resolves to it
        // carries no "][" - so the whole reference survives on a line that merely looks like prose to us.
        var toc = "# Knowledge Base\n\n- [Alpha](system/alpha.md)\nSee [outside] for more.\n\n" + definition + "\n";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 10_000);

        block.Text.Should().NotContain("etc/passwd");
        block.Text.Should().Contain("system/alpha.md", "a contained entry must survive its neighbour's refusal");
        block.Refused.Should().Contain(refused => refused.Contains("etc/passwd", StringComparison.Ordinal));
    }

    [Fact]
    public void RenderTableOfContents_KeepsAContainedLinkInsideABlockContainer()
    {
        // The over-refusal pin for the rule above. Refusing every line where a container marker precedes a
        // "[" was the cheaper remedy on offer and it would have cost this line, which is an ordinary entry
        // wearing block-quote clothing. The markers are stripped before the definition test, not treated as
        // evidence: what makes a definition dangerous is the ":" after the label, not the bullet in front.
        var toc = "# Knowledge Base\n\n> - [Alpha](system/alpha.md)\n- [Beta](system/beta.md)\n";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 10_000);

        block.Text.Should().Contain("system/alpha.md", "a contained link is a contained link in any container");
        block.Text.Should().Contain("system/beta.md");
        block.Refused.Should().BeEmpty();
    }

    [Fact]
    public void RenderTableOfContents_RefusesADestinationCarryingANestedLink()
    {
        // An angle-delimited destination may not contain an unescaped "<", so to a CommonMark reader this
        // is not one link at all: the outer link fails to parse and the NESTED "[b](../../../etc/passwd)"
        // renders on its own. Our scan closes the destination at the ">" near the end, unwraps it, and the
        // result reduces to a contained path - the ".." inside it cancel a segment that came from the same
        // destination. Fixed by refusing an extracted destination that still carries an angle bracket,
        // which is a point check over what the scan produced and leaves the extent logic to #258.
        var toc =
            "# Knowledge Base\n\n- [a](<system/ok.md<[b](../../../etc/passwd)>)\n- [Beta](system/beta.md)\n";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 10_000);

        block.Text.Should().NotContain("etc/passwd");
        block.Text.Should().Contain("system/beta.md", "a contained entry must survive its neighbour's refusal");
        block.Refused.Should().Contain(refused => refused.Contains("etc/passwd", StringComparison.Ordinal));
    }

    [Fact]
    public void RenderTableOfContents_KeepsAnAngleDelimitedDestinationThatIsContained()
    {
        // The over-refusal pin for the rule above: the angle brackets that DELIMIT a destination are not
        // the defect, an angle bracket INSIDE one is. Refusing every line carrying a "<" was the coarser
        // remedy available, and it would drop this line - a form our own generator does not emit but one
        // the degraded route is meant to carry when a human wrote it.
        var toc = "# Knowledge Base\n\n- [Alpha](<system/alpha.md>)\n- [Beta](system/beta.md)\n";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 10_000);

        block.Text.Should().Contain("system/alpha.md");
        block.Listed.Should().Be(2);
        block.Refused.Should().BeEmpty();
    }

    [Fact]
    public void RenderTableOfContents_RefusesABareDestinationFollowedByASecondLink()
    {
        // Found by sweeping the inner-"<" axis one form over rather than by report. A bare destination is
        // split at the first space on the premise that what follows is a CommonMark title - but a title is
        // quoted or parenthesised, and "[b](...)" is neither, so the outer link does not parse and the
        // second link renders exactly as in the nested case above. We validated "system/ok.md" and handed
        // the agent a line whose only real link is the one we never looked at.
        var toc =
            "# Knowledge Base\n\n- [a](system/ok.md [b](../../../etc/passwd))\n- [Beta](system/beta.md)\n";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 10_000);

        block.Text.Should().NotContain("etc/passwd");
        block.Text.Should().Contain("system/beta.md", "a contained entry must survive its neighbour's refusal");
        block.Refused.Should().Contain(refused => refused.Contains("etc/passwd", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_ClearsATitleWhoseReferenceLabelEscapesItsBracket()
    {
        // The escaped-label spelling on the metadata route. A title is not one line by construction - it
        // comes out of JSON, where "\n" is an ordinary character - so it can place a definition of its own.
        var entries = new[]
        {
            Entry("system/alpha.md", "See [foo\\]]\n\n[foo\\]]: ../../../etc/passwd", ["auth"]),
        };

        var block = KnowledgeDigest.Render(entries, KbRoot, charBudget: 10_000, omitted: 0);

        block.Text.Should().NotContain("etc/passwd");
        block.Text.Should().Contain("/workspace/store/KnowledgeBase/system/alpha.md", "the entry is kept");
        block.Neutralized.Should().ContainSingle().Which.File.Should().Be("system/alpha.md");
    }

    [Theory]
    [InlineData("- [Alpha](system/alpha.md \"A title\")")]
    [InlineData("- [Alpha](<system/alpha.md> \"A title\")")]
    public void RenderTableOfContents_RefusesAContainedLinkCarryingATitle(string line)
    {
        // A KNOWN LIMITATION pinned deliberately, and the one still open under #258 after this change: a
        // CommonMark title is valid syntax on a contained link, and both spellings of it are refused here -
        // the bare form because a bare destination cannot contain whitespace, the angle form because the ")"
        // no longer follows the ">". Fail-CLOSED, so it costs precision rather than containment: the line is
        // dropped and reported, never rendered. Our own generator emits no titles, so nothing real is lost
        // today; this pin exists so that reading titles properly is a visible change to a stated behaviour
        // rather than a silent one, and so #258 can be checked against the code instead of believed.
        var toc = "# Knowledge Base\n\n" + line + "\n- [Beta](system/beta.md)\n";

        var block = KnowledgeDigest.RenderTableOfContents(toc, KbRoot, charBudget: 10_000);

        block.Text.Should().NotContain("system/alpha.md", "the title makes the link unreadable to us");
        block.Text.Should().Contain("system/beta.md", "a contained entry must survive its neighbour's refusal");
        block.Refused.Should().ContainSingle();
    }
}
