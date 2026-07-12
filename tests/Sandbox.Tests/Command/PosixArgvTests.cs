using AchieveAi.LmDotnetTools.Sandbox.Command;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Command;

public class PosixArgvTests
{
    /// <summary>
    /// Reverses <see cref="PosixArgv.Quote"/> exactly: strip the wrapping single quotes, then turn the
    /// escape sequence <c>'\''</c> back into a literal quote. This is the deterministic inverse of the
    /// single-quoting scheme, so a successful round-trip proves the quoting preserved the token verbatim
    /// (what a POSIX shell would recover) without needing to spawn a shell.
    /// </summary>
    private static string PosixUnquote(string quoted)
    {
        quoted.Should().StartWith("'").And.EndWith("'");
        var body = quoted[1..^1];
        return body.Replace("'\\''", "'");
    }

    [Theory]
    [InlineData("simple")]
    [InlineData("")]
    [InlineData("with space")]
    [InlineData("it's")]
    [InlineData("'")]
    [InlineData("''")]
    [InlineData("$(rm -rf /)")]
    [InlineData("`whoami`")]
    [InlineData("a; rm -rf / #")]
    [InlineData("a && b || c")]
    [InlineData("pipe | grep x")]
    [InlineData("redirect > /etc/passwd")]
    [InlineData("$HOME/${VAR}")]
    [InlineData("glob*?[a-z]")]
    [InlineData("tab\tnewline\nreturn\r")]
    [InlineData("quote'injection'; echo pwned")]
    [InlineData("\"double\"")]
    [InlineData("back\\slash")]
    public void Quote_RoundTripsExactly_ForHostileTokens(string token)
    {
        var quoted = PosixArgv.Quote(token);

        PosixUnquote(quoted).Should().Be(token);
    }

    [Fact]
    public void Quote_EmptyString_ProducesExplicitEmptyToken()
    {
        PosixArgv.Quote("").Should().Be("''");
    }

    [Fact]
    public void Quote_SingleQuote_UsesCanonicalEscape()
    {
        PosixArgv.Quote("'").Should().Be(@"''\'''");
    }

    [Fact]
    public void Quote_NulByte_Throws()
    {
        var act = () => PosixArgv.Quote("a\0b");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Join_QuotesEachTokenAndSeparatesWithSpaces()
    {
        var result = PosixArgv.Join(["git", "commit", "-m", "hello world"]);

        result.Should().Be("'git' 'commit' '-m' 'hello world'");
    }

    [Fact]
    public void Join_HostileTokens_NeverEscapeTheirOwnQuoting()
    {
        var result = PosixArgv.Join(["echo", "a'; rm -rf / #"]);

        // Exactly two space-separated tokens survive; the injection stays inside the second token.
        result.Should().Be(@"'echo' 'a'\''; rm -rf / #'");
    }

    [Fact]
    public void Join_EmptyArgv_Throws()
    {
        var act = () => PosixArgv.Join([]);

        act.Should().Throw<ArgumentException>();
    }
}
