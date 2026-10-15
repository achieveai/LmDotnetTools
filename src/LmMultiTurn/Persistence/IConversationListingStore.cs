using AchieveAi.LmDotnetTools.LmCore.Identity;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>A read-only listing projection, never a full metadata row for a write.</summary>
public sealed record ConversationListingRow
{
    public required string ThreadId { get; init; }
    public required long LastUpdated { get; init; }
    public string? TenantId { get; init; }
    public string? OwnerUserId { get; init; }
    public string? OwnerAppId { get; init; }
    public Visibility? Visibility { get; init; }
    public required IReadOnlyDictionary<string, string?> Properties { get; init; }

    internal static ConversationListingRow FromMetadata(ThreadMetadata metadata, IReadOnlySet<string> propertyKeys) =>
        new()
        {
            ThreadId = metadata.ThreadId,
            LastUpdated = metadata.LastUpdated,
            TenantId = metadata.TenantId,
            OwnerUserId = metadata.OwnerUserId,
            OwnerAppId = metadata.OwnerAppId,
            Visibility = metadata.Visibility,
            Properties = propertyKeys
                .Where(key => metadata.Properties?.ContainsKey(key) == true)
                .ToDictionary(key => key, key => metadata.Properties![key]?.ToString(), StringComparer.Ordinal),
        };
}

/// <summary>Optional fresh listing reads that do not materialize unrelated property values.</summary>
public interface IConversationListingStore
{
    Task<IReadOnlyList<ConversationListingRow>> ListConversationRowsAsync(
        IReadOnlySet<string> propertyKeys,
        ConversationListScope? scope = null,
        int limit = 50,
        int offset = 0,
        ConversationListOptions? options = null,
        CancellationToken ct = default
    );
}

/// <summary>Retains full-store behavior when the optional listing capability is unavailable.</summary>
public static class ConversationListingStoreExtensions
{
    public static async Task<IReadOnlyList<ConversationListingRow>> ListConversationRowsAsync(
        this IConversationStore store,
        IReadOnlySet<string> propertyKeys,
        ConversationListScope? scope = null,
        int limit = 50,
        int offset = 0,
        ConversationListOptions? options = null,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(propertyKeys);
        var keys = new HashSet<string>(propertyKeys, StringComparer.Ordinal);
        if (store is IConversationListingStore listing)
        {
            return await listing
                .ListConversationRowsAsync(keys, scope, limit, offset, options, ct)
                .ConfigureAwait(false);
        }

        var rows = scope is null
            ? await store.ListThreadsAsync(limit, offset, options, ct).ConfigureAwait(false)
            : await store.ListThreadsAsync(scope, limit, offset, options, ct).ConfigureAwait(false);
        return rows.Select(row => ConversationListingRow.FromMetadata(row, keys)).ToArray();
    }
}
