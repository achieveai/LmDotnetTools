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

    /// <summary>
    /// Resolves a conversation thread to a LIVE sandbox workspace session for IN-PROCESS BACKGROUND
    /// work that has no caller — no inbound request, no principal, no credential of its own. Same
    /// resolution as <see cref="ResolveThreadWorkspaceSessionAsync"/> in every respect except that
    /// the cross-actor provenance comparison is not performed, because there is no actor to compare.
    /// See <see cref="SandboxSessionRegistry.ResolveThreadWorkspaceSessionForBackgroundAsync"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Never call this from a request-handling path.</b> The provenance check this skips is what
    /// stops one app reading another app's session, and a route that reached this method would be
    /// handing any caller the owner's session. The distinction is not "trusted vs untrusted code" —
    /// it is whether a caller exists at all. A controller always has one, even when it is null
    /// (null means "the interactive UI", which is a provenance, not an absence).
    /// </para>
    /// <para>
    /// Its one consumer is the workspace transcript mirror (#251/#253), which writes a thread's own
    /// record into that thread's own workspace on a drain loop. Presenting <c>null</c> there was not
    /// the mirror claiming to be the UI, it was the mirror having nothing to say — and the registry
    /// read that silence as a foreign claim, so an S2S-created conversation reported
    /// <see cref="SandboxSessionResolutionOutcome.CredentialConflict"/> on every flush forever and
    /// got no transcript at all.
    /// </para>
    /// <para>
    /// This grants no new reach. The gateway call underneath uses the binding's own stored
    /// credential either way; what changes is only whether an absent caller is mistaken for a
    /// mismatched one.
    /// </para>
    /// </remarks>
    Task<SandboxSessionResolution> ResolveThreadWorkspaceSessionForBackgroundAsync(
        string threadId,
        string persistedWorkspaceId,
        CancellationToken ct = default
    );

    /// <summary>Lists a workspace directory's rich entries (name/type/size/nameLossy). Propagates <see cref="SandboxException"/>.</summary>
    Task<IReadOnlyList<SandboxDirectoryEntry>> ListWorkspaceDirectoryAsync(
        string sessionId,
        string relativePath,
        CancellationToken ct = default
    );

    /// <summary>Reads a workspace file's raw bytes, capped at <paramref name="maxBytes"/> (null = 64 MiB). Propagates <see cref="SandboxException"/>.</summary>
    Task<byte[]> ReadWorkspaceFileBytesAsync(
        string sessionId,
        string relativePath,
        long? maxBytes,
        CancellationToken ct = default
    );

    /// <summary>Writes a workspace file's raw bytes (upload). Propagates <see cref="SandboxException"/>.</summary>
    Task WriteWorkspaceFileBytesAsync(
        string sessionId,
        string relativePath,
        byte[] bytes,
        CancellationToken ct = default
    );

    /// <summary>Runs a workspace command (the delete <c>rm</c> seam). A non-zero exit is returned on the result, not thrown.</summary>
    Task<SandboxCommandResult> ExecuteWorkspaceCommandAsync(
        string sessionId,
        SandboxCommand command,
        CancellationToken ct = default
    );
}
