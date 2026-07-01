using CodeReviewDaemon.Sample.Workspace.Git;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P3.3 — <see cref="GitModulesParser"/> turns a raw <c>.gitmodules</c> into structured entries
/// (plan §3.1, parse before any init). Entries missing a path or url are dropped (they cannot be
/// fetched), and parsing never trusts or executes anything — validation happens later.
/// </summary>
public sealed class GitModulesParserTests
{
    [Fact]
    public void Parse_reads_path_and_url_for_each_submodule_section()
    {
        const string content = """
            [submodule "vendor/shared-lib"]
            	path = vendor/shared-lib
            	url = https://github.com/acme/shared-lib.git
            [submodule "vendor/utils"]
            	path = vendor/utils
            	url = ../utils.git
            	branch = main
            """;

        var entries = GitModulesParser.Parse(content);

        entries.Should().HaveCount(2);
        entries[0].Should().Be(new SubmoduleEntry("vendor/shared-lib", "vendor/shared-lib", "https://github.com/acme/shared-lib.git"));
        entries[1].Should().Be(new SubmoduleEntry("vendor/utils", "vendor/utils", "../utils.git"));
    }

    [Fact]
    public void Parse_drops_an_entry_missing_a_url()
    {
        const string content = """
            [submodule "broken"]
            	path = vendor/broken
            """;

        GitModulesParser.Parse(content).Should().BeEmpty();
    }

    [Fact]
    public void Parse_ignores_comments_and_blank_lines()
    {
        const string content = """
            # a comment
            ; another comment

            [submodule "lib"]
            	path = lib
            	url = https://github.com/acme/lib.git
            """;

        GitModulesParser.Parse(content).Should().ContainSingle();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_returns_empty_for_missing_content(string? content)
    {
        GitModulesParser.Parse(content).Should().BeEmpty();
    }
}
