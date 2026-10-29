using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;

namespace LmStreaming.Sample.Persistence;

/// <summary>
/// Owns the shared SQLite store separately from its non-owning indexed wrapper. DI constructs this
/// dependency before the agent pool, so reverse disposal drains the pool's writers before closing it.
/// Both disposal paths are supported because test hosts may dispose their container synchronously.
/// </summary>
internal sealed class SqliteConversationStoreLifetime(SqliteConversationStore store) : IDisposable, IAsyncDisposable
{
    public SqliteConversationStore Store { get; } = store;

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync() => Store.DisposeAsync();
}
