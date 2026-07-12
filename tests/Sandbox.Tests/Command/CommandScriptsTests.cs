using AchieveAi.LmDotnetTools.Sandbox.Command;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Command;

public class CommandScriptsTests
{
    private static string[] AllScripts() =>
        [
            CommandScripts.BuildRun("abcdef0123456789abcdef0123456789", "digesthex", "'ls' '-la'", "sub/dir", 120),
            CommandScripts.BuildProbe("abcdef0123456789abcdef0123456789"),
            CommandScripts.BuildRead("abcdef0123456789abcdef0123456789", "stdout", 12288, 12288),
            CommandScripts.BuildClean("abcdef0123456789abcdef0123456789"),
            CommandScripts.BuildGc(256),
        ];

    [Fact]
    public void BuildRun_EmbedsThePosixQuotedArgvVerbatim()
    {
        var quoted = PosixArgv.Join(["git", "commit", "-m", "a'; rm -rf / #"]);

        var script = CommandScripts.BuildRun("op", "digest", quoted, string.Empty, 120);

        script.Should().Contain(quoted);
    }

    [Fact]
    public void BuildRun_QuotesTheWorkingDirectory()
    {
        var script = CommandScripts.BuildRun("op", "digest", "'ls'", "a b/c", 120);

        // The working directory is single-quoted, so a space can never split it into a second token.
        script.Should().Contain("'a b/c'");
    }

    [Fact]
    public void NoScript_ReferencesTheGatewaysUnstableOutputTxtPath()
    {
        foreach (var script in AllScripts())
        {
            script
                .Should()
                .NotContain(
                    "output_",
                    "the SDK must never depend on the gateway's unstable output_*.txt truncation file"
                );
        }
    }

    [Fact]
    public void EveryScript_ReferencesTheReservedArtifactRoot()
    {
        foreach (var script in AllScripts())
        {
            script.Should().Contain(CommandArtifactLayout.ArtifactRootRelative);
        }
    }

    [Fact]
    public void ParseRequest_RoundTripsRun()
    {
        var request = CommandScripts.ParseRequest(CommandScripts.BuildRun("op-dir", "the-digest", "'ls'", "sub", 120));

        request.Kind.Should().Be(CommandScriptKind.Run);
        request.OperationDirectory.Should().Be("op-dir");
        request.Digest.Should().Be("the-digest");
    }

    [Fact]
    public void ParseRequest_RoundTripsProbeCleanAndGc()
    {
        CommandScripts.ParseRequest(CommandScripts.BuildProbe("op-dir")).Kind.Should().Be(CommandScriptKind.Probe);
        CommandScripts.ParseRequest(CommandScripts.BuildClean("op-dir")).Kind.Should().Be(CommandScriptKind.Clean);

        var gc = CommandScripts.ParseRequest(CommandScripts.BuildGc(256));
        gc.Kind.Should().Be(CommandScriptKind.Gc);
        gc.Max.Should().Be(256);
    }

    [Fact]
    public void ParseRequest_RoundTripsReadWithOffsetAndLength()
    {
        var request = CommandScripts.ParseRequest(CommandScripts.BuildRead("op-dir", "stderr", 4096, 12288));

        request.Kind.Should().Be(CommandScriptKind.Read);
        request.OperationDirectory.Should().Be("op-dir");
        request.Stream.Should().Be("stderr");
        request.Offset.Should().Be(4096);
        request.Length.Should().Be(12288);
    }

    [Fact]
    public void ParseRequest_MissingMarker_Throws()
    {
        var act = () => CommandScripts.ParseRequest("echo not a wrapper\n");

        act.Should().Throw<FormatException>();
    }
}
