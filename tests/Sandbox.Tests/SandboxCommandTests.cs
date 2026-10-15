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
    [InlineData("   ")]
    [InlineData("a\nb")]
    [InlineData("review/op-1")]
    [InlineData("a b")]
    [InlineData("a\\b")]
    [InlineData("café")]
    [InlineData(".")]
    [InlineData("..")]
    public void Constructor_InvalidOperationId_Throws(string operationId)
    {
        var act = () => new SandboxCommand(["ls"], operationId: operationId);

        act.Should().Throw<ArgumentException>().WithParameterName("operationId");
    }

    [Fact]
    public void Constructor_CanonicalizesSurroundingWhitespaceOnOperationId()
    {
        new SandboxCommand(["ls"], operationId: "  op-1.2_3  ").OperationId.Should().Be("op-1.2_3");
    }

    [Fact]
    public void Constructor_TooLongOperationId_Throws()
    {
        var act = () => new SandboxCommand(["ls"], operationId: new string('a', 200));

        act.Should().Throw<ArgumentException>().WithParameterName("operationId");
    }

    [Fact]
    public void ExecutionTimeout_DefaultsToNull()
    {
        new SandboxCommand(["ls"]).ExecutionTimeout.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ExecutionTimeout_NonPositive_Throws(int seconds)
    {
        var act = () => new SandboxCommand(["ls"]) { ExecutionTimeout = TimeSpan.FromSeconds(seconds) };

        act.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("ExecutionTimeout");
    }

    [Fact]
    public void ExecutionTimeout_AtTheMaximum_IsAccepted()
    {
        var command = new SandboxCommand(["ls"]) { ExecutionTimeout = SandboxCommand.MaxExecutionTimeout };

        command.ExecutionTimeout.Should().Be(TimeSpan.FromMilliseconds(int.MaxValue));
    }

    [Fact]
    public void ExecutionTimeout_AboveTheMaximum_Throws()
    {
        // One tick over the bound, and TimeSpan.MaxValue: both would otherwise overflow the SDK's poll
        // deadline (UtcNow + timeout) or a caller's CancelAfter only after the operation was submitted.
        foreach (var timeout in new[] { SandboxCommand.MaxExecutionTimeout + TimeSpan.FromTicks(1), TimeSpan.MaxValue })
        {
            var act = () => new SandboxCommand(["ls"]) { ExecutionTimeout = timeout };

            act.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("ExecutionTimeout");
        }
    }

    [Fact]
    public void Environment_CannotBeMutatedThroughACastToIDictionary()
    {
        var command = new SandboxCommand(["ls"])
        {
            Environment = new Dictionary<string, string> { ["GIT_CONFIG_COUNT"] = "1" },
        };

        var act = () => ((IDictionary<string, string>)command.Environment!)["BAD=X"] = "v";

        act.Should().Throw<NotSupportedException>();
        command.Environment.Should().Equal(new Dictionary<string, string> { ["GIT_CONFIG_COUNT"] = "1" });
    }

    [Fact]
    public void Environment_DefaultsToNull()
    {
        new SandboxCommand(["ls"]).Environment.Should().BeNull();
    }

    [Fact]
    public void Environment_StoresADefensiveCopy()
    {
        var source = new Dictionary<string, string> { ["GIT_CONFIG_COUNT"] = "1" };

        var command = new SandboxCommand(["git", "status"]) { Environment = source };
        source["GIT_CONFIG_COUNT"] = "2";
        source["INJECTED"] = "x";

        command.Environment.Should().Equal(new Dictionary<string, string> { ["GIT_CONFIG_COUNT"] = "1" });
    }

    [Theory]
    [InlineData("", "v")]
    [InlineData("A=B", "v")]
    [InlineData("A\0B", "v")]
    [InlineData("A", "v\0w")]
    public void Environment_InvalidEntry_Throws(string key, string value)
    {
        var act = () => new SandboxCommand(["ls"]) { Environment = new Dictionary<string, string> { [key] = value } };

        act.Should().Throw<ArgumentException>().WithParameterName("Environment");
    }

    [Fact]
    public void Environment_EmptyValue_IsAllowed()
    {
        var command = new SandboxCommand(["ls"]) { Environment = new Dictionary<string, string> { ["EMPTY"] = "" } };

        command.Environment.Should().ContainKey("EMPTY").WhoseValue.Should().BeEmpty();
    }
}
