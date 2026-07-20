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

    /// <summary>
    /// The persisted workspace mount id (<c>session_mounts.id</c>) the gateway's direct file/command
    /// APIs are keyed by, when the gateway reports one. Present on a create/get result from a
    /// #119-inclusive gateway; <c>null</c> on a <see cref="SandboxClient.ListAsync"/> result (the list
    /// response carries no volumes) or against a pre-#119 gateway. Callers rarely need this directly —
    /// the SDK resolves it internally per session — but it is surfaced so a caller already holding a
    /// create result can avoid a redundant lookup.
    /// </summary>
    public long? WorkspaceMountId { get; }

    public SandboxInfo(
        string sessionId,
        string? containerId = null,
        string? workspaceContainerPath = null,
        long? workspaceMountId = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionId = sessionId;
        ContainerId = containerId;
        WorkspaceContainerPath = workspaceContainerPath;
        WorkspaceMountId = workspaceMountId;
    }
}
