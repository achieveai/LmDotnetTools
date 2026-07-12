namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// A sandbox the gateway is tracking, as returned by <see cref="SandboxClient.CreateAsync"/>,
/// <see cref="SandboxClient.GetAsync"/>, and <see cref="SandboxClient.ListAsync"/>.
/// </summary>
public sealed class SandboxInfo
{
    /// <summary>Gateway session id; the value sent as the <c>X-Session-ID</c> header on session-scoped calls.</summary>
    public string SessionId { get; }

    /// <summary>Gateway-allocated container id backing the sandbox, when the gateway reports one.</summary>
    public string? ContainerId { get; }

    /// <summary>
    /// The workspace path INSIDE the sandbox container, when the gateway reports one. This is a
    /// remote path meaningful to the gateway/container, never a local host path — the SDK never
    /// resolves or creates host-filesystem paths.
    /// </summary>
    public string? WorkspaceContainerPath { get; }

    public SandboxInfo(string sessionId, string? containerId = null, string? workspaceContainerPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionId = sessionId;
        ContainerId = containerId;
        WorkspaceContainerPath = workspaceContainerPath;
    }
}
