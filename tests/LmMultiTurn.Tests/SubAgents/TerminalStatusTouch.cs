using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace LmMultiTurn.Tests.SubAgents;

/// <summary>
/// Tells the sub-agent manager's terminal-status push apart from the child's own metadata writes, which
/// go through the same atomic <see cref="IConversationStore.UpdateMetadataAsync"/>. The push is a touch:
/// it hands back the row it is given so a store decorator can stamp it, while the child's end-of-run
/// write always builds a new record. A test store that gates or counts "the push" must check this, or
/// the child's write can land on its gate first.
/// </summary>
internal static class TerminalStatusTouch
{
    /// <summary>
    /// Whether <paramref name="update"/> hands back its input untouched. Evaluates the callback once on a
    /// throwaway row; the callbacks under test are pure, and one that throws is not a touch.
    /// </summary>
    public static bool Is(string threadId, Func<ThreadMetadata?, ThreadMetadata> update)
    {
        var row = new ThreadMetadata { ThreadId = threadId, LastUpdated = 1 };
        try
        {
            return ReferenceEquals(update(row), row);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
