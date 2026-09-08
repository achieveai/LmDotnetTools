namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>Source-compatibility shims for positional cancellation in conversation listings.</summary>
/// <remarks>
/// Import this namespace to use the legacy call forms on an interface or concrete store.
/// Applicable instance methods take precedence, so an untyped <c>default</c> still selects
/// the current options API. These extensions do not restore removed instance binary signatures.
/// </remarks>
public static class ConversationStoreExtensions
{
    /// <summary>Lists threads using the legacy positional cancellation call form.</summary>
    /// <param name="store">The conversation store.</param>
    /// <param name="limit">Maximum number of threads to return.</param>
    /// <param name="offset">Number of threads to skip.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Thread metadata in the default listing order.</returns>
    [Obsolete("Use ListThreadsAsync(limit, offset, options: null, ct: cancellationToken) instead.")]
    public static Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
        this IConversationStore store,
        int limit,
        int offset,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.ListThreadsAsync(limit, offset, options: null, ct: cancellationToken);
    }

    /// <summary>Lists readable threads using the legacy positional cancellation call form.</summary>
    /// <param name="store">The conversation store.</param>
    /// <param name="scope">The principal's tenant, identity, role and resolved grants.</param>
    /// <param name="limit">Maximum number of threads to return.</param>
    /// <param name="offset">Number of threads to skip.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Readable thread metadata in the default listing order.</returns>
    [Obsolete("Use ListThreadsAsync(scope, limit, offset, options: null, ct: cancellationToken) instead.")]
    public static Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
        this IConversationStore store,
        ConversationListScope scope,
        int limit,
        int offset,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.ListThreadsAsync(scope, limit, offset, options: null, ct: cancellationToken);
    }
}
