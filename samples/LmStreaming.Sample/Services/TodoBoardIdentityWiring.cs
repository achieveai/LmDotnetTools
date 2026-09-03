using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.Misc.Utils;

namespace LmStreaming.Sample.Services;

/// <summary>
///     Gives the conversation's todo board a way to turn an assignee name into an agent identity, by
///     wiring <see cref="TaskManager.AssigneeResolver" /> to the collaboration's agent directory.
/// </summary>
/// <remarks>
///     <para>
///         Lives here rather than in LmMultiTurn because this is the seam between two projects that do
///         not know about each other: <c>Misc</c> (the board) is a published package with a narrow
///         dependency set, and LmMultiTurn (the directory) does not reference it. The host is the one
///         place both are visible, and it is also the only caller.
///     </para>
///     <para>
///         Everything here is scoped to ONE root conversation. Agent identifiers are ordinals
///         (<c>agent-1</c>, <c>agent-2</c>, …) handed out per root conversation, so the same identifier
///         names a different agent in a different conversation.
///     </para>
/// </remarks>
public static class TodoBoardIdentityWiring
{
    /// <summary>
    ///     Points <paramref name="board" /> at <paramref name="collaboration" />'s directory for
    ///     assignee resolution.
    /// </summary>
    /// <param name="board">The conversation's todo board.</param>
    /// <param name="collaboration">The conversation's root collaboration.</param>
    /// <param name="rootThreadId">
    ///     The root conversation's thread id — the scope every agent identifier in
    ///     <paramref name="collaboration" /> is numbered under. Passed explicitly rather than read off
    ///     the board, because the board's own thread id is stamped later in host startup than this call.
    /// </param>
    public static void Attach(TaskManager board, AgentCollaborationSetup collaboration, string rootThreadId)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(collaboration);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootThreadId);

        var directory = collaboration.Directory;
        board.AssigneeResolver = name => Resolve(directory, rootThreadId, name);
    }

    /// <summary>
    ///     Resolves one assignee name against <paramref name="directory" />, within the root
    ///     conversation identified by <paramref name="rootThreadId" />.
    /// </summary>
    internal static TaskManager.AssigneeResolution Resolve(
        AgentCollaborationDirectory directory,
        string rootThreadId,
        string name
    )
    {
        var target = name;
        if (SubAgentThreadIds.TryGetAgentId(name, out var scopedAgentId))
        {
            // A sub-agent transcript thread id is the store-wide reference, and its scope segment is
            // the only thing distinguishing this conversation's agent-1 from another's. Reading the
            // identifier out and resolving it alone would throw that away and hand the claim to
            // whichever agent happens to carry the same number here. Rebuilding the reference under
            // THIS root and requiring it to match compares the two scopes without writing a second
            // matcher for a shape SubAgentThreadIds already owns.
            if (!string.Equals(SubAgentThreadIds.For(rootThreadId, scopedAgentId), name, StringComparison.Ordinal))
            {
                return new TaskManager.AssigneeResolution(null, null, TaskManager.AssigneeLiveness.Unknown);
            }

            target = scopedAgentId;
        }

        var resolution = directory.Resolve(target);
        if (resolution.Entry is { } entry)
        {
            // The canonical identifier, not the display name: the board compares ownership ordinally,
            // and an identifier is the only thing here guaranteed to be unique within the conversation.
            return new TaskManager.AssigneeResolution(
                entry.AgentId,
                entry.AgentId,
                entry.IsLive ? TaskManager.AssigneeLiveness.Live : TaskManager.AssigneeLiveness.Unreachable
            );
        }

        return resolution.FailureCode switch
        {
            AgentDirectoryFailureCodes.AmbiguousName => new TaskManager.AssigneeResolution(
                null,
                null,
                TaskManager.AssigneeLiveness.Unknown,
                CandidatesNamed(directory, target)
            ),

            // #676: the agent existed, and a restart took it away. "Gone" and "never existed" lead to
            // different recovery actions, so the distinction has to survive the trip to the board.
            // The identifier is asked for separately because a tombstone resolves to a null entry by
            // construction — and it is the identifier rather than the target, so an agent referred to
            // by display name after a restart is keyed the same way it was keyed before one.
            AgentDirectoryFailureCodes.TargetNotLive when directory.InvalidatedAgentId(target) is { } goneAgentId =>
                new TaskManager.AssigneeResolution(goneAgentId, goneAgentId, TaskManager.AssigneeLiveness.Unreachable),

            _ => new TaskManager.AssigneeResolution(null, null, TaskManager.AssigneeLiveness.Unknown),
        };
    }

    /// <summary>
    ///     The identifiers of every agent carrying <paramref name="name" />, ordered so the refusal
    ///     naming them reads the same way twice.
    /// </summary>
    private static IReadOnlyList<string> CandidatesNamed(AgentCollaborationDirectory directory, string name) =>
        [
            .. directory
                .Snapshot()
                .Where(entry => string.Equals(entry.Name, name, StringComparison.Ordinal))
                .Select(entry => entry.AgentId)
                .Order(StringComparer.Ordinal),
        ];
}
