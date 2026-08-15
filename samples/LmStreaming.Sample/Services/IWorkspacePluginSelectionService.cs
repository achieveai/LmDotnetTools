using LmStreaming.Sample.Models;

namespace LmStreaming.Sample.Services;

/// <summary>
/// Applies an explicit plugin-selection change to a workspace: validate, persist, and migrate every
/// live sandbox session the workspace currently has.
/// <para>
/// This exists as an interface purely as a test seam. The implementation is sealed with no virtual
/// members, so <c>WorkspacesController</c>'s tests cannot proxy it; they substitute a hand-written
/// stub for this interface instead, which keeps the controller's error-mapping tests free of a real
/// sandbox gateway.
/// </para>
/// </summary>
public interface IWorkspacePluginSelectionService
{
    /// <summary>
    /// Applies <paramref name="dto"/>'s explicit plugin selection to <paramref name="workspaceId"/>.
    /// Only call this when <see cref="WorkspaceUpdate.PluginSelection"/> is set: an update that omits
    /// the selection changes nothing the sandbox cares about and must stay an ordinary store write.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The workspace does not exist.</exception>
    /// <exception cref="UnsupportedWorkspacePluginsException">
    /// The selection names a plugin outside the workspace's effective marketplaces.
    /// </exception>
    /// <exception cref="GatewayPluginFilteringUnsupportedException">
    /// The gateway does not (or is not known to) support plugin filtering.
    /// </exception>
    /// <exception cref="Persistence.WorkspaceRevisionConflictException">
    /// The supplied <c>pluginsRevision</c> is stale or missing.
    /// </exception>
    /// <exception cref="AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox.SandboxSessionRestartTimeoutException">
    /// A live session still had a run in progress when the bounded idle wait expired.
    /// </exception>
    /// <exception cref="AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox.SandboxSessionReplacementFailedException">
    /// A replacement session could not be created. Nothing was persisted and no live session changed.
    /// </exception>
    Task<Workspace> ApplyPluginSelectionUpdateAsync(
        string workspaceId,
        WorkspaceUpdate dto,
        CancellationToken ct = default
    );
}
