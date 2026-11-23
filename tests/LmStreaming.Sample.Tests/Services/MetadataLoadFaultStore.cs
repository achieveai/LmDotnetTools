using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// The host's <see cref="IConversationStore"/>, plus a one-shot fault: the next metadata load of a chosen thread
/// throws instead of reading. Lets a test fail one exact read, such as the reload a sandbox release commits from.
/// </summary>
internal sealed class MetadataLoadFaultStore(IConversationStore inner) : IConversationStore
{
    private string? _failNextLoadOf;

    /// <summary>Replaces the registered conversation store with one that wraps it.</summary>
    public static MetadataLoadFaultStore Install(IServiceCollection services)
    {
        var registered =
            services
                .LastOrDefault(descriptor => descriptor.ServiceType == typeof(IConversationStore))
                ?.ImplementationInstance as IConversationStore
            ?? throw new InvalidOperationException("The host registers no IConversationStore instance to wrap.");
        var faulty = new MetadataLoadFaultStore(registered);
        services.RemoveAll<IConversationStore>();
        services.AddSingleton<IConversationStore>(faulty);
        return faulty;
    }

    /// <summary>Makes the next <see cref="LoadMetadataAsync"/> of <paramref name="threadId"/> throw.</summary>
    public void FailNextLoad(string threadId) => Volatile.Write(ref _failNextLoadOf, threadId);

    public Task<ThreadMetadata?> LoadMetadataAsync(string threadId, CancellationToken ct = default)
    {
        var failing = Volatile.Read(ref _failNextLoadOf);
        return
            failing is not null
            && string.Equals(failing, threadId, StringComparison.Ordinal)
            && ReferenceEquals(Interlocked.CompareExchange(ref _failNextLoadOf, null, failing), failing)
            ? throw new IOException($"Reading the metadata of {threadId} failed.")
            : inner.LoadMetadataAsync(threadId, ct);
    }

    public Task AppendMessagesAsync(
        string threadId,
        IReadOnlyList<PersistedMessage> messages,
        CancellationToken ct = default
    ) => inner.AppendMessagesAsync(threadId, messages, ct);

    public Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(string threadId, CancellationToken ct = default) =>
        inner.LoadMessagesAsync(threadId, ct);

    public Task<long> GetMessageWatermarkAsync(string threadId, CancellationToken ct = default) =>
        inner.GetMessageWatermarkAsync(threadId, ct);

    public Task<IReadOnlyList<PersistedMessage>> LoadMessageRangeAsync(
        string threadId,
        long fromSeq,
        long toSeq,
        int limit,
        CancellationToken ct = default
    ) => inner.LoadMessageRangeAsync(threadId, fromSeq, toSeq, limit, ct);

    public Task ReplaceMessageAsync(string threadId, PersistedMessage replacement, CancellationToken ct = default) =>
        inner.ReplaceMessageAsync(threadId, replacement, ct);

    public Task NumberLegacyMessagesAsync(string threadId, CancellationToken ct = default) =>
        inner.NumberLegacyMessagesAsync(threadId, ct);

    public Task SaveMetadataAsync(string threadId, ThreadMetadata metadata, CancellationToken ct = default) =>
        inner.SaveMetadataAsync(threadId, metadata, ct);

    public Task UpdateMetadataAsync(
        string threadId,
        Func<ThreadMetadata?, ThreadMetadata> update,
        CancellationToken ct = default
    ) => inner.UpdateMetadataAsync(threadId, update, ct);

    public Task DeleteThreadAsync(string threadId, CancellationToken ct = default) =>
        inner.DeleteThreadAsync(threadId, ct);

    public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
        int limit = 50,
        int offset = 0,
        ConversationListOptions? options = null,
        CancellationToken ct = default
    ) => inner.ListThreadsAsync(limit, offset, options, ct);

    public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
        ConversationListScope scope,
        int limit = 50,
        int offset = 0,
        ConversationListOptions? options = null,
        CancellationToken ct = default
    ) => inner.ListThreadsAsync(scope, limit, offset, options, ct);
}
