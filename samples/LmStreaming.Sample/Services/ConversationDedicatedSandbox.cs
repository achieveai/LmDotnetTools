using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using LmStreaming.Sample.SandboxApps;

namespace LmStreaming.Sample.Services;

/// <summary>
/// The conversation-scoped switch that gives a thread its own sandbox (<see cref="WorkspaceRef.OwnerThreadId"/>),
/// created with the conversation's working directory as the gateway working directory: the agent writes only
/// there, and the rest of the workspace is read-only to it (gateway ADR 0044). Without it every conversation on a
/// workspace shares one sandbox and can write anywhere in the workspace.
/// <para>
/// A Reviewd review must write only in its own PR folder, never in another review's slot, yet all reviews run on
/// one workspace. So the sandbox, unlike the workspace, is per thread. Setup tools keep using the shared one.
/// </para>
/// </summary>
public static class ConversationDedicatedSandbox
{
    /// <summary>
    /// Property key in a thread's <c>ThreadMetadata.Properties</c> holding
    /// <c>ProvisionConversationRequest.DedicatedSandbox</c>. Written only when true, and read back on every agent
    /// (re)creation, so the choice survives a restart and a mode or provider switch.
    /// </summary>
    public const string PropertyKey = "sample.dedicatedSandbox";

    /// <summary>
    /// Whether the conversation was provisioned with a dedicated sandbox. Absence or a missing thread reads as
    /// false. Fails closed like <see cref="ConversationReadOnlyEgress.ReadAsync"/>: any non-boolean value throws,
    /// because reading it as false would put the thread in the shared sandbox, writable everywhere.
    /// </summary>
    /// <exception cref="InvalidOperationException">The stored value is not a boolean.</exception>
    public static Task<bool> ReadAsync(IConversationStore store, string threadId, CancellationToken ct = default) =>
        ThreadPropertyValue.ReadFailClosedBoolAsync(store, threadId, PropertyKey, "run it in the shared sandbox", ct);

    /// <summary>The error code a route answers when a dedicated conversation asks for a mode it cannot have.</summary>
    public const string ModeUnsupportedCode = "dedicated_sandbox_mode_unsupported";

    /// <summary>
    /// Why a dedicated conversation may not run in a mode with these capabilities, or null when it may. Mini App
    /// debugging runs an app through the gateway's operations API, which the working-directory jail does not cover
    /// (gateway ADR 0044), so it would let a jailed agent act outside its directory.
    /// </summary>
    public static string? ModeRefusal(ModeCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return capabilities.MiniAppDebugTools
            ? "A conversation with a dedicated sandbox cannot use a mode with Mini App debugging: it runs apps "
                + "through the gateway's operations API, which is not confined to the conversation's working directory."
            : null;
    }

    /// <summary>
    /// The gateway working directory for a dedicated sandbox. A workspace that sets its own working directory has
    /// already narrowed where agents may write, so a conversation directory must lie inside it; anything else would
    /// widen the workspace's jail. It may not be, hold or lie inside the Mini App directory either: the host runs
    /// the apps there outside the jail, so an agent that could write one could act anywhere in the workspace.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The conversation directory is outside the workspace's, or is, holds or lies inside the Mini App directory.
    /// </exception>
    public static string ResolveWorkingDir(string conversationDirectory, string? workspaceWorkingDir)
    {
        var directory = ConversationWorkingDirectory.Validate(conversationDirectory);
        // The apps sit at the workspace root, so only the root holds them. Case-insensitive: a Windows workspace
        // resolves "Mini-Web-Apps" to the same directory.
        if (
            directory == "."
            || string.Equals(directory.Split('/')[0], SandboxAppDiscovery.AppsPath, StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new InvalidOperationException(
                $"Conversation working directory '{directory}' is, holds or lies inside '{SandboxAppDiscovery.AppsPath}', "
                    + "whose apps the host runs outside the sandbox's jail."
            );
        }

        if (string.IsNullOrWhiteSpace(workspaceWorkingDir))
        {
            return directory;
        }

        var outer = ConversationWorkingDirectory.Validate(workspaceWorkingDir);
        if (outer != "." && directory != outer && !directory.StartsWith(outer + "/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Conversation working directory '{directory}' is outside the workspace working directory '{outer}'."
            );
        }

        return directory;
    }
}
