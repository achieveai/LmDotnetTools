using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Sandbox;

/// <summary>
/// Pins the two failure signals a plugin-selection migration can raise. Both are caught and mapped
/// to HTTP status codes by the app layer, so the workspace id and the diagnostic payload have to be
/// readable off the exception object — not only interpolated into a message string.
/// </summary>
public class SandboxSessionExceptionsTests
{
    [Fact]
    public void SandboxSessionRestartTimeoutException_ExposesWorkspaceIdAndWaited()
    {
        var exception = new SandboxSessionRestartTimeoutException("ws-1", TimeSpan.FromSeconds(30));

        exception.WorkspaceId.Should().Be("ws-1");
        exception.Waited.Should().Be(TimeSpan.FromSeconds(30));
        exception.Message.Should().Contain("ws-1");
    }

    [Fact]
    public void SandboxSessionReplacementFailedException_ExposesWorkspaceIdAndInnerException()
    {
        // The inner exception is the only record of WHY the candidate create failed; the wrapper
        // must preserve it rather than flattening it to a message, or the gateway's own error is lost.
        var inner = new InvalidOperationException("boom");

        var exception = new SandboxSessionReplacementFailedException("ws-1", inner);

        exception.WorkspaceId.Should().Be("ws-1");
        exception.InnerException.Should().BeSameAs(inner);
    }
}
