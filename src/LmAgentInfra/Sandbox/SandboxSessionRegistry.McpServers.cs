using AchieveAi.LmDotnetTools.Sandbox;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

public sealed partial class SandboxSessionRegistry
{
    /// <summary>
    /// The MCP servers the plugins <paramref name="sessionId"/> admitted declare, as authored (gateway issue #224),
    /// through the session's own credential. Propagates <see cref="SandboxException"/>. SECRET: the rows can carry
    /// credentials; never log them.
    /// </summary>
    public Task<IReadOnlyList<SandboxMcpServer>> GetMcpServersAsync(string sessionId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return ClientFor(CredentialFor(sessionId)).GetMcpServersAsync(sessionId, ct);
    }
}
