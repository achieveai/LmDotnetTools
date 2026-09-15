using AchieveAi.LmDotnetTools.LmAgentInfra.Agents;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.Sandbox;
using LmStreaming.Sample.Persistence;

namespace LmStreaming.Sample.Services;

/// <summary>
/// Reapplies the merged sandbox environment (workspace &lt; mode &lt; provision, spec §5) to every
/// live sandbox session affected by a workspace or chat-mode env edit, and computes the merged map a
/// freshly-built agent's session should carry.
/// <para>
/// The shared session follows the MOST RECENTLY ACTIVATED thread (spec §5 — last wins, accepted):
/// <see cref="ReapplyForWorkspaceAsync"/> and <see cref="ReapplyForModeAsync"/> both resolve, per
/// affected session, only the thread <see cref="SandboxSessionRegistry.TryGetLastActivatedThread"/>
/// reports, never every thread that has ever touched the session.
/// </para>
/// <para>
/// Registered as a singleton (see <c>Program.cs</c>) so <see cref="Controllers.WorkspacesController"/>
/// and <see cref="Controllers.ChatModesController"/> can trigger a reapply from an update, and so the
/// agent-factory hook in <c>Program.cs</c> can compute/apply a thread's effective env at build time.
/// Members are <see langword="virtual"/> so tests can subclass/override without needing the real
/// dependencies wired.
/// </para>
/// <para>
/// SECURITY / PRIVACY: never logs an environment-variable VALUE — only key counts and, on an
/// <see cref="SandboxErrorKind.InvalidEnv"/> rejection, the offending key NAMES (never values).
/// </para>
/// </summary>
public class SandboxEnvApplier(
    IWorkspaceStore workspaces,
    IChatModeStore modes,
    IConversationStore conversations,
    SandboxSessionRegistry registry,
    ILogger<SandboxEnvApplier> logger
)
{
    /// <summary>
    /// Computes the effective env for a thread: workspace layer &lt; mode layer &lt; the thread's own
    /// provisioned (conversation) layer, later layers winning key-for-key (see
    /// <see cref="SandboxEnvRules.Merge"/>).
    /// </summary>
    public virtual async Task<IReadOnlyDictionary<string, string>> ComputeEffectiveAsync(
        string threadId,
        string workspaceId,
        string modeId,
        CancellationToken ct
    )
    {
        var workspace = await workspaces.GetAsync(workspaceId, ct).ConfigureAwait(false);
        var mode = await modes.GetModeAsync(modeId, ct).ConfigureAwait(false);
        var provision = await ConversationSandboxEnv.ReadAsync(conversations, threadId, ct).ConfigureAwait(false);

        return SandboxEnvRules.Merge(workspace?.Env, mode?.Env, provision);
    }

    /// <summary>
    /// Computes the effective env for <paramref name="threadId"/> and ensures the live session named
    /// <paramref name="sessionId"/> carries it (diff-and-PATCH; see
    /// <see cref="SandboxSessionRegistry.EnsureSessionEnvAsync"/>).
    /// <para>
    /// A gateway <see cref="SandboxErrorKind.InvalidEnv"/> rejection is logged (key names only) and
    /// rethrown — the caller's desired map is malformed and retrying here could never fix that.
    /// A <see cref="SandboxErrorKind.TransportTimeout"/> or <see cref="SandboxErrorKind.Unavailable"/>
    /// failure is logged and swallowed: the next activation recomputes the diff and tries again. Every
    /// other failure propagates.
    /// </para>
    /// </summary>
    public virtual async Task ApplyForThreadAsync(
        string threadId,
        string sessionId,
        string workspaceId,
        string modeId,
        CancellationToken ct
    )
    {
        var effective = await ComputeEffectiveAsync(threadId, workspaceId, modeId, ct).ConfigureAwait(false);

        try
        {
            var result = await registry.EnsureSessionEnvAsync(sessionId, effective, threadId, ct).ConfigureAwait(false);
            logger.LogDebug(
                "Sandbox env ensured for session {SessionId} (thread {ThreadId}): {Result}, {KeyCount} keys",
                sessionId,
                threadId,
                result,
                effective.Count
            );
        }
        catch (SandboxException ex) when (ex.Kind == SandboxErrorKind.InvalidEnv)
        {
            logger.LogWarning(
                "Sandbox gateway rejected env for session {SessionId} (thread {ThreadId}); invalid keys: {InvalidKeys}",
                sessionId,
                threadId,
                string.Join(", ", ex.InvalidKeys ?? [])
            );
            throw;
        }
        catch (SandboxException ex) when (ex.Kind is SandboxErrorKind.TransportTimeout or SandboxErrorKind.Unavailable)
        {
            logger.LogWarning(
                ex,
                "Sandbox env apply for session {SessionId} (thread {ThreadId}) failed transiently; will retry on next activation",
                sessionId,
                threadId
            );
        }
    }

    /// <summary>Reapplies the merged env to every live session bound to <paramref name="workspaceId"/>.</summary>
    public virtual async Task ReapplyForWorkspaceAsync(string workspaceId, CancellationToken ct)
    {
        foreach (var sessionId in registry.GetActiveSessionIds())
        {
            if (!registry.TryGetSessionById(sessionId, out var session) || session is null)
            {
                continue;
            }

            if (!string.Equals(session.WorkspaceId, workspaceId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!registry.TryGetLastActivatedThread(sessionId, out var threadId) || threadId is null)
            {
                // Seeded (at create) but never activated by a thread — nothing to reapply against yet.
                continue;
            }

            var modeId = await ReadModeIdAsync(threadId, ct).ConfigureAwait(false);
            await ApplyForThreadAsync(threadId, sessionId, workspaceId, modeId, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Reapplies the merged env to every live session whose last-activated thread runs under <paramref name="modeId"/>.</summary>
    public virtual async Task ReapplyForModeAsync(string modeId, CancellationToken ct)
    {
        foreach (var sessionId in registry.GetActiveSessionIds())
        {
            if (!registry.TryGetSessionById(sessionId, out var session) || session is null)
            {
                continue;
            }

            if (!registry.TryGetLastActivatedThread(sessionId, out var threadId) || threadId is null)
            {
                continue;
            }

            var threadModeId = await ReadModeIdAsync(threadId, ct).ConfigureAwait(false);
            if (!string.Equals(threadModeId, modeId, StringComparison.Ordinal))
            {
                continue;
            }

            await ApplyForThreadAsync(threadId, sessionId, session.WorkspaceId, modeId, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads a thread's bound mode id from its metadata property bag (same idiom as
    /// <c>ConversationsController.SendMessage</c>), falling back to the system default mode when the
    /// thread has no metadata, no property, or the thread was never provisioned with one.
    /// </summary>
    private async Task<string> ReadModeIdAsync(string threadId, CancellationToken ct)
    {
        var metadata = await conversations.LoadMetadataAsync(threadId, ct).ConfigureAwait(false);
        var persistedModeId =
            metadata?.Properties?.TryGetValue(MultiTurnAgentPool.ModePropertyKey, out var modeObj) == true
                ? modeObj?.ToString()
                : null;

        return persistedModeId ?? SystemChatModes.DefaultModeId;
    }
}
