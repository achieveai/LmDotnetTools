using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

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
    public static async Task<bool> ReadAsync(IConversationStore store, string threadId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        if (string.IsNullOrWhiteSpace(threadId))
        {
            return false;
        }

        var metadata = await store.LoadMetadataAsync(threadId, ct).ConfigureAwait(false);
        if (metadata?.Properties is not { } properties || !properties.TryGetValue(PropertyKey, out var raw))
        {
            return false;
        }

        return raw switch
        {
            bool value => value,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            _ => throw new InvalidOperationException(
                $"Conversation '{threadId}' has an unreadable '{PropertyKey}' value "
                    + $"({(raw is JsonElement element ? element.ValueKind.ToString() : raw?.GetType().Name ?? "null")}); "
                    + "refusing to build its agent rather than run it in the shared sandbox."
            ),
        };
    }

    /// <summary>
    /// The gateway working directory for a dedicated sandbox. A workspace that sets its own working directory has
    /// already narrowed where agents may write, so a conversation directory must lie inside it; anything else would
    /// widen the workspace's jail.
    /// </summary>
    /// <exception cref="InvalidOperationException">The conversation directory is outside the workspace's.</exception>
    public static string ResolveWorkingDir(string conversationDirectory, string? workspaceWorkingDir)
    {
        var directory = ConversationWorkingDirectory.Validate(conversationDirectory);
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
