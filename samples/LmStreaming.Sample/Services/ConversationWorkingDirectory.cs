using AchieveAi.LmDotnetTools.LmAgentInfra;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.Sandbox.Command;

namespace LmStreaming.Sample.Services;

/// <summary>
/// The conversation-scoped working directory: a workspace-relative directory <i>inside</i> the thread's
/// mounted workspace used as the immutable Gateway-native tool home, never a local provider cwd.
/// <para>
/// This exists because several conversations can legitimately share one mounted workspace while each
/// needing to operate in its own subtree — the code-review daemon runs every review against one
/// <c>nova-reviews</c> workspace and defaults each review to its own <c>.worktrees/&lt;Repo&gt;-&lt;N&gt;</c>
/// linked worktree. It is trusted orchestration metadata, never model output. Different homes use
/// separate sessions over the same mount/catalog identity. This is not confinement; the file browser
/// and direct SDK paths remain workspace-relative.
/// </para>
/// <para>
/// Key and reader live together, following <see cref="ConversationSubAgentModel"/> — see that type for
/// why the two halves are deliberately not split across files.
/// </para>
/// </summary>
public static class ConversationWorkingDirectory
{
    /// <summary>
    /// Property key in a thread's <c>ThreadMetadata.Properties</c> holding the <b>normalized</b>
    /// <c>ProvisionConversationRequest.WorkingDirectoryRelPath</c>. Written once at provision and read
    /// back on every agent (re)creation, so the tool default survives a restart and a mode/provider switch
    /// exactly like the thread's workspace binding does.
    /// </summary>
    public const string PropertyKey = "sample.workingDirectoryRelPath";

    /// <summary>
    /// Normalizes a caller-supplied value to the form that is persisted, or returns <c>null</c> for
    /// "no override" (null, empty, or a path that normalizes to the workspace root — all of which mean
    /// the pre-existing behaviour of running at the mount root).
    /// <para>
    /// The lexical rules are <see cref="WorkspaceRelativePath"/>'s, reused rather than restated: the same
    /// rule already governs <c>SandboxCommand.WorkingDirectory</c>, and a second copy here would be free
    /// to drift away from the one the gateway enforces. Throws <see cref="ArgumentException"/> for a
    /// rooted path, a Windows drive prefix, any backslash, a NUL byte, or a <c>..</c> segment.
    /// </para>
    /// </summary>
    public static string? Normalize(string? workingDirectoryRelPath, string paramName = "workingDirectoryRelPath")
    {
        var normalized = WorkspaceRelativePath.Normalize(workingDirectoryRelPath, paramName);
        return normalized.Length == 0 ? null : normalized;
    }

    internal static void EnsureProviderSupportsHome(string? home, string normalizedProviderId, bool needsSandbox)
    {
        if (
            home is not null
            && (
                !needsSandbox
                || normalizedProviderId
                    is "copilot"
                        or "codex"
                        or "claude"
                        or "copilot-mock"
                        or "codex-mock"
                        or "claude-mock"
            )
        )
            throw new ProviderUnavailableException(
                normalizedProviderId,
                "A provisioned review home requires Gateway-native API-provider tools; local CLI tools cannot honor a remote workspace home."
            );
    }

    /// <summary>
    /// Reads the conversation's normalized working directory, or <c>null</c> when the thread was
    /// provisioned without one (every conversation created before this field existed, and every
    /// UI-created chat).
    /// <para>
    /// <b>Absence</b> never throws — a missing thread, a missing property or a non-string value all mean
    /// "no home override", which is the pre-existing behaviour of running at the mount root. <b>Failure</b>
    /// does: a null <paramref name="store"/> is a wiring bug, and whatever the store's own
    /// <c>LoadMetadataAsync</c> raises propagates unchanged. This matches
    /// <see cref="ConversationSubAgentModel.ReadAsync"/>, the sibling reader on the same path.
    /// </para>
    /// <para>
    /// Note what absence does <i>not</i> mean: it is never a reason to invent a directory. A value that
    /// was written is honored or the build fails; a value that was never written leaves the agent where
    /// it already ran. Neither path guesses.
    /// </para>
    /// </summary>
    public static async Task<string?> ReadAsync(
        IConversationStore store,
        string threadId,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(store);

        if (string.IsNullOrWhiteSpace(threadId))
        {
            return null;
        }

        var metadata = await store.LoadMetadataAsync(threadId, ct).ConfigureAwait(false);
        if (metadata?.Properties is not { } properties || !properties.TryGetValue(PropertyKey, out var raw))
        {
            return null;
        }

        // The production store round-trips the property bag through JSON, so a string written at
        // provision reads back as a JsonElement; AsString is what handles both shapes.
        var value = ThreadPropertyValue.AsString(raw);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
