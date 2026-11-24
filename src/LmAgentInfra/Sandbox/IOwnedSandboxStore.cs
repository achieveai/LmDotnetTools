namespace AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

/// <summary>
/// One dedicated sandbox a conversation owns, as the host remembers it across restarts: the gateway session id and
/// what the sandbox was created for. Ids only, never a key or a secret.
/// </summary>
/// <param name="SessionId">The gateway session id.</param>
/// <param name="WorkspaceId">The registry's workspace id the sandbox was created on (the cache key's).</param>
/// <param name="GatewayWorkspace">The gateway workspace name it mounts (<see cref="SandboxSession.WorkspaceRelPath"/>).</param>
/// <param name="AppId">The app the sandbox was created under; only that app's credential can reach it.</param>
/// <param name="ReadOnlyEgress">Whether it was created with read-only ADO and GitHub egress.</param>
/// <param name="WorkingDir">The working directory it is jailed to, or <see langword="null"/> for none.</param>
/// <param name="Retired">
/// <see langword="true"/> once the conversation moved on from it (a release or a newer sandbox for the same slot). A
/// retired sandbox is never reused; it sleeps on the gateway until the conversation is deleted.
/// </param>
public sealed record OwnedSandboxRecord(
    string SessionId,
    string WorkspaceId,
    string GatewayWorkspace,
    string AppId,
    bool ReadOnlyEgress,
    string? WorkingDir,
    bool Retired
);

/// <summary>
/// Where the dedicated sandboxes each conversation owns are remembered, so a restarted host can reuse the current
/// one and a conversation delete can reach every one of them. Implemented by the host over its conversation store.
/// </summary>
public interface IOwnedSandboxStore
{
    /// <summary>The sandboxes <paramref name="threadId"/> owns, current and retired; empty when none or no such thread.</summary>
    Task<IReadOnlyList<OwnedSandboxRecord>> LoadAsync(string threadId, CancellationToken ct = default);

    /// <summary>
    /// Replaces the sandboxes <paramref name="threadId"/> owns with what <paramref name="update"/> returns, atomically
    /// with respect to every other write of the conversation. Writes nothing when the conversation no longer exists, so
    /// a late write never brings a deleted conversation back.
    /// </summary>
    Task UpdateAsync(
        string threadId,
        Func<IReadOnlyList<OwnedSandboxRecord>, IReadOnlyList<OwnedSandboxRecord>> update,
        CancellationToken ct = default
    );
}
