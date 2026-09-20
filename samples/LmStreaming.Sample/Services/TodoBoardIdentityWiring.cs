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
                return Unknown(directory);
            }

            target = scopedAgentId;
        }

        var resolution = directory.Resolve(target);
        if (resolution.Entry is { } entry)
        {
            // The canonical identifier, not the display name: the board compares ownership ordinally,
            // and an identifier is the only thing here guaranteed to be unique within the conversation.
            // That reason still holds — the name rides ALONGSIDE it as the display value, so the board
            // can say "reviewer" where it keys on "agent-1", and nothing compares the name.
            return new TaskManager.AssigneeResolution(
                entry.AgentId,
                entry.AgentId,
                entry.IsLive ? TaskManager.AssigneeLiveness.Live : TaskManager.AssigneeLiveness.Unreachable,
                Candidates: null,
                DisplayName: entry.Name
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

            _ => Unknown(directory),
        };
    }

    /// <summary>
    ///     Nothing resolved. The assignable names ride along so the board's refusal can name the agents
    ///     the caller could have meant instead of sending it after an id.
    /// </summary>
    private static TaskManager.AssigneeResolution Unknown(AgentCollaborationDirectory directory) =>
        new(
            null,
            null,
            TaskManager.AssigneeLiveness.Unknown,
            Candidates: null,
            DisplayName: null,
            AssignableNames(directory)
        );

    /// <summary>
    ///     Every agent the board could have meant: the ones still running first, then every other
    ///     registered agent with its lifecycle status appended — <c>"analyst (completed)"</c>. Sorted
    ///     within each half so a refusal reads the same way twice.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This used to filter on <see cref="AgentDirectoryEntry.IsLive" />, which read as "still
    ///         running" and is not what that flag means. A sub-agent that finishes its run is NOT
    ///         retired — <c>SubAgentManager</c> retires only where an agent stops existing (failed
    ///         spawn, cancelled queue entry, manager disposal), because a finished child keeps its loop
    ///         and a later message restarts it. So the filter was already letting completed agents
    ///         through, and the names it dropped were the ones that never ran at all. Either way it was
    ///         answering a question nobody asked: the board refuses only
    ///         <see cref="TaskManager.AssigneeLiveness.Unknown" />, so every name in the directory is a
    ///         name the very next call would have honoured, and a refusal that lists fewer names than
    ///         it accepts sends the caller round the same refusal again.
    ///     </para>
    ///     <para>
    ///         The bigger correction is the status, not the extra rows. Before, every name was offered
    ///         bare, so a caller reading the refusal could not tell the agent still working from the one
    ///         that errored out, and picking wrongly cost it another turn.
    ///     </para>
    ///     <para>
    ///         Status appended rather than a live/finished split, because status is what the caller is
    ///         actually choosing on and it is the vocabulary every other surface already publishes. An
    ///         unmarked list would read as "all of these are working on it now"; a two-bucket list would
    ///         make the reader guess which bucket <c>error</c> fell into.
    ///     </para>
    ///     <para>
    ///         Running first rather than one merged sort: an agent already in flight is the better
    ///         target whenever one fits, and the reader takes the head of the list.
    ///     </para>
    ///     <para>
    ///         Agents lost to a restart (#676) are absent, because a tombstone is not in the snapshot at
    ///         all. That is a gap in what this sentence can offer, not a decision made here.
    ///     </para>
    ///     <para>
    ///         Status is read straight off the entry and <see cref="AgentDirectoryEntry.IsLive" /> is not
    ///         consulted at all, deliberately: every production retirement sets the status first
    ///         (<c>AgentCollaborationBundle.RetireAgent</c> takes one), so the status already says what
    ///         happened, and deriving a second label from a flag whose meaning is itself unsettled would
    ///         put that argument into a sentence the model reads.
    ///     </para>
    /// </remarks>
    private static IReadOnlyList<string> AssignableNames(AgentCollaborationDirectory directory)
    {
        var byStatus = directory
            .Snapshot()
            .ToLookup(entry =>
                string.Equals(entry.Status, AgentCollaborationStatuses.Running, StringComparison.Ordinal)
            );

        return
        [
            .. Sorted(byStatus[true].Select(entry => entry.Name)),
            .. Sorted(byStatus[false].Select(entry => $"{entry.Name} ({entry.Status})")),
        ];

        // Distinct over the RENDERED text, so two agents sharing a name but not a status both survive:
        // "analyst (completed)" and "analyst (error)" are different answers to "who did you mean?".
        static IEnumerable<string> Sorted(IEnumerable<string> names) =>
            names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
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
