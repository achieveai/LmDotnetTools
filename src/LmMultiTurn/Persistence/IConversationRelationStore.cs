namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>A compact persisted sub-agent parent relation.</summary>
/// <param name="ThreadId">The child conversation.</param>
/// <param name="ParentThreadId">The conversation that spawned the child.</param>
public sealed record ConversationParentLink(string ThreadId, string ParentThreadId);

/// <summary>
/// Optional relation reads for stores that can avoid materializing unrelated conversation metadata.
/// </summary>
public interface IConversationRelationStore
{
    /// <summary>Lists every persisted sub-agent parent link in one consistent store read.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<ConversationParentLink>> ListSubAgentParentLinksAsync(CancellationToken ct = default);

    /// <summary>Lists conversations forked directly from <paramref name="threadId"/>.</summary>
    /// <param name="threadId">The source conversation.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<string>> ListDirectForkThreadIdsAsync(string threadId, CancellationToken ct = default);
}
