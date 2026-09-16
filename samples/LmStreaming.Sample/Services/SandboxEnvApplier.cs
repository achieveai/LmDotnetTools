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
    /// Failures are logged and swallowed, WHATEVER their type; only cancellation propagates. This runs on
    /// the turn path, where the user did not ask for an env change and cannot act on the error, and
    /// failing here fails their message: REST answers 500 and the WebSocket seam aborts the socket
    /// without a frame. The failure domain is wider than the sandbox, too: the reconcile reads the
    /// conversation metadata, the workspace catalog and the mode store, and a corrupt catalog raises
    /// <c>WorkspaceCatalogCorruptException</c>, which is not a <see cref="SandboxException"/>. Nor can a
    /// bad map be assumed to have been reported already: the 256-key ceiling and the case-insensitive
    /// duplicate rule apply to the MERGED map, which can first break here with no edit ever rejected.
    /// The session keeps the env it already had, and the next turn tries again.
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
        catch (Exception ex) when (ex is not OperationCanceledException)
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
    /// Never throws for a session's failure; see <see cref="ApplyIsolatedAsync"/>. Only cancellation
    /// propagates, so a caller that has already committed the edit should pass a token that is NOT the
    /// request's abort token.
    /// </remarks>
    public virtual async Task<SandboxEnvReapplyOutcome> ReapplyForWorkspaceAsync(
        string workspaceId,
        CancellationToken ct
    )
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

            await ApplyIsolatedAsync(
                    report,
                    threadId,
                    sessionId,
                    async () =>
                    {
                        var modeId = await ReadModeIdAsync(threadId, ct).ConfigureAwait(false);
                        await ApplyForThreadAsync(threadId, sessionId, workspaceId, modeId, ct).ConfigureAwait(false);
                        return true;
                    }
                )
                .ConfigureAwait(false);
        }

        return CompleteReapply(report, "workspace", workspaceId);
    }

    /// <summary>Reapplies the merged env to every live session whose last-activated thread runs under <paramref name="modeId"/>.</summary>
    /// <remarks>See <see cref="ReapplyForWorkspaceAsync"/> for the failure and cancellation contract.</remarks>
    public virtual async Task<SandboxEnvReapplyOutcome> ReapplyForModeAsync(string modeId, CancellationToken ct)
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

            // The mode read decides whether this session is in scope at all, so a failure to read it is
            // recorded against the session rather than skipped: the thread MAY be on this mode, and the
            // loop cannot tell.
            await ApplyIsolatedAsync(
                    report,
                    threadId,
                    sessionId,
                    async () =>
                    {
                        var threadModeId = await ReadModeIdAsync(threadId, ct).ConfigureAwait(false);
                        if (!string.Equals(threadModeId, modeId, StringComparison.Ordinal))
                        {
                            return false;
                        }

                        await ApplyForThreadAsync(threadId, sessionId, session.WorkspaceId, modeId, ct)
                            .ConfigureAwait(false);
                        return true;
                    }
                )
                .ConfigureAwait(false);
        }

        return CompleteReapply(report, "mode", modeId);
    }

    /// <summary>What the two reapply loops observed, so the aggregate can be logged and reported once.</summary>
    private sealed class ReapplyReport
    {
        public int Attempted { get; set; }

        public int Succeeded { get; set; }

        public List<SandboxEnvReapplyFailure> Failures { get; } = [];
    }

    /// <summary>
    /// Runs one session's reapply and records the outcome instead of letting it end the loop.
    /// <paramref name="apply"/> returns <see langword="false"/> when the session turned out to be out of
    /// scope, which counts as neither an attempt nor a failure.
    /// <para>
    /// The loop body used to be unguarded, so the FIRST session that failed aborted the whole reapply —
    /// every session after it silently kept the old env. Which sessions those were depended on the
    /// iteration order of a <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey, TValue}"/>'s
    /// keys, so the same edit could leave a different subset stale each time. Containment covers every
    /// failure type, not only sandbox ones: the reapply also reads the conversation, workspace and mode
    /// stores, and a store fault escaping here would abandon the remaining sessions just the same.
    /// </para>
    /// <para>
    /// Cancellation is deliberately NOT caught: it is not a session's failure and must stop the loop.
    /// </para>
    /// </summary>
    private async Task ApplyIsolatedAsync(
        ReapplyReport report,
        string threadId,
        string sessionId,
        Func<Task<bool>> apply
    )
    {
        try
        {
            if (!await apply().ConfigureAwait(false))
            {
                return;
            }

            report.Attempted++;
            report.Succeeded++;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ApplyForThreadAsync has already logged the key names for the InvalidEnv case; this line is
            // about WHICH session was left behind. Still no values.
            logger.LogWarning(
                ex,
                "Sandbox env reapply failed for session {SessionId} (thread {ThreadId}); continuing with the remaining sessions",
                sessionId,
                threadId
            );
            report.Attempted++;
            report.Failures.Add(new SandboxEnvReapplyFailure(sessionId, ex));
        }
    }

    /// <summary>Logs the aggregate and returns it; the caller decides what the user is told.</summary>
    private SandboxEnvReapplyOutcome CompleteReapply(ReapplyReport report, string scope, string id)
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

        return new SandboxEnvReapplyOutcome(report.Attempted, report.Succeeded, report.Failures);
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

/// <summary>One live session a reapply could not bring up to date, and why.</summary>
/// <param name="SessionId">The session left on its previous env.</param>
/// <param name="Error">The failure. Never carries an env VALUE.</param>
public sealed record SandboxEnvReapplyFailure(string SessionId, Exception Error);

/// <summary>
/// What a post-edit reapply did. The edit itself has already been persisted by the time this exists, so
/// a failure here is a PARTIAL success, never a rejected write.
/// </summary>
/// <param name="Attempted">Sessions that were in scope for the edit.</param>
/// <param name="Succeeded">Sessions now carrying the new merged env.</param>
/// <param name="Failures">Sessions left on their previous env, in no particular order.</param>
public sealed record SandboxEnvReapplyOutcome(
    int Attempted,
    int Succeeded,
    IReadOnlyList<SandboxEnvReapplyFailure> Failures
)
{
    /// <summary>A reapply that found nothing to do.</summary>
    public static SandboxEnvReapplyOutcome None { get; } = new(0, 0, []);

    /// <summary>
    /// The failure the caller should report, chosen by what the caller can ACT on rather than by
    /// position. <see cref="Failures"/> follows the unordered iteration of the live-session registry, so
    /// taking the first entry made the same edit answer with a key list on one run and with nothing
    /// useful on the next. A local validation failure names its keys and layer; a gateway
    /// <see cref="SandboxErrorKind.InvalidEnv"/> names the keys it refused. Anything else (a transport
    /// fault, a store fault) is not the caller's to fix, and the next turn on that session retries it.
    /// <see langword="null"/> when no failure names invalid keys.
    /// </summary>
    public SandboxEnvReapplyFailure? InvalidEnvFailure =>
        Failures.FirstOrDefault(f => f.Error is SandboxEnvValidationException)
        ?? Failures.FirstOrDefault(f => f.Error is SandboxException { Kind: SandboxErrorKind.InvalidEnv });

    /// <summary>The offending key NAMES of <see cref="InvalidEnvFailure"/>; empty when there is none.</summary>
    public IReadOnlyList<string> InvalidKeys =>
        InvalidEnvFailure?.Error switch
        {
            SandboxEnvValidationException v => v.Keys,
            SandboxException s => s.InvalidKeys ?? [],
            _ => [],
        };

    /// <summary>
    /// The <c>400 invalid_env</c> body for an edit that WAS persisted but whose env could not be applied
    /// to every live session. It keeps the <c>invalid_env</c> code and <c>keys</c> the client already
    /// handles as a partial success, and says in words and in <c>saved: true</c> that the write landed.
    /// A pre-commit validation rejection never produces this body, so the two are distinguishable.
    /// Carries key NAMES only, never values.
    /// </summary>
    /// <param name="subject">How the error message names the edited record, e.g. <c>Workspace 'ws-1'</c>.</param>
    /// <param name="editedLayer">The layer the edit changed, reported when the failure names no layer of its own.</param>
    public object ToSavedButNotAppliedBody(string subject, string editedLayer)
    {
        var failure =
            InvalidEnvFailure ?? throw new InvalidOperationException("There is no invalid-env failure to report.");
        return new
        {
            error = $"{subject} was saved, but its environment could not be applied to {Failures.Count} of {Attempted} live sandbox session(s): {failure.Error.Message}",
            code = "invalid_env",
            saved = true,
            layer = failure.Error is SandboxEnvValidationException v ? v.Layer : editedLayer,
            keys = InvalidKeys,
        };
    }
}
