using System.Text.Json;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;
using AchieveAi.LmDotnetTools.LmTestUtils.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LmMultiTurn.Tests.Persistence;

/// <summary>
/// Opens the three <see cref="IConversationStore"/> flavours by name, hands out a SECOND handle on the
/// same backing store (a fresh process for the file and SQLite stores; the same instance in memory,
/// which has no other process), and seeds rows that predate <see cref="PersistedMessage.Seq"/>.
/// </summary>
/// <remarks>
/// A second handle is what a "concurrent append" and a "restart" both are from the store's point of
/// view: another writer the first handle's in-memory state knows nothing about. The tests that need
/// one say so by calling <see cref="Reopen"/>, so a reader can tell a single-handle claim from a
/// cross-handle one.
/// </remarks>
internal sealed class ConversationStoreHarness : IAsyncDisposable
{
    /// <summary>Every store flavour, by name, so a failure says which one drifted.</summary>
    public static TheoryData<string> AllKinds => ["sqlite", "file", "memory"];

    /// <summary>The flavours that survive a process restart.</summary>
    public static TheoryData<string> DurableKinds => ["sqlite", "file"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"seq_{Guid.NewGuid():N}");
    private readonly List<IAsyncDisposable> _disposables = [];
    private readonly Dictionary<string, string> _backing = new(StringComparer.Ordinal);
    private InMemoryConversationStore? _memory;

    public ConversationStoreHarness()
    {
        _ = Directory.CreateDirectory(_root);
    }

    /// <summary>Opens the first handle on a fresh backing store of the given flavour.</summary>
    public IConversationStore Open(string kind)
    {
        switch (kind)
        {
            case "sqlite":
                _backing[kind] = Path.Combine(_root, $"conv_{Guid.NewGuid():N}.db");
                return Reopen(kind);
            case "file":
                _backing[kind] = Path.Combine(_root, $"file_{Guid.NewGuid():N}");
                _ = Directory.CreateDirectory(_backing[kind]);
                return Reopen(kind);
            default:
                _memory = new InMemoryConversationStore();
                return _memory;
        }
    }

    /// <summary>
    /// A second handle on the backing store <see cref="Open"/> created: a new instance for the durable
    /// flavours, the same instance for memory.
    /// </summary>
    public IConversationStore Reopen(string kind)
    {
        switch (kind)
        {
            case "sqlite":
                var sqlite = new SqliteConversationStore(_backing[kind]);
                _disposables.Add(sqlite);
                return sqlite;
            case "file":
                return new FileConversationStore(_backing[kind]);
            default:
                return _memory ?? throw new InvalidOperationException("Open the memory store first.");
        }
    }

    /// <summary>
    /// Puts <paramref name="threadId"/> into the state a thread written by a pre-Seq build is in: rows
    /// present, every <see cref="PersistedMessage.Seq"/> null. Not supported in memory, where no row
    /// can predate the running binary.
    /// </summary>
    public async Task SeedLegacyRowsAsync(string kind, string threadId, IReadOnlyList<PersistedMessage> rows)
    {
        switch (kind)
        {
            case "sqlite":
            {
                // Let the store create the schema, then strip the sequence numbers it assigned.
                var store = Reopen(kind);
                await store.AppendMessagesAsync(threadId, rows);
                await using var connection = new SqliteConnection($"Data Source={_backing[kind]}");
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE messages SET seq = NULL WHERE thread_id = $thread_id;";
                _ = command.Parameters.AddWithValue("$thread_id", threadId);
                _ = await command.ExecuteNonQueryAsync();
                return;
            }
            case "file":
            {
                var threadDir = Path.Combine(_backing[kind], threadId);
                _ = Directory.CreateDirectory(threadDir);
                var options = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                };
                var legacy = rows.Select(r => r with { Seq = null }).ToList();
                await File.WriteAllTextAsync(
                    Path.Combine(threadDir, "messages.json"),
                    JsonSerializer.Serialize(legacy, options)
                );
                return;
            }
            default:
                throw new NotSupportedException("The in-memory store has no rows older than the binary.");
        }
    }

    /// <summary>A minimal text row. <see cref="PersistedMessage.Seq"/> is left for the store to assign.</summary>
    public static PersistedMessage Row(
        string threadId,
        string id,
        long timestamp,
        int? orderIdx = 0,
        string runId = "run-1",
        string messageType = "TextMessage",
        string role = "User",
        string? messageJson = null
    ) =>
        new()
        {
            Id = id,
            ThreadId = threadId,
            RunId = runId,
            GenerationId = "gen-1",
            MessageOrderIdx = orderIdx,
            Timestamp = timestamp,
            MessageType = messageType,
            Role = role,
            MessageJson = messageJson ?? $$"""{"$type":"text","text":"{{id}}","role":"user"}""",
        };

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            await disposable.DisposeAsync();
        }

        SqliteConnection.ClearAllPools();
        await Task.Delay(50);
        DetachedStoreTeardown.Purge(_root);
    }
}
