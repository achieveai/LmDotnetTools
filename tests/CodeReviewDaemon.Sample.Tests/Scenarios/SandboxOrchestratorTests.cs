using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// Issue #153 M1 — <see cref="SandboxOrchestrator.BuildTransportHeaders"/> is the single place that
/// builds the header set every daemon direct <c>/mcp</c> transport stamps on its gateway connection:
/// the session binding plus the per-app credential the gateway requires on every app-facing call
/// (ADR 0029). Shared by <see cref="SandboxOrchestrator"/> itself and
/// <c>LiveReviewAgentLoopFactory</c>'s own <c>/mcp</c> transport.
/// </summary>
public sealed class SandboxOrchestratorTests
{
    [Fact]
    public void BuildTransportHeaders_includes_session_and_app_headers_when_a_key_is_present()
    {
        var credential = new SandboxCredential("codereview-daemon", "c2VjcmV0LWtleS12YWx1ZS1wYWRkZWQtb3V0LXRvLXRoaXJ0eS10d28=");

        var headers = SandboxOrchestrator.BuildTransportHeaders("session-123", credential);

        headers.Should().Contain("X-Session-ID", "session-123");
        headers.Should().Contain("X-Sbx-App-Id", "codereview-daemon");
        headers.Should().Contain("X-Sbx-App-Key", credential.AppKey);
        headers.Should().HaveCount(3);
    }

    [Fact]
    public void BuildTransportHeaders_omits_the_key_header_entirely_when_the_credential_carries_no_key()
    {
        var credential = new SandboxCredential("codereview-daemon", string.Empty);

        var headers = SandboxOrchestrator.BuildTransportHeaders("session-456", credential);

        headers.Should().Contain("X-Session-ID", "session-456");
        headers.Should().Contain("X-Sbx-App-Id", "codereview-daemon");
        headers.Should().NotContainKey("X-Sbx-App-Key");
        headers.Should().HaveCount(2);
    }
}
