namespace LmStreaming.Sample.Services;

/// <summary>
/// Reapplies the merged sandbox environment (workspace &lt; mode &lt; provision, spec §5) to every
/// live sandbox session affected by a workspace or chat-mode env edit.
/// <para>
/// STUB for this task: every method is a no-op. Task 4 fills in the real reapply logic (resolving
/// affected sessions and patching them via the sandbox gateway). Kept <see langword="virtual"/> so
/// tests can subclass/override without needing the real implementation, and registered as a
/// singleton so <see cref="Controllers.WorkspacesController"/> and
/// <see cref="Controllers.ChatModesController"/> can depend on it today.
/// </para>
/// </summary>
public class SandboxEnvApplier
{
    /// <summary>Reapplies the merged env to every live session bound to <paramref name="workspaceId"/>.</summary>
    public virtual Task ReapplyForWorkspaceAsync(string workspaceId, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Reapplies the merged env to every live session running under <paramref name="modeId"/>.</summary>
    public virtual Task ReapplyForModeAsync(string modeId, CancellationToken ct) => Task.CompletedTask;
}
