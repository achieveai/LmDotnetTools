using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

namespace LmStreaming.Sample.Services;

/// <summary>
///     The delivery leg shared by <see cref="TodoNudgeService" /> and <see cref="TodoDigestService" />:
///     resolves a board name to the conversation that should hear the notification and hands the
///     message over.
/// </summary>
/// <remarks>
///     <para>
///         <b>A sub-agent target is reached through its manager, never straight at its loop (#690).</b>
///         A child whose run finished normally retains its loop and owned provider for warm follow-ups.
///         <see cref="SubAgentManager.SendMessageAsync(string, IMessage, bool, CancellationToken)" />
///         coordinates lifecycle and admission: it injects into a running child, resumes a completed
///         reusable child, and rebuilds only when the existing runtime is unusable (or refuses observably).
///         This is the same door the collaboration messenger and the model's own SendMessage tool use.
///     </para>
///     <para>
///         The root conversation is different: its loop never disposes its provider at run end (the
///         pool disposes it only together with the loop), so a direct send there is safe and remains
///         the primary digest's path.
///     </para>
/// </remarks>
public static class TodoNotificationDelivery
{
    /// <summary>
    ///     A name that resolves to a live sub-agent is a <see cref="TodoNudgeTargetKind.SubAgent" />
    ///     target; anything else would land in the root conversation.
    /// </summary>
    public static TodoNudgeTargetKind ResolveTargetKind(SubAgentManager? manager, string name)
    {
        return manager is not null && manager.TryGetAgent(name, out _)
            ? TodoNudgeTargetKind.SubAgent
            : TodoNudgeTargetKind.RootConversation;
    }

    /// <summary>
    ///     Delivers <paramref name="message" /> to the sub-agent named <paramref name="targetName" />
    ///     when the manager knows it, otherwise to <paramref name="root" /> (a null name is the root
    ///     conversation's own address — the primary digest).
    /// </summary>
    /// <returns><c>true</c> when the target accepted the notification; <c>false</c> on a refusal.</returns>
    public static async ValueTask<bool> DeliverAsync(
        IMultiTurnAgent root,
        SubAgentManager? manager,
        string? targetName,
        NotifyMessage message,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(message);

        if (targetName is null || manager is null || !manager.TryGetAgent(targetName, out _))
        {
            return await root.TrySendAsync([message], ct: ct) is not null;
        }

        try
        {
            // Background on purpose: a board hook must not block on the child's whole turn. The typed
            // NotifyMessage travels as itself so the child's history and UI see a notification, not an
            // anonymous user turn.
            _ = await manager.SendMessageAsync(targetName, message, runInBackground: true, ct);
            return true;
        }
        catch (ObjectDisposedException)
        {
            // The manager (and with it every child) is being torn down with the conversation. Listed
            // before InvalidOperationException, its base type.
            return false;
        }
        catch (ArgumentException)
        {
            // The manager stopped tracking this name between the resolve above and the send.
            return false;
        }
        catch (InvalidOperationException)
        {
            // Still queued, or the restart could not take a concurrency slot in time: an observable
            // refusal the caller logs, rather than a run nobody can complete.
            return false;
        }
    }
}
