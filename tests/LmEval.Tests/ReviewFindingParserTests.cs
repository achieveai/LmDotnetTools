using AchieveAi.LmDotnetTools.LmEval.Findings;

namespace AchieveAi.LmDotnetTools.LmEval.Tests;

/// <summary>
/// The parser is deliberately shallow — it recovers what a review CITED, not whether the citation
/// resolves. These tests pin that boundary as much as the parsing itself.
/// </summary>
public class ReviewFindingParserTests
{
    [Fact]
    public void A_bracketed_severity_and_a_citation_are_both_recovered()
    {
        var findings = ReviewFindingParser.Parse(
            "[Blocker] src/Foo/Bar.cs:42 dereferences a null that the guard above cannot rule out."
        );

        var finding = findings.Should().ContainSingle().Subject;
        finding.Path.Should().Be("src/Foo/Bar.cs");
        finding.Line.Should().Be(42);
        finding.Severity.Should().Be("blocker");
    }

    [Fact]
    public void A_review_that_stated_no_severity_gets_a_null_one_rather_than_a_default()
    {
        // Defaulting to "info" would put a severity in the data that no reviewer wrote, and a
        // caller segmenting on severity would count it.
        var finding = ReviewFindingParser
            .Parse("src/Foo/Bar.cs:42 looks wrong to me.")
            .Should()
            .ContainSingle()
            .Subject;

        finding.Severity.Should().BeNull();
    }

    [Fact]
    public void An_ordinary_bracketed_reference_is_not_read_as_a_severity()
    {
        var finding = ReviewFindingParser
            .Parse("[RFC2119] src/Foo/Bar.cs:7 ignores the MUST.")
            .Should()
            .ContainSingle()
            .Subject;

        finding.Severity.Should().BeNull();
    }

    [Fact]
    public void Every_citation_on_a_line_is_recovered()
    {
        var findings = ReviewFindingParser.Parse(
            "[Minor] src/A.cs is fine but src/Foo/A.cs:1 and src/Foo/B.cs:2 disagree."
        );

        findings.Select(f => (f.Path, f.Line)).Should().Equal(("src/Foo/A.cs", 1), ("src/Foo/B.cs", 2));
        findings.Should().OnlyContain(f => f.Severity == "minor");
    }

    [Fact]
    public void Repeated_citations_are_preserved_rather_than_collapsed()
    {
        // A review citing the same line three times is a different review from one citing it once,
        // and this parser feeds a comparison between two reviews' citation surfaces.
        var findings = ReviewFindingParser.Parse(
            "src/Foo/A.cs:1 first\nsrc/Foo/A.cs:1 again\nsrc/Foo/A.cs:1 and again"
        );

        findings.Should().HaveCount(3);
    }

    [Fact]
    public void A_bare_token_with_no_directory_separator_is_not_a_citation()
    {
        // Ordinary prose containing "Program.cs:1" style text would otherwise read as a resolvable
        // path and inflate every citation count.
        ReviewFindingParser.Parse("See Program.cs:1 for context.").Should().BeEmpty();
    }

    [Fact]
    public void Windows_separators_are_recognised()
    {
        var finding = ReviewFindingParser
            .Parse(@"[High] src\Foo\Bar.cs:99 is unreachable.")
            .Should()
            .ContainSingle()
            .Subject;

        finding.Path.Should().Be(@"src\Foo\Bar.cs");
        finding.Line.Should().Be(99);
    }

    [Fact]
    public void A_bold_severity_prefix_is_recognised()
    {
        ReviewFindingParser
            .Parse("**Critical** src/Foo/Bar.cs:12 leaks a handle.")
            .Should()
            .ContainSingle()
            .Which.Severity.Should()
            .Be("critical");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("A review with prose and no citations at all.")]
    public void Text_with_nothing_to_recover_yields_no_findings(string? text)
    {
        ReviewFindingParser.Parse(text).Should().BeEmpty();
    }

    [Fact]
    public void The_severity_on_a_line_applies_to_that_line_only()
    {
        var findings = ReviewFindingParser.Parse(
            "[Blocker] src/Foo/A.cs:1 is broken.\nsrc/Foo/B.cs:2 is merely odd."
        );

        findings.Should().HaveCount(2);
        findings[0].Severity.Should().Be("blocker");
        findings[1].Severity.Should().BeNull();
    }
}
