namespace LmStreaming.Sample.Persistence;

/// <summary>
/// Refuses to start the SQLite-backed host over a deployment that still holds conversations in the
/// former FileConversationStore directory. Nothing imports that directory, so starting would silently
/// hide every existing conversation behind an empty store. The operator either moves the directory
/// aside or acknowledges starting without it.
/// </summary>
internal static class LegacyConversationStoreGuard
{
    /// <summary>Configuration key an operator sets to <c>true</c> to start without the legacy conversations.</summary>
    public const string AcknowledgeKey = "LmStreaming:LegacyFileStoreAcknowledged";

    /// <summary>
    /// Throws when <paramref name="legacyDirectory"/> holds at least one thread directory and the operator
    /// has not acknowledged starting without it.
    /// </summary>
    public static void EnsureNoUnimportedStore(string legacyDirectory, string conversationDbPath, bool acknowledged)
    {
        if (
            acknowledged
            || !Directory.Exists(legacyDirectory)
            || !Directory.EnumerateDirectories(legacyDirectory).Any()
        )
        {
            return;
        }

        throw new InvalidOperationException(
            $"Conversations from the former file-backed store exist at '{legacyDirectory}'. This host stores "
                + $"conversations in SQLite at '{conversationDbPath}' and does not import the old store, so they "
                + "would not be visible. Move the directory aside (for example to 'conversations.legacy') to "
                + $"start without them, or set {AcknowledgeKey}=true to start and keep it in place."
        );
    }
}
