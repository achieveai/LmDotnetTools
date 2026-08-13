namespace AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

/// <summary>
/// Thrown when a plugin-selection-driven session migration could not proceed because an active run on
/// the workspace's sandbox never went idle within the bounded wait window (spec Section 7, step 3).
/// <para>
/// The migration is aborted rather than forced: tearing a sandbox out from under a running turn would
/// fail that turn with an opaque transport error, whereas this exception lets the caller tell the user
/// exactly why nothing changed and that retrying once the run finishes will work. Nothing has been
/// persisted or created when this is raised.
/// </para>
/// </summary>
public sealed class SandboxSessionRestartTimeoutException : Exception
{
    public SandboxSessionRestartTimeoutException(string workspaceId, TimeSpan waited)
        : base(
            $"Workspace '{workspaceId}' still had an active run after waiting {waited} for it to go idle."
        )
    {
        WorkspaceId = workspaceId;
        Waited = waited;
    }

    /// <summary>The workspace whose sandbox session could not be restarted.</summary>
    public string WorkspaceId { get; }

    /// <summary>How long the migration waited for the active run to finish before giving up.</summary>
    public TimeSpan Waited { get; }
}
