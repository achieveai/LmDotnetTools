using CodeReviewDaemon.Sample.Agents;

namespace CodeReviewDaemon.Sample.Tests.Agents;

/// <summary>
/// Pins the layered ToC renderer (plan §13 / Task 2). Entries carry a scope-qualified
/// <c>RelPath</c> (e.g. <c>system/x.md</c>, <c>LmDotnetTools/y.md</c>); <see cref="KnowledgeTableOfContents.Render"/>
/// groups them under a <c>## &lt;scope&gt;</c> heading (scope = first path segment) with scopes and
/// entries sorted ordinal. Unscoped entries (no <c>/</c>) render flat directly under the header so the
/// legacy single-directory layout is preserved. Output is byte-stable so regen produces no spurious diffs.
/// </summary>
public class KnowledgeTableOfContentsTests
{
    [Fact]
    public void Render_groups_scoped_entries_under_a_scope_heading_sorted_ordinal()
    {
        var toc = KnowledgeTableOfContents.Render(
        [
            new KnowledgeEntry("system/a.md", "A"),
            new KnowledgeEntry("LmDotnetTools/b.md", "B"),
        ]);

        // Scopes sorted ordinal: 'L' (0x4C) before 's' (0x73), so LmDotnetTools precedes system.
        toc.Should().Be(
            "# Knowledge Base\n\n"
            + "## LmDotnetTools\n\n- [B](LmDotnetTools/b.md)\n"
            + "\n"
            + "## system\n\n- [A](system/a.md)\n");
    }

    [Fact]
    public void Render_sorts_entries_within_a_scope_ordinal()
    {
        var toc = KnowledgeTableOfContents.Render(
        [
            new KnowledgeEntry("system/apple.md", "apple"),
            new KnowledgeEntry("system/Zebra.md", "Zebra"),
        ]);

        // Ordinal (case-sensitive) sort by RelPath: 'Z' (0x5A) before 'a' (0x61).
        toc.Should().Be(
            "# Knowledge Base\n\n"
            + "## system\n\n- [Zebra](system/Zebra.md)\n- [apple](system/apple.md)\n");
    }

    [Fact]
    public void Render_lists_unscoped_entries_flat_under_the_header()
    {
        var toc = KnowledgeTableOfContents.Render(
        [
            new KnowledgeEntry("aaa-first.md", "Aaa First"),
            new KnowledgeEntry("null-checks.md", "Null Checks"),
        ]);

        toc.Should().Be(
            "# Knowledge Base\n\n- [Aaa First](aaa-first.md)\n- [Null Checks](null-checks.md)\n");
    }

    [Fact]
    public void Render_emits_the_generated_placeholder_for_an_empty_knowledge_base()
    {
        var toc = KnowledgeTableOfContents.Render([]);

        toc.Should().Be("# Knowledge Base\n\n_Table of contents (generated)._\n");
    }
}
