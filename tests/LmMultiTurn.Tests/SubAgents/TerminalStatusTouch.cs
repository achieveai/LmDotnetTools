using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

namespace LmMultiTurn.Tests.SubAgents;

/// <summary>
/// Tells the sub-agent manager's terminal-status push apart from the child's own metadata writes, which
/// go through the same atomic <see cref="IConversationStore.UpdateMetadataAsync"/>. The push writes the
/// child's <see cref="SubAgentRecord"/> with a terminal status (#868), keeping every other property so a
/// store decorator can still stamp the row; the child's end-of-run write never sets that record. A test
/// store that gates or counts "the push" must check this, or the child's write can land on its gate first.
/// </summary>
internal static class TerminalStatusTouch
{
    /// <summary>
    /// Whether <paramref name="update"/> writes a terminal <see cref="SubAgentRecord"/>. Evaluates the
    /// callback once on a throwaway row; the callbacks under test are pure, and one that throws is not a
    /// push.
    /// </summary>
    public static bool Is(string threadId, Func<ThreadMetadata?, ThreadMetadata> update)
    {
        var row = new ThreadMetadata { ThreadId = threadId, LastUpdated = 1 };
        try
        {
            return SubAgentRecord.Read(update(row))
                is { Status: SubAgentStatus.Completed or SubAgentStatus.Error or SubAgentStatus.Stopped };
        }
        catch (Exception)
        {
            return false;
        }
    }
}
