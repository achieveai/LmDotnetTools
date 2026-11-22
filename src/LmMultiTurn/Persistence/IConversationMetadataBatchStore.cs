namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>
/// Optional read for stores that can load many conversations' metadata in one store read, so a caller
/// that needs N rows takes one connection rather than N.
/// </summary>
public interface IConversationMetadataBatchStore
{
    /// <summary>
    /// Loads the metadata of every listed conversation that exists, each exactly as
    /// <see cref="IConversationStore.LoadMetadataAsync"/> would return it.
    /// </summary>
    /// <param name="threadIds">The conversations to load. Duplicates are allowed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The rows found, keyed by thread id. A conversation with no row is absent.</returns>
    Task<IReadOnlyDictionary<string, ThreadMetadata>> LoadMetadataBatchAsync(
        IReadOnlyCollection<string> threadIds,
        CancellationToken ct = default
    );
}
