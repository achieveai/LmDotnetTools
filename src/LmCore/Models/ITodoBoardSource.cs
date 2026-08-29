namespace AchieveAi.LmDotnetTools.LmCore.Models;

/// <summary>
///     A live todo board that can be read on demand — implemented by the conversation's task tool and
///     held by the agent pool so the read path can reach the running instance.
/// </summary>
/// <remarks>
///     <para>
///         This interface exists because the task tool ships in a leaf assembly the pool cannot reference.
///         Without it the pool would have to hold the instance as <c>object</c>, and the endpoint would
///         reflect over it.
///     </para>
///     <para>
///         Implementations must be safe to call from any thread at any time, including while the agent is
///         mid-mutation: a reader of a work board is by definition racing the worker.
///     </para>
/// </remarks>
public interface ITodoBoardSource
{
    /// <summary>
    ///     Captures the board as it stands right now. Never returns null; a board with no rows comes back
    ///     as an empty snapshot rather than as absence, so the caller decides what emptiness means.
    /// </summary>
    /// <param name="threadId">The conversation the snapshot is stamped with.</param>
    TodoBoardSnapshot GetTodoBoardSnapshot(string threadId);
}
