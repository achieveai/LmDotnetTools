using AchieveAi.LmDotnetTools.LmCore.Middleware;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests;

/// <summary>
///     Regression coverage for the sandbox "container unhealthy" guard. When the adopted/Docker-backed
///     sandbox gateway runs tools in a container whose <c>/etc/passwd</c> has no <c>sandbox</c> user,
///     EVERY tool call (Glob/Bash/…) comes back as a Docker exec 400 ("no matching entries in passwd
///     file" / "Timed out waiting for 'sandbox' user"). Left untouched, the model retries the same
///     infrastructure failure 500+ times. The guard converts that error class into ONE actionable
///     message so the model stops and tells the user to restart the gateway/container.
/// </summary>
public sealed class SandboxToolHealthTests
{
    [Theory]
    [InlineData(
        "Error executing MCP tool Glob: Request failed (remote): Internal error: Failed to create "
            + "exec: Docker responded with status code 400: unable to find user sandbox: no matching "
            + "entries in passwd file")]
    [InlineData(
        "Error executing MCP tool Glob: Request failed (remote): Internal error: Timed out waiting "
            + "for 'sandbox' user in container 72f3f918517825fb7b380d0a7db67aed06635d02")]
    [InlineData("Internal error: unable to find user sandbox: no matching entries in passwd file")]
    public void IsContainerUnhealthy_detects_known_gateway_signatures(string toolResult)
    {
        SandboxToolHealth.IsContainerUnhealthy(toolResult).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Found 3 files: a.cs, b.cs, c.cs")]
    [InlineData("Error executing MCP tool Bash: command failed: exit code 1")]
    public void IsContainerUnhealthy_ignores_normal_results_and_unrelated_errors(string? toolResult)
    {
        SandboxToolHealth.IsContainerUnhealthy(toolResult).Should().BeFalse();
    }

    [Fact]
    public async Task Wrap_replaces_unhealthy_result_with_a_single_actionable_error()
    {
        ToolHandler inner = (_, _, _) =>
            Task.FromResult<ToolHandlerResult>(
                ToolHandlerResult.FromText(
                    "Error executing MCP tool Glob: Request failed (remote): Internal error: Failed "
                        + "to create exec: Docker responded with status code 400: unable to find user "
                        + "sandbox: no matching entries in passwd file"));

        var wrapped = SandboxToolHealth.Wrap(inner);
        var result = await wrapped("{}", new ToolCallContext(), CancellationToken.None);

        var resolved = result.Should().BeOfType<ToolHandlerResult.Resolved>().Subject;
        resolved.Payload.IsError.Should().BeTrue();
        resolved.Payload.ErrorCode.Should().Be(SandboxToolHealth.UnhealthyErrorCode);
        resolved.Payload.Text.Should().Be(SandboxToolHealth.UnhealthyMessage);
    }

    [Fact]
    public void UnhealthyMessage_is_actionable()
    {
        // The model must learn NOT to retry, and the user must learn HOW to recover.
        SandboxToolHealth.UnhealthyMessage.Should().ContainEquivalentOf("restart");
        SandboxToolHealth.UnhealthyMessage.Should().ContainEquivalentOf("sandbox");
    }

    [Fact]
    public async Task Wrap_passes_through_healthy_results_unchanged()
    {
        ToolHandler inner = (_, _, _) =>
            Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("Found 3 files"));

        var wrapped = SandboxToolHealth.Wrap(inner);
        var result = await wrapped("{}", new ToolCallContext(), CancellationToken.None);

        result.ResultText.Should().Be("Found 3 files");
        result.Should().BeOfType<ToolHandlerResult.Resolved>().Which.Payload.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task Wrap_passes_through_deferred_results_unchanged()
    {
        ToolHandler inner = (_, _, _) => Task.FromResult<ToolHandlerResult>(new ToolHandlerResult.Deferred());

        var wrapped = SandboxToolHealth.Wrap(inner);
        var result = await wrapped("{}", new ToolCallContext(), CancellationToken.None);

        result.Should().BeOfType<ToolHandlerResult.Deferred>();
    }
}
