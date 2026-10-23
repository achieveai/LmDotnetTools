namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>
/// Optional thread-id lookup capability for stores that can avoid materializing conversation
/// metadata when a caller needs only ids under an ordinal prefix.
/// </summary>
public interface IConversationThreadIdLookupStore
{
    /// <summary>
    /// Lists thread ids beginning with <paramref name="prefix"/>, ordered ordinally.
    /// </summary>
    /// <param name="prefix">Thread-id prefix to match.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<string>> ListThreadIdsByPrefixAsync(string prefix, CancellationToken ct = default);
}
