using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

namespace CodeReviewDaemon.Sample.Workspace.Sandbox;

/// <summary>
/// Builds the transport header set the daemon's RETAINED direct <c>ModelContextProtocol</c> client
/// stamps on its gateway <c>/mcp</c> connection for DYNAMIC TOOL DISCOVERY (see
/// <c>LiveReviewAgentLoopFactory</c>). All command/file work now goes through the typed
/// <see cref="AchieveAi.LmDotnetTools.Sandbox.SandboxClient"/> (which stamps these same headers itself
/// via <see cref="SandboxSessionAdapter"/>); this helper survives only for the one MCP transport the
/// daemon still opens by hand to enumerate the gateway's tool catalogue.
/// </summary>
internal static class DaemonMcpTransportHeaders
{
    /// <summary>
    /// The session binding (<c>X-Session-ID</c>) plus the per-app credential the gateway requires on
    /// every app-facing call (ADR 0029). <c>X-Sbx-App-Key</c> is omitted entirely when the credential
    /// carries no key — the keyless <c>AUTH_ENFORCE=off</c> dev path — rather than sent as an empty
    /// header value. This is the EXACT header set the old <c>SandboxOrchestrator.BuildTransportHeaders</c>
    /// produced and the typed <c>SandboxClient</c> stamps, so every daemon transport is identical.
    /// </summary>
    internal static Dictionary<string, string> BuildTransportHeaders(string sessionId, SandboxCredential credential)
    {
        var headers = new Dictionary<string, string>
        {
            ["X-Session-ID"] = sessionId,
        };

        credential.StampHeaders(headers);

        return headers;
    }
}
