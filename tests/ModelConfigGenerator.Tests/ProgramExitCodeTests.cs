using Xunit;

namespace AchieveAi.LmDotnetTools.ModelConfigGenerator.Tests;

/// <summary>
///     Exit-code contract for the tool's informational flags.
/// </summary>
/// <remarks>
///     <para>
///         These assert the RETURN VALUE, which is what a shell sees as <c>$?</c>. The defect they pin was
///         invisible to every other kind of check: <c>--help</c> printed the complete, correct help text and
///         then exited 1, so it looked right to a human and failed for any script, CI step or smoke test that
///         branches on the exit code — where it reads as a broken binary rather than a wrong return statement.
///     </para>
///     <para>
///         Cause was an overloaded sentinel: <c>ParseArguments</c> returned <c>null</c> both for "help was
///         printed" and for "the arguments were bad", and <c>Main</c> mapped that single null to 1. The two
///         cases are now distinguished before parsing, so the negative case below is as load-bearing as the
///         positive ones — exit 1 must still mean bad arguments, or the fix has simply moved the confusion.
///     </para>
///     <para>
///         Deliberately not covered: the no-argument invocation, which performs the real generation run and
///         calls the OpenRouter API. A test that reaches the network is not a test of an exit code.
///     </para>
/// </remarks>
public class ProgramExitCodeTests
{
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("--HELP")] // the parser lowercases before switching; Main must agree, or the two disagree
    public async Task Help_exits_zero_and_prints_the_usage(string flag)
    {
        var (exitCode, output) = await RunAsync(flag);

        Assert.Equal(0, exitCode);
        Assert.Contains("Usage:", output);
        Assert.Contains("--list-families", output);
    }

    [Fact]
    public async Task List_families_exits_zero_and_prints_the_families()
    {
        var (exitCode, output) = await RunAsync("--list-families");

        Assert.Equal(0, exitCode);
        Assert.Contains("Supported model families:", output);
    }

    [Fact]
    public async Task An_unknown_option_still_exits_one()
    {
        var (exitCode, _) = await RunAsync("--no-such-flag");

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Mutually_exclusive_options_still_exit_one()
    {
        var (exitCode, _) = await RunAsync("--reasoning-only", "--multimodal-only");

        Assert.Equal(1, exitCode);
    }

    /// <summary>
    ///     Invokes <see cref="Program.Main" /> with stdout captured, so a test can tell "returned 0" from
    ///     "returned 0 and actually printed something" — a bare exit-code assertion would pass against a
    ///     help handler that printed nothing at all.
    /// </summary>
    private static async Task<(int ExitCode, string Output)> RunAsync(params string[] args)
    {
        var original = Console.Out;
        using var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            var exitCode = await Program.Main(args);
            return (exitCode, captured.ToString());
        }
        finally
        {
            Console.SetOut(original);
        }
    }
}
