namespace AchieveAi.LmDotnetTools.LmMultiTurn.TodoBoard;

/// <summary>
///     Thrown by <see cref="ConversationTodoProjection.SaveAsync" />'s update callback to DECLINE a board
///     write for a conversation whose metadata row no longer exists — throwing is the only way to
///     decline, because every <c>IConversationStore.UpdateMetadataAsync</c> persists whatever the
///     callback returns.
/// </summary>
/// <remarks>
///     A dedicated type, not a bare <see cref="InvalidOperationException" /> (#590 review F-003): the
///     decline is a deliberate, FINAL control-flow signal ("this conversation is gone — stop trying"),
///     while the store infrastructure throws <see cref="InvalidOperationException" /> subtypes of its own
///     for genuinely transient faults — notably <see cref="ObjectDisposedException" /> from the SQLite
///     connection factory, which derives from it. A catch keyed on the base type cannot tell "the callee
///     declined" from "the callee broke", so it would swallow a store fault as if the write had
///     succeeded. Catch THIS type exactly to honor the decline; let everything else keep the pending
///     write alive for retry. Derives from <see cref="InvalidOperationException" /> so pre-existing
///     callers catching the base type (the <c>GET /todos</c> write-through) keep working unchanged.
/// </remarks>
public sealed class TodoBoardDeclinedException : InvalidOperationException
{
    public TodoBoardDeclinedException(string message)
        : base(message) { }
}
