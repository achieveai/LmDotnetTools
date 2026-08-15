namespace AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

/// <summary>
/// Thrown when a candidate sandbox session could not be created (or committed) during a plugin-selection
/// migration, after any partially-created candidates were aborted (spec Section 7, step 5).
/// <para>
/// This is a wrapper, never a replacement: the gateway's own failure is the only thing that explains
/// what went wrong, so it is always carried as <see cref="Exception.InnerException"/>. Raising this
/// means the migration rolled back — the live sessions and the persisted selection are unchanged.
/// </para>
/// </summary>
public sealed class SandboxSessionReplacementFailedException : Exception
{
    public SandboxSessionReplacementFailedException(string workspaceId, Exception innerException)
        : base($"Failed to replace sandbox session(s) for workspace '{workspaceId}'.", innerException)
    {
        WorkspaceId = workspaceId;
    }

    /// <summary>The workspace whose sandbox session(s) could not be replaced.</summary>
    public string WorkspaceId { get; }
}
