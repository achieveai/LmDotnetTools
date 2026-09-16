using System.Runtime.ExceptionServices;
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
    /// <para>
    /// The MERGED map is validated before it is returned, and not only each layer as it was stored.
    /// Two of the rules are map-level and so cannot be enforced one layer at a time: the 256-key
    /// ceiling (three layers of 100 keys each are individually fine and together are not) and the
    /// case-insensitive duplicate rule (a workspace's <c>foo</c> and a mode's <c>FOO</c> survive
    /// <see cref="SandboxEnvRules.Merge"/> as two Ordinal entries that the gateway treats as one
    /// name). The gateway rejects either at CREATE, which is the worst place to find out: the
    /// conversation simply cannot start, with the failure surfacing from the agent build rather than
    /// from the edit that caused it. Failing here reports the offending keys instead.
    /// </para>
    /// </summary>
    /// <exception cref="SandboxEnvValidationException">
    /// The merged map breaks a rule. <see cref="SandboxEnvValidationException.Layer"/> is
    /// <c>"effective"</c> — the individual layers may each be valid, so no single one can be blamed.
    /// </exception>
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

        var merged = SandboxEnvRules.Merge(workspace?.Env, mode?.Env, provision);
        SandboxEnvRules.Validate(merged, "effective");
        return merged;
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

    /// <summary>
    /// Reconciles the env of whatever session <paramref name="threadId"/> is bound to, immediately before
    /// that thread takes a turn. Does nothing for a conversation with no sandbox binding.
    /// <para>
    /// This is load-bearing, not belt-and-braces. A gateway session is shared by
    /// <c>(workspaceId, appId)</c>, so two conversations in the same workspace run in the SAME sandbox and
    /// therefore share ONE env map, while each conversation's effective map is its own (the provision
    /// layer is per-conversation, and the mode layer follows whichever mode that conversation is on).
    /// Applying the map only when an agent is CONSTRUCTED means the second conversation to start leaves
    /// its env in place, and the first conversation's next turn — served by a pooled agent that is never
    /// rebuilt — runs against the other conversation's variables. Spec §5 says the shared session follows
    /// the most recently ACTIVATED thread; activation is a turn, not an object lifetime.
    /// </para>
    /// <para>
    /// Failures are logged and swallowed. This runs on the turn path, where the user cannot act on the
    /// error and did not ask for an env change: the edit that introduced a bad map already answered
    /// <c>400 invalid_env</c> to whoever made it (see <see cref="CompleteReapply"/>), and refusing the
    /// turn here would strand the conversation instead.
    /// </para>
    /// </summary>
    public virtual async Task ApplyForActivationAsync(string threadId, CancellationToken ct)
    {
        if (
            !registry.TryGetEstablishedBinding(threadId, out var binding)
            || binding?.SessionId is not { Length: > 0 } sessionId
        )
        {
            // No sandbox binding: a non-sandbox conversation, or one whose binding predates this process.
            return;
        }

        try
        {
            var modeId = await ReadModeIdAsync(threadId, ct).ConfigureAwait(false);
            await ApplyForThreadAsync(threadId, sessionId, binding.WorkspaceRef.Id, modeId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SandboxException or SandboxEnvValidationException)
        {
            logger.LogWarning(
                ex,
                "Could not reconcile sandbox env for thread {ThreadId} before its turn; the session keeps the env it already had",
                threadId
            );
        }
    }

    /// <summary>Reapplies the merged env to every live session bound to <paramref name="workspaceId"/>.</summary>
    /// <remarks>
    /// Every session is attempted; see <see cref="ApplyIsolatedAsync"/> for why one session's rejection
    /// must not abandon the rest.
    /// </remarks>
    public virtual async Task ReapplyForWorkspaceAsync(string workspaceId, CancellationToken ct)
    {
        var report = new ReapplyReport();

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
            await ApplyIsolatedAsync(report, threadId, sessionId, workspaceId, modeId, ct).ConfigureAwait(false);
        }

        CompleteReapply(report, "workspace", workspaceId);
    }

    /// <summary>Reapplies the merged env to every live session whose last-activated thread runs under <paramref name="modeId"/>.</summary>
    /// <remarks>See <see cref="ReapplyForWorkspaceAsync"/> for the per-session isolation contract.</remarks>
    public virtual async Task ReapplyForModeAsync(string modeId, CancellationToken ct)
    {
        var report = new ReapplyReport();

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

            await ApplyIsolatedAsync(report, threadId, sessionId, session.WorkspaceId, modeId, ct)
                .ConfigureAwait(false);
        }

        CompleteReapply(report, "mode", modeId);
    }

    /// <summary>What the two reapply loops observed, so the aggregate can be logged and reported once.</summary>
    private sealed class ReapplyReport
    {
        public int Attempted { get; set; }

        public int Succeeded { get; set; }

        public List<(string SessionId, Exception Error)> Failures { get; } = [];
    }

    /// <summary>
    /// Runs <see cref="ApplyForThreadAsync"/> for one session and records the outcome instead of letting
    /// it end the loop.
    /// <para>
    /// The loop body used to be unguarded, so the FIRST session whose merged map the gateway rejected
    /// (<see cref="SandboxErrorKind.InvalidEnv"/>) or whose effective map failed validation
    /// (<see cref="SandboxEnvValidationException"/>) aborted the whole reapply — every session after it
    /// silently kept the old env. Which sessions those were depended on the iteration order of a
    /// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey, TValue}"/>'s keys, so the same
    /// edit could leave a different subset stale each time. A bad map belongs to ONE thread's layers; it
    /// says nothing about the other sessions sharing the workspace or mode.
    /// </para>
    /// <para>
    /// Cancellation is deliberately NOT caught: <see cref="OperationCanceledException"/> is not a
    /// sandbox failure and must stop the loop.
    /// </para>
    /// </summary>
    private async Task ApplyIsolatedAsync(
        ReapplyReport report,
        string threadId,
        string sessionId,
        string workspaceId,
        string modeId,
        CancellationToken ct
    )
    {
        report.Attempted++;
        try
        {
            await ApplyForThreadAsync(threadId, sessionId, workspaceId, modeId, ct).ConfigureAwait(false);
            report.Succeeded++;
        }
        catch (Exception ex) when (ex is SandboxException or SandboxEnvValidationException)
        {
            // ApplyForThreadAsync has already logged the key names for the InvalidEnv case; this line is
            // about WHICH session was left behind. Still no values.
            logger.LogWarning(
                ex,
                "Sandbox env reapply failed for session {SessionId} (thread {ThreadId}); continuing with the remaining sessions",
                sessionId,
                threadId
            );
            report.Failures.Add((sessionId, ex));
        }
    }

    /// <summary>
    /// Logs the aggregate and then rethrows the first failure, if any, preserving its original type and
    /// stack.
    /// <para>
    /// The rethrow is not vestigial: <c>WorkspacesController</c> and <c>ChatModesController</c> both turn
    /// a <see cref="SandboxErrorKind.InvalidEnv"/> escape into <c>400 { code: "invalid_env" }</c>, which
    /// the client treats as a PARTIAL success — the record was saved, the sandbox was not fully updated.
    /// Swallowing every failure here to "isolate" them would answer 200 and tell the user their env is
    /// live in sandboxes where it is not.
    /// </para>
    /// </summary>
    private void CompleteReapply(ReapplyReport report, string scope, string id)
    {
        logger.LogInformation(
            "Sandbox env reapply for {Scope} {Id}: {Succeeded}/{Attempted} sessions updated, {FailedCount} failed ({FailedSessions})",
            scope,
            id,
            report.Succeeded,
            report.Attempted,
            report.Failures.Count,
            string.Join(", ", report.Failures.Select(f => f.SessionId))
        );

        if (report.Failures.Count > 0)
        {
            ExceptionDispatchInfo.Capture(report.Failures[0].Error).Throw();
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
