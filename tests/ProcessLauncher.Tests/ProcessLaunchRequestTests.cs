using System.Text;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.ProcessLauncher.Tests;

public class ProcessLaunchRequestTests
{
    [Fact]
    public void Defaults_AreReasonable()
    {
        var request = new ProcessLaunchRequest
        {
            Agent = CliAgentKind.Codex,
            ExecutableHint = "codex",
            Arguments = ["app-server"],
        };

        request.EnvironmentOverrides.Should().BeEmpty();
        request.HostPaths.Should().BeEmpty();
        request.WorkingDirectory.Should().BeNull();
        request.ExecutableOverride.Should().BeNull();
        request.NodeJsPath.Should().BeNull();
        request.StandardOutputEncoding.Should().BeOfType<UTF8Encoding>();
        request.StandardErrorEncoding.Should().BeOfType<UTF8Encoding>();
    }

    [Fact]
    public void RecordEquality_HoldsAcrossCopiesWithSameValues()
    {
        var a = new ProcessLaunchRequest
        {
            Agent = CliAgentKind.Codex,
            ExecutableHint = "codex",
            Arguments = ["x"],
        };

        var b = a with { };

        a.Should().Be(b);
    }

    [Fact]
    public void HostPaths_AreReadOnly()
    {
        var request = new ProcessLaunchRequest
        {
            Agent = CliAgentKind.Claude,
            ExecutableHint = "cli.js",
            Arguments = [],
            HostPaths = [new HostPathReference("/tmp", HostPathKind.WorkingDirectory)],
        };

        request.HostPaths.Should().HaveCount(1);
        request.HostPaths.Should().BeAssignableTo<IReadOnlyList<HostPathReference>>();
    }

    [Fact]
    public void EnvironmentOverrides_AcceptNullValuesToClearVariables()
    {
        var env = new Dictionary<string, string?>
        {
            ["FOO"] = "bar",
            ["BAZ"] = null,
        };

        var request = new ProcessLaunchRequest
        {
            Agent = CliAgentKind.Copilot,
            ExecutableHint = "copilot",
            Arguments = [],
            EnvironmentOverrides = env,
        };

        request.EnvironmentOverrides["BAZ"].Should().BeNull();
        request.EnvironmentOverrides["FOO"].Should().Be("bar");
    }
}
