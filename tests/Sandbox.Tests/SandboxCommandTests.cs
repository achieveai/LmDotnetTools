using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

public class SandboxCommandTests
{
    [Fact]
    public void Constructor_ValidCommand_StoresArgumentsAndNormalizedWorkingDirectory()
    {
        var command = new SandboxCommand(["git", "status"], "./sub/dir/", "op-1");

        command.Arguments.Should().Equal("git", "status");
        command.WorkingDirectory.Should().Be("./sub/dir/");
        command.NormalizedWorkingDirectory.Should().Be("sub/dir");
        command.OperationId.Should().Be("op-1");
    }

    [Fact]
    public void Constructor_DefaultsWorkingDirectoryAndOperationIdToNull()
    {
        var command = new SandboxCommand(["ls"]);

        command.WorkingDirectory.Should().BeNull();
        command.NormalizedWorkingDirectory.Should().BeEmpty();
        command.OperationId.Should().BeNull();
    }

    [Fact]
    public void Constructor_ArgumentsAreDefensivelyCopied()
    {
        var source = new List<string> { "ls", "-la" };
        var command = new SandboxCommand(source);

        source[0] = "rm";

        command.Arguments.Should().Equal("ls", "-la");
    }

    [Fact]
    public void Constructor_NullArguments_Throws()
    {
        var act = () => new SandboxCommand(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_EmptyArguments_Throws()
    {
        var act = () => new SandboxCommand([]);

        act.Should().Throw<ArgumentException>().WithParameterName("arguments");
    }

    [Fact]
    public void Constructor_NulByteInArgument_Throws()
    {
        var act = () => new SandboxCommand(["echo", "a\0b"]);

        act.Should().Throw<ArgumentException>().WithParameterName("arguments");
    }

    [Fact]
    public void Constructor_EmptyStringArgument_IsAllowed()
    {
        var command = new SandboxCommand(["echo", ""]);

        command.Arguments.Should().Equal("echo", "");
    }

    [Theory]
    [InlineData("/etc")]
    [InlineData("../escape")]
    [InlineData(@"C:\Windows")]
    [InlineData("a\0b")]
    public void Constructor_InvalidWorkingDirectory_Throws(string workingDirectory)
    {
        var act = () => new SandboxCommand(["ls"], workingDirectory);

        act.Should().Throw<ArgumentException>().WithParameterName("workingDirectory");
    }

    [Theory]
    [InlineData("")]
    [InlineData("a\nb")]
    public void Constructor_InvalidOperationId_Throws(string operationId)
    {
        var act = () => new SandboxCommand(["ls"], operationId: operationId);

        act.Should().Throw<ArgumentException>().WithParameterName("operationId");
    }

    [Fact]
    public void Constructor_TooLongOperationId_Throws()
    {
        var act = () => new SandboxCommand(["ls"], operationId: new string('a', 200));

        act.Should().Throw<ArgumentException>().WithParameterName("operationId");
    }
}
