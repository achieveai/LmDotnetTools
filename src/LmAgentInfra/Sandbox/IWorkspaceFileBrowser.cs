using AchieveAi.LmDotnetTools.Sandbox;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

/// <summary>
/// The narrow surface the file-browser controller depends on: non-creating session resolution plus the
/// credentialed workspace file operations, all funnelled through the registry's client/credential
/// discipline. Implemented by <see cref="SandboxSessionRegistry"/>. Exists so the HTTP layer depends on an
/// abstraction (and can be unit-tested with a fake) rather than on the concrete infrastructure singleton
/// or a raw <c>SandboxClient</c> handle.
/// </summary>
public interface IWorkspaceFileBrowser
{
    /// <summary>
    /// Resolves a conversation thread to a LIVE sandbox workspace session without ever provisioning a
    /// first-time session. See <see cref="SandboxSessionRegistry.ResolveThreadWorkspaceSessionAsync"/>.
    /// </summary>
    Task<SandboxSessionResolution> ResolveThreadWorkspaceSessionAsync(
        string threadId,
        string persistedWorkspaceId,
        SandboxCredential? requestCredential,
        CancellationToken ct = default
    );

    /// <summary>Lists a workspace directory's rich entries (name/type/size/nameLossy). Propagates <see cref="SandboxException"/>.</summary>
    Task<IReadOnlyList<SandboxDirectoryEntry>> ListWorkspaceDirectoryAsync(string sessionId, string relativePath, CancellationToken ct = default);

    /// <summary>Reads a workspace file's raw bytes, capped at <paramref name="maxBytes"/> (null = 64 MiB). Propagates <see cref="SandboxException"/>.</summary>
    Task<byte[]> ReadWorkspaceFileBytesAsync(string sessionId, string relativePath, long? maxBytes, CancellationToken ct = default);

    /// <summary>Writes a workspace file's raw bytes (upload). Propagates <see cref="SandboxException"/>.</summary>
    Task WriteWorkspaceFileBytesAsync(string sessionId, string relativePath, byte[] bytes, CancellationToken ct = default);

    /// <summary>Runs a workspace command (the delete <c>rm</c> seam). A non-zero exit is returned on the result, not thrown.</summary>
    Task<SandboxCommandResult> ExecuteWorkspaceCommandAsync(string sessionId, SandboxCommand command, CancellationToken ct = default);
}
