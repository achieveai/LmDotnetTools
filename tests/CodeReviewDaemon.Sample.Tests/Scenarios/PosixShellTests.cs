using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P3.0 — every git argument the daemon runs (branch names, paths, submodule URLs) is
/// attacker-influenced, so <see cref="PosixShell"/> must render the argument vector into a single
/// command line where each token is passed literally and can never break out into shell
/// metacharacters, command substitution, or argument injection. These tests pin the single-quote
/// escaping and the working-directory prefix.
/// </summary>
public sealed class PosixShellTests
{
    [Fact]
    public void Quote_wraps_a_plain_token_in_single_quotes()
    {
        PosixShell.Quote("widgets").Should().Be("'widgets'");
    }

    [Fact]
    public void Quote_renders_an_empty_string_as_the_empty_quoted_token()
    {
        PosixShell.Quote(string.Empty).Should().Be("''");
    }

    [Fact]
    public void Quote_escapes_an_embedded_single_quote_using_the_canonical_technique()
    {
        // O'Brien -> 'O'\''Brien' : close quote, escaped literal quote, reopen quote.
        PosixShell.Quote("O'Brien").Should().Be("'O'\\''Brien'");
    }

    [Theory]
    [InlineData("a; rm -rf /", "'a; rm -rf /'")]
    [InlineData("$(whoami)", "'$(whoami)'")]
    [InlineData("`id`", "'`id`'")]
    [InlineData("a && b", "'a && b'")]
    [InlineData("a | b", "'a | b'")]
    [InlineData("a > out", "'a > out'")]
    [InlineData("a\nb", "'a\nb'")]
    [InlineData("--upload-pack=evil", "'--upload-pack=evil'")]
    public void Quote_neutralizes_shell_metacharacters(string raw, string expected)
    {
        PosixShell.Quote(raw).Should().Be(expected);
    }

    [Fact]
    public void BuildCommandLine_joins_quoted_argv_with_spaces()
    {
        var command = new SandboxCommand(["git", "clone", "https://github.com/acme/widgets.git"]);

        PosixShell
            .BuildCommandLine(command)
            .Should()
            .Be("'git' 'clone' 'https://github.com/acme/widgets.git'");
    }

    [Fact]
    public void BuildCommandLine_quotes_a_branch_name_containing_metacharacters()
    {
        var command = new SandboxCommand(["git", "checkout", "feature/$(rm -rf ~)"]);

        PosixShell
            .BuildCommandLine(command)
            .Should()
            .Be("'git' 'checkout' 'feature/$(rm -rf ~)'");
    }

    [Fact]
    public void BuildCommandLine_prefixes_a_working_directory_with_a_guarded_cd()
    {
        var command = new SandboxCommand(["git", "status"], WorkingDirectory: "/work/repo");

        PosixShell
            .BuildCommandLine(command)
            .Should()
            .Be("cd -- '/work/repo' && 'git' 'status'");
    }

    [Fact]
    public void BuildCommandLine_quotes_a_working_directory_containing_spaces()
    {
        var command = new SandboxCommand(["ls"], WorkingDirectory: "/work/my repo");

        PosixShell.BuildCommandLine(command).Should().Be("cd -- '/work/my repo' && 'ls'");
    }

    [Fact]
    public void BuildCommandLine_throws_when_argv_is_empty()
    {
        var command = new SandboxCommand([]);

        var act = () => PosixShell.BuildCommandLine(command);

        act.Should().Throw<ArgumentException>();
    }
}
